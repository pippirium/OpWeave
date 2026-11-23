#nullable enable

// T-W3.2-07 [Edit] tests: local:T-W3.2-07:cancel-workflow — SPEC §0.6 SG-14,
// §H1 cancel_workflow, §E6 item 2, §H4 item 5. AgentApi is called directly
// (no Pipeline, SPEC §H1 test rule) against a real RunnerHost/FileRunStore:
// Queued/Running/Waiting/Suspended journal exactly one cancel_requested;
// AwaitingApproval/NeedsReview/terminal Runs are refused OW-RUN-014 with
// the journal untouched; an unknown runId is OW-RUN-010 and a malformed
// one OW-API-001; a second cancel while the first pends stays a single
// journaled request.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class AgentCancelWorkflowTests
    {
        private const string WorkflowId = "example.agent-cancel";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveAgentCancelTests",
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

        // The service under test: the fixture's real Run Store for the
        // journal reconstruction and the coordinator's §E6 entry point as
        // the cancel seam — the same pair production wiring binds.
        private static AgentApiService Api(RunnerHostFixture fixture) =>
            new AgentApiService(
                fixture.Host.Store,
                new AgentApiCursorCodec(new byte[32]),
                fixture.Host.Coordinator.Cancel);

        private static string Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        private static (CompiledPlan Plan, WorkflowDocument Doc) SingleStep(
            string operationId,
            char hashSeed,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            Idempotency idempotency = Idempotency.Idempotent)
        {
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "s", operationId, hashSeed,
                    outputTypes: outputTypes, idempotency: idempotency),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "s", Operation = operationId, Version = 1 } },
                Array.Empty<ControlEdge>());
            return (plan, doc);
        }

        // An approvals-carrying Plan — the RunnerHostFixture.Plan builder
        // with the asks flowing into requiredEffects and the public effects
        // union so the §D0.1 load-time planHash cross-check holds
        // (mirrors the ApprovalWorkflowTests builder).
        private static CompiledPlan ApprovalPlan(string operationId, char hashSeed)
        {
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "ask", operationId, hashSeed,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var approvals = new List<ApprovalRequirement>
            {
                new ApprovalRequirement("ask", "asset.delete", operationId + " v1"),
            };
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
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(steps, approvals),
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
                RunnerHostFixture.Json("{}"),
                steps,
                Array.Empty<PlanOutput>(),
                PlanBuilder.UnionEffects(steps),
                approvals,
                Array.Empty<PlanSourceMapEntry>(),
                input);
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

        private byte[] JournalBytes(string runId) =>
            File.ReadAllBytes(RunnerHostFixture.JournalPath(_root, runId));

        private static void AssertRefusedOwRun014(
            AgentApiResponse response, RunState? before, RunState? after, string runId)
        {
            Assert.IsFalse(response.Ok, "the refusal must be a business failure");
            var error = (JObject)response.ToWire()["errors"]![0]!;
            Assert.AreEqual("OW-RUN-014", (string?)error["code"], runId);
            Assert.AreEqual("error", (string?)error["severity"], runId);
            Assert.AreEqual(false, (bool?)error["retryable"], runId);
            Assert.AreEqual(before, after,
                "the refusal must not change the Run's state");
        }

        private static int CancelRequestedCount(string root, string runId) =>
            RunnerHostFixture.JournalCount(root, runId, "\"type\":\"cancel_requested\"");

        // ── Permitted states: exactly one cancel_requested (SPEC §E6) ────

        // A Queued Run's Agent cancel journals the §E6 request, the next
        // Tick ends it Cancelled (OW-RUN-005 Info), and a later cancel hits
        // the terminal refusal.
        [Test]
        public void CancelQueuedRun_JournalsOneRequestAndEndsCancelled()
        {
            var fixture = NewFixture();
            var (plan, doc) = SingleStep("test.cancel.marker", 'a');
            var runId = Accept(fixture, plan, doc);
            Assert.AreEqual(RunState.Queued, fixture.Host.Coordinator.StateOf(runId));

            var api = Api(fixture);
            var response = api.CancelWorkflow(new CancelWorkflowRequest(runId, "stop it"));
            Assert.IsTrue(response.Ok, "cancel failed: " + Describe(response.Errors));
            Assert.AreEqual(runId, (string?)response.Data!["runId"]);
            Assert.AreEqual("queued", (string?)response.Data!["state"]);
            Assert.AreEqual(true, (bool?)response.Data!["cancelRequested"]);

            Assert.AreEqual(1, CancelRequestedCount(_root, runId),
                "the journal carries exactly one cancel_requested");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"cancel_requested\"", "\"reason\":\"stop it\""),
                "the caller's reason is journaled");
            Assert.AreEqual(RunState.Queued, fixture.Host.Coordinator.StateOf(runId),
                "SPEC §E6 item 2: the state changes on the next Tick, not at request time");

            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(_root, runId, "OW-RUN-005"),
                "the cancelled end carries the OW-RUN-005 Info diagnostic");

            // A further cancel now hits the terminal refusal.
            var refused = api.CancelWorkflow(new CancelWorkflowRequest(runId, "again"));
            AssertRefusedOwRun014(refused,
                RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId), runId);
            Assert.AreEqual(1, CancelRequestedCount(_root, runId));
        }

        // A Running Run whose current step is Waiting: the request is
        // journaled, the next Tick invokes IOperation.Cancel exactly once,
        // ends the Run Cancelled — and the completed step's §F7 changes
        // stay listed (they are never reverted).
        [Test]
        public void CancelRunningRun_EndsCancelledWithCompletedChanges()
        {
            var fixture = NewFixture();
            var marker = new ChangeMarkerOperation("test.cancel.marker");
            var waitable = new CancelCountingWaitable("test.cancel.waitable");
            fixture.Register(marker, 'a');
            fixture.Register(waitable, 'c');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "m", "test.cancel.marker", 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: 1),
                RunnerHostFixture.Step(1, "w", "test.cancel.waitable", 'c',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "m", Operation = "test.cancel.marker", Version = 1 },
                    new NodeData { Id = "w", Operation = "test.cancel.waitable", Version = 1 },
                },
                new[] { new ControlEdge("m", "w") });

            var runId = Accept(fixture, plan, doc);
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"step_waiting\""),
                "fixture must reach the Waiting-step shape");

            var api = Api(fixture);
            var response = api.CancelWorkflow(new CancelWorkflowRequest(runId, "halt"));
            Assert.IsTrue(response.Ok, "cancel failed: " + Describe(response.Errors));
            Assert.AreEqual("running", (string?)response.Data!["state"]);
            Assert.AreEqual(1, CancelRequestedCount(_root, runId));

            tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(1, waitable.CancelCalls,
                "a Waiting step is cancelled through IOperation.Cancel exactly once");
            Assert.AreEqual(RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_cancelled\"", "\"reason\":\"halt\""),
                "the step end is journaled with the masked reason");
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(_root, runId, "OW-RUN-005"));

            // SPEC §E6 item 3: completed changes are not reverted — the §F7
            // list keeps the finished step's record.
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"step_succeeded\"", "\"action\":\"created\"",
                    "\"path\":\"Assets/AgentCancel/created.txt\""),
                "the completed step's change list stays journaled");
        }

        // A Suspended Run accepts the request the same way — the journal
        // takes it and the Run stays Suspended until recovery drives it
        // (the pending cancel is left for the resume, SPEC §E6 item 2).
        [Test]
        public void CancelSuspendedRun_JournalsRequestAndStaysSuspended()
        {
            var fixture = NewFixture();
            var waitable = new CancelCountingWaitable("test.cancel.waitable");
            fixture.Register(waitable, 'a');
            var (plan, doc) = SingleStep("test.cancel.waitable", 'a',
                outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" });
            var runId = Accept(fixture, plan, doc);
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));

            fixture.Host.OnPlayModeStateChanged(PlayModeStateChange.ExitingEditMode);
            Assert.AreEqual(RunState.Suspended, fixture.Host.Coordinator.StateOf(runId));

            var response = Api(fixture).CancelWorkflow(
                new CancelWorkflowRequest(runId, "stop"));
            Assert.IsTrue(response.Ok, "cancel failed: " + Describe(response.Errors));
            Assert.AreEqual("suspended", (string?)response.Data!["state"]);
            Assert.AreEqual(1, CancelRequestedCount(_root, runId));
            Assert.AreEqual(RunState.Suspended, fixture.Host.Coordinator.StateOf(runId),
                "the suspended Run's cancel waits for the resume path");
        }

        // SPEC §0.6 SG-14: a competing second cancel while the first still
        // pends is the same accepted request — the journal keeps exactly
        // one cancel_requested.
        [Test]
        public void SecondCancelWhilePending_DoesNotDuplicateTheRequest()
        {
            var fixture = NewFixture();
            var (plan, doc) = SingleStep("test.cancel.marker", 'a');
            var runId = Accept(fixture, plan, doc);

            var api = Api(fixture);
            Assert.IsTrue(api.CancelWorkflow(new CancelWorkflowRequest(runId, "first")).Ok);
            var journalBefore = JournalBytes(runId);
            var second = api.CancelWorkflow(new CancelWorkflowRequest(runId, "second"));
            Assert.IsTrue(second.Ok, "the pending request is already accepted");
            Assert.AreEqual(1, CancelRequestedCount(_root, runId),
                "the duplicate request must not journal a second record");
            Assert.AreEqual(journalBefore, JournalBytes(runId),
                "the duplicate response must not mutate the journal");
        }

        // An omitted reason is journaled with the command's fixed
        // provenance marker (the reason? argument is optional, SPEC §H1).
        [Test]
        public void CancelWithoutReason_JournalsTheDefaultProvenance()
        {
            var fixture = NewFixture();
            var (plan, doc) = SingleStep("test.cancel.marker", 'a');
            var runId = Accept(fixture, plan, doc);

            var response = Api(fixture).CancelWorkflow(new CancelWorkflowRequest(runId));
            Assert.IsTrue(response.Ok);
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"cancel_requested\"", "\"reason\":\"cancel_workflow\""),
                "the omitted reason uses the documented default");
        }

        // ── Forbidden states: OW-RUN-014 and no mutation (SPEC §0.6 SG-14) ─

        // SPEC §H4 item 5: the approval wait is human-only — an Agent
        // cancel on AwaitingApproval is refused, the journal is byte-identical
        // and the pending approval still stands.
        [Test]
        public void CancelAwaitingApproval_IsRefusedWithoutMutation()
        {
            var fixture = NewFixture();
            var marker = new ChangeMarkerOperation("test.cancel.marker");
            fixture.Register(marker, 'a');
            var plan = ApprovalPlan("test.cancel.marker", 'a');
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "ask", Operation = "test.cancel.marker", Version = 1 } },
                Array.Empty<ControlEdge>());
            var runId = Accept(fixture, plan, doc);
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Host.Coordinator.StateOf(runId));

            var journalBefore = JournalBytes(runId);
            var response = Api(fixture).CancelWorkflow(new CancelWorkflowRequest(runId, "stop"));
            AssertRefusedOwRun014(response,
                RunState.AwaitingApproval, fixture.Host.Coordinator.StateOf(runId), runId);
            Assert.AreEqual(journalBefore, JournalBytes(runId),
                "the refusal must not touch the journal");
            Assert.IsNotNull(fixture.Host.Approvals.DetailOf(runId),
                "the pending approval still waits for the human");
        }

        // The NeedsReview resolution is human-only as well — the same
        // refusal leaves the Run, its step and the journal untouched.
        [Test]
        public void CancelNeedsReview_IsRefusedWithoutMutation()
        {
            var fixture = NewFixture();
            var op = new ReviewBoundOperation("test.cancel.review");
            fixture.Register(op, 'a');
            var (plan, doc) = SingleStep("test.cancel.review", 'a',
                outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                idempotency: Idempotency.Reconcilable);
            var runId = Accept(fixture, plan, doc);
            for (var i = 0;
                i < 10 && fixture.Host.Coordinator.StateOf(runId) != RunState.NeedsReview;
                i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.AreEqual(RunState.NeedsReview, fixture.Host.Coordinator.StateOf(runId),
                "the fixture must reach NeedsReview");

            var journalBefore = JournalBytes(runId);
            var response = Api(fixture).CancelWorkflow(new CancelWorkflowRequest(runId, "stop"));
            AssertRefusedOwRun014(response,
                RunState.NeedsReview, fixture.Host.Coordinator.StateOf(runId), runId);
            Assert.AreEqual(journalBefore, JournalBytes(runId));
        }

        // Every terminal state refuses — the Run is already over.
        [TestCase(RunState.Succeeded)]
        [TestCase(RunState.Failed)]
        [TestCase(RunState.Cancelled)]
        public void CancelTerminalRun_IsRefusedWithoutMutation(RunState terminal)
        {
            var fixture = NewFixture();
            string runId;
            switch (terminal)
            {
                case RunState.Succeeded:
                {
                    var marker = new ChangeMarkerOperation("test.cancel.marker");
                    fixture.Register(marker, 'a');
                    var (plan, doc) = SingleStep("test.cancel.marker", 'a',
                        outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" });
                    runId = Accept(fixture, plan, doc);
                    break;
                }
                case RunState.Failed:
                {
                    var failing = new FailingOperation("test.cancel.failing");
                    fixture.Register(failing, 'a');
                    var (plan, doc) = SingleStep("test.cancel.failing", 'a',
                        outputTypes: new Dictionary<string, string> { ["out"] = "core.String" });
                    runId = Accept(fixture, plan, doc);
                    break;
                }
                default:
                {
                    var (plan, doc) = SingleStep("test.cancel.marker", 'a');
                    runId = Accept(fixture, plan, doc);
                    Assert.IsTrue(
                        Api(fixture).CancelWorkflow(new CancelWorkflowRequest(runId, "x")).Ok);
                    break;
                }
            }
            for (var i = 0;
                i < 20 && fixture.Host.Coordinator.StateOf(runId) != terminal;
                i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.AreEqual(terminal, fixture.Host.Coordinator.StateOf(runId),
                "the fixture must reach the terminal state");

            var journalBefore = JournalBytes(runId);
            var requestsBefore = CancelRequestedCount(_root, runId);
            var response = Api(fixture).CancelWorkflow(new CancelWorkflowRequest(runId, "stop"));
            AssertRefusedOwRun014(response,
                terminal, fixture.Host.Coordinator.StateOf(runId), runId);
            Assert.AreEqual(journalBefore, JournalBytes(runId));
            Assert.AreEqual(requestsBefore, CancelRequestedCount(_root, runId),
                "a refused cancel never journals");
        }

        // ── Unknown and malformed runIds ─────────────────────────────────

        [Test]
        public void CancelUnknownRunId_IsOwRun010()
        {
            var fixture = NewFixture();
            var response = Api(fixture).CancelWorkflow(
                new CancelWorkflowRequest("run_01J00000000000000000000000", "stop"));

            Assert.IsFalse(response.Ok);
            var error = (JObject)response.ToWire()["errors"]![0]!;
            Assert.AreEqual("OW-RUN-010", (string?)error["code"]);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-run")]
        [TestCase("run_tooshort")]
        public void CancelMalformedRunId_IsOwApi001(string? runId)
        {
            var fixture = NewFixture();
            var response = Api(fixture).CancelWorkflow(
                new CancelWorkflowRequest(runId, "stop"));

            Assert.IsFalse(response.Ok);
            var error = (JObject)response.ToWire()["errors"]![0]!;
            Assert.AreEqual("OW-API-001", (string?)error["code"]);
            Assert.AreEqual("$.runId", (string?)error["jsonPath"]);
        }

        // ── Fixture operations ────────────────────────────────────────────

        /// <summary>
        /// An Immediate that records one §F7 change and its output — the
        /// completed-changes fixture: the change stays listed when the Run
        /// is cancelled after this step succeeded.
        /// </summary>
        private sealed class ChangeMarkerOperation : IOperation
        {
            internal ChangeMarkerOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Records a change and marks execution.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                ctx.Changes.Created(
                    new ChangeTarget("asset", "Assets/AgentCancel/created.txt"),
                    undoable: true);
                ctx.SetOutput("mark", "done:" + Descriptor.Id);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// A Waitable that never finishes — drives the Running-with-Waiting
        /// and Suspended shapes; counts its SPEC §E6
        /// <see cref="IOperation.Cancel"/> invocations.
        /// </summary>
        private sealed class CancelCountingWaitable : IOperation
        {
            internal CancelCountingWaitable(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits forever; counts cancels.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }
            internal int CancelCalls { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(RunnerHostFixture.Json("\"t0\""), "never", 60000);

            public StepResult Poll(StepContext ctx) =>
                StepResult.Waiting(RunnerHostFixture.Json("\"t1\""), "never", 60000);

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx) => CancelCalls++;
        }

        /// <summary>
        /// A Reconcilable step whose Execute fails retryably and whose
        /// Reconcile never decides — the §E7/§F5 NeedsReview fixture.
        /// </summary>
        private sealed class ReviewBoundOperation : IOperation
        {
            private readonly Diagnostic _error;

            internal ReviewBoundOperation(string id)
            {
                _error = ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true);
                Descriptor = Op.Define(id, 1)
                    .Summary("Fails retryably; never decides.")
                    .Output("out", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Reconcilable)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{\"seen\":true}"));

            public StepResult Execute(StepContext ctx) => StepResult.Failed(_error);

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never decides.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>A step that fails once, finally — the Failed fixture.</summary>
        private sealed class FailingOperation : IOperation
        {
            private readonly Diagnostic _error;

            internal FailingOperation(string id)
            {
                _error = ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_007, "A final failure.");
                Descriptor = Op.Define(id, 1)
                    .Summary("Fails once, finally.")
                    .Output("out", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Unsafe)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) => StepResult.Failed(_error);

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Unsafe steps never reconcile.");

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
