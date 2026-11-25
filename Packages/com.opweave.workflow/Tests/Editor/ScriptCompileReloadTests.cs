#nullable enable

// T-W4.2-02 [Edit] tests: local:T-W4.2-02:script-create-compile — the
// real compile drive of unity.script.create + unity.script.compile
// (SPEC §G8 items 2–3, §I.4, §G4, §F6). The [UnityTest]s hold nothing
// across the reload (UTF resurrects the iterator but not its fields), so
// each phase is a static helper and the persisted input — the
// resumeToken plus the script path — lives under
// <project>/Library/OpWeaveScriptCompileProbe/. The scripts the
// Operations write live under Assets/Scripts/OpWeaveScriptCompileProbe/
// (inside the default codePaths) and are removed by [UnityTearDown] —
// the ScriptsCompiledReloadTests pattern.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// SPEC §G8 item 3 / §T.3: a real <c>unity.script.create</c> write
    /// followed by the real <c>unity.script.compile</c> wait across the
    /// domain reload the compile causes, and the broken-script half —
    /// OW-RUN-009 with the error line in the hint and on the journaled
    /// <c>unity_compile</c> records (SPEC §F6).
    /// </summary>
    internal sealed class ScriptCompileReloadTests
    {
        private const string ProbeFolder = "Assets/Scripts/OpWeaveScriptCompileProbe";
        private const string GoodName = "CompileProbe.cs";
        private const string BrokenName = "BrokenCompileProbe.cs";

        private const string GoodSource =
            "namespace Game.Probe\n{\n" +
            "    public sealed class CompileProbe : UnityEngine.MonoBehaviour\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private const string BrokenSource =
            "namespace Game.Probe\n{\n" +
            "    public sealed class BrokenCompileProbe\n" +
            "    {\n" +
            "        public const int Value = ;\n" +
            "    }\n" +
            "}\n";

        private static readonly IReadOnlyDictionary<string, string> CreateInputTypes =
            new Dictionary<string, string>
            {
                ["path"] = "unity.AssetPath",
                ["content"] = "core.String",
                ["overwrite"] = "core.Bool",
            };

        private static readonly IReadOnlyDictionary<string, string> CreateOutputTypes =
            new Dictionary<string, string>
            {
                ["script"] = "unity.AssetRef<unity.MonoScript>",
            };

        private static readonly IReadOnlyDictionary<string, string> CompileOutputTypes =
            new Dictionary<string, string>
            {
                ["result"] = "core.Json",
            };

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveScriptCompileProbe");

        private static string StatePath => Path.Combine(ProbeDir, "state.txt");
        private static string JournalPath => Path.Combine(ProbeDir, "run-journal.ndjson");
        private static string SessionProbePath => Path.Combine(ProbeDir, "session.txt");

        // T-W2.4-09: a per-Editor-session token each phase-1 stores in
        // SessionState and writes into the session probe file beside the
        // persisted wait state; the post-yield phases read both to prove
        // the persisted state belongs to this session's phase-1 rather
        // than residue of an earlier run (the ChangeSetDomainReloadTests
        // pattern). PersistState does not touch it — the Waiting re-persist
        // keeps the phase-1 token so file and SessionState stay paired.
        private const string ProbeTokenKey = "OpWeave.W2409.ScriptCompile.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume
        // is detected. leg is a method local, so a real reload resets it;
        // the this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static string ProbeDisk =>
            Path.Combine(ProjectRoot, ProbeFolder.Replace('/', Path.DirectorySeparatorChar));

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private static void Crumb(string message) => Debug.Log("[W4202] " + message);

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;
            public static PhaseResult Pass() => new PhaseResult { Ok = true };
            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        // The §F6 sink: the same journal records the bound step's
        // IRunLogSink appends — what get_run_logs reads back.
        private sealed class JournalFileSink : IRunLogSink
        {
            private readonly string _path;
            private readonly string _session;
            private int _seq;

            internal JournalFileSink(string path, string session)
            {
                _path = path;
                _session = session;
            }

            public void Write(
                Severity level, string message, string? code = null, JsonValue? data = null)
            {
                File.AppendAllText(_path,
                    JournalCodec.WriteRecord(new JournalRecord(
                        ++_seq, UtcTimestamp.Now(), _session, JournalRecordType.Log,
                        0, null,
                        JsonValue.From(new JObject
                        {
                            ["level"] = level == Severity.Error ? "error"
                                : level == Severity.Warning ? "warning" : "info",
                            ["message"] = message,
                            ["data"] = data?.ToToken() ?? new JObject(),
                        }))));
            }
        }

        private static void CleanupProbeArtifacts()
        {
            var dirty = false;
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
                dirty = true;
            }
            if (Directory.Exists(ProbeDisk))
            {
                Directory.Delete(ProbeDisk, recursive: true);
                dirty = true;
            }
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a held file is left for the next run */ }
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            CompileErrorCapture.Clear();
            CompileWarningCapture.Clear();
        }

        // Setup and teardown must never yield an ExpectDomainReload
        // instruction (RecompileScripts): UTF's PrepareForDomainReload
        // records the CURRENT TestEnumerator's program counter — still a
        // different test's method enumerator during setup and teardown —
        // and the resume then applies that stale PC to the next test
        // method's fresh enumerator, jumping it past phase-1 entirely when
        // the captured enumerator was abandoned mid-state. The probe-script
        // deletions queue their compile under the runner's
        // LockReloadAssemblies, so the reload they need drains at the next
        // test body's own `yield return new RecompileScripts()`, whose
        // captured PC is the correct one. A leftover broken script's queued
        // compile likewise drains there; nothing here awaits it.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup cleanup enter; probeDir=" + Directory.Exists(ProbeDir));
            CleanupProbeArtifacts();
            yield break;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            Crumb("teardown cleanup enter; probeDir=" + Directory.Exists(ProbeDir));
            CleanupProbeArtifacts();
            yield break;
        }

        private static void ArmSessionProbe()
        {
            var sessionToken = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeTokenKey, sessionToken);
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(SessionProbePath, sessionToken + "\n");
        }

        // The phase-1 probe proves phase-1 ran in THIS Editor session: the
        // wait-state file must exist AND the session file's content must
        // equal the token phase-1 stored in SessionState. Both Library/ and
        // SessionState survive the reload, so a stale-resume hijack
        // (phase-1 skipped: no file, no token) and residue of an earlier
        // session (file yes, token no) both fail the check — the caller
        // then re-drives a real phase-1 instead of skipping into phase-2,
        // and the persisted-token poll reports it as a hard failure rather
        // than resuming a previous session's wait.
        private static bool ProbeWrittenThisSession()
        {
            if (!File.Exists(StatePath) || !File.Exists(SessionProbePath))
            {
                return false;
            }
            var expectedToken = SessionState.GetString(ProbeTokenKey, "");
            return expectedToken.Length > 0 &&
                File.ReadAllText(SessionProbePath).Trim() == expectedToken;
        }

        // The success half of local:T-W4.2-02:script-create-compile: the
        // real Operations — script.create writes a valid script inside the
        // code paths, script.compile requests and waits — and the wait
        // resumes from its persisted token in the new domain, landing the
        // §I.4 result payload.
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator CreateThenCompile_CompletesAfterDomainReload_WithResultJson()
        {
            var phase1 = PhaseGoodBeforeReload();
            Crumb("phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True,
                "pre-reload phase failed: " + phase1.Failure);

            // T-W2.4-09 stale-resume self-heal: a predecessor's captured UTF
            // resume PC applies to this fresh enumerator and lands it at one
            // of the post-yield states inside this loop (or in the poll loop
            // below) — with every local reset to default. Each continuation
            // drains, then MUST pass the this-session probe check: a probe
            // phase-1 never wrote re-drives a real phase-1 + compile +
            // reload instead of skipping silently into the poll. If the
            // probe still cannot be produced after the bound, the loop
            // exits and the poll's stale-state failure is loud.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var legGuard = 0;
                while (DomainSentinelAlive && legGuard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the generated script's compile did not produce a domain reload");

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // Deleting the probe folder guarantees the script re-write
                // queues a real compile rather than a byte-identical no-op.
                CleanupProbeArtifacts();
                var redrive = PhaseGoodBeforeReload();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var guard = 0;
            while (guard++ < 600)
            {
                var check = PollPersistedWait();
                if (check.Kind == StepResultKind.Failed)
                {
                    Assert.Fail("the resumed compile wait failed: " +
                        check.Code + " " + check.Hint);
                }
                if (check.Kind == StepResultKind.Succeeded)
                {
                    var payload = check.Result;
                    Assert.IsNotNull(payload, "the wait must land the result payload");
                    Assert.That((JArray?)payload!["errors"], Is.Not.Null.And.Count.EqualTo(0),
                        "a clean compile reports no errors");
                    Assert.That((JArray?)payload["warnings"], Is.Not.Null,
                        "the §I.4 result carries a warnings member");
                    yield break;
                }
                yield return null;
            }
            Assert.Fail("the resumed compile wait stayed Pending after the reload");
        }

        // The failure half: the real Operations drive a broken-script
        // compile — no domain reload — and the resumed wait fails with
        // OW-RUN-009 carrying the error line, while the journaled
        // unity_compile records carry the same file(line,column) detail
        // get_run_logs surfaces (SPEC §F6).
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator BrokenCompile_FailsOwRun009_AndJournalsErrorLineAndFile()
        {
            LogAssert.ignoreFailingMessages = true;
            var phase1 = PhaseBrokenBeforeCompile();
            Crumb("phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True,
                "pre-compile phase failed: " + phase1.Failure);

            // T-W2.4-09 stale-resume self-heal: a predecessor's captured UTF
            // resume PC can land this fresh enumerator at one of the
            // post-yield states inside this loop — skipping the broken
            // script write entirely. Each continuation drains, then MUST
            // pass the this-session probe check or re-drive a real phase-1;
            // after the bound, the poll loop's OW-RUN-009 expectations fail
            // loudly on the missing state.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts(true, false);
                LogAssert.ignoreFailingMessages = false;
                var legGuard = 0;
                while (EditorApplication.isCompiling && legGuard++ < 600)
                {
                    yield return null;
                }

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // Deleting the probe folder guarantees the broken-script
                // re-write queues a real compile rather than a
                // byte-identical no-op.
                CleanupProbeArtifacts();
                LogAssert.ignoreFailingMessages = true;
                var redrive = PhaseBrokenBeforeCompile();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-compile phase failed: " + redrive.Failure);
            }
            Assert.That(ProbeWrittenThisSession(),
                "the persisted wait probe is missing or stale — phase-1 did " +
                "not execute in this Editor session");

            var guard = 0;
            while (guard++ < 600)
            {
                var check = PollPersistedWait();
                if (check.Kind == StepResultKind.Waiting)
                {
                    yield return null;
                    continue;
                }
                Assert.That(check.Kind, Is.EqualTo(StepResultKind.Failed),
                    "a broken script must fail the wait, got " + check.Kind);
                Assert.That(check.Code, Is.EqualTo(ErrorCodes.OW_RUN_009));
                Assert.That(check.Hint, Does.Contain("BrokenCompileProbe"),
                    "the OW-RUN-009 hint must carry the captured error line");
                Assert.That(check.Hint, Does.Contain("error CS"),
                    "the OW-RUN-009 hint must carry the compiler message");

                // SPEC §F6: the journaled log records — the records
                // get_run_logs serves — carry the error line and file.
                var lines = File.ReadAllLines(JournalPath);
                Assert.That(lines.Any(line =>
                        line.Contains("unity_compile") &&
                        line.Contains("BrokenCompileProbe") &&
                        line.Contains("error CS")),
                    Is.True,
                    "the run's logs must carry the compile error's file and line; " +
                    "journal: " + string.Join("\n", lines));
                yield break;
            }
            Assert.Fail("the compile wait stayed Pending over a failed compile");
        }

        // Phase 1 (success): unity.script.create writes the valid script
        // under the code path, unity.script.compile arms the wait and
        // requests the compilation — the resumeToken plus the script path
        // are persisted before the reload takes the domain.
        private static PhaseResult PhaseGoodBeforeReload()
        {
            try
            {
                EnsureFolder(ProbeFolder);
                var created = new CreateScriptOperation().Execute(CreateContext(
                    ProbeFolder + "/" + GoodName, GoodSource));
                if (created.Kind != StepResultKind.Succeeded)
                {
                    return PhaseResult.Fail("script.create failed: " + Describe(created));
                }
                var waiting = new CompileScriptsOperation().Execute(CompileContext());
                if (waiting.Kind != StepResultKind.Waiting || waiting.ResumeToken is null)
                {
                    return PhaseResult.Fail("script.compile did not return Waiting");
                }
                PersistState(waiting.ResumeToken, ProbeFolder + "/" + GoodName);
                ArmSessionProbe();
                ArmDomainSentinel();
                Crumb("phase-1 complete: script written, wait armed, token persisted");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-1 threw " + e.GetType().Name + ": " + e.Message);
                return PhaseResult.Fail("phase-1 threw: " + e.Message);
            }
        }

        // Phase 1 (broken): the same real Operations over a syntactically
        // broken source — the write itself succeeds; the compile must not.
        // The §F6 step-log binding is installed like the context factory
        // does on a real Run: journal path, step index, the step's log
        // sink and the flush hook.
        private static PhaseResult PhaseBrokenBeforeCompile()
        {
            try
            {
                EnsureFolder(ProbeFolder);
                var created = new CreateScriptOperation().Execute(CreateContext(
                    ProbeFolder + "/" + BrokenName, BrokenSource));
                if (created.Kind != StepResultKind.Succeeded)
                {
                    return PhaseResult.Fail("script.create failed: " + Describe(created));
                }
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(JournalPath, JournalCodec.WriteHeader(
                    new JournalHeader("run-probe", "planhash-probe")));
                CompileErrorCapture.BindStepLog(
                    JournalPath, 0,
                    new JournalFileSink(JournalPath, "session-probe"),
                    () => { });
                var waiting = new CompileScriptsOperation().Execute(CompileContext());
                if (waiting.Kind != StepResultKind.Waiting || waiting.ResumeToken is null)
                {
                    return PhaseResult.Fail("script.compile did not return Waiting");
                }
                PersistState(waiting.ResumeToken, ProbeFolder + "/" + BrokenName);
                ArmSessionProbe();
                Crumb("phase-1 complete: broken script written, wait armed");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-1 threw " + e.GetType().Name + ": " + e.Message);
                return PhaseResult.Fail("phase-1 threw: " + e.Message);
            }
        }

        private sealed class PersistedPoll
        {
            internal StepResultKind Kind;
            internal string Code = string.Empty;
            internal string Hint = string.Empty;
            internal JObject? Result;
        }

        // One fresh Poll of the persisted resumeToken through the real
        // Operation — the same call a resumed Waitable step makes in this
        // domain.
        private static PersistedPoll PollPersistedWait()
        {
            var poll = new PersistedPoll();
            if (!ProbeWrittenThisSession())
            {
                // A resumed wait must never consume a previous Editor
                // session's persisted token: missing and stale probe state
                // are both hard failures, never a silent success.
                poll.Kind = StepResultKind.Failed;
                poll.Code = File.Exists(StatePath) ? "stale-state" : "missing-state";
                return poll;
            }
            if (!TryReadState(out var token, out var path))
            {
                poll.Kind = StepResultKind.Failed;
                poll.Code = "missing-state";
                return poll;
            }
            var context = CompileContext(token);
            var result = new CompileScriptsOperation().Poll(context);
            poll.Kind = result.Kind;
            if (result.Error is { } error)
            {
                poll.Code = error.Code;
                poll.Hint = error.Hint ?? string.Empty;
            }
            if (result.Kind == StepResultKind.Succeeded &&
                context.Outputs.TryGetValue("result", out var payload))
            {
                poll.Result = payload.ToToken() as JObject;
            }
            if (result.Kind == StepResultKind.Waiting && result.ResumeToken is { } next)
            {
                PersistState(next, path);
            }
            return poll;
        }

        private static void PersistState(JsonValue token, string scriptPath)
        {
            var state = new JObject
            {
                ["token"] = token.ToToken(),
                ["scriptPath"] = scriptPath,
            };
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(StatePath, state.ToString());
        }

        private static bool TryReadState(out JsonValue token, out string scriptPath)
        {
            token = null!;
            scriptPath = string.Empty;
            if (!File.Exists(StatePath))
            {
                return false;
            }
            var state = JObject.Parse(File.ReadAllText(StatePath));
            if (state["token"] is not JObject stored ||
                state["scriptPath"] is not JValue { Type: JTokenType.String } storedPath)
            {
                return false;
            }
            token = JsonValue.From(stored);
            scriptPath = (string)storedPath.Value!;
            return true;
        }

        private static UnityStepContext CreateContext(string path, string content) =>
            new UnityStepContext(
                new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(
                        JsonValue.From(JToken.FromObject(path))),
                    ["content"] = InputValue.Present(
                        JsonValue.From(JToken.FromObject(content))),
                },
                CreateInputTypes,
                CreateOutputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "script"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

        private static UnityStepContext CompileContext(JsonValue? resumeToken = null) =>
            new UnityStepContext(
                new Dictionary<string, InputValue>(),
                new Dictionary<string, string>(),
                CompileOutputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(1, "compile"),
                new StepLogSink(
                    "run", 1, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                resumeToken);

        private static void EnsureFolder(string folderPath)
        {
            var segments = folderPath.Split('/');
            var parent = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var child = parent + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(child))
                {
                    Assert.That(AssetDatabase.CreateFolder(parent, segments[i]),
                        Is.Not.Empty, "the probe folder must be created: " + child);
                }
                parent = child;
            }
        }

        private static string Describe(StepResult result) =>
            result.Kind +
            (result.Error is null
                ? string.Empty
                : " " + result.Error.Code + " " + result.Error.Message);
    }
}
