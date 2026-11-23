#nullable enable

// T-W3.2-04 [Edit] tests: local:T-W3.2-04:run-commands — SPEC §H1
// get_run_status / get_run_logs / list_runs, the §H2 item 3
// thread-safe status snapshots and the ≤20 s long-poll, §H3 items
// 2–4 (compact {node,state,attempt}, the 64 KiB page cap, the
// approval hint), the §F6 SG-08 debug policy and credential mask,
// the §F7 change list and the §0.4 main-thread rule. AgentApi is
// called directly (no Pipeline, SPEC §H1 test rule) against a real
// RunnerHost over a real FileRunStore: Queued, Running-with-Waiting,
// AwaitingApproval and finished Runs are exercised, and the status,
// log and list reads are also issued on a background thread — which
// the store-access instrumentation proves never reaches mutable
// Core state.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class AgentRunObservationTests
    {
        private const string WorkflowId = "example.agent-observe";
        private const string OpEmit = "test.obs.emit";
        private const string OpWait = "test.obs.waitable";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<RunStatusTracker> _trackers = new List<RunStatusTracker>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveAgentObserveTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var tracker in _trackers)
            {
                tracker.Dispose();
            }
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RunnerHostFixture NewFixture(string? settingsJson = null)
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            if (settingsJson is not null)
            {
                fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(settingsJson);
            }
            return fixture;
        }

        // The service under test over all its observation seams: the
        // tracker's load/list pair is the real Run Store — invoked on the
        // main thread only, which is the tracker's own contract — while
        // the §F6 log read and the §F7 change read are the production
        // read-only journal projections over the fixture's real files.
        // The injected debugLogging reads the fixture's live settings,
        // exactly like the host's production seam.
        private AgentApiService Api(RunnerHostFixture fixture) =>
            Api(
                fixture,
                runId => fixture.Host.Store.Load(runId),
                () => fixture.Host.Store.List(new RunQuery()));

        private AgentApiService Api(
            RunnerHostFixture fixture,
            Func<string, Result<RunSnapshot>> load,
            Func<Result<IReadOnlyList<RunIndexEntry>>> list)
        {
            var tracker = new RunStatusTracker(fixture.Host.Events, load, list);
            _trackers.Add(tracker);
            var root = fixture.Host.RunStoreRoot;
            return new AgentApiService(
                fixture.Host.Store,
                new AgentApiCursorCodec(new byte[32]),
                fixture.Host.Coordinator.Cancel,
                runStatus: tracker,
                runStoreRoot: root,
                debugLogging: () =>
                    fixture.LoadSettings() is { Ok: true, Value: { } settings } &&
                    settings.DebugLogging,
                readChanges: runId =>
                {
                    var records = JournaledChanges.Read(
                        JournaledChanges.PathOf(root, runId), out var failure);
                    return failure is null
                        ? Result<IReadOnlyList<ChangeRecord>>.Success(records)
                        : Result<IReadOnlyList<ChangeRecord>>.Failure(
                            ErrorCodes.Create(ErrorCodes.OW_STORE_002, failure));
                });
        }

        private static string Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        private static void Drive(OpWeaveRunnerHost host, string runId, int maxTicks = 50)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    return;
                }
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not reach a terminal state within " + maxTicks + " ticks.");
        }

        private static void Flush(RunnerHostFixture fixture)
        {
            var flushed = fixture.Host.FlushJournal();
            Assert.IsTrue(flushed.Ok, "journal flush failed: " + Describe(flushed.Diagnostics));
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

        private static string ErrorCode(AgentApiResponse response) =>
            (string?)((JObject)response.ToWire()["errors"]![0]!)["code"] ?? "";

        private static string? ErrorPath(AgentApiResponse response) =>
            (string?)((JObject)response.ToWire()["errors"]![0]!)["jsonPath"];

        // ── Plan/document builders ───────────────────────────────────────────

        private static (CompiledPlan Plan, WorkflowDocument Doc) SingleStep(
            string workflowId,
            string operationId,
            char hashSeed,
            string nodeId = "s",
            IReadOnlyDictionary<string, string>? outputTypes = null,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            Idempotency idempotency = Idempotency.Idempotent)
        {
            var plan = RunnerHostFixture.Plan(workflowId, new[]
            {
                RunnerHostFixture.Step(0, nodeId, operationId, hashSeed,
                    inputs: inputs, outputTypes: outputTypes, idempotency: idempotency),
            });
            var doc = RunnerHostFixture.Doc(
                workflowId,
                new[] { new NodeData { Id = nodeId, Operation = operationId, Version = 1 } },
                Array.Empty<ControlEdge>());
            return (plan, doc);
        }

        // The marker→waitable pair that holds a Run mid-execution: step 0
        // succeeds with an output, step 1 stays Waiting until released.
        // ControlledWaitableOperation declares no output port, so the
        // step declares none either — a declared-but-never-written output
        // would fail the released step as OW-RUN-007.
        private static (CompiledPlan Plan, WorkflowDocument Doc) MarkerThenWaitable(
            string workflowId, string markerId, char markerSeed, string waitableId, char waitableSeed)
        {
            var plan = RunnerHostFixture.Plan(workflowId, new[]
            {
                RunnerHostFixture.Step(0, "m", markerId, markerSeed,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: 1),
                RunnerHostFixture.Step(1, "w", waitableId, waitableSeed),
            });
            var doc = RunnerHostFixture.Doc(
                workflowId,
                new[]
                {
                    new NodeData { Id = "m", Operation = markerId, Version = 1 },
                    new NodeData { Id = "w", Operation = waitableId, Version = 1 },
                },
                new[] { new ControlEdge("m", "w") });
            return (plan, doc);
        }

        // An approvals-carrying Plan — the RunnerHostFixture.Plan builder
        // with the ask flowing into requiredEffects and the public effects
        // union so the §D0.1 load-time planHash cross-check holds
        // (mirrors the AgentCancelWorkflowTests builder).
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
            var input = JsonValue.From(HashInput(
                WorkflowId, steps, approvals, resolvedOutputs: new JArray()));
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

        // A Plan with workflow outputs resolved to step positions —
        // resolvedWorkflow.outputs carries the {id, from:{node,port}}
        // declarations so the §D0.1 cross-check against the public
        // outputs member holds.
        private static CompiledPlan OutputPlan(
            string workflowId,
            IReadOnlyList<PlanStep> steps,
            IReadOnlyList<PlanOutput> outputs)
        {
            var resolvedOutputs = new JArray();
            foreach (var output in outputs)
            {
                resolvedOutputs.Add(new JObject
                {
                    ["id"] = output.Id,
                    ["from"] = new JObject
                    {
                        ["node"] = steps[output.StepIndex].NodeId,
                        ["port"] = output.Port,
                    },
                });
            }
            var input = JsonValue.From(HashInput(
                workflowId, steps, Array.Empty<ApprovalRequirement>(), resolvedOutputs));
            return new CompiledPlan(
                "plan_" + Identifiers.NewUlid(),
                CanonicalJson.Sha256(input.ToToken()),
                workflowId,
                new string('b', 64),
                new string('c', 64),
                RunnerHostFixture.Json("{}"),
                steps,
                outputs,
                PlanBuilder.UnionEffects(steps),
                Array.Empty<ApprovalRequirement>(),
                Array.Empty<PlanSourceMapEntry>(),
                input);
        }

        // The SG-12 twelve-member planHash input (SPEC §0.6 SG-12),
        // shared by the approval and output builders — the step-derived
        // members go through the shared PlanHashInputParts so the
        // persisted public surface and the hash input agree byte-exactly.
        private static JObject HashInput(
            string workflowId,
            IReadOnlyList<PlanStep> steps,
            IReadOnlyList<ApprovalRequirement> approvals,
            JArray resolvedOutputs)
        {
            return new JObject
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
                    ["id"] = workflowId,
                    ["inputs"] = new JArray(),
                    ["outputs"] = resolvedOutputs,
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
        }

        private static (CompiledPlan Plan, WorkflowDocument Doc) ManySteps(
            string workflowId, string operationId, char hashSeed, int count)
        {
            var steps = new List<PlanStep>();
            var nodes = new List<NodeData>();
            var edges = new List<ControlEdge>();
            for (var i = 0; i < count; i++)
            {
                var node = "s" + i.ToString("D2");
                steps.Add(RunnerHostFixture.Step(i, node, operationId, hashSeed,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: i + 1 < count ? i + 1 : -1));
                nodes.Add(new NodeData { Id = node, Operation = operationId, Version = 1 });
                if (i + 1 < count)
                {
                    edges.Add(new ControlEdge(node, "s" + (i + 1).ToString("D2")));
                }
            }
            return (RunnerHostFixture.Plan(workflowId, steps),
                RunnerHostFixture.Doc(workflowId, nodes, edges));
        }

        // A Run held Running with its second step Waiting — the
        // "waiting" shape of the ticket's verification method.
        private static string RunningWithWaitingStep(
            RunnerHostFixture fixture,
            RunnerHostFixture.ControlledWaitableOperation waitable,
            IOperation? marker = null,
            string workflowId = WorkflowId)
        {
            var emit = marker ?? new EmitOperation(OpEmit, fixture.Host);
            fixture.Register(emit, 'a');
            fixture.Register(waitable, 'c');
            var (plan, doc) = MarkerThenWaitable(workflowId, emit.Descriptor.Id, 'a', OpWait, 'c');
            var runId = Accept(fixture, plan, doc);
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
            return runId;
        }

        private static JArray Steps(AgentApiResponse response) =>
            (JArray)response.Data!["steps"]!;

        // ── get_run_status: the Run shapes ───────────────────────────────────

        // A Queued Run reports its identity and the Plan's pending steps —
        // no current step, no outputs, no approval surface.
        [Test]
        public void StatusOfQueuedRun_ReportsPlanStepsAndIdentity()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" });
            var runId = Accept(fixture, plan, doc);

            var response = Api(fixture).GetRunStatus(new GetRunStatusRequest(runId));

            Assert.IsTrue(response.Ok, "status failed: " + Describe(response.Errors));
            var data = response.Data!;
            Assert.AreEqual(runId, (string?)data["runId"]);
            Assert.AreEqual(WorkflowId, (string?)data["workflowId"]);
            Assert.AreEqual("queued", (string?)data["state"]);
            Assert.IsNotNull(data["createdAt"], "the journaled creation time is reported");
            Assert.IsNotNull(data["updatedAt"], "the journaled update time is reported");
            Assert.IsNull(data["currentStep"], "a Queued Run has no current step");
            Assert.IsNull(data["outputs"], "no terminal record — no outputs");
            Assert.IsNull(data["approvals"], "no approval surface on a Queued Run");
            Assert.IsNull(data["hint"]);
            var steps = Steps(response);
            Assert.AreEqual(1, steps.Count);
            Assert.AreEqual("s", (string?)steps[0]!["node"]);
            Assert.AreEqual("pending", (string?)steps[0]!["state"]);
            Assert.AreEqual(0, (int?)steps[0]!["attempt"]);
        }

        // A Running Run whose current step is Waiting: the wait label
        // and the completed step's outputs show in full verbosity while
        // compact drops them to {node,state,attempt}.
        [Test]
        public void StatusOfWaitingRun_ReportsWaitAndCurrentStep()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.ControlledWaitableOperation(OpWait);
            var runId = RunningWithWaitingStep(fixture, waitable);
            var api = Api(fixture);

            var full = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsTrue(full.Ok, "status failed: " + Describe(full.Errors));
            var data = full.Data!;
            Assert.AreEqual("running", (string?)data["state"]);
            Assert.AreEqual("w", (string?)data["currentStep"],
                "the Waiting step is the Run's current step");
            var steps = Steps(full);
            Assert.AreEqual("succeeded", (string?)steps[0]!["state"]);
            Assert.AreEqual(1, (int?)steps[0]!["attempt"]);
            Assert.AreEqual(
                "done:" + OpEmit, (string?)steps[0]!["outputs"]!["mark"],
                "full verbosity carries the persisted step outputs");
            Assert.AreEqual("waiting", (string?)steps[1]!["state"]);
            Assert.AreEqual("held", (string?)steps[1]!["waitingFor"],
                "the §E0 wait label is reported in full verbosity");

            var compact = api.GetRunStatus(new GetRunStatusRequest(runId, Verbosity: "compact"));
            Assert.IsTrue(compact.Ok);
            foreach (var entry in Steps(compact))
            {
                var members = new List<string>();
                foreach (var property in ((JObject)entry!).Properties())
                {
                    members.Add(property.Name);
                }
                CollectionAssert.AreEquivalent(
                    new[] { "node", "state", "attempt" }, members,
                    "compact reduces every step to {node,state,attempt} (SPEC §H3 item 2)");
            }
        }

        // The approval-pending Run carries the Plan's required approvals
        // and the human-only §H3 item 4 hint — the Agent can relay it,
        // never perform it (SPEC §H4 item 5).
        [Test]
        public void StatusOfAwaitingApprovalRun_CarriesApprovalsAndHint()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var plan = ApprovalPlan(OpEmit, 'a');
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "ask", Operation = OpEmit, Version = 1 } },
                Array.Empty<ControlEdge>());
            var runId = Accept(fixture, plan, doc);
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Host.Coordinator.StateOf(runId));

            var response = Api(fixture).GetRunStatus(new GetRunStatusRequest(runId));

            Assert.IsTrue(response.Ok, "status failed: " + Describe(response.Errors));
            var data = response.Data!;
            Assert.AreEqual("awaiting_approval", (string?)data["state"]);
            var approvals = (JArray)data["approvals"]!;
            Assert.AreEqual(1, approvals.Count);
            Assert.AreEqual("ask", (string?)approvals[0]!["nodeId"]);
            Assert.AreEqual("asset.delete", (string?)approvals[0]!["effect"]);
            Assert.AreEqual(OpEmit + " v1", (string?)approvals[0]!["detail"]);
            var hint = (string?)data["hint"];
            Assert.IsNotNull(hint, "the AwaitingApproval Run carries the human-only hint");
            StringAssert.Contains("Unity Run Viewer", hint!);
        }

        // A finished Run reports its terminal state and the workflow
        // outputs the run_finished record resolved.
        [Test]
        public void StatusOfSucceededRun_CarriesWorkflowOutputs()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "n1", OpEmit, 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: 1),
                RunnerHostFixture.Step(1, "n2", OpEmit, 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var plan = OutputPlan(
                WorkflowId, steps, new[] { new PlanOutput("result", 1, "mark") });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "n1", Operation = OpEmit, Version = 1 },
                    new NodeData { Id = "n2", Operation = OpEmit, Version = 1 },
                },
                new[] { new ControlEdge("n1", "n2") });
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));

            var response = Api(fixture).GetRunStatus(new GetRunStatusRequest(runId));

            Assert.IsTrue(response.Ok, "status failed: " + Describe(response.Errors));
            var data = response.Data!;
            Assert.AreEqual("succeeded", (string?)data["state"]);
            Assert.AreEqual(
                "done:" + OpEmit, (string?)data["outputs"]!["result"],
                "the terminal record's resolved workflow output is reported");
            Assert.IsNull(data["currentStep"], "a terminal Run has no current step");
        }

        // SPEC §H3 test 1: the compact status of a 50-step Run fits 8 KiB.
        [Test]
        public void CompactStatusOfFiftyStepRun_StaysUnderEightKiB()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var (plan, doc) = ManySteps(WorkflowId, OpEmit, 'a', 50);
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId, maxTicks: 80);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));

            var response = Api(fixture).GetRunStatus(
                new GetRunStatusRequest(runId, Verbosity: "compact"));

            Assert.IsTrue(response.Ok, "status failed: " + Describe(response.Errors));
            Assert.AreEqual(50, Steps(response).Count);
            Assert.LessOrEqual(
                response.Utf8ByteCount, 8 * 1024,
                "the 50-step compact status must fit 8 KiB (SPEC §H3 test 1)");
            Assert.IsFalse(response.Truncated);
        }

        // The cancelled Run's pending flag reaches the status snapshot
        // through the main-thread refresh cancel_workflow performs — the
        // request publishes no §E8 event of its own.
        [Test]
        public void CancelledRunStatus_ReportsCancelRequested()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.ControlledWaitableOperation(OpWait);
            var runId = RunningWithWaitingStep(fixture, waitable);
            var api = Api(fixture);

            var cancelled = api.CancelWorkflow(new CancelWorkflowRequest(runId, "stop"));
            Assert.IsTrue(cancelled.Ok, "cancel failed: " + Describe(cancelled.Errors));

            var status = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsTrue(status.Ok);
            Assert.AreEqual(true, (bool?)status.Data!["cancelRequested"],
                "the journaled cancel request is visible without waiting for the next Tick");
        }

        // ── get_run_status: long-poll ────────────────────────────────────────

        // A long-poll on a background thread returns as soon as the E8
        // event replaces the snapshot — far inside the 20 s bound.
        [Test]
        public void LongPoll_ReturnsWhenTheSnapshotChanges()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.ControlledWaitableOperation(OpWait);
            var runId = RunningWithWaitingStep(fixture, waitable);
            var api = Api(fixture);

            var pending = Task.Run(
                () => api.GetRunStatus(new GetRunStatusRequest(runId, WaitSeconds: 20)));
            Thread.Sleep(300);

            waitable.Release = true;
            // The fixture clock is frozen: the waiting step's next Poll was
            // scheduled one poll interval past its last UtcNow, so a Tick at
            // the same UtcNow skips it and no §E8 event ever fires to pulse
            // the tracker. Advance the clock so the released Poll is due.
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));

            Assert.IsTrue(
                pending.Wait(TimeSpan.FromSeconds(15)),
                "the long-poll must return on the state change, not the timeout");
            var response = pending.Result;
            Assert.IsTrue(response.Ok, "status failed: " + Describe(response.Errors));
            // The poll may wake on the step_succeeded reload (Run still
            // Running) or the run_finished one — either is a replaced
            // snapshot, and both record the step's success.
            Assert.AreEqual(
                "succeeded", (string?)Steps(response)[1]!["state"],
                "the woken poll observes a newer snapshot");
        }

        // With nothing changing, the poll sleeps the requested duration
        // and re-reads the unchanged snapshot — never longer.
        [Test]
        public void LongPoll_TimesOutWithinWaitSeconds()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.ControlledWaitableOperation(OpWait);
            var runId = RunningWithWaitingStep(fixture, waitable);
            var api = Api(fixture);

            var elapsed = Stopwatch.StartNew();
            var response = api.GetRunStatus(new GetRunStatusRequest(runId, WaitSeconds: 1));
            elapsed.Stop();

            Assert.IsTrue(response.Ok, "status failed: " + Describe(response.Errors));
            Assert.AreEqual("running", (string?)response.Data!["state"]);
            Assert.GreaterOrEqual(
                elapsed.Elapsed, TimeSpan.FromMilliseconds(900),
                "the poll waits the requested duration");
            Assert.Less(
                elapsed.Elapsed, TimeSpan.FromSeconds(10),
                "the poll returns within waitSeconds (SPEC §H2 item 3)");
        }

        // waitSeconds 0 never sleeps, and a finished Run cannot change —
        // even the maximum 20 s returns immediately.
        [Test]
        public void LongPoll_ReturnsImmediatelyForZeroAndFinishedRuns()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" });
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            var api = Api(fixture);

            var elapsed = Stopwatch.StartNew();
            var status = api.GetRunStatus(new GetRunStatusRequest(runId, WaitSeconds: 20));
            elapsed.Stop();

            Assert.IsTrue(status.Ok, "status failed: " + Describe(status.Errors));
            Assert.AreEqual("succeeded", (string?)status.Data!["state"]);
            Assert.Less(
                elapsed.Elapsed, TimeSpan.FromSeconds(5),
                "a terminal Run's wait returns immediately");
        }

        // ── Background-thread reads (SPEC §0.4 / §H2 item 3) ────────────────

        // All three observation commands answer on a background thread;
        // the instrumented load/list seams prove the only Run Store
        // touches happened on the main thread (event dispatch and seed),
        // so the background calls never reached mutable Core state.
        [Test]
        public void BackgroundThreadReads_TouchNoMutableCoreState()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.ControlledWaitableOperation(OpWait);
            var marker = new EmitOperation(
                OpEmit, fixture.Host,
                writes: new[]
                {
                    (Severity.Info, "bg-read-info-entry", (JsonValue?)null),
                },
                changePaths: new[] { "Assets/Observe/background.txt" });
            var runId = RunningWithWaitingStep(fixture, waitable, marker);
            Flush(fixture);

            var loadThreads = new List<int>();
            var listThreads = new List<int>();
            var mainThread = Environment.CurrentManagedThreadId;
            var api = Api(
                fixture,
                runId =>
                {
                    lock (loadThreads)
                    {
                        loadThreads.Add(Environment.CurrentManagedThreadId);
                    }
                    return fixture.Host.Store.Load(runId);
                },
                () =>
                {
                    lock (listThreads)
                    {
                        listThreads.Add(Environment.CurrentManagedThreadId);
                    }
                    return fixture.Host.Store.List(new RunQuery());
                });

            var status = Task.Run(() => api.GetRunStatus(new GetRunStatusRequest(runId)));
            var logs = Task.Run(() => api.GetRunLogs(new GetRunLogsRequest(runId)));
            var list = Task.Run(() => api.ListRuns(new ListRunsRequest(Limit: 10)));

            Assert.IsTrue(
                Task.WaitAll(new Task[] { status, logs, list }, TimeSpan.FromSeconds(30)),
                "the background commands must complete");
            Assert.IsTrue(status.Result.Ok, "status failed: " + Describe(status.Result.Errors));
            Assert.AreEqual("running", (string?)status.Result.Data!["state"]);
            Assert.IsTrue(logs.Result.Ok, "logs failed: " + Describe(logs.Result.Errors));
            Assert.IsTrue(list.Result.Ok, "list failed: " + Describe(list.Result.Errors));
            foreach (var thread in loadThreads)
            {
                Assert.AreEqual(mainThread, thread,
                    "the Run Store load seam must only run on the main thread");
            }
            foreach (var thread in listThreads)
            {
                Assert.AreEqual(mainThread, thread,
                    "the Run Store list seam must only run on the main thread");
            }
        }

        // ── get_run_logs ─────────────────────────────────────────────────────

        // The log read and the §F7 change list come back together, served
        // from the journal of a still-Running Run.
        [Test]
        public void LogsOfRunningRun_CarryEntriesAndJournaledChanges()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.ControlledWaitableOperation(OpWait);
            var marker = new EmitOperation(
                OpEmit, fixture.Host,
                writes: new[]
                {
                    (Severity.Info, "observe-info-entry", (JsonValue?)null),
                    (Severity.Warning, "observe-warning-entry", (JsonValue?)null),
                },
                changePaths: new[] { "Assets/Observe/created.txt" });
            var runId = RunningWithWaitingStep(fixture, waitable, marker);
            Flush(fixture);

            var response = Api(fixture).GetRunLogs(new GetRunLogsRequest(runId));

            Assert.IsTrue(response.Ok, "logs failed: " + Describe(response.Errors));
            var data = response.Data!;
            Assert.AreEqual(runId, (string?)data["runId"]);
            var messages = new List<string>();
            foreach (var entry in (JArray)data["logs"]!)
            {
                messages.Add((string?)entry!["message"] ?? "");
                Assert.IsNotNull(entry["seq"], "every entry carries its journal seq");
                Assert.IsNotNull(entry["ts"], "every entry carries its timestamp");
                Assert.IsNotNull(entry["level"]);
            }
            CollectionAssert.Contains(messages, "observe-info-entry");
            CollectionAssert.Contains(messages, "observe-warning-entry");
            var changes = (JArray)data["changes"]!;
            Assert.AreEqual(1, changes.Count, "the §F7 union is served with the logs");
            Assert.AreEqual(0, (int?)changes[0]!["step"]);
            Assert.AreEqual(1, (int?)changes[0]!["attempt"]);
            Assert.AreEqual("m", (string?)changes[0]!["nodeId"]);
            Assert.AreEqual("created", (string?)changes[0]!["action"]);
            Assert.AreEqual("asset", (string?)changes[0]!["target"]!["kind"]);
            Assert.AreEqual("Assets/Observe/created.txt", (string?)changes[0]!["target"]!["path"]);
            Assert.AreEqual(true, (bool?)changes[0]!["undoable"]);
        }

        // The level argument is the minimum severity served (§F6 default
        // info-and-above) and the step argument keeps one Plan step's
        // entries.
        [Test]
        public void Logs_FilterByLevelAndStep()
        {
            var fixture = NewFixture();
            fixture.Register(
                new EmitOperation(
                    OpEmit, fixture.Host,
                    writes: new[]
                    {
                        (Severity.Info, "step-zero-info", (JsonValue?)null),
                        (Severity.Warning, "step-zero-warning", (JsonValue?)null),
                    }),
                'a');
            fixture.Register(
                new EmitOperation(
                    "test.obs.emit2", fixture.Host,
                    writes: new[]
                    {
                        (Severity.Info, "step-one-info", (JsonValue?)null),
                    }),
                'e');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "e0", OpEmit, 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: 1),
                RunnerHostFixture.Step(1, "e1", "test.obs.emit2", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "e0", Operation = OpEmit, Version = 1 },
                    new NodeData { Id = "e1", Operation = "test.obs.emit2", Version = 1 },
                },
                new[] { new ControlEdge("e0", "e1") });
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Flush(fixture);
            var api = Api(fixture);

            var warnings = api.GetRunLogs(new GetRunLogsRequest(runId, Level: "warning"));
            Assert.IsTrue(warnings.Ok);
            var messages = new List<string>();
            var levels = new List<string>();
            foreach (var entry in (JArray)warnings.Data!["logs"]!)
            {
                messages.Add((string?)entry!["message"] ?? "");
                levels.Add((string?)entry!["level"] ?? "");
            }
            CollectionAssert.Contains(messages, "step-zero-warning");
            CollectionAssert.DoesNotContain(messages, "step-zero-info");
            CollectionAssert.DoesNotContain(levels, "info");

            var zero = api.GetRunLogs(new GetRunLogsRequest(runId, Step: 0));
            Assert.IsTrue(zero.Ok);
            var zeroMessages = new List<string>();
            foreach (var entry in (JArray)zero.Data!["logs"]!)
            {
                Assert.AreEqual(0, (int?)entry!["step"],
                    "the step filter keeps only that Plan step's entries");
                zeroMessages.Add((string?)entry["message"] ?? "");
            }
            CollectionAssert.Contains(zeroMessages, "step-zero-info");

            var one = api.GetRunLogs(new GetRunLogsRequest(runId, Step: 1));
            Assert.IsTrue(one.Ok);
            var oneMessages = new List<string>();
            foreach (var entry in (JArray)one.Data!["logs"]!)
            {
                Assert.AreEqual(1, (int?)entry!["step"]);
                oneMessages.Add((string?)entry["message"] ?? "");
            }
            CollectionAssert.Contains(oneMessages, "step-one-info");
            CollectionAssert.DoesNotContain(oneMessages, "step-zero-info");
        }

        // An approval-pending Run has not executed — its journal yields
        // no step-bound entries and an empty §F7 change union.
        [Test]
        public void LogsOfAwaitingApprovalRun_AreEmpty()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var plan = ApprovalPlan(OpEmit, 'a');
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "ask", Operation = OpEmit, Version = 1 } },
                Array.Empty<ControlEdge>());
            var runId = Accept(fixture, plan, doc);

            var response = Api(fixture).GetRunLogs(new GetRunLogsRequest(runId));

            Assert.IsTrue(response.Ok, "logs failed: " + Describe(response.Errors));
            foreach (var entry in (JArray)response.Data!["logs"]!)
            {
                Assert.IsNull(
                    entry!["step"], "nothing executed — no entry is step-bound");
            }
            Assert.AreEqual(0, ((JArray)response.Data!["changes"]!).Count,
                "no executed step — no durable changes");
        }

        // The 64 KiB cap pages the filtered log list through the signed
        // cursor; the §F7 union rides on every page.
        [Test]
        public void Logs_PageThroughCursor()
        {
            var fixture = NewFixture();
            var burst = new LogBurstOperation("test.obs.burst", count: 700);
            fixture.Register(burst, 'a');
            var (plan, doc) = SingleStep(WorkflowId, "test.obs.burst", 'a');
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Flush(fixture);
            var api = Api(fixture);

            var seqs = new List<long>();
            var pages = 0;
            string? cursor = null;
            do
            {
                var page = api.GetRunLogs(new GetRunLogsRequest(runId, Cursor: cursor));
                Assert.IsTrue(page.Ok, "page " + pages + " failed: " + Describe(page.Errors));
                foreach (var entry in (JArray)page.Data!["logs"]!)
                {
                    seqs.Add((long)entry!["seq"]!);
                }
                Assert.IsNotNull(page.Data!["changes"], "every page is self-contained");
                cursor = page.NextCursor;
                pages++;
            }
            while (cursor is not null && pages < 20);

            Assert.Greater(pages, 1, "a ~700-entry log stream must page under the 64 KiB cap");
            Assert.AreEqual(700, seqs.Count,
                "paging must deliver every entry exactly once");
            for (var i = 1; i < seqs.Count; i++)
            {
                Assert.Greater(seqs[i], seqs[i - 1], "the entries keep journal order");
            }
        }

        // The §F3 item 3 interrupted-write tail is the one journal
        // condition the read may serve through: the decoded prefix rides
        // with a warning instead of being presented as complete.
        [Test]
        public void Logs_InterruptedTail_ServesPrefixWithWarning()
        {
            var fixture = NewFixture();
            var marker = new EmitOperation(
                OpEmit, fixture.Host,
                writes: new[]
                {
                    (Severity.Info, "surviving-entry", (JsonValue?)null),
                });
            fixture.Register(marker, 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" });
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Flush(fixture);
            // The torn record an interrupted writer leaves behind — it
            // never committed, so §F3 item 3 drops it.
            File.AppendAllText(
                RunnerHostFixture.JournalPath(_root, runId),
                "{\"seq\":999,\"type\":\"log\",\"level\":\"info\",");

            var response = Api(fixture).GetRunLogs(new GetRunLogsRequest(runId));

            Assert.IsTrue(response.Ok, "the decodable prefix is still served");
            Assert.GreaterOrEqual(response.Warnings.Count, 1,
                "the dropped tail rides as a warning, never silently");
            var logs = (JArray)response.Data!["logs"]!;
            Assert.GreaterOrEqual(logs.Count, 1);
            Assert.AreEqual("surviving-entry", (string?)logs[0]!["message"]);
        }

        // ── get_run_logs: the §F6 SG-08 debug policy ─────────────────────────

        // Debug entries journaled while the policy allowed them are
        // suppressed at read time the moment the live debugLogging is
        // false — the ticket's "false/omitted" half.
        [Test]
        public void Logs_SuppressDebugWhenPolicyIsOff()
        {
            var fixture = NewFixture("{\"version\":2,\"debugLogging\":true}");
            var marker = new EmitOperation(
                OpEmit, fixture.Host,
                writes: new[] { (Severity.Info, "info-still-served", (JsonValue?)null) },
                debugMessage: "debug-must-stay-hidden");
            fixture.Register(marker, 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" });
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Flush(fixture);
            // The debug record is durable; only the live policy decides
            // whether the API serves it.
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"level\":\"debug\"", "debug-must-stay-hidden"),
                "the fixture must journal the debug record first");
            fixture.LoadSettings =
                () => OpWeaveSettingsLoader.Parse("{\"version\":2,\"debugLogging\":false}");

            var api = Api(fixture);
            var debugLevel = api.GetRunLogs(new GetRunLogsRequest(runId, Level: "debug"));
            Assert.IsTrue(debugLevel.Ok);
            foreach (var entry in (JArray)debugLevel.Data!["logs"]!)
            {
                Assert.AreNotEqual("debug", (string?)entry!["level"],
                    "the §F6 SG-08 filter suppresses debug while the setting is off");
                StringAssert.DoesNotContain(
                    "debug-must-stay-hidden", (string?)entry["message"] ?? "");
            }
            var anyLevel = api.GetRunLogs(new GetRunLogsRequest(runId));
            Assert.IsTrue(anyLevel.Ok);
            foreach (var entry in (JArray)anyLevel.Data!["logs"]!)
            {
                Assert.AreNotEqual("debug", (string?)entry!["level"]);
            }
        }

        // With debugLogging:true the debug entry is served — but only
        // when the caller asks for the debug floor, and always through
        // the sanitized §F6 records.
        [Test]
        public void Logs_ServeDebugOnlyWhenPolicyPermits()
        {
            var fixture = NewFixture("{\"version\":2,\"debugLogging\":true}");
            var marker = new EmitOperation(
                OpEmit, fixture.Host,
                writes: new[] { (Severity.Info, "info-still-served", (JsonValue?)null) },
                debugMessage: "debug-now-authorized",
                debugData: RunnerHostFixture.Json("{\"detail\":\"payload\"}"));
            fixture.Register(marker, 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" });
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Flush(fixture);
            var api = Api(fixture);

            var debug = api.GetRunLogs(new GetRunLogsRequest(runId, Level: "debug"));
            Assert.IsTrue(debug.Ok);
            var found = false;
            foreach (var entry in (JArray)debug.Data!["logs"]!)
            {
                if ((string?)entry!["level"] == "debug")
                {
                    found = true;
                    Assert.AreEqual("debug-now-authorized", (string?)entry["message"]);
                    Assert.AreEqual(0, (int?)entry["step"]);
                    Assert.AreEqual(
                        "payload", (string?)entry["data"]!["detail"],
                        "the sanitized entry keeps its structured data");
                }
            }
            Assert.IsTrue(found, "the authorized debug entry must be served");

            // The default minimum level is info — even an authorized
            // debug record stays below an omitted level filter.
            var defaultLevel = api.GetRunLogs(new GetRunLogsRequest(runId));
            Assert.IsTrue(defaultLevel.Ok);
            foreach (var entry in (JArray)defaultLevel.Data!["logs"]!)
            {
                Assert.AreNotEqual("debug", (string?)entry!["level"],
                    "the §F6 default of info-and-above still applies");
            }
        }

        // Credentials are masked on the served records: URL userinfo and
        // the token/password/secret/authorization keys all collapse to
        // "***" before the Agent ever sees them (SPEC §F6).
        [Test]
        public void Logs_MaskCredentialsInMessagesAndData()
        {
            var fixture = NewFixture();
            var marker = new EmitOperation(
                OpEmit, fixture.Host,
                writes: new[]
                {
                    (Severity.Info,
                        "clone https://user:s3cretP4ss@example.com/repo.git finished",
                        (JsonValue?)null),
                    (Severity.Info, "credentials-follow",
                        RunnerHostFixture.Json(
                            "{\"token\":\"abc123XYZ\",\"password\":\"pw-value\"," +
                            "\"secret\":\"shh-secret\",\"authorization\":\"Bearer zzz\"}")),
                });
            fixture.Register(marker, 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" });
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Flush(fixture);

            var response = Api(fixture).GetRunLogs(new GetRunLogsRequest(runId));
            Assert.IsTrue(response.Ok);
            var json = response.ToJson();
            foreach (var secret in new[]
                     {
                         "s3cretP4ss", "abc123XYZ", "pw-value", "shh-secret", "Bearer zzz",
                     })
            {
                StringAssert.DoesNotContain(secret, json,
                    "the credential must never reach the API response");
            }
            StringAssert.Contains("https://***@example.com", json,
                "URL userinfo collapses to the masked form");
            StringAssert.Contains("\"token\":\"***\"", json,
                "sensitive-keyed values collapse to ***");
        }

        // No Plan or code text is emitted by either observation command:
        // a constant step input planted in the Plan never appears in a
        // response, whatever the verbosity.
        [Test]
        public void ObservationResponses_NeverEmitPlanText()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                inputs: new Dictionary<string, InputSource>
                {
                    ["text"] = new Constant(
                        RunnerHostFixture.Json("\"PLAN-TEXT-MARKER-7f3d\"")),
                });
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Flush(fixture);
            var api = Api(fixture);

            var full = api.GetRunStatus(new GetRunStatusRequest(runId));
            var compact = api.GetRunStatus(new GetRunStatusRequest(runId, Verbosity: "compact"));
            var logs = api.GetRunLogs(new GetRunLogsRequest(runId));
            var list = api.ListRuns(new ListRunsRequest(Limit: 10));
            Assert.IsTrue(full.Ok && compact.Ok && logs.Ok && list.Ok);
            foreach (var response in new[] { full, compact, logs, list })
            {
                StringAssert.DoesNotContain(
                    "PLAN-TEXT-MARKER-7f3d", response.ToJson(),
                    "Plan text is never auto-emitted by the observation API");
            }
        }

        // ── list_runs ────────────────────────────────────────────────────────

        // Runs accepted after the tracker exists enrol through their E8
        // events; the state and workflowId filters select the matching
        // subset with the journaled timestamps.
        [Test]
        public void ListRuns_FiltersStateAndWorkflow()
        {
            var fixture = NewFixture();
            var api = Api(fixture);
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');

            // The finished Run completes before the still-queued ones are
            // accepted — a Tick would advance every Run the coordinator
            // holds, not just the one being driven.
            var (alphaPlan2, alphaDoc2) = SingleStep("example.observe.alpha", OpEmit, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" });
            var alphaDone = Accept(fixture, alphaPlan2, alphaDoc2);
            Drive(fixture.Host, alphaDone);
            var (alphaPlan, alphaDoc) = SingleStep("example.observe.alpha", OpEmit, 'a');
            var alphaQueued = Accept(fixture, alphaPlan, alphaDoc);
            var (betaPlan, betaDoc) = SingleStep("example.observe.beta", OpEmit, 'a');
            var betaQueued = Accept(fixture, betaPlan, betaDoc);

            var all = api.ListRuns(new ListRunsRequest(Limit: 10));
            Assert.IsTrue(all.Ok, "list failed: " + Describe(all.Errors));
            var ids = new List<string>();
            foreach (var run in (JArray)all.Data!["runs"]!)
            {
                ids.Add((string?)run!["runId"] ?? "");
                Assert.IsNotNull(run["state"]);
                Assert.IsNotNull(run["createdAt"]);
                Assert.IsNotNull(run["updatedAt"]);
                Assert.IsNotNull(run["workflowId"]);
            }
            CollectionAssert.AreEquivalent(
                new[] { alphaQueued, alphaDone, betaQueued }, ids);

            var queued = api.ListRuns(new ListRunsRequest(State: "queued", Limit: 10));
            Assert.IsTrue(queued.Ok);
            var queuedIds = new List<string>();
            foreach (var run in (JArray)queued.Data!["runs"]!)
            {
                queuedIds.Add((string?)run!["runId"] ?? "");
                Assert.AreEqual("queued", (string?)run["state"]);
            }
            CollectionAssert.AreEquivalent(new[] { alphaQueued, betaQueued }, queuedIds);

            var alpha = api.ListRuns(
                new ListRunsRequest(WorkflowId: "example.observe.alpha", Limit: 10));
            Assert.IsTrue(alpha.Ok);
            var alphaIds = new List<string>();
            foreach (var run in (JArray)alpha.Data!["runs"]!)
            {
                alphaIds.Add((string?)run!["runId"] ?? "");
            }
            CollectionAssert.AreEquivalent(new[] { alphaQueued, alphaDone }, alphaIds);

            var combined = api.ListRuns(new ListRunsRequest(
                State: "succeeded", WorkflowId: "example.observe.alpha", Limit: 10));
            Assert.IsTrue(combined.Ok);
            var combinedRuns = (JArray)combined.Data!["runs"]!;
            Assert.AreEqual(1, combinedRuns.Count);
            Assert.AreEqual(alphaDone, (string?)combinedRuns[0]!["runId"]);
        }

        // The required limit pages the listing through the signed
        // cursor; every run arrives exactly once.
        [Test]
        public void ListRuns_PagesWithLimitAndCursor()
        {
            var fixture = NewFixture();
            var api = Api(fixture);
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var runIds = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a');
                runIds.Add(Accept(fixture, plan, doc));
            }

            var seen = new List<string>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var page = api.ListRuns(new ListRunsRequest(Limit: 2, Cursor: cursor));
                Assert.IsTrue(page.Ok, "page failed: " + Describe(page.Errors));
                foreach (var run in (JArray)page.Data!["runs"]!)
                {
                    seen.Add((string?)run!["runId"] ?? "");
                }
                cursor = page.NextCursor;
                pages++;
            }
            while (cursor is not null && pages < 10);

            Assert.AreEqual(2, pages, "limit 2 over 3 runs yields exactly two pages");
            CollectionAssert.AreEquivalent(runIds, seen);
        }

        // An empty store lists an empty page — not an error.
        [Test]
        public void ListRuns_EmptyStore_ReturnsEmptyPage()
        {
            var fixture = NewFixture();
            var response = Api(fixture).ListRuns(new ListRunsRequest(Limit: 5));

            Assert.IsTrue(response.Ok, "list failed: " + Describe(response.Errors));
            Assert.AreEqual(0, ((JArray)response.Data!["runs"]!).Count);
            Assert.IsNull(response.NextCursor);
        }

        // ── Error mapping (SPEC §J) ──────────────────────────────────────────

        [Test]
        public void UnknownRunId_EveryLookupIsOwRun010()
        {
            var fixture = NewFixture();
            var api = Api(fixture);
            const string missing = "run_01J00000000000000000000000";

            Assert.AreEqual(
                "OW-RUN-010",
                ErrorCode(api.GetRunStatus(new GetRunStatusRequest(missing))));
            Assert.AreEqual(
                "OW-RUN-010",
                ErrorCode(api.GetRunLogs(new GetRunLogsRequest(missing))));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not-a-run")]
        [TestCase("run_tooshort")]
        public void MalformedRunId_EveryLookupIsOwApi001(string? runId)
        {
            var fixture = NewFixture();
            var api = Api(fixture);

            var status = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsFalse(status.Ok);
            Assert.AreEqual("OW-API-001", ErrorCode(status));
            Assert.AreEqual("$.runId", ErrorPath(status));
            var logs = api.GetRunLogs(new GetRunLogsRequest(runId));
            Assert.IsFalse(logs.Ok);
            Assert.AreEqual("OW-API-001", ErrorCode(logs));
            Assert.AreEqual("$.runId", ErrorPath(logs));
        }

        [TestCase(-1)]
        [TestCase(21)]
        public void WaitSecondsOutsideRange_IsOwApi001(int waitSeconds)
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a');
            var runId = Accept(fixture, plan, doc);

            var response = Api(fixture).GetRunStatus(
                new GetRunStatusRequest(runId, WaitSeconds: waitSeconds));

            Assert.IsFalse(response.Ok);
            Assert.AreEqual("OW-API-001", ErrorCode(response));
            Assert.AreEqual("$.waitSeconds", ErrorPath(response));
        }

        [Test]
        public void UnknownVerbosity_IsOwApi001()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a');
            var runId = Accept(fixture, plan, doc);

            var response = Api(fixture).GetRunStatus(
                new GetRunStatusRequest(runId, Verbosity: "verbose"));

            Assert.IsFalse(response.Ok);
            Assert.AreEqual("OW-API-001", ErrorCode(response));
            Assert.AreEqual("$.verbosity", ErrorPath(response));
        }

        [Test]
        public void UnknownLogLevel_IsOwApi001()
        {
            var fixture = NewFixture();
            var runId = "run_01J00000000000000000000000";
            var response = Api(fixture).GetRunLogs(
                new GetRunLogsRequest(runId, Level: "trace"));

            Assert.IsFalse(response.Ok);
            Assert.AreEqual("OW-API-001", ErrorCode(response));
            Assert.AreEqual("$.level", ErrorPath(response));
        }

        [Test]
        public void NegativeLogStep_IsOwApi001()
        {
            var fixture = NewFixture();
            var runId = "run_01J00000000000000000000000";
            var response = Api(fixture).GetRunLogs(
                new GetRunLogsRequest(runId, Step: -1));

            Assert.IsFalse(response.Ok);
            Assert.AreEqual("OW-API-001", ErrorCode(response));
            Assert.AreEqual("$.step", ErrorPath(response));
        }

        [Test]
        public void ListRuns_MissingOrNonPositiveLimit_IsOwApi001()
        {
            var fixture = NewFixture();
            var api = Api(fixture);

            foreach (var request in new ListRunsRequest[]
                     {
                         new ListRunsRequest(),
                         new ListRunsRequest(Limit: 0),
                         new ListRunsRequest(Limit: -3),
                     })
            {
                var response = api.ListRuns(request);
                Assert.IsFalse(response.Ok);
                Assert.AreEqual("OW-API-001", ErrorCode(response));
                Assert.AreEqual("$.limit", ErrorPath(response));
            }
        }

        [Test]
        public void ListRuns_UnknownState_IsOwApi001()
        {
            var fixture = NewFixture();
            var response = Api(fixture).ListRuns(
                new ListRunsRequest(State: "sleeping", Limit: 5));

            Assert.IsFalse(response.Ok);
            Assert.AreEqual("OW-API-001", ErrorCode(response));
            Assert.AreEqual("$.state", ErrorPath(response));
        }

        [Test]
        public void InvalidCursor_IsOwApi002()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a');
            var runId = Accept(fixture, plan, doc);
            var api = Api(fixture);

            var logs = api.GetRunLogs(
                new GetRunLogsRequest(runId, Cursor: "not-a-real-cursor"));
            Assert.IsFalse(logs.Ok);
            Assert.AreEqual("OW-API-002", ErrorCode(logs));
            var list = api.ListRuns(
                new ListRunsRequest(Limit: 5, Cursor: "not-a-real-cursor"));
            Assert.IsFalse(list.Ok);
            Assert.AreEqual("OW-API-002", ErrorCode(list));
        }

        // ── production installation (SPEC §H1) ───────────────────────────

        // The production installer — the same Install call the domain entry
        // point performs over OpWeaveRunnerHost.Current — builds the tracker,
        // Run Store and journal seams over a real RunnerHost: the installed
        // AgentApiHost.Api answers the observation commands for a real Run
        // with no test-only construction.
        [Test]
        public void InstalledService_AnswersObservationCommands()
        {
            var fixture = NewFixture();
            fixture.Register(new EmitOperation(OpEmit, fixture.Host), 'a');
            var (plan, doc) = SingleStep(WorkflowId, OpEmit, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" });
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));

            var installed = AgentApiInstaller.Install(fixture.Host);
            try
            {
                Assert.AreSame(installed, AgentApiHost.Api,
                    "the constructed service is the one the host serves");

                var status = AgentApiHost.Api.GetRunStatus(
                    new GetRunStatusRequest(runId));
                Assert.IsTrue(status.Ok, "status failed: " + Describe(status.Errors));
                Assert.AreEqual(runId, (string?)status.Data!["runId"]);
                Assert.AreEqual("succeeded", (string?)status.Data!["state"]);

                var list = AgentApiHost.Api.ListRuns(new ListRunsRequest(Limit: 10));
                Assert.IsTrue(list.Ok, "list failed: " + Describe(list.Errors));
                var ids = new List<string>();
                foreach (var run in (JArray)list.Data!["runs"]!)
                {
                    ids.Add((string?)run!["runId"] ?? "");
                }
                CollectionAssert.Contains(ids, runId);

                var logs = AgentApiHost.Api.GetRunLogs(new GetRunLogsRequest(runId));
                Assert.IsTrue(logs.Ok, "logs failed: " + Describe(logs.Errors));
                Assert.IsNotNull(logs.Data!["changes"],
                    "the installed journaled-change read answers");
            }
            finally
            {
                AgentApiInstaller.Reset();
            }
        }

        // ── Fixture operations ───────────────────────────────────────────────

        // An Immediate that writes the requested log entries, one §F7
        // created-change per path and — through the host's production
        // debug seam — the debug diagnostic the §F6 SG-08 policy gates.
        private sealed class EmitOperation : IOperation
        {
            private readonly OpWeaveRunnerHost _host;
            private readonly IReadOnlyList<(Severity Level, string Message, JsonValue? Data)>
                _writes;
            private readonly IReadOnlyList<string> _changePaths;
            private readonly string? _debugMessage;
            private readonly JsonValue? _debugData;

            internal EmitOperation(
                string id,
                OpWeaveRunnerHost host,
                IReadOnlyList<(Severity Level, string Message, JsonValue? Data)>? writes = null,
                IReadOnlyList<string>? changePaths = null,
                string? debugMessage = null,
                JsonValue? debugData = null)
            {
                _host = host;
                _writes = writes ?? Array.Empty<(Severity, string, JsonValue?)>();
                _changePaths = changePaths ?? Array.Empty<string>();
                _debugMessage = debugMessage;
                _debugData = debugData;
                Descriptor = Op.Define(id, 1)
                    .Summary("Emits log entries, changes and a debug diagnostic.")
                    .Input("text", "core.String", required: false)
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
                foreach (var (level, message, data) in _writes)
                {
                    ctx.Log.Write(level, message, data: data);
                }
                if (_debugMessage is not null)
                {
                    _host.DebugLog(ctx.Log, _debugMessage, data: _debugData);
                }
                foreach (var path in _changePaths)
                {
                    ctx.Changes.Created(new ChangeTarget("asset", path), undoable: true);
                }
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

        // An Immediate that writes a fixed number of sizeable info
        // entries — the ≥64 KiB log stream the cursor paging test needs.
        private sealed class LogBurstOperation : IOperation
        {
            private readonly int _count;
            private readonly string _pad;

            internal LogBurstOperation(string id, int count, int padLength = 100)
            {
                _count = count;
                _pad = new string('x', padLength);
                Descriptor = Op.Define(id, 1)
                    .Summary("Writes many log entries.")
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
                for (var i = 0; i < _count; i++)
                {
                    ctx.Log.Write(Severity.Info, "burst-" + i.ToString("D4") + "-" + _pad);
                }
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
    }
}
