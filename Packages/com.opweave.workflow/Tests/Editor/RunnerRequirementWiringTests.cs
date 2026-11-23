#nullable enable

// T-W2.1-04 [Edit] tests: local:T-W2.1-04:requirement-wiring — the merged
// G1 evaluators (T-W2.1-02) wired into the RunLoop's injected requirement
// check (SPEC §E4 step 2): a violated scene_loaded fails the step with
// OW-UNITY-003 before Preflight, a not_compiling Wait leaves the step
// unstarted with no diagnostic until compiling clears, and path_writable
// is judged by the settings snapshot each acceptance validated
// (OW-PERM-002 outside it).
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RunnerRequirementWiringTests
    {
        private const string WorkflowId = "example.reqwire";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveReqWiringTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // SPEC §E4 step 2 + §G1 item 4: a violated scene_loaded fails the
        // step with OW-UNITY-003 — before Preflight, before Execute.
        [Test]
        public void ViolatedSceneLoadedFailsStepWithOwUnity003BeforePreflight()
        {
            var fixture = NewFixture();
            var gated = new RunnerHostFixture.GatedOperation(
                "test.req.gated_scene", "scene_loaded", "scene", fixture.Events);
            var opener = new RunnerHostFixture.MarkerOperation("test.req.opener", fixture.Events);
            fixture.Register(gated, 'a');
            fixture.Register(opener, 'c');

            var sceneRef = RunnerHostFixture.Json(
                "{\"kind\":\"scene\",\"path\":\"Assets/Probe/never-loaded.unity\"}");
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "first", "test.req.opener", 'c', next: 1,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(1, "gated", "test.req.gated_scene", 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["scene"] = new Constant(sceneRef),
                    },
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "first", Operation = "test.req.opener", Version = 1 },
                    new NodeData { Id = "gated", Operation = "test.req.gated_scene", Version = 1 },
                },
                new[] { new ControlEdge("first", "gated") });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            for (var i = 0; i < 10 &&
                fixture.Host.Coordinator.StateOf(runId) != RunState.Failed; i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }

            Assert.AreEqual(RunState.Failed, fixture.Host.Coordinator.StateOf(runId));
            // The violated requirement stopped the step before Preflight and
            // Execute (SPEC §E4 step 2 ordering).
            Assert.AreEqual(0, gated.PreflightCalls, "Preflight ran before the check");
            Assert.AreEqual(0, gated.ExecuteCalls, "Execute ran after a violation");
            Assert.AreEqual(1, opener.ExecuteCalls);
            var lines = RunnerHostFixture.JournalLines(_root, runId);
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_started\"", "\"step\":1,"),
                "a violated requirement must not reach StepStarted");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "OW-UNITY-003"),
                Is.True, "the StepFailed did not carry OW-UNITY-003");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"run_finished\""),
                Is.True);
        }

        // SPEC §E4 step 2 / §1.4 / the 2026-11-06 owner decision: while the
        // Editor compiles a not_compiling requirement reports Wait — no
        // StepStarted, no diagnostic, the step re-evaluates from step 1 next
        // Tick; once compiling clears the step starts.
        [Test]
        public void NotCompilingWaitLeavesStepUnstartedThenStarts()
        {
            var fixture = NewFixture();
            var gated = new RunnerHostFixture.GatedOperation(
                "test.req.gated_compile", "not_compiling", null, fixture.Events);
            var held = new RunnerHostFixture.ControlledWaitableOperation("test.req.held");
            fixture.Register(gated, 'a');
            fixture.Register(held, 'c');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "first", "test.req.held", 'c', next: 1,
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(1, "gated", "test.req.gated_compile", 'a',
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "first", Operation = "test.req.held", Version = 1 },
                    new NodeData { Id = "gated", Operation = "test.req.gated_compile", Version = 1 },
                },
                new[] { new ControlEdge("first", "gated") });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            // Tick 1: starts the run; the held step enters Waiting and the
            // gated step is not reached — the deterministic pause before
            // the gated step can start.
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(1, held.ExecuteCalls);
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
            Assert.AreEqual(0, gated.ExecuteCalls);

            // Compiling begins — reported as OpWeave-initiated so the §1.4
            // Editor-state gate does not suspend the run (an external compile
            // would suspend instead of reaching the requirement).
            fixture.Editor.IsCompiling = true;
            fixture.OpWeaveBusy = true;

            // The held step completes while compiling: the same Tick
            // advances to the gated step, whose not_compiling requirement
            // reports Wait — the step is left unstarted, no diagnostic is
            // written and the Run stays Running.
            held.Release = true;
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(10));
            tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.IsEmpty(tick.Diagnostics,
                "a not_compiling Wait emits no diagnostic (SPEC §E4 step 2)");
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
            Assert.AreEqual(0, gated.PreflightCalls);
            Assert.AreEqual(0, gated.ExecuteCalls);
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_started\"", "\"step\":1,"),
                "a not_compiling Wait wrote StepStarted");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"step_failed\""));

            // A second compiling Tick re-evaluates the step from E4 step 1 —
            // identical: still unstarted, still quiet.
            tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.IsEmpty(tick.Diagnostics);
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
            Assert.AreEqual(0, gated.ExecuteCalls);
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_started\"", "\"step\":1,"));

            // Compilation finishes — the next Tick re-evaluates and the step
            // starts and completes the run.
            fixture.Editor.IsCompiling = false;
            fixture.OpWeaveBusy = false;
            for (var i = 0; i < 10 &&
                fixture.Host.Coordinator.StateOf(runId) == RunState.Running; i++)
            {
                tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.AreEqual(1, gated.PreflightCalls);
            Assert.AreEqual(1, gated.ExecuteCalls);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_started\"", "\"step\":1,"),
                Is.True);
        }

        // SPEC §G1 item 4 / §H4.1 (permission correctness): each accepted
        // Run's path_writable check is judged by the settings its own
        // acceptance validated — a settings file tightened after a
        // permissive acceptance binds the next Run.
        [Test]
        public void PathWritableAppliesTheSettingsValidatedAtEachAcceptance()
        {
            var fixture = NewFixture();
            var gated = new RunnerHostFixture.GatedOperation(
                "test.req.gated_path", "path_writable", "path", fixture.Events,
                inputName: "path", inputType: "core.String");
            fixture.Register(gated, 'a');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "write", "test.req.gated_path", 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["path"] = new Constant(
                            RunnerHostFixture.Json("\"Assets/Probe/out.txt\"")),
                    },
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "write", Operation = "test.req.gated_path", Version = 1 } },
                Array.Empty<ControlEdge>());

            // Permissive settings: the path is inside the allowed set and
            // the Run succeeds.
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"allowedPaths\":[\"Assets/\",\"Packages/\"]}");
            var first = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(first.Ok, "accept failed: " + Describe(first.Diagnostics));
            var firstRunId = first.Value!.RunId;
            for (var i = 0; i < 10; i++)
            {
                var state = fixture.Host.Coordinator.StateOf(firstRunId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    break;
                }
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(firstRunId));
            Assert.AreEqual(1, gated.ExecuteCalls);

            // The settings file tightens to a set the path is outside: the
            // next acceptance must judge the same path by the new snapshot —
            // OW-PERM-002, not the stale permissive set.
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"allowedPaths\":[\"Packages/\"]}");
            var second = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(second.Ok, "accept failed: " + Describe(second.Diagnostics));
            var secondRunId = second.Value!.RunId;
            for (var i = 0; i < 10 &&
                fixture.Host.Coordinator.StateOf(secondRunId) != RunState.Failed; i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.AreEqual(RunState.Failed, fixture.Host.Coordinator.StateOf(secondRunId));
            Assert.AreEqual(1, gated.ExecuteCalls, "the gated step must not run again");
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    _root, secondRunId, "\"type\":\"step_started\"", "\"step\":0,"),
                "a violated path_writable must not reach StepStarted");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, secondRunId, "OW-PERM-002"),
                Is.True, "the StepFailed did not carry OW-PERM-002");
        }

        // SPEC §H4.1 / decision D-R6 (fail closed): an invalid settings file
        // refuses the Run outright; once repaired the newly validated
        // allowedPaths apply — never a default Assets/ fallback.
        [Test]
        public void PathWritableAfterSettingsRepairUsesTheRepairedSet()
        {
            var fixture = NewFixture();
            var gated = new RunnerHostFixture.GatedOperation(
                "test.req.gated_path", "path_writable", "path", fixture.Events,
                inputName: "path", inputType: "core.String");
            fixture.Register(gated, 'a');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "write", "test.req.gated_path", 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["path"] = new Constant(
                            RunnerHostFixture.Json("\"Assets/Probe/out.txt\"")),
                    },
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "write", Operation = "test.req.gated_path", Version = 1 } },
                Array.Empty<ControlEdge>());

            // Invalid settings: the gate refuses the accept before any side
            // effect (D-R6 — no Run is admitted).
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse("{\"version\":99}");
            var refused = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsFalse(refused.Ok, "an invalid settings file must refuse the Run");
            var sawConfigError = false;
            foreach (var diagnostic in refused.Diagnostics)
            {
                sawConfigError |= diagnostic.Code == ErrorCodes.OW_CFG_001;
            }
            Assert.IsTrue(sawConfigError, "the refusal did not carry OW-CFG-001");

            // The file is repaired with a restrictive set: the Run is now
            // admitted and its path check is judged by the repaired set —
            // an Assets/ path fails OW-PERM-002 even though the documented
            // default would have allowed it.
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"allowedPaths\":[\"Packages/\"]}");
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            for (var i = 0; i < 10 &&
                fixture.Host.Coordinator.StateOf(runId) != RunState.Failed; i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.AreEqual(RunState.Failed, fixture.Host.Coordinator.StateOf(runId));
            Assert.AreEqual(0, gated.ExecuteCalls);
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "OW-PERM-002"),
                Is.True, "the StepFailed did not carry OW-PERM-002");
        }
    }
}
