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

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var probesPresent = AssetDatabase.IsValidFolder(ProbeFolder) ||
                Directory.Exists(ProbeDisk);
            CleanupProbeArtifacts();
            if (probesPresent)
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            var probesPresent = AssetDatabase.IsValidFolder(ProbeFolder) ||
                Directory.Exists(ProbeDisk);
            CleanupProbeArtifacts();
            if (probesPresent || EditorUtility.scriptCompilationFailed)
            {
                // A probe script — above all a broken one — must not leak
                // into the next test's compile state.
                yield return new RecompileScripts(false);
            }
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

            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the generated script's compile did not produce a domain reload");

            guard = 0;
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

            yield return new RecompileScripts(true, false);
            LogAssert.ignoreFailingMessages = false;
            var guard = 0;
            while (EditorApplication.isCompiling && guard++ < 600)
            {
                yield return null;
            }

            guard = 0;
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
