#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.4-02 [Edit] fixtures (SPEC §H8 items 2–3, §H4 item 4,
// local:T-W5.4-02:gui-actions-display): the seven graph actions on a
// fixture graph carrying an ask effect — Validate, Dry Run, Run, Cancel,
// Export JSON, Import JSON, open Run Viewer — the pre-start confirmation
// panel that approves every ask together (accept runs; decline starts no
// Run and surfaces OW-PERM-003), and the §E8 execution display that
// repaints each node's DefaultColor live while a tracked Run reports
// step states. The runner host is the real OpWeaveRunnerHost over a
// probe Run Store; the confirmation, the open-viewer command and the
// emit-channel trigger are the injected seams the actions define. Below
// 6000.5 the OpWeave.GraphToolkit asmdef is excluded and this whole file
// compiles to nothing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.GraphToolkit;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class GraphActionsDisplayTests
    {
        private const string TempDir = "Assets/GraphActionsDisplayTests";
        private const string TempPath = TempDir + "/graph." + OpWeaveGraph.AssetExtension;
        private const string TempJsonPath = TempDir + "/graph.opweave.json";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private Func<RegistrySnapshot?>? _previousSource;
        private Func<Result<RunPolicyEnvironment>?>? _previousPolicyEnvironment;
        private Action<OpWeaveGraph>? _previousRequestProcessing;
        private Action<double, Action>? _previousSchedule;
        private Func<OpWeaveRunnerHost?>? _previousRunnerHost;
        private Func<RunApprovalDetail, bool>? _previousConfirm;
        private Action? _previousOpenRunViewer;

        private RunnerHostFixture _fixture = null!;
        private AskDeleteOperation _askOp = null!;
        private PlannedChangeOperation _planOp = null!;
        private RunnerHostFixture.MarkerOperation _markerOp = null!;
        private RunnerHostFixture.LabelWaitableOperation _waitOp = null!;
        private readonly List<string> _events = new List<string>();
        private RecordingSink _sink = new RecordingSink();
        private Action? _deferred;
        private bool _confirmAnswer;
        private RunApprovalDetail? _confirmDetail;
        private int _confirmCalls;
        private int _runViewerOpens;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "GraphActionsDisplayTests",
                "t" + Guid.NewGuid().ToString("N"));
            _fixture = new RunnerHostFixture(_root);
            _fixtures.Add(_fixture);

            // The fixture Registry: core.start as a Standard registration
            // like production, the ticket's fixture Operations as Custom.
            var start = new StartOperation();
            _fixture.Operations.Add(new RegisteredOperation(
                start.Descriptor, OperationOrigin.Standard, "OpWeave.Operations",
                null, new string('a', 64), new string('b', 64))
            {
                Implementation = start,
            });
            _askOp = new AskDeleteOperation();
            _planOp = new PlannedChangeOperation();
            _markerOp = new RunnerHostFixture.MarkerOperation("test.ga.mark", _events);
            _waitOp = new RunnerHostFixture.LabelWaitableOperation("test.ga.wait");
            _fixture.Register(_askOp, 'c');
            _fixture.Register(_planOp, 'e');
            _fixture.Register(_markerOp, '1');
            _fixture.Register(_waitOp, '3');
            _fixture.Register(TypeMismatchProducer(), '5');
            _fixture.Register(TypeMismatchConsumer(), '7');
            _fixture.Register(CompatibleSupplier(), '9');

            _previousSource = GraphToolkitRegistry.SnapshotSource;
            GraphToolkitRegistry.SnapshotSource = () => _fixture.Snapshot();
            _previousPolicyEnvironment = GraphLiveValidation.PolicyEnvironment;
            GraphLiveValidation.PolicyEnvironment =
                () => _fixture.Host.CurrentRunPolicyEnvironment();
            _previousRequestProcessing = GraphLiveValidation.RequestProcessing;
            GraphLiveValidation.RequestProcessing =
                graph => GraphLiveValidation.OnGraphChanged(graph, _sink);
            _previousSchedule = GraphLiveValidation.Schedule;
            GraphLiveValidation.Schedule = (_, action) => _deferred = action;
            _previousRunnerHost = GraphActions.RunnerHost;
            GraphActions.RunnerHost = () => _fixture.Host;
            _previousConfirm = GraphActions.ConfirmAskEffects;
            _confirmAnswer = true;
            _confirmDetail = null;
            _confirmCalls = 0;
            GraphActions.ConfirmAskEffects = detail =>
            {
                _confirmCalls++;
                _confirmDetail = detail;
                return _confirmAnswer;
            };
            _previousOpenRunViewer = GraphActions.OpenRunViewerCommand;
            _runViewerOpens = 0;
            GraphActions.OpenRunViewerCommand = () => _runViewerOpens++;
        }

        [TearDown]
        public void TearDown()
        {
            if (_previousSource is not null)
            {
                GraphToolkitRegistry.SnapshotSource = _previousSource;
            }
            if (_previousPolicyEnvironment is not null)
            {
                GraphLiveValidation.PolicyEnvironment = _previousPolicyEnvironment;
            }
            if (_previousRequestProcessing is not null)
            {
                GraphLiveValidation.RequestProcessing = _previousRequestProcessing;
            }
            if (_previousSchedule is not null)
            {
                GraphLiveValidation.Schedule = _previousSchedule;
            }
            if (_previousRunnerHost is not null)
            {
                GraphActions.RunnerHost = _previousRunnerHost;
            }
            if (_previousConfirm is not null)
            {
                GraphActions.ConfirmAskEffects = _previousConfirm;
            }
            if (_previousOpenRunViewer is not null)
            {
                GraphActions.OpenRunViewerCommand = _previousOpenRunViewer;
            }
            GraphJsonSync.OpenChoiceProvider = null;
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
            if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
            {
                AssetDatabase.DeleteAsset(TempPath);
            }
            if (AssetDatabase.LoadMainAssetAtPath(TempJsonPath) is not null ||
                File.Exists(TempJsonPath))
            {
                AssetDatabase.DeleteAsset(TempJsonPath);
            }
            if (AssetDatabase.IsValidFolder(TempDir))
            {
                AssetDatabase.DeleteAsset(TempDir);
            }
        }

        // ── Validate (SPEC §H8 item 2) ─────────────────────────────────

        [Test]
        public void Validate_FailingGraph_PublishesNodeDiagnostic_ThenPasses()
        {
            WithGraph(graph =>
            {
                var (producer, consumer) = WireMismatch(graph);

                var failed = GraphActions.Validate(graph);
                Assert.IsFalse(failed.Ok, "a type-mismatched graph must not validate");
                Assert.IsTrue(
                    failed.Diagnostics.Any(
                        d => d.Code == "OW-TYPE-002" && d.NodeId == "nConsumer"),
                    "the action must carry the OW-TYPE-002 diagnostic for nConsumer");

                // The diagnostics were published through the same emit
                // channel the deferred pass uses (SPEC §H8 item 1–2).
                Assert.IsTrue(
                    _sink.Entries.Any(e =>
                        e.Level == "error" && e.Message.Contains("OW-TYPE-002")),
                    "the action must publish its diagnostics onto the emit channel");
                Assert.IsTrue(
                    graph.latestDiagnostics.Any(d => d.Code == "OW-TYPE-002"));

                // The repair a user performs on this graph: drop the
                // mismatched wire, then finish the workflow — the
                // consumer's required 'target' still needs a supplier
                // and every node still needs a control edge to be
                // reachable from start (OW-VAL-005 / OW-VAL-002;
                // SPEC §A1, §D5). The chain becomes
                // start → nSupplier → nConsumer → nProducer so the
                // supplier runs before the consumer it feeds.
                Assert.IsTrue(graph.Disconnect(
                    producer.GetOutputPortByName("ref"),
                    consumer.GetInputPortByName("target")));
                var start = graph.GetNodes().OfType<StartNode>().Single();
                var supplier = new OperationNode
                {
                    operationId = "test.ga.supplier",
                    version = 1,
                    nodeId = "nSupplier",
                };
                graph.AddNode(supplier);
                Assert.IsTrue(graph.Disconnect(
                    start.GetOutputPortByName(StartNode.NextOutputName),
                    consumer.GetInputPortByName(OperationNode.ControlInputName)),
                    "control edge start → consumer");
                Assert.IsTrue(graph.Connect(
                    start.GetOutputPortByName(StartNode.NextOutputName),
                    supplier.GetInputPortByName(OperationNode.ControlInputName)),
                    "control edge start → supplier");
                Assert.IsTrue(graph.Connect(
                    supplier.GetOutputPortByName("next"),
                    consumer.GetInputPortByName(OperationNode.ControlInputName)),
                    "control edge supplier → consumer");
                Assert.IsTrue(graph.Connect(
                    consumer.GetOutputPortByName("next"),
                    producer.GetInputPortByName(OperationNode.ControlInputName)),
                    "control edge consumer → producer");
                Assert.IsTrue(graph.Connect(
                    supplier.GetOutputPortByName("ref"),
                    consumer.GetInputPortByName("target")),
                    "the compatible supplier feeds 'target'");
                var passed = GraphActions.Validate(graph);
                Assert.IsTrue(passed.Ok, "the repaired graph must validate: "
                    + Describe(passed));
                Assert.AreEqual("Validation passed.", passed.Message);
            });
        }

        // ── Dry Run (SPEC §H8 item 2, §D8) ─────────────────────────────

        [Test]
        public void DryRun_ReportsPlannedChanges_WithoutExecuting()
        {
            WithGraph(graph =>
            {
                WireChain(graph, ("nPlanned", "test.ga.plan"));

                var result = GraphActions.DryRun(graph);

                Assert.IsTrue(result.Ok, "the dry run must pass: " + Describe(result));
                StringAssert.Contains("1 planned change(s)", result.Message);
                StringAssert.Contains("guarantee: none", result.Message,
                    "the SPEC §D8 guarantee marker must be reported");
                Assert.Greater(_planOp.PreflightCalls, 0,
                    "the dry run must drive Preflight");
                Assert.AreEqual(
                    0, _planOp.ExecuteCalls,
                    "a dry run never executes the Operation");
            });
        }

        // ── Run (SPEC §H8 item 2, §H4 item 4) ──────────────────────────

        [Test]
        public void Run_NoAsks_AcceptsWithoutConfirmation()
        {
            WithGraph(graph =>
            {
                WireChain(graph, ("nMark", "test.ga.mark"));
                _confirmAnswer = false;

                var result = GraphActions.Run(graph);

                Assert.AreEqual(0, _confirmCalls,
                    "a Plan without asks must never reach the panel");
                Assert.IsTrue(result.Ok, Describe(result));
                Assert.IsNotNull(result.RunId);
                Assert.AreEqual(
                    RunState.Queued,
                    _fixture.Host.Coordinator.StateOf(result.RunId!));
                RunUntilTerminal(result.RunId!);
                Assert.AreEqual(
                    RunState.Succeeded,
                    _fixture.Host.Coordinator.StateOf(result.RunId!));
                CollectionAssert.Contains(_events, "execute:test.ga.mark");
            });
        }

        [Test]
        public void Run_WithAsks_Approve_AllAsksListed_RunsAndPaintsNodeStates()
        {
            WithGraph(graph =>
            {
                var ask = WireChain(graph, ("nAsk", "test.ga.ask"));
                _confirmAnswer = true;

                var result = GraphActions.Run(graph);

                // SPEC §H4 item 4: the pre-start panel is asked exactly
                // once and lists every ask effect together.
                Assert.AreEqual(1, _confirmCalls,
                    "the confirmation must be asked exactly once");
                var detail = _confirmDetail!;
                Assert.AreEqual(1, detail.Nodes.Count,
                    "every ask node must be listed together");
                Assert.AreEqual("nAsk", detail.Nodes[0].NodeId);
                CollectionAssert.Contains(
                    detail.Nodes[0].ApprovalEffects.ToList(), EffectNames.AssetDelete);
                Assert.IsEmpty(detail.RunId,
                    "the panel decides before any Run exists");

                Assert.IsTrue(result.Ok, Describe(result));
                var runId = result.RunId!;
                Assert.AreEqual(
                    RunState.Queued,
                    _fixture.Host.Coordinator.StateOf(runId),
                    "the approved Run must leave AwaitingApproval");
                Assert.IsTrue(
                    RunnerHostFixture.JournalHas(_root, runId, "approval_granted"),
                    "the panel's approval must land on the journal's "
                    + "human-only path (SPEC §H4 item 3)");
                Assert.AreEqual(runId, GraphExecutionDisplay.RunIdOf(graph),
                    "the execution display must track the accepted Run");

                RunUntilTerminal(runId);
                Assert.AreEqual(
                    RunState.Succeeded,
                    _fixture.Host.Coordinator.StateOf(runId));
                Assert.AreEqual(1, _askOp.ExecuteCalls);

                // SPEC §H8 item 3: the per-node step states were painted
                // live and the windowless record kept them.
                var states = graph.executionLog
                    .Where(e => e.nodeId == "nAsk")
                    .Select(e => e.state)
                    .ToList();
                CollectionAssert.Contains(states, StepState.Running);
                CollectionAssert.Contains(states, StepState.Succeeded);
                Assert.AreEqual(
                    GraphExecutionDisplay.ColorFor(StepState.Succeeded),
                    ask.DefaultColor,
                    "the node must carry its terminal step-state colour");
                Assert.IsNull(
                    GraphExecutionDisplay.RunIdOf(graph),
                    "a terminal Run detaches the display");
            });
        }

        [Test]
        public void Run_WithAsks_Decline_StartsNoRun_AndReportsPerm003()
        {
            WithGraph(graph =>
            {
                WireChain(graph, ("nAsk", "test.ga.ask"));
                _confirmAnswer = false;

                var result = GraphActions.Run(graph);

                Assert.AreEqual(1, _confirmCalls);
                Assert.IsFalse(result.Ok, "a declined Run must not start");
                Assert.IsTrue(
                    result.Diagnostics.Any(d => d.Code == "OW-PERM-003"),
                    "the declined path surfaces the existing OW-PERM-003");
                var runs = _fixture.Host.Store.List(new RunQuery());
                Assert.IsTrue(runs.Ok);
                Assert.IsEmpty(runs.Value!,
                    "declining must not start or persist any Run");
                Assert.IsNull(
                    GraphExecutionDisplay.RunIdOf(graph),
                    "a declined Run attaches no display");
            });
        }

        [Test]
        public void Run_InvalidGraph_RefusesBeforeAccept()
        {
            WithGraph(graph =>
            {
                WireMismatch(graph);

                var result = GraphActions.Run(graph);

                Assert.IsFalse(result.Ok);
                Assert.AreEqual(0, _confirmCalls,
                    "a graph that does not validate never reaches the panel");
                var runs = _fixture.Host.Store.List(new RunQuery());
                Assert.IsTrue(runs.Ok);
                Assert.IsEmpty(runs.Value!);
                Assert.IsTrue(
                    graph.latestDiagnostics.Any(d => d.Code == "OW-TYPE-002"),
                    "the refusal's diagnostics are published onto the nodes");
            });
        }

        // ── Cancel (SPEC §H8 item 2) ───────────────────────────────────

        [Test]
        public void Cancel_NoTrackedRun_Fails_RunningRun_IsCancelled()
        {
            WithGraph(graph =>
            {
                var none = GraphActions.Cancel(graph);
                Assert.IsFalse(none.Ok, "Cancel without a tracked Run must refuse");

                WireChain(graph, ("nWait", "test.ga.wait"));
                var run = GraphActions.Run(graph);
                Assert.IsTrue(run.Ok, Describe(run));
                var runId = run.RunId!;

                // Drive until the waitable step is in flight, then cancel.
                RunSteps(runId, 4);
                var cancelled = GraphActions.Cancel(graph);
                Assert.IsTrue(cancelled.Ok, cancelled.Message);
                Assert.AreEqual(runId, cancelled.RunId);
                Assert.IsTrue(
                    RunnerHostFixture.JournalHas(_root, runId, "cancel_requested"),
                    "the GUI cancel must record the journaled request");

                RunUntilTerminal(runId);
                Assert.AreEqual(
                    RunState.Cancelled,
                    _fixture.Host.Coordinator.StateOf(runId));
                Assert.IsNull(GraphExecutionDisplay.RunIdOf(graph));
            });
        }

        // ── Export / Import JSON (SPEC §H8 item 2, §H7 item 3) ─────────

        [Test]
        public void ExportThenImport_RoundTripsTheJson()
        {
            WithGraph(graph =>
            {
                WireChain(graph, ("nAsk", "test.ga.ask"));

                var export = GraphActions.ExportJson(graph);
                Assert.IsTrue(export.Ok, export.Message);
                Assert.IsFalse(
                    string.IsNullOrEmpty(graph.jsonPath),
                    "the save must link the graph to its JSON");
                Assert.IsTrue(File.Exists(graph.jsonPath),
                    "the save must write the JSON file");

                // An external canonical edit — as an Agent edit would be —
                // produces the §H7.3 choice; reloading applies it.
                var text = File.ReadAllText(graph.jsonPath);
                StringAssert.Contains("\"nAsk\"", text);
                File.WriteAllText(
                    graph.jsonPath, text.Replace("\"nAsk\"", "\"nRenamed\""));
                GraphJsonSync.OpenChoiceProvider =
                    _ => GraphJsonSyncChoice.ReloadJson;
                var import = GraphActions.ImportJson(graph);

                Assert.IsTrue(import.Ok, import.Message);
                var ids = NodeIds(graph);
                CollectionAssert.Contains(ids, "nRenamed");
                CollectionAssert.DoesNotContain(ids, "nAsk");
            });
        }

        [Test]
        public void ImportJson_UnlinkedGraph_Refuses()
        {
            WithGraph(graph =>
            {
                var import = GraphActions.ImportJson(graph);
                Assert.IsFalse(import.Ok);
                StringAssert.Contains("no linked JSON", import.Message);
            });
        }

        // ── Open Run Viewer (SPEC §H8 item 2, §H5) ─────────────────────

        [Test]
        public void OpenRunViewer_InvokesTheCommand()
        {
            var result = GraphActions.OpenRunViewer();
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(1, _runViewerOpens,
                "the action must drive the Run Viewer entry once");
        }

        // ── Execution display (SPEC §H8 item 3, §E8) ───────────────────

        [Test]
        public void Display_PaintsPerNodeStates_UnknownNode_UsesInfoFallback()
        {
            WithGraph(graph =>
            {
                var ask = WireChain(graph, ("nAsk", "test.ga.ask"));
                var events = new StubRunEvents();
                const string runId = "run_01JTESTDISPLAY00000000000";

                GraphExecutionDisplay.Attach(graph, events, runId);
                Assert.AreEqual(runId, GraphExecutionDisplay.RunIdOf(graph));

                // An event for a node the graph does not carry cannot be
                // coloured: it degrades to the GraphLogger-info fallback.
                events.PublishStep(
                    new StepStateChanged(runId, 0, "nMissing", StepState.Running, 1));
                Assert.AreEqual(1, graph.pendingDisplayInfos.Count);
                Assert.AreEqual(1, graph.executionLog.Count);

                // The queued info drains through the next processing pass's
                // sink — the same channel the badges ride.
                GraphLiveValidation.OnGraphChanged(graph, _sink);
                Assert.IsTrue(
                    _sink.Entries.Any(e =>
                        e.Level == "info" && e.Message.Contains("nMissing")),
                    "the unknown node must surface as a GraphLogger info");
                Assert.IsEmpty(graph.pendingDisplayInfos,
                    "the drain must consume the queued fallback");

                // An event for a present node repaints it live.
                events.PublishStep(
                    new StepStateChanged(runId, 1, "nAsk", StepState.Running, 1));
                Assert.AreEqual(
                    GraphExecutionDisplay.ColorFor(StepState.Running),
                    ask.DefaultColor);
                events.PublishStep(
                    new StepStateChanged(runId, 1, "nAsk", StepState.Succeeded, 1));
                Assert.AreEqual(
                    GraphExecutionDisplay.ColorFor(StepState.Succeeded),
                    ask.DefaultColor);

                // Events of another Run are ignored.
                var before = graph.executionLog.Count;
                events.PublishStep(new StepStateChanged(
                    "run_01JOTHER00000000000000", 0, "nAsk", StepState.Running, 1));
                Assert.AreEqual(before, graph.executionLog.Count);

                // The terminal Run state unsubscribes.
                events.PublishRun(new RunStateChanged(runId, RunState.Succeeded));
                Assert.IsNull(GraphExecutionDisplay.RunIdOf(graph));
                events.PublishStep(
                    new StepStateChanged(runId, 9, "nAsk", StepState.Failed, 2));
                Assert.AreEqual(
                    GraphExecutionDisplay.ColorFor(StepState.Succeeded),
                    ask.DefaultColor,
                    "a detached display must stop painting");
            });
        }

        // ── fixtures ────────────────────────────────────────────────────

        // Chains start → each (nodeId, operationId) in order; returns the
        // last added node.
        private static OperationNode WireChain(
            OpWeaveGraph graph, params (string nodeId, string operationId)[] nodes)
        {
            var start = new StartNode { nodeId = "start" };
            graph.AddNode(start);
            var previous = start.GetOutputPortByName(StartNode.NextOutputName);
            OperationNode last = null!;
            foreach (var (nodeId, operationId) in nodes)
            {
                var node = new OperationNode
                {
                    operationId = operationId,
                    version = 1,
                    nodeId = nodeId,
                };
                graph.AddNode(node);
                Assert.IsTrue(graph.Connect(
                    previous,
                    node.GetInputPortByName(OperationNode.ControlInputName)),
                    "control edge → " + nodeId);
                // The §A4 control chain: a v1 Operation's single control
                // output is named "next" (OperationDescriptorBuilder).
                previous = node.GetOutputPortByName("next");
                last = node;
            }
            return last;
        }

        // The start → consumer control edge plus the mismatched
        // producer.ref → consumer.target OwValue wire — the OW-TYPE-002
        // fixture shared with the live-validation suite's shape.
        private static (OperationNode producer, OperationNode consumer)
            WireMismatch(OpWeaveGraph graph)
        {
            var consumer = WireChain(graph, ("nConsumer", "test.ga.consumer"));
            var producer = new OperationNode
            {
                operationId = "test.ga.producer",
                version = 1,
                nodeId = "nProducer",
            };
            graph.AddNode(producer);
            Assert.IsTrue(graph.Connect(
                producer.GetOutputPortByName("ref"),
                consumer.GetInputPortByName("target")),
                "mismatched OwValue ports still connect — the type check "
                + "is validation's job (SPEC §H6.3)");
            return (producer, consumer);
        }

        private static List<string> NodeIds(OpWeaveGraph graph)
        {
            var ids = new List<string>();
            foreach (var node in graph.GetNodes())
            {
                var nodeId = GraphJsonConverter.NodeIdOf(node);
                if (nodeId is not null)
                {
                    ids.Add(nodeId);
                }
            }
            return ids;
        }

        private void RunSteps(string runId, int maxTicks)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var tick = _fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + tick.Diagnostics.Count);
            }
        }

        private void RunUntilTerminal(string runId)
        {
            for (var i = 0; i < 20; i++)
            {
                var state = _fixture.Host.Coordinator.StateOf(runId);
                if (state is RunState.Succeeded or RunState.Failed or RunState.Cancelled)
                {
                    return;
                }
                var tick = _fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed");
            }
        }

        private static string Describe(GraphActionResult result) =>
            result.Message + " [" + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)) + "]";

        private void WithGraph(Action<OpWeaveGraph> body)
        {
            var folderCreated = false;
            var assetCreated = false;
            try
            {
                if (!AssetDatabase.IsValidFolder(TempDir))
                {
                    AssetDatabase.CreateFolder("Assets", "GraphActionsDisplayTests");
                    folderCreated = true;
                }
                if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
                {
                    AssetDatabase.DeleteAsset(TempPath);
                }
                var graph = GraphDatabase.CreateGraph<OpWeaveGraph>(TempPath);
                assetCreated = graph is not null;
                Assert.IsNotNull(graph, "GraphDatabase.CreateGraph must return a graph");
                graph!.OnEnable();
                body(graph);
            }
            finally
            {
                if (assetCreated)
                {
                    AssetDatabase.DeleteAsset(TempPath);
                }
                if (AssetDatabase.LoadMainAssetAtPath(TempJsonPath) is not null ||
                    File.Exists(TempJsonPath))
                {
                    AssetDatabase.DeleteAsset(TempJsonPath);
                }
                if (folderCreated)
                {
                    AssetDatabase.DeleteAsset(TempDir);
                }
            }
        }

        // The ask-effect fixture of local:T-W5.4-02: asset.delete is an
        // ask for the human policy under the default settings (SPEC §H4,
        // §H4.1).
        private sealed class AskDeleteOperation : IOperation
        {
            internal AskDeleteOperation()
            {
                Descriptor = Op.Define("test.ga.ask", 1)
                    .Summary("Ask fixture declaring asset.delete.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.AssetDelete)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }
            internal int ExecuteCalls { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                ctx.SetOutput("done", true);
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

        // The §D8 dry-run fixture: Preflight reports a planned change the
        // action must surface without executing it.
        private sealed class PlannedChangeOperation : IOperation
        {
            internal PlannedChangeOperation()
            {
                Descriptor = Op.Define("test.ga.plan", 1)
                    .Summary("Dry-run fixture reporting a planned change.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }
            internal int PreflightCalls { get; private set; }
            internal int ExecuteCalls { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx)
            {
                PreflightCalls++;
                return new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    new[]
                    {
                        new PlannedChange(
                            "nPlanned", "asset.write",
                            "Assets/dryrun.txt", EffectNames.AssetModify),
                    },
                    RunnerHostFixture.Json("{}"));
            }

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
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

        // The OW-TYPE-002 pair: a PrefabAssetRef output is not assignable
        // to a GameObjectRef input.
        private static TypedPortsOperation TypeMismatchProducer() =>
            new TypedPortsOperation("test.ga.producer", "unity.PrefabAssetRef", null);

        private static TypedPortsOperation TypeMismatchConsumer() =>
            new TypedPortsOperation("test.ga.consumer", null, "unity.GameObjectRef");

        // The repair-side supplier: its 'ref' output carries the very
        // GameObjectRef the consumer's required 'target' demands — the
        // compatible data edge that clears OW-VAL-005 once the
        // mismatched wire is gone.
        private static TypedPortsOperation CompatibleSupplier() =>
            new TypedPortsOperation("test.ga.supplier", "unity.GameObjectRef", null);

        private sealed class TypedPortsOperation : IOperation
        {
            internal TypedPortsOperation(string id, string? outputType, string? inputType)
            {
                var define = Op.Define(id, 1)
                    .Summary("Typed-ports fixture.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent);
                if (outputType is not null)
                {
                    define.Output("ref", outputType);
                }
                if (inputType is not null)
                {
                    define.Input("target", inputType, required: true);
                }
                Descriptor = define.Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException();

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();

            public void Cancel(StepContext ctx)
            {
            }
        }

        private sealed class StubRunEvents : IRunEvents
        {
            public event Action<RunStateChanged>? RunStateChanged;
            public event Action<StepStateChanged>? StepStateChanged;
#pragma warning disable CS0067 // interface members the display never consumes
            public event Action<RunLogEntry>? LogAppended;
            public event Action<IReadOnlyList<RegistryChange>>? RegistryChanged;
#pragma warning restore CS0067

            internal void PublishStep(StepStateChanged change) =>
                StepStateChanged?.Invoke(change);

            internal void PublishRun(RunStateChanged change) =>
                RunStateChanged?.Invoke(change);
        }

        private sealed class Entry
        {
            internal string Level = "";
            internal string Message = "";
            internal object? Context;
        }

        private sealed class RecordingSink : IErrorsAndWarnings
        {
            internal readonly List<Entry> Entries = new List<Entry>();

            public void LogError(object message, object context) =>
                Entries.Add(new Entry
                {
                    Level = "error", Message = message?.ToString() ?? "", Context = context,
                });

            public void LogWarning(object message, object context) =>
                Entries.Add(new Entry
                {
                    Level = "warning", Message = message?.ToString() ?? "", Context = context,
                });

            public void Log(object message, object context) =>
                Entries.Add(new Entry
                {
                    Level = "info", Message = message?.ToString() ?? "", Context = context,
                });
        }
    }
}
#endif
