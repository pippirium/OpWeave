#nullable enable
// T-W2.4-02 [Edit] tests: local:T-W2.4-02:changeset — SPEC §F7 across a real
// domain reload. The Run's change list lives in memory, so the host a Run gets
// after a reload owns none of what its earlier steps changed; without the
// journal-backed restore it would finalize a list holding only the post-reload
// tail, and review and a manual rollback would work from that. Follows the
// SPEC §T.3 / L-14 pattern of RunnerHostDomainReloadTests (marker script +
// RecompileScripts + domain sentinel; UTF locals do not survive, so everything
// crosses the reload in probe files under Library/).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class ChangeSetDomainReloadTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveChangeSetReloadProbe");

        private static string ProbeFile => Path.Combine(ProbeDir, "probe.txt");
        private static string WaitProbeFile => Path.Combine(ProbeDir, "probe-wait.txt");
        private static string MarkerFolder => "Assets/Editor/OpWeaveChangeSetReloadProbe";
        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));
        private static string ProbeAssetFolder => "Assets/OpWeaveChangeSetReloadProbe";
        private static string PreloadPath => ProbeAssetFolder + "/pre-reload.mat";
        private static string PostloadPath => ProbeAssetFolder + "/post-reload.mat";
        private static string WaitPrePath => ProbeAssetFolder + "/wait-pre.mat";
        private static string WaitPostPath => ProbeAssetFolder + "/wait-post.mat";

        private const string WorkflowId = "example.changeset.reload";
        private const string WaitWorkflowId = "example.changeset.reload.wait";
        private const string NodeRecord = "rec";
        private const string NodeHold = "hold";
        private const string NodeWait = "wait";
        private const string OpRecord = "test.changeset.reload.record";
        private const string OpHold = "test.changeset.reload.hold";
        private const string OpWait = "test.changeset.reload.wait";

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveChangeSetReloadProbe\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        // A per-Editor-session token phase-1 stores in SessionState and writes
        // into the probe file; phase-2 reads both to prove the file belongs to
        // this session's phase-1 rather than residue of an earlier run.
        private const string ProbeTokenKey = "OpWeave.W2402.ProbeToken";
        private const string WaitProbeTokenKey = "OpWeave.W2402.WaitProbeToken";

        // T-W2.4-06: bound on phase-1 re-drive legs after a predecessor's stale
        // resume is detected. leg is a method local, so a real reload resets it;
        // the this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private static void Crumb(string message) => Debug.Log("[W2402R] " + message);

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;
            public static PhaseResult Pass() => new PhaseResult { Ok = true };
            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        // The marker script and the probe assets are the only things that make the
        // first half a real Run; both are gone before the next test runs. The
        // Library probe tree itself is NEVER deleted here: it holds the probe
        // identity files AND the in-flight Run stores, whose coordinator.lock a
        // live Run keeps open for the whole Editor process (SPEC §E2 — the OS
        // handle outlives the domain and is never closed), so no setup or
        // teardown pass may ever remove a mid-flight store or a persisted Run
        // identity, whatever ordering the full suite produces. Store roots are
        // fresh GUIDs per execution and the probe files carry a per-session
        // token, so the residue a previous session leaves is harmless.
        private static bool CleanupArtifacts()
        {
            var dirty = false;
            if (AssetDatabase.IsValidFolder(ProbeAssetFolder))
            {
                AssetDatabase.DeleteAsset(ProbeAssetFolder);
                dirty = true;
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
        // instruction (RecompileScripts): UTF's PrepareForDomainReload records
        // the CURRENT TestEnumerator's program counter — still a different
        // test's method enumerator during setup and teardown — and the resume
        // then applies that stale PC to the next test method's fresh
        // enumerator, jumping it past phase-1 entirely when the captured
        // enumerator was abandoned mid-state. The asset deletions above queue
        // their compile under the runner's LockReloadAssemblies, so the reload
        // they need drains at the next test body's own
        // `yield return new RecompileScripts()`, whose captured PC is the
        // correct one.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup cleanup enter; probeDir=" + Directory.Exists(ProbeDir));
            var dirty = CleanupArtifacts();
            Crumb("setup cleanup exit; assetsDirty=" + dirty);
            yield break;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            Crumb("teardown cleanup enter; probeDir=" + Directory.Exists(ProbeDir));
            var dirty = CleanupArtifacts();
            Crumb("teardown cleanup exit; assetsDirty=" + dirty);
            yield break;
        }

        // Phase 1 (pre-reload): a Run whose first step changes an asset and
        // journals that change, left mid-flight on a Waiting step so its list is
        // still open when the domain dies. Its changes.json slot must still be the
        // empty one CreateRun wrote.
        private static PhaseResult RunPhaseBeforeReload()
        {
            // A fresh Store root per execution: the handle the running Run leaves on
            // coordinator.lock survives the reload, so a reused root would deny the
            // next execution's Run (§E2).
            var storeRoot = Path.Combine(
                ProbeDir, "t" + Guid.NewGuid().ToString("N"));
            try
            {
                var fixture = new RunnerHostFixture(storeRoot);
                var recorder = new RecordingOperation();
                var holder = new RunnerHostFixture.ControlledWaitableOperation(OpHold);
                fixture.Register(recorder, 'a');
                fixture.Register(holder, 'c');

                var steps = new[]
                {
                    RunnerHostFixture.Step(
                        0, NodeRecord, OpRecord, 'a',
                        outputTypes: new Dictionary<string, string> { ["guid"] = "core.String" },
                        next: 1,
                        idempotency: Idempotency.Unsafe) with
                    {
                        Effects = new[] { EffectNames.AssetCreate },
                    },
                    RunnerHostFixture.Step(
                        1, NodeHold, OpHold, 'c',
                        outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                        next: -1,
                        idempotency: Idempotency.Idempotent),
                };
                var plan = RunnerHostFixture.Plan(WorkflowId, steps);
                var doc = RunnerHostFixture.Doc(
                    WorkflowId,
                    new[]
                    {
                        new NodeData { Id = NodeRecord, Operation = OpRecord, Version = 1 },
                        new NodeData { Id = NodeHold, Operation = OpHold, Version = 1 },
                    },
                    new[] { new ControlEdge(NodeRecord, NodeHold) });

                // The probe folder itself is created BEFORE the Run is accepted:
                // a folder the step would have to make inside Execute reports its
                // own import to that step (auto-attribution as `created`), an
                // element the finalized list should not be charged with.
                EnsureProbeFolder();

                var accepted = fixture.Host.AcceptRun(
                    plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
                if (!accepted.Ok)
                {
                    return PhaseResult.Fail("accept failed");
                }
                var runId = accepted.Value!.RunId;

                for (var i = 0; i < 3; i++)
                {
                    var tick = fixture.Host.Tick();
                    if (!tick.Ok)
                    {
                        return PhaseResult.Fail("tick " + i + " failed");
                    }
                }
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.Running)
                {
                    return PhaseResult.Fail(
                        "the Run must still be executing when the domain reloads");
                }
                if (!RunnerHostFixture.JournalHasAll(
                        storeRoot, runId,
                        "\"type\":\"step_succeeded\"",
                        "\"nodeId\":\"" + NodeRecord + "\"",
                        "\"action\":\"created\""))
                {
                    return PhaseResult.Fail("the pre-reload change was not journaled");
                }
                if (ReadChanges(storeRoot, runId).Count != 0)
                {
                    return PhaseResult.Fail(
                        "the change list must not be finalized while the Run runs");
                }

                Directory.CreateDirectory(ProbeDir);
                var token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
                File.WriteAllText(
                    ProbeFile, storeRoot + "\n" + runId + "\n" + PreloadPath + "\n" +
                        token + "\n");
                Crumb("probe written: runId=" + runId + " storeRoot=" + storeRoot +
                    " reloads=" + DomainReloadCounter.Current);

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

        // Phase 2 (post-reload): the reconstructed host owns no change list, so the
        // resumed Run's next step attempt must open one that already holds what the
        // earlier step journaled. Finalizing then writes the union — the change the
        // old domain made and the one this domain records.
        private static PhaseResult RunPhaseAfterReload()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(ProbeFile))
                {
                    return PhaseResult.Fail(
                        expectedToken.Length == 0
                            ? "phase-1 never executed in this Editor session (" +
                                ProbeFile + " was never written; probeDir exists=" +
                                Directory.Exists(ProbeDir) + ")"
                            : "the persisted Run identity " + ProbeFile +
                                " was deleted after phase-1 wrote it");
                }
                var lines = File.ReadAllLines(ProbeFile);
                var storeRoot = lines[0];
                var runId = lines[1];
                var preloaded = lines[2];
                if (expectedToken.Length == 0 || lines.Length < 4 ||
                    lines[3] != expectedToken)
                {
                    return PhaseResult.Fail(
                        "the persisted Run identity is stale — a previous Editor " +
                        "session wrote " + ProbeFile);
                }
                Crumb("probe read ok: runId=" + runId +
                    " reloads=" + DomainReloadCounter.Current);

                fixture = new RunnerHostFixture(storeRoot);
                // The step context factory of the resumed Run: the attempt of step 1,
                // whose own change this domain records.
                var attempt = fixture.Host.ChangeSets.OpenAttempt(
                    runId, new StepChangeRecorder(1, NodeHold), 1, NodeHold, 1);

                CreateMaterial(PostloadPath);
                // The CreateAsset import already reported this asset as `created`
                // to the step; the explicit record is the same fact, so it names
                // the same action and the two merge into one element.
                attempt.Created(
                    new ChangeTarget(
                        "asset", PostloadPath, AssetDatabase.AssetPathToGUID(PostloadPath)),
                    undoable: false);

                var failure = fixture.Host.ChangeSets.FinalizeRun(runId);
                if (failure is not null)
                {
                    return PhaseResult.Fail(
                        "the finalize after the reload reported " + failure.Code + " " +
                        failure.Message);
                }

                var changes = ReadChanges(storeRoot, runId);
                var before = ElementUnder(changes, preloaded);
                var after = ElementUnder(changes, PostloadPath);

                // The change the reloaded domain made is still the Run's — recorded
                // before the reload, journaled by its step, restored from that.
                if ((string?)before["nodeId"] != NodeRecord || (int)before["step"]! != 0)
                {
                    return PhaseResult.Fail(
                        "the pre-reload change lost its step attribution: " + changes);
                }
                if ((string?)before["action"] != "created")
                {
                    return PhaseResult.Fail("the pre-reload change lost its action: " + changes);
                }
                var preloadGuid = AssetDatabase.AssetPathToGUID(preloaded);
                if ((string?)before["target"]?["guid"] != preloadGuid ||
                    string.IsNullOrEmpty(preloadGuid))
                {
                    return PhaseResult.Fail(
                        "the pre-reload change lost the created asset's guid: " + changes);
                }
                if ((string?)after["nodeId"] != NodeHold || (int)after["step"]! != 1 ||
                    (string?)after["action"] != "created")
                {
                    return PhaseResult.Fail(
                        "the post-reload change is not listed as this domain's: " + changes);
                }
                if (changes.Count != 2)
                {
                    return PhaseResult.Fail(
                        "the finalized list is not the union of both halves: " + changes);
                }
                Crumb("phase-2 complete: both halves of the change list finalized");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        // ── SPEC §F7 test: a Waiting step's durable changes cross the reload ──

        // Phase 1 (pre-reload): a one-step Waitable Run whose Execute records a
        // change and returns Waiting — the 2026-11-08 §F2 decision journals that
        // attempt's cumulative list on the StepWaiting itself, Flushed, so the
        // record survives the domain dying mid-attempt.
        private static PhaseResult RunWaitingPhaseBeforeReload()
        {
            var storeRoot = Path.Combine(
                ProbeDir, "w" + Guid.NewGuid().ToString("N"));
            try
            {
                EnsureProbeFolder();
                var fixture = new RunnerHostFixture(storeRoot);
                var waitable = new RecordingWaitableOperation();
                fixture.Register(waitable, 'd');

                var steps = new[]
                {
                    RunnerHostFixture.Step(
                        0, NodeWait, OpWait, 'd',
                        outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                        next: -1,
                        idempotency: Idempotency.Idempotent),
                };
                var plan = RunnerHostFixture.Plan(WaitWorkflowId, steps);
                var doc = RunnerHostFixture.Doc(
                    WaitWorkflowId,
                    new[] { new NodeData { Id = NodeWait, Operation = OpWait, Version = 1 } },
                    Array.Empty<ControlEdge>());

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
                    return PhaseResult.Fail("the Execute tick failed");
                }
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.Running ||
                    waitable.PollCalls != 0)
                {
                    return PhaseResult.Fail(
                        "the step must be waiting mid-attempt when the domain reloads");
                }
                // The pre-reload change must be durable ON the StepWaiting: a
                // reload kills the in-memory recorder, so only the journaled
                // cumulative list can seed the resumed attempt.
                if (!RunnerHostFixture.JournalHasAll(
                        storeRoot, runId,
                        "\"type\":\"step_waiting\"",
                        "\"changes\"",
                        "\"action\":\"created\""))
                {
                    return PhaseResult.Fail(
                        "the StepWaiting did not carry the attempt's cumulative changes");
                }

                Directory.CreateDirectory(ProbeDir);
                var waitToken = Guid.NewGuid().ToString("N");
                SessionState.SetString(WaitProbeTokenKey, waitToken);
                File.WriteAllText(
                    WaitProbeFile, storeRoot + "\n" + runId + "\n" + waitToken + "\n");
                Crumb("wait probe written: runId=" + runId + " storeRoot=" + storeRoot +
                    " reloads=" + DomainReloadCounter.Current);

                Directory.CreateDirectory(MarkerDisk);
                File.WriteAllText(Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
                AssetDatabase.Refresh();
                ArmDomainSentinel();
                Crumb("wait phase-1 complete: runId=" + runId);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("wait phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2 (post-reload): the §F4 recovery pass resumes the Waiting step
        // on a fresh host; its restored recorder makes the Poll's StepSucceeded
        // carry the WHOLE attempt's list, and the finalized changes.json holds
        // the pre- and post-reload elements exactly once — with the attempt.
        private static PhaseResult RunWaitingPhaseAfterReload()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var expectedWaitToken = SessionState.GetString(WaitProbeTokenKey, "");
                if (!File.Exists(WaitProbeFile))
                {
                    return PhaseResult.Fail(
                        expectedWaitToken.Length == 0
                            ? "wait phase-1 never executed in this Editor session (" +
                                WaitProbeFile + " was never written; probeDir exists=" +
                                Directory.Exists(ProbeDir) + ")"
                            : "the persisted Run identity " + WaitProbeFile +
                                " was deleted after phase-1 wrote it");
                }
                var lines = File.ReadAllLines(WaitProbeFile);
                var storeRoot = lines[0];
                var runId = lines[1];
                if (expectedWaitToken.Length == 0 || lines.Length < 3 ||
                    lines[2] != expectedWaitToken)
                {
                    return PhaseResult.Fail(
                        "the persisted Run identity is stale — a previous Editor " +
                        "session wrote " + WaitProbeFile);
                }
                Crumb("wait probe read ok: runId=" + runId +
                    " reloads=" + DomainReloadCounter.Current);

                fixture = new RunnerHostFixture(storeRoot);
                var waitable = new RecordingWaitableOperation();
                fixture.Register(waitable, 'd');

                var recovered = fixture.Host.RecoverRuns();
                if (!recovered.Ok)
                {
                    return PhaseResult.Fail("the recovery pass failed");
                }
                for (var i = 0; i < 20; i++)
                {
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    if (state == RunState.Succeeded || state == RunState.Failed ||
                        state == RunState.Cancelled)
                    {
                        break;
                    }
                    fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                    var tick = fixture.Host.Tick();
                    if (!tick.Ok)
                    {
                        return PhaseResult.Fail("a post-reload tick failed");
                    }
                }
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.Succeeded)
                {
                    return PhaseResult.Fail(
                        "the resumed Run did not succeed: " +
                        fixture.Host.Coordinator.StateOf(runId));
                }
                if (waitable.PollCalls == 0)
                {
                    return PhaseResult.Fail("the resumed step was never polled");
                }
                if (!RunnerHostFixture.JournalHasAll(
                        storeRoot, runId,
                        "\"type\":\"step_succeeded\"",
                        WaitPrePath,
                        WaitPostPath))
                {
                    return PhaseResult.Fail(
                        "the StepSucceeded did not carry the whole attempt's changes");
                }

                var changes = ReadChanges(storeRoot, runId);
                var before = ElementUnder(changes, WaitPrePath);
                var after = ElementUnder(changes, WaitPostPath);
                if ((string?)before["action"] != "created" ||
                    (string?)before["nodeId"] != NodeWait || (int)before["step"]! != 0)
                {
                    return PhaseResult.Fail(
                        "the pre-reload change lost its attribution: " + changes);
                }
                if ((string?)after["action"] != "created" ||
                    (string?)after["nodeId"] != NodeWait || (int)after["step"]! != 0)
                {
                    return PhaseResult.Fail(
                        "the post-reload change lost its attribution: " + changes);
                }
                if ((int)before["attempt"]! != 1 || (int)after["attempt"]! != 1)
                {
                    return PhaseResult.Fail(
                        "the elements lost the attempt that made them: " + changes);
                }
                if (changes.Count != 2)
                {
                    return PhaseResult.Fail(
                        "the finalized list is not both changes exactly once: " + changes);
                }
                Crumb("wait phase-2 complete: pre- and post-reload changes finalized");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("wait phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator AWaitingStepsChangesSurviveTheDomainReloadMidAttempt()
        {
            var phase1 = RunWaitingPhaseBeforeReload();
            Crumb("wait phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True, "pre-reload phase failed: " + phase1.Failure);

            // T-W2.4-06 stale-resume self-heal: a predecessor's captured UTF
            // resume PC applies to this fresh enumerator and lands it at one of
            // the two post-yield states inside this loop — with every local
            // (leg included) reset to default. Both continuations drain, then
            // MUST pass the this-session probe check: a probe phase-1 never
            // wrote re-drives a real phase-1 + compile + reload instead of
            // skipping silently into phase-2. If the probe still cannot be
            // produced after the bound, the loop exits and phase-2's existing
            // never-executed diagnostic fails loudly.
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

                if (ProbeWrittenThisSession(WaitProbeFile, WaitProbeTokenKey) ||
                    leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // A marker file an abandoned leg left behind carries identical
                // bytes, so deleting the probe assets guarantees the re-write
                // queues a real compile rather than a no-op Refresh.
                CleanupArtifacts();
                var redrive = RunWaitingPhaseBeforeReload();
                Crumb("wait phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var phase2 = RunWaitingPhaseAfterReload();
            Crumb("wait phase-2 ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True, "post-reload phase failed: " + phase2.Failure);
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator ChangesMadeBeforeADomainReloadAreFinalizedWithTheOnesAfterIt()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True, "pre-reload phase failed: " + phase1.Failure);

            // T-W2.4-06 stale-resume self-heal: see the landing analysis on the
            // wait test — same structure, same two resumable states, same
            // convergence on the probe check before phase-2 may run.
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

                if (ProbeWrittenThisSession(ProbeFile, ProbeTokenKey) ||
                    leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // A marker file an abandoned leg left behind carries identical
                // bytes, so deleting the probe assets guarantees the re-write
                // queues a real compile rather than a no-op Refresh.
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

        // ── probe fixtures ──────────────────────────────────────────────────

        // The phase-1 probe proves phase-1 ran in THIS Editor session: the file
        // must exist AND its last line must equal the token phase-1 stored in
        // SessionState. Both Library/ and SessionState survive the reload, so a
        // stale-resume hijack (phase-1 skipped: no file, no token) and residue
        // of an earlier session (file yes, token no) both fail the check — the
        // caller then re-drives a real phase-1 instead of skipping into
        // phase-2.
        private static bool ProbeWrittenThisSession(string probeFile, string tokenKey)
        {
            if (!File.Exists(probeFile))
            {
                return false;
            }
            var expectedToken = SessionState.GetString(tokenKey, "");
            if (expectedToken.Length == 0)
            {
                return false;
            }
            var lines = File.ReadAllLines(probeFile);
            return lines.Length > 0 && lines[lines.Length - 1] == expectedToken;
        }

        private static void EnsureProbeFolder()
        {
            if (!AssetDatabase.IsValidFolder(ProbeAssetFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveChangeSetReloadProbe");
            }
        }

        private static void CreateMaterial(string assetPath)
        {
            EnsureProbeFolder();
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.CreateAsset(material, assetPath);
            Assert.That(
                AssetDatabase.AssetPathToGUID(assetPath), Is.Not.Null.And.Not.Empty,
                "the probe Material must be imported");
        }

        /// <summary>
        /// The step whose change the first domain makes: a real Material asset,
        /// recorded explicitly the way SPEC §C2 item 6 asks — so the element reaches
        /// the durable <c>step_succeeded.changes</c> member the reload restores from.
        /// </summary>
        private sealed class RecordingOperation : OperationBase
        {
            public override OperationDescriptor Descriptor { get; } =
                Op.Define(OpRecord, 1)
                    .Summary("Creates the pre-reload probe Material.")
                    .Output("guid", "core.String")
                    .Effects(EffectNames.AssetCreate)
                    .Undo(UndoSupport.None)
                    .Idempotency(Idempotency.Unsafe)
                    .Build();

            public override StepResult Execute(StepContext ctx)
            {
                CreateMaterial(PreloadPath);
                var guid = AssetDatabase.AssetPathToGUID(PreloadPath);
                ctx.Changes.Created(new ChangeTarget("asset", PreloadPath, guid), undoable: false);
                ctx.SetOutput("guid", guid);
                return StepResult.Succeeded();
            }
        }

        /// <summary>
        /// The step that crosses the reload mid-attempt: Execute records the
        /// pre-reload change and returns Waiting (journaled on the StepWaiting's
        /// cumulative <c>changes</c>, SPEC §F2 attempt-durability); the resumed
        /// Poll records the post-reload one and succeeds — the
        /// <c>step_succeeded</c> then carries the whole attempt.
        /// </summary>
        private sealed class RecordingWaitableOperation : OperationBase
        {
            internal int PollCalls { get; private set; }

            public override OperationDescriptor Descriptor { get; } =
                Op.Define(OpWait, 1)
                    .Summary("Records one change, waits across the reload, records another.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.AssetCreate)
                    .Undo(UndoSupport.None)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };

            public override StepResult Execute(StepContext ctx)
            {
                CreateMaterial(WaitPrePath);
                ctx.Changes.Created(
                    new ChangeTarget(
                        "asset", WaitPrePath, AssetDatabase.AssetPathToGUID(WaitPrePath)),
                    undoable: false);
                return StepResult.Waiting(RunnerHostFixture.Json("\"held\""), "held", 1);
            }

            public override StepResult Poll(StepContext ctx)
            {
                PollCalls++;
                CreateMaterial(WaitPostPath);
                ctx.Changes.Created(
                    new ChangeTarget(
                        "asset", WaitPostPath, AssetDatabase.AssetPathToGUID(WaitPostPath)),
                    undoable: false);
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }
        }

        // ── changes.json reading ────────────────────────────────────────────

        private static JArray ReadChanges(string root, string runId)
        {
            var path = Path.Combine(root, "Runs", runId, "changes.json");
            Assert.IsTrue(File.Exists(path), "the Run Store holds no changes.json");
            var parsed = JsonValue.Parse(File.ReadAllText(path));
            Assert.IsTrue(parsed.Ok, "changes.json must parse");
            var token = parsed.Value!.ToToken();
            Assert.IsInstanceOf<JArray>(token, "changes.json is a change array (SPEC §F7)");
            return (JArray)token;
        }

        private static JObject ElementUnder(JArray changes, string assetPath)
        {
            var element = changes.OfType<JObject>().FirstOrDefault(
                e => string.Equals((string?)e["target"]?["path"], assetPath,
                    StringComparison.Ordinal));
            Assert.IsNotNull(
                element,
                "changes.json lists no element for " + assetPath + ": " + changes.ToString());
            return element!;
        }
    }
}
