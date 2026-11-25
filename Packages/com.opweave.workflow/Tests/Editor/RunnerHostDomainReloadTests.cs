#nullable enable
// T-W2.1-04 [Edit] tests: local:T-W2.1-04:runner-host — the
// beforeAssemblyReload flush of SPEC §F3 item 1 across a real domain
// reload: a Buffered journal record still pending when scripts recompile
// must be on disk afterwards. Follows the SPEC §T.3 / L-14 pattern of
// CoordinatorRunLockReloadTests (marker script + RecompileScripts + domain
// sentinel; UTF locals do not survive, so everything crosses the reload in
// probe files under Library/). T-W2.4-10: the probe file carries a
// per-Editor-session token mirrored in SessionState, setup and teardown
// never yield a reload emitter (the queued cleanup compile drains at the
// test body's own boundary), and the boundary loop re-drives a phase-1 a
// stale resume skipped — the ChangeSetDomainReloadTests shape.
using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RunnerHostDomainReloadTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveRunnerHostProbe");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string ProbeFile => Path.Combine(ProbeDir, "probe.txt");
        private static string MarkerFolder => "Assets/Editor/OpWeaveRunnerHostProbe";
        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveRunnerHostProbe\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private const string WorkflowId = "example.runnerhost.reload";

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // A per-Editor-session token phase-1 stores in SessionState and
        // writes as the probe file's last line; phase-2 reads both to
        // prove the file belongs to this session's phase-1 — a stale
        // resume that skipped phase-1 (no file, no nonce), a probe
        // deleted by cleanup, and residue of an earlier session (file
        // yes, nonce no) each fail closed, never silently.
        private const string ProbeSessionKey = "OpWeave.W2104.ProbeToken";

        // Bound on phase-1 re-drive legs after a stale resume is
        // detected (the T-W2.4-06 self-heal); the this-session probe
        // check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeSessionKey, nonce);
            return nonce;
        }

        // The probe proves phase-1 ran in THIS Editor session: the file
        // must exist AND its last line must equal the nonce phase-1
        // stored in SessionState — both survive the reload, so a skipped
        // phase-1 and cross-session residue both fail the check and the
        // caller re-drives a real phase-1 instead of skipping silently
        // into phase-2.
        private static bool ProbeWrittenThisSession()
        {
            if (!File.Exists(ProbeFile))
            {
                return false;
            }
            var expected = SessionState.GetString(ProbeSessionKey, "");
            if (expected.Length == 0)
            {
                return false;
            }
            var lines = File.ReadAllLines(ProbeFile);
            return lines.Length > 0 &&
                lines[lines.Length - 1] == expected;
        }

        private static void Crumb(string message) => Debug.Log("[W2104] " + message);

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;
            public static PhaseResult Pass() => new PhaseResult { Ok = true };
            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        private static bool CleanupArtifacts()
        {
            var dirty = false;
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            if (AssetDatabase.IsValidFolder(MarkerFolder))
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
                dirty = true;
            }
            if (Directory.Exists(MarkerDisk))
            {
                Directory.Delete(MarkerDisk, recursive: true);
                dirty = true;
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            return dirty;
        }

        // Setup and teardown must never yield an ExpectDomainReload
        // instruction (RecompileScripts): UTF's PrepareForDomainReload
        // records the CURRENT TestEnumerator's program counter — still a
        // different test's method enumerator while setup or teardown is
        // running — and the resume then applies that stale PC to the next
        // test method's fresh enumerator, jumping it past phase-1
        // entirely (T-W2.4-10). The marker-folder deletion above queues
        // its compile under the runner's LockReloadAssemblies, so the
        // reload it needs drains at the test body's own
        // `yield return new RecompileScripts()`, whose captured PC is the
        // correct one.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var dirty = CleanupArtifacts();
            Crumb("setup cleanup exit; assetsDirty=" + dirty);
            yield break;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var dirty = CleanupArtifacts();
            Crumb("teardown cleanup exit; assetsDirty=" + dirty);
            yield break;
        }

        // Phase 1 (pre-reload): install a probe host over a probe Run Store,
        // run a waitable step to a second Waiting so its step_progress
        // record stays Buffered in memory, verify it is NOT on disk yet,
        // persist the runId and arm the reload. The installed host's
        // beforeAssemblyReload subscription performs the §F3 item 1 flush.
        private static PhaseResult RunPhaseBeforeReload()
        {
            try
            {
                var fixture = new RunnerHostFixture(StoreRoot);
                var waitable = new RunnerHostFixture.LabelWaitableOperation(
                    "test.reload.waitable");
                fixture.Register(waitable, 'a');
                var steps = new[]
                {
                    RunnerHostFixture.Step(0, "w", "test.reload.waitable", 'a',
                        outputTypes: new System.Collections.Generic.Dictionary<string, string>
                        {
                            ["done"] = "core.Bool",
                        },
                        idempotency: OpWeave.Core.Operations.Idempotency.Idempotent),
                };
                var plan = RunnerHostFixture.Plan(WorkflowId, steps);
                var doc = RunnerHostFixture.Doc(
                    WorkflowId,
                    new[]
                    {
                        new NodeData
                        {
                            Id = "w", Operation = "test.reload.waitable", Version = 1,
                        },
                    },
                    Array.Empty<ControlEdge>());

                fixture.Install();
                var accepted = fixture.Host.AcceptRun(
                    plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
                if (!accepted.Ok)
                {
                    return PhaseResult.Fail("accept failed");
                }
                var runId = accepted.Value!.RunId;

                var tick = fixture.Host.Tick();
                if (!tick.Ok)
                {
                    return PhaseResult.Fail("first tick failed");
                }
                // Let the poll deadline and the 5 s progress interval pass,
                // then poll again: the changed label emits step_progress —
                // Buffered, so it stays pending in memory.
                fixture.Clock.Advance(TimeSpan.FromSeconds(6));
                tick = fixture.Host.Tick();
                if (!tick.Ok)
                {
                    return PhaseResult.Fail("second tick failed");
                }
                var journal = RunnerHostFixture.JournalPath(StoreRoot, runId);
                var content = File.ReadAllText(journal);
                if (content.Contains("\"type\":\"step_progress\""))
                {
                    return PhaseResult.Fail(
                        "the step_progress record reached disk before the flush");
                }
                if (!content.Contains("\"type\":\"step_waiting\""))
                {
                    return PhaseResult.Fail("no step_waiting record was journaled");
                }

                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(
                    ProbeFile, runId + "\n" + ArmProbeNonce() + "\n");

                Directory.CreateDirectory(MarkerDisk);
                File.WriteAllText(Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
                AssetDatabase.Refresh();
                ArmDomainSentinel();
                Crumb("phase-1 complete: runId=" + runId);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2 (post-reload): the beforeAssemblyReload subscription ran
        // during the recompile — the previously pending Buffered record must
        // be on disk now.
        private static PhaseResult RunPhaseAfterReload()
        {
            try
            {
                var expectedToken = SessionState.GetString(ProbeSessionKey, "");
                if (!File.Exists(ProbeFile))
                {
                    return PhaseResult.Fail(
                        expectedToken.Length == 0
                            ? "phase-1 never executed in this Editor session (" +
                                ProbeFile + " was never written; probeDir exists=" +
                                Directory.Exists(ProbeDir) + ")"
                            : "the persisted run identity " + ProbeFile +
                                " was deleted after phase-1 wrote it");
                }
                var lines = File.ReadAllLines(ProbeFile);
                var runId = lines[0];
                if (expectedToken.Length == 0 || lines.Length < 2 ||
                    lines[lines.Length - 1] != expectedToken)
                {
                    return PhaseResult.Fail(
                        "the persisted run identity is stale — a previous " +
                        "Editor session or an abandoned leg wrote " +
                        ProbeFile + "; refusing to trust it");
                }
                var journal = RunnerHostFixture.JournalPath(StoreRoot, runId);
                if (!File.Exists(journal))
                {
                    return PhaseResult.Fail("the journal file is missing after reload");
                }
                var content = File.ReadAllText(journal);
                if (!content.Contains("\"type\":\"step_progress\""))
                {
                    return PhaseResult.Fail(
                        "the buffered step_progress record was not flushed across the reload");
                }
                if (!content.Contains("stage-1"))
                {
                    return PhaseResult.Fail("the flushed record does not carry the stage-1 label");
                }
                Crumb("phase-2 complete: buffered record on disk");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator BufferedRecordsAreFlushedAcrossDomainReload()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True, "pre-reload phase failed: " + phase1.Failure);

            // T-W2.4-06 stale-resume self-heal: a predecessor's captured
            // UTF resume PC applies to this fresh enumerator and lands it
            // at a post-yield state inside this loop — with every local
            // (leg included) reset to default. Each leg drains, then MUST
            // pass the this-session probe check: a probe phase-1 never
            // wrote re-drives a real phase-1 + compile + reload instead
            // of skipping silently into phase-2. After the bound the loop
            // exits and phase-2's never-executed diagnostic fails loudly.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
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
                // A marker file an abandoned leg left behind carries
                // identical bytes, so deleting the probe assets
                // guarantees the re-write queues a real compile, never
                // a no-op Refresh.
                CleanupArtifacts();
                var redrive = RunPhaseBeforeReload();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var phase2 = RunPhaseAfterReload();
            Crumb("phase-2 ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True, "post-reload phase failed: " + phase2.Failure);
        }
    }
}
