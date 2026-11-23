#nullable enable
// T-W4.3-01 [Edit] tests: local:T-W4.3-01:recovery-startup — the SPEC §F4
// recovery start-up pass driven through OpWeaveRunnerHost in the Editor: the
// Run accepted while compiling that only starts after the compile and the
// domain reload it causes (SPEC §E2 test 4, allocation D-18), the mutating
// Run whose exclusion the pass CONFIRMS across a real reload before it
// resumes it from the verified journal, the Queued/AwaitingApproval Runs that
// come back startable and approvable, and the Play Mode exit that suspends a
// Run and returns it through EnteredEditMode.
//
// The reload mechanics are the SPEC §T.3 / L-14 pattern of
// CoordinatorRunLockReloadTests and RunnerHostDomainReloadTests: a marker
// script under Assets/Editor/, RecompileScripts, and a domain sentinel that
// proves the domain changed. Iterator locals do not survive, so every phase is
// a static helper returning a plain [Serializable] result and everything that
// must cross the reload sits in a probe file under Library/.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Recovery;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.3-01:recovery-startup</c> in Unity 6000.0 EditMode: the
    /// host-side rules of SPEC §F4 items 1/2/6, §E2 item 3 and §1.4 — accept
    /// during compilation, cross the compile's reload, confirm the exclusion,
    /// re-attach the Runs that hold no lock, and reconcile the Suspended Run.
    /// </summary>
    internal sealed class RunRecoveryHostTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveRunRecoveryProbe");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string StateFile => Path.Combine(ProbeDir, "state.txt");
        private static string LockJsonPath =>
            Path.Combine(StoreRoot, CoordinatorRunLock.StateFileName);

        private static string MarkerFolder => "Assets/Editor/OpWeaveRunRecoveryProbe";
        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveRunRecoveryProbe\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private const string WorkflowId = "example.recovery.host";
        private const string MarkerOp = "test.recovery.mark";
        private const string HoldOp = "test.recovery.hold";

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private static void Crumb(string message) => Debug.Log("[W4301] " + message);

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static PhaseResult Pass() => new PhaseResult { Ok = true };

            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // Leaves the probe Run Store with nothing a later test could mistake for
        // live state, whatever the test that just ran did or failed to do. The
        // exclusion this process still holds on this root — the previous domain's
        // after a reload, or a fixture's that never ended its Run — is taken
        // through the same seam the recovery pass uses and ended the way an orderly
        // Run end does, which empties lock.json BEFORE freeing the handle: a
        // dropped-only end would leave the abandoned-state record behind, and that
        // record alone keeps every later Run on this root refusing with OW-RUN-002
        // even once the exclusion is free. Then both hand-off records for this root
        // are dropped. With a live owner elsewhere nothing here is possible: the
        // obtain is denied, the probe Store stays for the next run, and that is
        // reported rather than destroyed.
        private static void ReleaseLeakedState()
        {
            var obtained = RunLockHandoff.TryObtain(StoreRoot);
            if (!obtained.Ok || obtained.Value is not { } held)
            {
                Crumb("the probe Run Store is excluded elsewhere: " +
                    Describe(obtained.Diagnostics));
                RunLockHandoff.Clear(StoreRoot);
                return;
            }
            if (!held.Release().Ok)
            {
                held.Dispose();
            }
            RunLockHandoff.Clear(StoreRoot);
        }

        private static bool CleanupArtifacts()
        {
            ReleaseLeakedState();
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            var dirty = false;
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
            return dirty;
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            if (CleanupArtifacts())
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (CleanupArtifacts())
            {
                yield return new RecompileScripts(false);
            }
        }

        // ── Plan builders ───────────────────────────────────────────────

        private static PlanStep Step(
            int index,
            string nodeId,
            string operationId,
            char hashSeed,
            IReadOnlyList<string>? effects = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            int next = -1) =>
            new PlanStep(
                index,
                nodeId,
                operationId,
                1,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64),
                new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = next },
                effects ?? (IReadOnlyList<string>)Array.Empty<string>(),
                30,
                new RetryPolicy(),
                Idempotency.Idempotent);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"fixture JSON must parse: {text}");
            return parsed.Value!;
        }

        // The SG-12 hash input with the asks folded into requiredEffects, so
        // an approvals-carrying Plan revalidates on Load (SPEC §D7) exactly as
        // the approval fixtures build theirs.
        private static CompiledPlan Plan(
            IReadOnlyList<PlanStep> steps,
            IReadOnlyList<ApprovalRequirement>? approvals = null)
        {
            var asks = approvals ?? Array.Empty<ApprovalRequirement>();
            var hashInput = new JObject
            {
                ["canonicalJsonVersion"] = 1,
                ["environmentIdentifiers"] = new JObject
                {
                    ["unityVersion"] = "6000.0.85f1",
                    ["editorOS"] = "windows",
                    ["activeBuildTarget"] = "windows",
                    ["scriptingBackend"] = "mono",
                    ["apiCompatibilityLevel"] = "netstandard2.1",
                    ["scriptingDefineSymbols"] = new JArray(),
                },
                ["executionInputs"] = PlanHashInputParts.ExecutionInputs(
                    Array.Empty<string>(), null),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(steps, asks),
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = WorkflowId,
                    ["inputs"] = new JArray(),
                    ["outputs"] = new JArray(),
                },
                ["retry"] = PlanHashInputParts.Retry(steps),
                ["steps"] = PlanHashInputParts.Steps(steps),
                ["timeouts"] = PlanHashInputParts.Timeouts(steps),
                ["transitions"] = PlanHashInputParts.Transitions(steps),
                ["usedCodecFingerprints"] = new JArray(),
                ["usedOperationFingerprints"] =
                    PlanHashInputParts.OperationFingerprints(steps),
                ["usedTypeFingerprints"] = new JArray(),
            };
            var input = JsonValue.From(hashInput);
            return new CompiledPlan(
                "plan_" + Identifiers.NewUlid(),
                CanonicalJson.Sha256(input.ToToken()),
                WorkflowId,
                new string('b', 64),
                new string('c', 64),
                Json("{}"),
                steps,
                Array.Empty<PlanOutput>(),
                PlanBuilder.UnionEffects(steps),
                asks,
                Array.Empty<PlanSourceMapEntry>(),
                input);
        }

        // A single immediate marker step.
        private static CompiledPlan MarkPlan() => Plan(new[]
        {
            Step(0, "m", MarkerOp, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
        });

        // One Waitable step the test releases — leaves the Run Running, holding
        // its exclusion, with the wait journaled.
        private static CompiledPlan HoldPlan() => Plan(new[]
        {
            Step(0, "h", HoldOp, 'b'),
        });

        // The ask: the same marker step carrying the approval requirement the
        // Run must wait for.
        private static CompiledPlan AskPlan() => Plan(
            new[]
            {
                Step(0, "ask", MarkerOp, 'a',
                    effects: new[] { "asset.delete" },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            },
            new[]
            {
                new ApprovalRequirement("ask", "asset.delete", MarkerOp + " v1"),
            });

        private static WorkflowDocument Doc(string nodeId, string operationId) =>
            RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = nodeId, Operation = operationId, Version = 1 } },
                Array.Empty<ControlEdge>());

        private static void RegisterOperations(RunnerHostFixture fixture) =>
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(MarkerOp, fixture.Events), 'a');

        private static string Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc, bool queue)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, new AcceptOptions(null, queue));
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        private static void Tick(RunnerHostFixture fixture)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "the tick failed: " + Describe(tick.Diagnostics));
        }

        private static void RunUntilTerminal(RunnerHostFixture fixture, string runId)
        {
            for (var i = 0; i < 8; i++)
            {
                Tick(fixture);
                if (fixture.Host.Coordinator.StateOf(runId) is { } state &&
                    (state == RunState.Succeeded || state == RunState.Failed ||
                     state == RunState.Cancelled))
                {
                    Assert.AreEqual(
                        RunState.Succeeded, state,
                        "the Run " + runId + " ended " + state +
                        "; journal: " + string.Join(
                            " | ", RunnerHostFixture.JournalLines(StoreRoot, runId)));
                    return;
                }
            }
            Assert.Fail(
                "the Run " + runId + " did not end within 8 ticks; state=" +
                fixture.Host.Coordinator.StateOf(runId));
        }

        private static RunRecoveryReport Recover(RunnerHostFixture fixture)
        {
            var recovered = fixture.Host.RecoverRuns();
            Assert.IsTrue(recovered.Ok, "the recovery pass failed: " +
                Describe(recovered.Diagnostics));
            Assert.IsNotNull(recovered.Value);
            return recovered.Value!;
        }

        private static RunRecoveryOutcome OutcomeOf(RunRecoveryReport report, string runId)
        {
            foreach (var result in report.Runs)
            {
                if (string.Equals(result.RunId, runId, StringComparison.Ordinal))
                {
                    return result.Outcome;
                }
            }
            Assert.Fail("the recovery report does not mention " + runId);
            return RunRecoveryOutcome.NeedsReview;
        }

        private static void ArmReload()
        {
            Directory.CreateDirectory(MarkerDisk);
            File.WriteAllText(Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
            AssetDatabase.Refresh();
            ArmDomainSentinel();
        }

        // ── SPEC §E2 test 4: accepted while compiling, starts after the ──
        // ── compile AND the domain reload that compile causes (§F4 1/2) ──

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator RunAcceptedWhileCompilingStartsAfterTheCompileAndItsReload()
        {
            var before = RunPhaseBeforeCompileReload();
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Crumb("iterator resumed; domain sentinel alive=" + DomainSentinelAlive);
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

            var after = RunPhaseAfterCompileReload();
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // Phase 1: the §E2 item 4 start gate holds a Run accepted while the
        // Editor compiles — it stays Queued through the ticks — and the reload
        // is armed while it sits there.
        private static PhaseResult RunPhaseBeforeCompileReload()
        {
            // An abort must not leave this host installed: its update subscription
            // would keep driving this Store through every later test of the domain,
            // and its exclusion would keep those tests out. The success path keeps
            // it installed on purpose — the §F3 item 1 flush its
            // beforeAssemblyReload subscription performs is what puts phase 1's
            // journal on disk before the reload.
            RunnerHostFixture? fixture = null;
            try
            {
                fixture = new RunnerHostFixture(StoreRoot);
                RegisterOperations(fixture);
                fixture.Install();
                fixture.Editor.IsCompiling = true;
                var runId = Accept(fixture, MarkPlan(), Doc("m", MarkerOp), queue: false);
                Tick(fixture);
                Tick(fixture);
                if (fixture.Host.Coordinator.StateOf(runId) != RunState.Queued)
                {
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "a Run accepted while compiling started anyway: " + state);
                }
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(StateFile, runId + "\n" + fixture.SessionId + "\n");
                ArmReload();
                Crumb("phase-1 complete: queued across the compile, runId=" + runId);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2: the reload killed the managed host. The pass re-attaches the
        // Queued Run — and only after the compile really ended does the start
        // gate admit it, which is SPEC §E2 test 4's point.
        private static PhaseResult RunPhaseAfterCompileReload()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                Assert.IsFalse(EditorApplication.isCompiling,
                    "the probe compile must have finished before this phase runs");
                var runId = File.ReadAllLines(StateFile)[0];

                fixture = new RunnerHostFixture(StoreRoot);
                RegisterOperations(fixture);
                // The fake mirrors the real Editor state the production probe
                // would report: this reload WAS the compile's, and it is over.
                fixture.Editor.IsCompiling = EditorApplication.isCompiling;

                var report = Recover(fixture);
                Assert.IsTrue(report.ExclusionHeld);
                Assert.AreEqual(1, report.Runs.Count);
                Assert.AreEqual(
                    RunRecoveryOutcome.Reattached, OutcomeOf(report, runId),
                    "the Queued Run must come back as the journal has it");
                Assert.AreEqual(RunState.Queued, fixture.Host.Coordinator.StateOf(runId));
                Assert.IsFalse(
                    RunnerHostFixture.JournalHas(StoreRoot, runId, "\"type\":\"run_resumed\""),
                    "re-attachment alone writes no resume record");

                RunUntilTerminal(fixture, runId);
                Assert.IsTrue(
                    fixture.Events.Contains("execute:" + MarkerOp),
                    "the re-attached Run never executed its step");
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId, "\"type\":\"run_finished\"", "\"state\":\"succeeded\""),
                    "the Run did not finish through its own journal");
                Assert.AreEqual(
                    0, new FileInfo(LockJsonPath).Length,
                    "lock.json is emptied before the exclusion is released");
                fixture.Dispose();
                Crumb("phase-2 complete: queued Run started and finished after the reload");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ── The reload that keeps the exclusion: confirm, verify, resume ──

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator MutatingRunIsConfirmedAndResumedAcrossDomainReload()
        {
            var before = RunPhaseBeforeHoldReload();
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Crumb("iterator resumed; domain sentinel alive=" + DomainSentinelAlive);
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

            var after = RunPhaseAfterHoldReload();
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // Phase 1: one mutating Run holds the exclusion mid-wait (its lock.json
        // record carries this domain's session id) while a Queued and an
        // AwaitingApproval Run wait behind it, then the reload is armed.
        private static PhaseResult RunPhaseBeforeHoldReload()
        {
            // As in the compile fixture: only the success path keeps the host
            // installed, because that is the one whose held exclusion the reloaded
            // domain must confirm. Every abort releases it here so a failed phase
            // cannot lock the next test out of this Run Store.
            RunnerHostFixture? fixture = null;
            try
            {
                fixture = new RunnerHostFixture(StoreRoot);
                RegisterOperations(fixture);
                fixture.Register(
                    new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
                fixture.Install();
                var holdId = Accept(fixture, HoldPlan(), Doc("h", HoldOp), queue: false);
                Tick(fixture);
                if (fixture.Host.Coordinator.StateOf(holdId) != RunState.Running)
                {
                    var state = fixture.Host.Coordinator.StateOf(holdId);
                    fixture.Dispose();
                    return PhaseResult.Fail("the hold Run did not start: " + state);
                }
                var lockJson = File.ReadAllText(LockJsonPath);
                if (!lockJson.Contains("\"runId\":\"" + holdId + "\"") ||
                    !lockJson.Contains("\"editorSessionId\":\"" + fixture.SessionId + "\""))
                {
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "lock.json does not record the live owner: " + lockJson);
                }
                var queuedId = Accept(fixture, MarkPlan(), Doc("m", MarkerOp), queue: true);
                var askPlan = AskPlan();
                var askId = Accept(fixture, askPlan, Doc("ask", MarkerOp), queue: true);

                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(
                    StateFile,
                    holdId + "\n" + queuedId + "\n" + askId + "\n" + askPlan.PlanHash + "\n");
                ArmReload();
                Crumb("phase-1 complete: hold=" + holdId + " queued=" + queuedId +
                    " ask=" + askId);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2: the exclusion the dead domain held is still OS-held by this
        // process, so a fresh acquisition is denied — the pass can only get
        // inside the critical section by confirming that handle, which is what
        // SPEC §E2 item 3 requires before it verifies the journal and resumes.
        // The two inactive Runs re-attach and stay approvable and startable.
        private static PhaseResult RunPhaseAfterHoldReload()
        {
            RunnerHostFixture? fixture = null;
            try
            {
                var persisted = File.ReadAllLines(StateFile);
                if (persisted.Length < 4)
                {
                    return PhaseResult.Fail(
                        "the persisted Run ids are incomplete: " + persisted.Length + " line(s)");
                }
                var holdId = persisted[0];
                var queuedId = persisted[1];
                var askId = persisted[2];
                var askPlanHash = persisted[3];

                var denied = CoordinatorRunLock.TryAcquire(StoreRoot);
                Assert.IsFalse(
                    denied.Ok,
                    "the handle the reloaded domain left behind no longer excludes — the pass " +
                    "could not be said to CONFIRM the exclusion it was handed");

                fixture = new RunnerHostFixture(StoreRoot);
                var waitable = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
                RegisterOperations(fixture);
                fixture.Register(waitable, 'b');

                var report = Recover(fixture);
                Assert.IsTrue(report.ExclusionHeld);
                Assert.AreEqual(
                    RunRecoveryOutcome.Resumed, OutcomeOf(report, holdId),
                    "the excluded Run must be resumed on its verified journal");
                Assert.AreEqual(
                    RunRecoveryOutcome.Reattached, OutcomeOf(report, queuedId));
                Assert.AreEqual(
                    RunRecoveryOutcome.Reattached, OutcomeOf(report, askId));
                Assert.IsTrue(
                    RunnerHostFixture.JournalHas(StoreRoot, holdId, "\"type\":\"run_resumed\""),
                    "SPEC §F4 item 6: the resume is journaled");
                var lockJson = File.ReadAllText(LockJsonPath);
                Assert.IsTrue(
                    lockJson.Contains("\"runId\":\"" + holdId + "\"") &&
                    lockJson.Contains(
                        "\"editorSessionId\":\"" + fixture.SessionId + "\""),
                    "SPEC §F4 item 6: lock.json carries this session while the same " +
                    "exclusion stays held: " + lockJson);
                Assert.IsFalse(
                    RunnerHostFixture.JournalHas(
                        StoreRoot, queuedId, "\"type\":\"run_resumed\""),
                    "an inactive Run is re-attached, never driven");

                var approved = fixture.Host.Approvals.Approve(askId, askPlanHash);
                Assert.IsTrue(
                    approved.Ok,
                    "the re-attached Run refused the human decision: " +
                    Describe(approved.Diagnostics));
                Assert.AreEqual(RunState.Queued, fixture.Host.Coordinator.StateOf(askId));

                waitable.Release = true;
                RunUntilTerminal(fixture, holdId);
                Assert.AreEqual(
                    0, new FileInfo(LockJsonPath).Length,
                    "the resumed Run's orderly end emptied lock.json");
                RunUntilTerminal(fixture, queuedId);
                RunUntilTerminal(fixture, askId);
                Assert.IsTrue(
                    ExclusionReleased(),
                    "every Run ended, so no exclusion may stay held");
                fixture.Dispose();
                Crumb("phase-2 complete: confirmed, resumed, re-attached and finished");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool ExclusionReleased()
        {
            var probe = CoordinatorRunLock.TryAcquire(StoreRoot);
            if (!probe.Ok)
            {
                return false;
            }
            probe.Value!.Dispose();
            return true;
        }

        // ── SPEC §1.4 / §E1: the Play Mode exit and the Edit Mode return ──

        /// <summary>Leaving Edit Mode journals run_suspended at the event and
        /// keeps the exclusion; returning without a domain reload runs the same
        /// reconciliation, which resumes the Suspended Run through the §E1
        /// table's only way back to Running. The host also keeps naming this
        /// Editor's own Run for SPEC §E2 item 3 (a) while it holds the
        /// exclusion, and forgets it once the journal ended.</summary>
        [Test]
        public void PlayModeExitSuspendsTheRunAndEditModeReEntryReconcilesIt()
        {
            var fixture = new RunnerHostFixture(StoreRoot);
            try
            {
                var waitable = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
                fixture.Register(waitable, 'b');
                fixture.Install();
                var runId = Accept(fixture, HoldPlan(), Doc("h", HoldOp), queue: false);
                Tick(fixture);
                Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
                Assert.AreEqual(
                    runId, RunLockHandoff.OwnedRunId(StoreRoot),
                    "the host records the Run this Editor holds the exclusion for");

                fixture.Host.OnPlayModeStateChanged(PlayModeStateChange.ExitingEditMode);
                Assert.AreEqual(RunState.Suspended, fixture.Host.Coordinator.StateOf(runId));
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId, "\"type\":\"run_suspended\"", "\"reason\":\"play_mode\""),
                    "the suspension must be journaled at the event itself");

                // The reconciliation on re-entry: the §E3 item 4 gate still
                // holds the Run while the Editor is busy.
                fixture.Editor.IsUpdating = true;
                fixture.Host.OnPlayModeStateChanged(PlayModeStateChange.EnteredEditMode);
                Assert.AreEqual(RunState.Suspended, fixture.Host.Coordinator.StateOf(runId));
                Assert.AreEqual(0, RunnerHostFixture.JournalCount(
                    StoreRoot, runId, "\"type\":\"run_resumed\""));

                fixture.Editor.IsUpdating = false;
                fixture.Host.OnPlayModeStateChanged(PlayModeStateChange.EnteredEditMode);
                Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
                Assert.AreEqual(1, RunnerHostFixture.JournalCount(
                    StoreRoot, runId, "\"type\":\"run_resumed\""));

                waitable.Release = true;
                RunUntilTerminal(fixture, runId);
                Assert.IsNull(
                    RunLockHandoff.OwnedRunId(StoreRoot),
                    "a terminal Run's record is dropped with the exclusion");
            }
            finally
            {
                fixture.Dispose();
            }
        }

        // ── SPEC §E2 item 3: the hand-off records belong to their own root ──

        /// <summary>W4.3-R02: both §E2 item 3 hand-off records belong to the Run
        /// Store root that wrote them. Editor process state is shared by every host
        /// in the process: the production host over <c>Library/OpWeave</c> is
        /// installed by <c>[InitializeOnLoad]</c> and runs its own §F4 pass on the
        /// first delayCall of every domain — including the reload a recovery fixture
        /// crosses, which happens strictly before the fixture resumes. One shared
        /// pair of records let that pass spend the fixture root's handle, so the
        /// reload lost the exclusion it must CONFIRM before it may resume, and left
        /// the previous domain's OS handle unrecoverable for every later test (the
        /// <see cref="MutatingRunIsConfirmedAndResumedAcrossDomainReload"/>
        /// fixture above is that scenario end to end).</summary>
        [Test]
        public void HandoffRecordsOfDifferentRunStoreRootsDoNotOverwriteEachOther()
        {
            var rootA = Path.Combine(StoreRoot, "handoff-a");
            var rootB = Path.Combine(StoreRoot, "handoff-b");
            var runA = Identifiers.NewRunId();
            var runB = Identifiers.NewRunId();
            try
            {
                RunLockHandoff.RecordHandle(rootA, new IntPtr(0x1000));
                RunLockHandoff.RecordOwnedRun(rootA, runA);
                RunLockHandoff.RecordHandle(rootB, new IntPtr(0x2000));
                RunLockHandoff.RecordOwnedRun(rootB, runB);

                Assert.AreEqual(
                    runA, RunLockHandoff.OwnedRunId(rootA),
                    "recording another Run Store root spent this root's hand-off");
                Assert.AreEqual(
                    runB, RunLockHandoff.OwnedRunId(rootB),
                    "the second root's own hand-off was not recorded");

                RunLockHandoff.Clear(rootB);
                Assert.AreEqual(
                    runA, RunLockHandoff.OwnedRunId(rootA),
                    "clearing another Run Store root spent this root's hand-off");
                Assert.IsNull(
                    RunLockHandoff.OwnedRunId(rootB),
                    "the cleared root's record still reads back");
            }
            finally
            {
                RunLockHandoff.Clear(rootA);
                RunLockHandoff.Clear(rootB);
            }
        }

        // ── SPEC §F4 item 1's registration: recovery after the Registry ──

        /// <summary>The production host registers the recovery pass in the
        /// ordered startup list, behind the Registry rebuild slot, so §F4 item
        /// 1's "C3 の Registry 再構築が終わってから" holds for any registration
        /// order, and the §F8 retention pass behind recovery, so an unfinished
        /// Run is re-attached before finished-Run candidates are selected (the
        /// hooks are not run here: that would rebuild the shared production
        /// Registry and touch the real Run Store).</summary>
        [Test]
        public void ProductionHostRegistersRecoveryAfterTheRegistryRebuild()
        {
            var host = OpWeaveRunnerHost.CreateProduction();
            try
            {
                Assert.AreEqual(
                    new[] { "registry-build", "run-recovery", "run-retention" },
                    host.StartupHooks.RegisteredNames());
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
