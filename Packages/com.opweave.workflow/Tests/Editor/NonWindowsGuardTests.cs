#nullable enable
// T-W4.6-08 [Edit] tests: SPEC §E2 item 3 "v1 の対象外の OS" (decision D)
// through the real RunCoordinator start gate and the SPEC §F4 recovery
// pass of OpWeaveRunnerHost — with the CoordinatorRunLock platform seam
// forced off Windows a mutating Run is never started (OW-RUN-002) and the
// pass resumes nothing.
//
// The probe Run Store lives under Library/OpWeaveNonWindowsGuardProbe/ —
// the same Library-rooted probe shape the recovery fixtures use; nothing
// tracked is modified. No domain reload is needed, so both tests are
// plain [Test]s that run on Windows like every other fixture here: the
// seam — restored in a finally and again in [TearDown] — is what carries
// the non-Windows world into the product code.
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using OpWeave.Core.Recovery;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.6-08:non-windows-guard</c> — decision D proven through
    /// the real start gate and the real recovery pass with
    /// <see cref="CoordinatorRunLock.IsWindowsPlatform"/> forced
    /// <see langword="false"/>: a mutating Run's outcome carries OW-RUN-002
    /// and the §F4 pass resumes nothing.
    /// </summary>
    internal sealed class NonWindowsGuardTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveNonWindowsGuardProbe");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string LockPath =>
            Path.Combine(StoreRoot, CoordinatorRunLock.LockFileName);
        private static string LockJsonPath =>
            Path.Combine(StoreRoot, CoordinatorRunLock.StateFileName);

        private const string WorkflowId = "example.nonwindows.guard";
        private const string MarkerOp = "test.nonwindows.mark";
        private const string HoldOp = "test.nonwindows.hold";

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

        // At least one OW-RUN-002 diagnostic carrying the non-Windows cause —
        // the SPEC §E2 item 3 "unobtainable exclusion" answer the guard gives.
        private static void AssertNonWindowsRefusal(
            IReadOnlyList<Diagnostic> diagnostics)
        {
            Diagnostic? found = null;
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Code == "OW-RUN-002")
                {
                    found = diagnostic;
                    break;
                }
            }
            Assert.IsNotNull(found,
                "expected an OW-RUN-002 diagnostic, got: " + Describe(diagnostics));
            StringAssert.Contains(
                "OpWeave v1 supports the Windows Editor only", found!.Message);
        }

        // Leaves the probe Run Store with nothing a later test could mistake
        // for live state — the same shape as the recovery fixtures' cleanup:
        // any exclusion this process still holds on the root is taken through
        // the hand-off seam and ended the orderly way (lock.json emptied
        // before the handle drops), then both SessionState records are
        // cleared. A live owner elsewhere is reported, never destroyed.
        private static void CleanupProbeArtifacts()
        {
            var obtained = RunLockHandoff.TryObtain(StoreRoot);
            if (obtained.Ok && obtained.Value is { } held)
            {
                if (!held.Release().Ok)
                {
                    held.Dispose();
                }
            }
            RunLockHandoff.Clear(StoreRoot);
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
        }

        [SetUp]
        public void SetUp() => CleanupProbeArtifacts();

        [TearDown]
        public void TearDown()
        {
            // Belt-and-braces behind each test's own finally: the seam must
            // never stay forced — every later lock acquisition in this Editor
            // domain would answer the same fail-closed result.
            CoordinatorRunLock.IsWindowsPlatform =
                Environment.OSVersion.Platform == PlatformID.Win32NT;
            CleanupProbeArtifacts();
        }

        // SPEC §E2 item 3 (decision D): the real start gate — the non-queued
        // Accept's own exclusion probe and the queued Run's TryStartNext
        // inside the update Tick — never starts a mutating Run off Windows;
        // each outcome carries OW-RUN-002 and coordinator.lock is never
        // opened or created.
        [Test]
        public void MutatingRun_NotStarted_OwRun002()
        {
            var fixture = new RunnerHostFixture(StoreRoot);
            var previous = CoordinatorRunLock.IsWindowsPlatform;
            try
            {
                fixture.Register(
                    new RunnerHostFixture.MarkerOperation(MarkerOp, fixture.Events), 'a');
                var plan = RunnerHostFixture.Plan(WorkflowId, new[]
                {
                    RunnerHostFixture.Step(0, "m", MarkerOp, 'a',
                        outputTypes: new Dictionary<string, string>
                        {
                            ["mark"] = "core.String",
                        }),
                });
                var doc = RunnerHostFixture.Doc(
                    WorkflowId,
                    new[] { new NodeData { Id = "m", Operation = MarkerOp, Version = 1 } },
                    Array.Empty<ControlEdge>());

                CoordinatorRunLock.IsWindowsPlatform = false;

                // The non-queued execute probes the exclusion before anything
                // is persisted: refused with OW-RUN-002, and no Run exists.
                var direct = fixture.Host.AcceptRun(
                    plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
                Assert.IsFalse(direct.Ok,
                    "a mutating execute must be refused off Windows");
                AssertNonWindowsRefusal(direct.Diagnostics);
                Assert.IsFalse(
                    Directory.Exists(Path.Combine(StoreRoot, "Runs")),
                    "a refused execute persists no Run");

                // The queued Run is accepted — the queue never probes — then
                // the start gate refuses to start it on every Tick.
                var queued = fixture.Host.AcceptRun(
                    plan, doc, ActorKind.Agent, new AcceptOptions(null, true));
                Assert.IsTrue(queued.Ok,
                    "the queued accept does not probe the exclusion: " +
                    Describe(queued.Diagnostics));
                var runId = queued.Value!.RunId;

                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok,
                    "the gate's denial is reported, never fatal: " +
                    Describe(tick.Diagnostics));
                AssertNonWindowsRefusal(tick.Diagnostics);
                Assert.AreEqual(
                    RunState.Queued, fixture.Host.Coordinator.StateOf(runId),
                    "the start gate never starts the mutating Run off Windows");
                Assert.IsFalse(
                    fixture.Events.Contains("execute:" + MarkerOp),
                    "the Run's step never executed");
                Assert.IsFalse(File.Exists(LockPath),
                    "coordinator.lock was never opened or created");
            }
            finally
            {
                CoordinatorRunLock.IsWindowsPlatform = previous;
                fixture.Dispose();
            }
        }

        // SPEC §E2 item 3 (decision D) + §F4 item 1: with the seam forced the
        // recovery pass cannot take the exclusion, so it resumes nothing —
        // the Run this Editor's dead session owned is reported NeedsReview,
        // no run_resumed is journaled, and the lock.json residue stays.
        [Test]
        public void Recovery_ResumesNothing()
        {
            var runId = SeedAbandonedRunningRun();

            var previous = CoordinatorRunLock.IsWindowsPlatform;
            CoordinatorRunLock.IsWindowsPlatform = false;
            var fixture = new RunnerHostFixture(StoreRoot);
            try
            {
                var recovered = fixture.Host.RecoverRuns();
                Assert.IsTrue(recovered.Ok,
                    "the pass reports instead of failing: " +
                    Describe(recovered.Diagnostics));
                var report = recovered.Value!;
                Assert.IsFalse(report.ExclusionHeld,
                    "the pass never takes the exclusion off Windows");
                Assert.AreEqual(1, report.Runs.Count,
                    "only this Editor's own Run is reported: " +
                    Describe(report.Diagnostics));
                Assert.AreEqual(runId, report.Runs[0].RunId);
                Assert.AreEqual(
                    RunRecoveryOutcome.NeedsReview, report.Runs[0].Outcome,
                    "SPEC §E2 item 3 (a): the Editor's own Run is NeedsReview, never resumed");
                AssertNonWindowsRefusal(report.Diagnostics);
                Assert.IsNull(
                    fixture.Host.Coordinator.StateOf(runId),
                    "nothing was re-attached or resumed");
                Assert.IsFalse(
                    RunnerHostFixture.JournalHas(
                        StoreRoot, runId, "\"type\":\"run_resumed\""),
                    "no resume was ever journaled");
                Assert.IsTrue(
                    File.ReadAllText(LockJsonPath).Contains("\"runId\":\"" + runId + "\""),
                    "the recorded owner residue is left in place");
            }
            finally
            {
                CoordinatorRunLock.IsWindowsPlatform = previous;
                fixture.Dispose();
            }
        }

        // One Windows-truth pass leaves the abandoned world the §F4 pass
        // reads: a Running Run held mid-wait by a ControlledWaitableOperation,
        // the lock.json owner record and the SessionState hand-off records —
        // then the host drops its exclusion the way a crashed Editor does.
        private static string SeedAbandonedRunningRun()
        {
            var fixture = new RunnerHostFixture(StoreRoot);
            try
            {
                fixture.Register(
                    new RunnerHostFixture.ControlledWaitableOperation(HoldOp), 'b');
                // The real wiring, not a seeded record: Install() makes the
                // §E8 state change record the Run this Editor holds the
                // exclusion for — SPEC §E2 item 3 (a)'s "自分の Run".
                fixture.Install();
                var plan = RunnerHostFixture.Plan(WorkflowId, new[]
                {
                    RunnerHostFixture.Step(0, "h", HoldOp, 'b'),
                });
                var doc = RunnerHostFixture.Doc(
                    WorkflowId,
                    new[] { new NodeData { Id = "h", Operation = HoldOp, Version = 1 } },
                    Array.Empty<ControlEdge>());
                var accepted = fixture.Host.AcceptRun(
                    plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
                Assert.IsTrue(accepted.Ok,
                    "the seed Run refused: " + Describe(accepted.Diagnostics));
                var runId = accepted.Value!.RunId;
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok,
                    "the seed Run's start failed: " + Describe(tick.Diagnostics));
                Assert.AreEqual(
                    RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                    "the seed Run must be Running when the owner dies");
                Assert.AreEqual(
                    runId, RunLockHandoff.OwnedRunId(StoreRoot),
                    "the wiring recorded which Run this Editor holds the exclusion for");
                var flushed = fixture.Host.FlushJournal();
                Assert.IsTrue(flushed.Ok,
                    "the journal flush failed: " + Describe(flushed.Diagnostics));
                Assert.IsTrue(
                    File.ReadAllText(LockJsonPath).Contains("\"runId\":\"" + runId + "\""),
                    "the seed must record the owner in lock.json");
                return runId;
            }
            finally
            {
                // Abandoned-state disposal (SPEC §E2): the exclusion drops and
                // lock.json keeps the recorded owner — the residue a crashed
                // owner leaves for the §F4 pass to reconcile.
                fixture.Dispose();
            }
        }
    }
}
