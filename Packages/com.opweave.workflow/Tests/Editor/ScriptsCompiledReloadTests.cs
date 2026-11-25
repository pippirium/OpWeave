#nullable enable

// T-W4.1-01 [Edit] tests: local:T-W4.1-01:scripts-compiled — the SPEC §T.3
// domain-reload cases of the §G4 ScriptsCompiled wait. The [UnityTest]s
// hold nothing across the reload (UTF resurrects the iterator but not its
// fields), so each phase is a static helper and the persisted input — the
// resumeToken plus the counter snapshots — lives under
// <project>/Library/OpWeaveCompileProbe/. The script that forces the
// recompile lives under Assets/Editor/OpWeaveCompileProbe/ and the folder
// is removed by [UnityTearDown] — the AssetImportReloadTests pattern.
using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// SPEC §T.3 / §G4: the compile wait survives a domain reload — the
    /// condition is reconstructed from the persisted resumeToken alone —
    /// a failed compile yields OW-RUN-009 with the error line, and the
    /// §T.4 L-05 no-change request completes on observed completion
    /// whether or not a reload happens.
    /// </summary>
    internal sealed class ScriptsCompiledReloadTests
    {
        private const string ProbeFolder = "Assets/Editor/OpWeaveCompileProbe";
        private const string MarkerName = "ReloadMarker.cs";
        private const string BrokenName = "BrokenProbe.cs";

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveCompileProbe\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private const string BrokenSource =
            "namespace OpWeaveCompileProbe\n{\n" +
            "    internal static class BrokenProbe\n" +
            "    {\n" +
            "        public const int Value = ;\n" +
            "    }\n" +
            "}\n";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveCompileProbe");

        private static string StatePath => Path.Combine(ProbeDir, "compile-state.txt");
        private static string L05Path => Path.Combine(ProbeDir, "l05.txt");
        private static string SessionProbePath => Path.Combine(ProbeDir, "session.txt");

        // T-W2.4-09: a per-Editor-session token each phase-1 stores in
        // SessionState and writes into the session probe file beside the
        // persisted wait state (a JSON document whose reader ignores
        // unknown members); the post-yield phases read both to prove the
        // persisted state belongs to this session's phase-1 rather than
        // residue of an earlier run (the ChangeSetDomainReloadTests
        // pattern).
        private const string ProbeTokenKey = "OpWeave.W2409.ScriptsCompiled.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume
        // is detected. leg is a method local, so a real reload resets it;
        // the this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static string ProbeDisk =>
            Path.Combine(ProjectRoot, ProbeFolder.Replace('/', Path.DirectorySeparatorChar));

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private static void Crumb(string message) => UnityEngine.Debug.Log("[G4] " + message);

        [Serializable]
        private sealed class ProbePhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static ProbePhaseResult Pass() => new ProbePhaseResult { Ok = true };

            public static ProbePhaseResult Fail(string message) =>
                new ProbePhaseResult { Ok = false, Failure = message };
        }

        private static void CleanupProbeArtifacts()
        {
            var assetsDirty = false;
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
                assetsDirty = true;
            }
            if (Directory.Exists(ProbeDisk))
            {
                Directory.Delete(ProbeDisk, recursive: true);
                assetsDirty = true;
            }
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a held file is left for the next run */ }
            }
            if (assetsDirty)
            {
                AssetDatabase.Refresh();
            }
            CompileErrorCapture.Clear();
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
        // captured PC is the correct one.
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

        // The phase-1 probe proves phase-1 ran in THIS Editor session: the
        // wait-state file must exist AND the session file's content must
        // equal the token phase-1 stored in SessionState. Both Library/ and
        // SessionState survive the reload, so a stale-resume hijack
        // (phase-1 skipped: no file, no token) and residue of an earlier
        // session (file yes, token no) both fail the check — the caller
        // then re-drives a real phase-1 instead of skipping into phase-2.
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

        // The reload-during-Waiting case of
        // local:T-W4.1-01:scripts-compiled: a compile wait armed before the
        // reload completes in the new domain — from the persisted
        // resumeToken alone — with the §G4 ReloadCount moved past the
        // request-time snapshot.
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator ScriptsCompiled_CompletesAfterDomainReload_FromTokenOnly()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 returned ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True,
                "pre-reload phase failed: " + phase1.Failure);

            // T-W2.4-09 stale-resume self-heal: a predecessor's captured UTF
            // resume PC applies to this fresh enumerator and lands it at one
            // of the post-yield states inside this loop (or in the poll loop
            // below) — with every local reset to default. Each continuation
            // drains, then MUST pass the this-session probe check: a probe
            // phase-1 never wrote re-drives a real phase-1 + marker + reload
            // instead of skipping silently into phase-2. If the probe still
            // cannot be produced after the bound, the loop exits and
            // phase-2's existing never-executed diagnostic fails loudly.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var legGuard = 0;
                while (DomainSentinelAlive && legGuard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the probe compile did not produce a domain reload");

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // Deleting the probe folder guarantees the marker re-write
                // queues a real compile rather than a byte-identical no-op.
                CleanupProbeArtifacts();
                var redrive = RunPhaseBeforeReload();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var phase2 = RunPhaseAfterReload();
            Crumb("phase-2 returned ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True,
                "post-reload phase failed: " + phase2.Failure);

            // The Editor may still be settling right after the reload — a
            // Pending verdict is lawful until it is idle, so keep polling
            // the persisted token across frames.
            var guard = 0;
            while (guard++ < 600)
            {
                var check = CheckPersistedToken();
                if (check.Kind == WaitStatusKind.Done)
                {
                    yield break;
                }
                if (check.Kind == WaitStatusKind.Failed)
                {
                    Assert.Fail("the resumed compile wait failed: " +
                        check.Code + " " + check.Hint);
                }
                yield return null;
            }
            Assert.Fail("the resumed compile wait stayed Pending after the reload");
        }

        // The broken-script case of local:T-W4.1-01:scripts-compiled: the
        // compile fails without a reload and the wait — again resumed from
        // its persisted token — fails with OW-RUN-009 carrying the error
        // line the pipeline callback captured.
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator ScriptsCompiled_BrokenScript_FailsWithOwRun009()
        {
            LogAssert.ignoreFailingMessages = true;
            var phase1 = RunPhaseBeforeBrokenCompile();
            Crumb("phase-1 returned ok=" + phase1.Ok + " failure=" + phase1.Failure);
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
                var redrive = RunPhaseBeforeBrokenCompile();
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
                var check = CheckPersistedToken();
                if (check.Kind == WaitStatusKind.Pending)
                {
                    yield return null;
                    continue;
                }
                Assert.That(check.Kind, Is.EqualTo(WaitStatusKind.Failed),
                    "a broken script must fail the wait, got " + check.Kind);
                Assert.That(check.Code, Is.EqualTo("OW-RUN-009"));
                Assert.That(check.Hint, Does.Contain("BrokenProbe"),
                    "the OW-RUN-009 hint must carry the captured error line");
                Assert.That(check.Hint, Does.Contain("error CS"),
                    "the OW-RUN-009 hint must carry the compiler message");
                yield break;
            }
            Assert.Fail("the compile wait stayed Pending over a failed compile");
        }

        // The L-05 case of local:T-W4.1-01:scripts-compiled: a
        // RequestScriptCompilation with no source changes must not be the
        // reload-or-nothing case — the wait completes on observed
        // completion (the pipeline's completion counter or the reload
        // counter), and whether a reload happened is recorded, never
        // assumed (SPEC §T.4 L-05).
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator RequestScriptCompilation_NoChanges_CompletesAndRecordsL05()
        {
            var phase1 = ArmWaitForNoChange();
            Crumb("phase-1 returned ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True,
                "pre-request phase failed: " + phase1.Failure);

            // T-W2.4-09 stale-resume self-heal: a predecessor's captured UTF
            // resume PC can land this fresh enumerator past the yield —
            // skipping ArmWaitForNoChange so RecordL05 would read residue or
            // nothing. Each continuation drains (the bound stays a grace,
            // never an expectation — L-05), then MUST pass the this-session
            // probe check or re-drive a real arm+request; after the bound
            // RecordL05's missing-state diagnostic fails loudly.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts(false, false);
                // Give a queued reload its chance, but a no-change request
                // may leave the domain standing — the bound is a grace, not
                // an expectation (L-05).
                var legGuard = 0;
                while (DomainSentinelAlive && legGuard++ < 600)
                {
                    yield return null;
                }

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupProbeArtifacts();
                var redrive = ArmWaitForNoChange();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-request phase failed: " + redrive.Failure);
            }

            var record = RecordL05();
            Assert.That(record.Ok, Is.True,
                "the L-05 recording failed: " + record.Failure);

            var guard = 0;
            while (guard++ < 600)
            {
                var check = CheckPersistedToken();
                if (check.Kind == WaitStatusKind.Done)
                {
                    yield break;
                }
                if (check.Kind == WaitStatusKind.Failed)
                {
                    Assert.Fail("the no-change compile wait failed: " +
                        check.Code + " " + check.Hint);
                }
                yield return null;
            }
            Assert.Fail(
                "the no-change compile wait stayed Pending — no observable " +
                "completion arrived (see " + L05Path + ")");
        }

        // Phase 1 of the reload case: arm the §G4 capture window like the
        // Waitable Operation does, request the compile, snapshot and
        // persist the resumeToken with the live counters — the only state
        // the post-reload phase gets — then write the marker script whose
        // compile forces the reload.
        private static ProbePhaseResult RunPhaseBeforeReload()
        {
            try
            {
                Crumb("phase-1 begin: arming the compile wait");
                CompileErrorCapture.BeginStep();
                CompilationPipeline.RequestScriptCompilation();
                var token = ScriptsCompiledCondition.InitialToken();
                PersistWait(token);

                Directory.CreateDirectory(ProbeDisk);
                File.WriteAllText(Path.Combine(ProbeDisk, MarkerName), MarkerSource);
                AssetDatabase.Refresh();
                ArmDomainSentinel();
                Crumb("phase-1 complete: resumeToken persisted, marker written, sentinel armed");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-1 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("phase-1 threw an unexpected exception: " + e);
            }
        }

        // Phase 1 of the broken case: same arm, but the probe script is
        // the syntax error the OW-RUN-009 verdict must surface.
        private static ProbePhaseResult RunPhaseBeforeBrokenCompile()
        {
            try
            {
                Crumb("phase-1 begin: arming the compile wait over a broken script");
                CompileErrorCapture.BeginStep();
                var token = ScriptsCompiledCondition.InitialToken();
                PersistWait(token);

                Directory.CreateDirectory(ProbeDisk);
                File.WriteAllText(Path.Combine(ProbeDisk, BrokenName), BrokenSource);
                CompilationPipeline.RequestScriptCompilation();
                ArmDomainSentinel();
                Crumb("phase-1 complete: resumeToken persisted, broken script written");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-1 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("phase-1 threw an unexpected exception: " + e);
            }
        }

        // Phase 1 of the L-05 case: the wait is armed and requested, the
        // counter snapshots persisted — no script changes are written.
        private static ProbePhaseResult ArmWaitForNoChange()
        {
            try
            {
                Crumb("phase-1 begin: arming the no-change compile wait");
                CompileErrorCapture.BeginStep();
                var token = ScriptsCompiledCondition.InitialToken();
                PersistWait(token);
                CompilationPipeline.RequestScriptCompilation();
                ArmDomainSentinel();
                Crumb("phase-1 complete: resumeToken persisted, request issued, no changes");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-1 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("phase-1 threw an unexpected exception: " + e);
            }
        }

        // Phase 2 (new domain): read the persisted resumeToken — the only
        // surviving state — confirm the §G4 counter proves the reload and
        // ask a fresh condition once.
        private static ProbePhaseResult RunPhaseAfterReload()
        {
            try
            {
                Crumb("phase-2 begin: reading persisted resumeToken");
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(StatePath))
                {
                    return ProbePhaseResult.Fail(
                        expectedToken.Length == 0
                            ? "phase-1 never executed in this Editor session (" +
                                StatePath + " was never written; probeDir exists=" +
                                Directory.Exists(ProbeDir) + ")"
                            : "the persisted wait state " + StatePath +
                                " was deleted after phase-1 wrote it");
                }
                if (expectedToken.Length == 0 || !File.Exists(SessionProbePath) ||
                    File.ReadAllText(SessionProbePath).Trim() != expectedToken)
                {
                    return ProbePhaseResult.Fail(
                        "the persisted wait state is stale — a previous Editor " +
                        "session wrote " + SessionProbePath);
                }
                if (!TryReadWait(out var token, out var baseline))
                {
                    return ProbePhaseResult.Fail("the persisted wait state is missing");
                }
                if (DomainReloadCounter.Current <= baseline)
                {
                    return ProbePhaseResult.Fail(
                        "OpWeave.ReloadCount did not increase across the reload: " +
                        baseline + " -> " + DomainReloadCounter.Current);
                }
                var verdict = new ScriptsCompiledCondition().Check(token);
                Crumb("phase-2 fresh-condition verdict=" + verdict.Kind);
                if (verdict.Kind == WaitStatusKind.Failed)
                {
                    return ProbePhaseResult.Fail(
                        "the restored condition failed the compile: " + verdict.Error!.Code);
                }
                Crumb("phase-2 done: the token alone rebuilt the wait (" + verdict.Kind + ")");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-2 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("phase-2 threw an unexpected exception: " + e);
            }
        }

        // The L-05 record: whether the no-change request reloaded the
        // domain, how far each §G4 counter moved and what the condition
        // observed — persisted under Library/ so the run can report the
        // measured behavior rather than an assumption (SPEC §T.4 L-05).
        private static ProbePhaseResult RecordL05()
        {
            try
            {
                if (!TryReadWait(out _, out var reloadBaseline, out var finishedBaseline))
                {
                    return ProbePhaseResult.Fail("the persisted wait state is missing");
                }
                var reloadOccurred = !DomainSentinelAlive;
                var record = new JObject
                {
                    ["reloadOccurred"] = reloadOccurred,
                    ["reloadDelta"] = DomainReloadCounter.Current - reloadBaseline,
                    ["finishedDelta"] =
                        CompileErrorCapture.FinishedCount - finishedBaseline,
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["isUpdating"] = EditorApplication.isUpdating,
                    ["scriptCompilationFailed"] = EditorUtility.scriptCompilationFailed,
                };
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(L05Path, record.ToString());
                Crumb("L-05 record: " + record.ToString(Newtonsoft.Json.Formatting.None));
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("l05 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("the L-05 recording threw: " + e);
            }
        }

        private sealed class PersistedCheck
        {
            internal WaitStatusKind Kind;
            internal string Code = string.Empty;
            internal string Hint = string.Empty;
        }

        // One fresh check of the persisted resumeToken — the same call a
        // resumed Waitable step's Poll would make in this domain.
        private static PersistedCheck CheckPersistedToken()
        {
            var check = new PersistedCheck();
            if (!ProbeWrittenThisSession())
            {
                // A resumed wait must never consume a previous Editor
                // session's persisted token: missing and stale probe state
                // are both hard failures, never a silent success.
                check.Kind = WaitStatusKind.Failed;
                check.Code = File.Exists(StatePath) ? "stale-state" : "missing-state";
                return check;
            }
            if (!TryReadWait(out var token, out _, out _))
            {
                check.Kind = WaitStatusKind.Failed;
                check.Code = "missing-state";
                return check;
            }
            var verdict = new ScriptsCompiledCondition().Check(token);
            check.Kind = verdict.Kind;
            if (verdict.Error is { } error)
            {
                check.Code = error.Code;
                check.Hint = error.Hint ?? string.Empty;
            }
            return check;
        }

        // The wait state file: the resumeToken plus the counter snapshots
        // at request time — persisted like the journal's StepWaiting
        // record keeps the token, so the post-reload phase gets nothing
        // but durable state.
        private static void PersistWait(JsonValue token)
        {
            var state = new JObject
            {
                ["token"] = token.ToToken(),
                ["reloads"] = DomainReloadCounter.Current,
                ["compiles"] = CompileErrorCapture.FinishedCount,
            };
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(StatePath, state.ToString());
            var sessionToken = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeTokenKey, sessionToken);
            File.WriteAllText(SessionProbePath, sessionToken + "\n");
            Crumb("persisted wait state: " +
                state.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static bool TryReadWait(
            out JsonValue token, out int reloadBaseline) =>
            TryReadWait(out token, out reloadBaseline, out _);

        private static bool TryReadWait(
            out JsonValue token, out int reloadBaseline, out int finishedBaseline)
        {
            token = null!;
            reloadBaseline = 0;
            finishedBaseline = 0;
            if (!File.Exists(StatePath))
            {
                return false;
            }
            var state = JObject.Parse(File.ReadAllText(StatePath));
            var parsed = JsonValue.Parse(state["token"]!.ToString());
            if (!parsed.Ok)
            {
                return false;
            }
            token = parsed.Value!;
            reloadBaseline = (int)state["reloads"]!;
            finishedBaseline = (int)state["compiles"]!;
            return true;
        }
    }
}
