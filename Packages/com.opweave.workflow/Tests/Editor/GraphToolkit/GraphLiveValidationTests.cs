#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.4-01 [Edit] tests (SPEC §H8 item 1, §D1, §D4): the debounced live
// validation — a graph change arms the 300 ms quiet window, the deferred
// pass exports the graph through §H7 and runs the §D static validation,
// and each diagnostic lands on the responsible node as a GraphLogger
// error/warning. A type-mismatched data connection surfaces OW-TYPE-002 on
// the target node, and removing the wire clears it. Graph Toolkit hands
// OnGraphChanged a logger whose errorsAndWarnings sink is internal to the
// toolkit module, so the suite drives the same public IErrorsAndWarnings
// shape the production LoggerSink adapter forwards to; the clock, the
// deferral and the processing re-trigger are injected for a deterministic
// debounce. Below 6000.5 the OpWeave.GraphToolkit asmdef is excluded and
// this whole file compiles to nothing.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.GraphToolkit;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class GraphLiveValidationTests
    {
        private const string TempDir = "Assets/GraphLiveValidationTests";
        private const string TempPath = TempDir + "/graph." + OpWeaveGraph.AssetExtension;

        private Func<RegistrySnapshot?>? _previousSource;
        private Func<double>? _previousClock;
        private Action<double, Action>? _previousSchedule;
        private Action<OpWeaveGraph>? _previousRequestProcessing;
        private Func<Result<RunPolicyEnvironment>?>? _previousPolicyEnvironment;

        private double _now;
        private Action? _deferred;
        private RecordingSink _sink = null!;
        private RegistrySnapshot? _snapshot;

        [SetUp]
        public void SetUp()
        {
            _snapshot = BuildSnapshot();
            _previousSource = GraphToolkitRegistry.SnapshotSource;
            var snapshot = _snapshot;
            GraphToolkitRegistry.SnapshotSource = () => snapshot;

            _previousClock = GraphLiveValidation.Clock;
            _previousSchedule = GraphLiveValidation.Schedule;
            _previousRequestProcessing = GraphLiveValidation.RequestProcessing;
            _previousPolicyEnvironment = GraphLiveValidation.PolicyEnvironment;

            _now = 0;
            _deferred = null;
            _sink = new RecordingSink();
            GraphLiveValidation.Clock = () => _now;
            GraphLiveValidation.Schedule = (_, action) => _deferred = action;
            // The deferred pass arrives as another processing pass: simulate
            // the framework re-invoking the change callback.
            GraphLiveValidation.RequestProcessing =
                graph => GraphLiveValidation.OnGraphChanged(graph, _sink);
            GraphLiveValidation.PolicyEnvironment = () =>
                Result<RunPolicyEnvironment>.Success(
                    new RunPolicyEnvironment(OpWeaveSettings.Default));
        }

        [TearDown]
        public void TearDown()
        {
            if (_previousSource is not null)
            {
                GraphToolkitRegistry.SnapshotSource = _previousSource;
            }
            if (_previousClock is not null)
            {
                GraphLiveValidation.Clock = _previousClock;
            }
            if (_previousSchedule is not null)
            {
                GraphLiveValidation.Schedule = _previousSchedule;
            }
            if (_previousRequestProcessing is not null)
            {
                GraphLiveValidation.RequestProcessing = _previousRequestProcessing;
            }
            if (_previousPolicyEnvironment is not null)
            {
                GraphLiveValidation.PolicyEnvironment = _previousPolicyEnvironment;
            }
            _previousSource = null;
            _previousClock = null;
            _previousSchedule = null;
            _previousRequestProcessing = null;
            _previousPolicyEnvironment = null;
            if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
            {
                AssetDatabase.DeleteAsset(TempPath);
            }
            if (AssetDatabase.IsValidFolder(TempDir))
            {
                AssetDatabase.DeleteAsset(TempDir);
            }
        }

        // ── SPEC §H8 test: mismatched connection → OW-TYPE-002 on the
        //    target node; fixing the connection clears the diagnostic ────

        [Test]
        public void MismatchedConnection_ShowsOwType002OnTarget_ThenClears()
        {
            WithGraph(graph =>
            {
                graph.OnEnable();
                var start = new StartNode { nodeId = "start" };
                var producer = new OperationNode
                {
                    operationId = "test.lv.producer",
                    version = 1,
                    nodeId = "nProducer",
                };
                var consumer = new OperationNode
                {
                    operationId = "test.lv.consumer",
                    version = 1,
                    nodeId = "nConsumer",
                };
                graph.AddNode(start);
                graph.AddNode(producer);
                graph.AddNode(consumer);
                Assert.IsTrue(graph.Connect(
                    start.GetOutputPortByName(StartNode.NextOutputName),
                    consumer.GetInputPortByName(OperationNode.ControlInputName)),
                    "control edge start → consumer");
                var output = producer.GetOutputPortByName("ref");
                var input = consumer.GetInputPortByName("target");
                Assert.IsTrue(graph.Connect(output, input),
                    "mismatched OwValue ports still connect — the type check "
                    + "is H8's validation job (SPEC §H6.3)");

                // 1. Real change → quiet window armed; the deferred pass is
                //    the one that validates and emits.
                GraphLiveValidation.OnGraphChanged(graph, _sink);
                Assert.IsNotNull(_deferred, "a change must arm the debounce");
                Assert.IsEmpty(_sink.Errors,
                    "no validation inside the quiet window itself");

                _now += GraphLiveValidation.DebounceSeconds;
                _deferred!();

                var typeError = _sink.Errors
                    .Where(e => e.Message.Contains("OW-TYPE-002"))
                    .ToList();
                Assert.AreEqual(1, typeError.Count,
                    "exactly one OW-TYPE-002 must be reported");
                Assert.AreSame(consumer, typeError[0].Context,
                    "OW-TYPE-002 must land on the target node");

                // 2. Fix the problem — remove the mismatched wire — and the
                //    badge must clear after the next quiet window.
                Assert.IsTrue(graph.Disconnect(output, input));
                GraphLiveValidation.OnGraphChanged(graph, _sink);
                Assert.IsNotNull(_deferred, "the fix re-arms the debounce");

                _sink.Clear();
                _now += GraphLiveValidation.DebounceSeconds;
                _deferred!();

                Assert.IsFalse(
                    _sink.Entries.Any(e => e.Message.Contains("OW-TYPE-002")),
                    "fixing the connection must clear OW-TYPE-002");
                Assert.IsFalse(
                    graph.latestDiagnostics.Any(d => d.Code == "OW-TYPE-002"),
                    "the stored diagnostics must not keep OW-TYPE-002");
            });
        }

        [Test]
        public void Debounce_NewerChangePushesTheQuietWindowOut()
        {
            WithGraph(graph =>
            {
                graph.OnEnable();
                var producer = new OperationNode
                {
                    operationId = "test.lv.producer",
                    version = 1,
                    nodeId = "nProducer",
                };
                var consumer = new OperationNode
                {
                    operationId = "test.lv.consumer",
                    version = 1,
                    nodeId = "nConsumer",
                };
                graph.AddNode(producer);
                graph.AddNode(consumer);
                graph.Connect(
                    producer.GetOutputPortByName("ref"),
                    consumer.GetInputPortByName("target"));

                GraphLiveValidation.OnGraphChanged(graph, _sink);
                Assert.IsNotNull(_deferred);

                // A second change at t+0.2 pushes the deadline to t+0.5;
                // firing the first scheduled action early must re-arm, not
                // validate.
                _now += GraphLiveValidation.DebounceSeconds - 0.1;
                GraphLiveValidation.OnGraphChanged(graph, _sink);
                _deferred!();
                Assert.IsEmpty(_sink.Errors,
                    "validation must not run before the quiet window elapses");
                Assert.IsNotNull(_deferred, "the early fire must re-arm");

                _now += GraphLiveValidation.DebounceSeconds;
                _deferred!();
                Assert.IsTrue(
                    _sink.Errors.Any(e => e.Message.Contains("OW-TYPE-002")),
                    "validation runs once the quiet window has elapsed");
            });
        }

        [Test]
        public void ProductionOverride_DrivesDebounceAndDeferredPass()
        {
            // R1: the required behavior must hold through the PRODUCTION
            // callback — OpWeaveGraph.OnGraphChanged(GraphLogger), the
            // override Graph Toolkit invokes — not only the internal
            // IErrorsAndWarnings overload the other tests drive.  A null
            // logger is legal at this seam (Emit no-ops on a null sink)
            // and the diagnostic state is asserted on the graph itself.
            WithGraph(graph =>
            {
                GraphLiveValidation.RequestProcessing =
                    g => g.OnGraphChanged(null!);
                graph.OnEnable();
                var start = new StartNode { nodeId = "start" };
                var producer = new OperationNode
                {
                    operationId = "test.lv.producer",
                    version = 1,
                    nodeId = "nProducer",
                };
                var consumer = new OperationNode
                {
                    operationId = "test.lv.consumer",
                    version = 1,
                    nodeId = "nConsumer",
                };
                graph.AddNode(start);
                graph.AddNode(producer);
                graph.AddNode(consumer);
                graph.Connect(
                    start.GetOutputPortByName(StartNode.NextOutputName),
                    consumer.GetInputPortByName(OperationNode.ControlInputName));
                graph.Connect(
                    producer.GetOutputPortByName("ref"),
                    consumer.GetInputPortByName("target"));

                // The override itself must arm the debounce — no direct
                // GraphLiveValidation call happens in production.
                graph.OnGraphChanged(null!);
                Assert.IsNotNull(_deferred,
                    "the production OnGraphChanged must arm the quiet window");

                _now += GraphLiveValidation.DebounceSeconds;
                _deferred!();
                Assert.IsTrue(
                    graph.latestDiagnostics.Any(d => d.Code == "OW-TYPE-002"),
                    "the deferred pass must store OW-TYPE-002 on the graph");
            });
        }

        [UnityTest]
        public IEnumerator ProductionTrigger_DeferredPass_DeliversAndClears()
        {
            // R1 (r5): retain the PRODUCTION trigger end-to-end — the
            // real delayCall scheduler pump, the real editor clock, and
            // the production DefaultRequestProcessing that — finding no
            // view to consume its empty-undo-record poke — issues the
            // identical GraphProcessingHelper.ProcessGraph call a
            // GraphViewEditorWindow would.  Nothing in the debounce →
            // poke → processing → callback → emit chain is substituted:
            // ProcessGraph builds the live GraphLogger, invokes the
            // production OnGraphChanged(GraphLogger) callback, and Emit
            // delivers through the real LoggerSink into the logger's
            // errorsAndWarnings — the toolkit's own received diagnostic
            // state, the feed a view materializes node badges from (the
            // last windowless-observable boundary; markers themselves
            // are view-bound and cannot exist in batchmode).  The test
            // goes red if the production trigger never causes a toolkit
            // pass, if the callback or LoggerSink is bypassed, or if the
            // entry misses the consumer node's model.
            GraphLiveValidation.Clock = _previousClock!;
            GraphLiveValidation.Schedule = _previousSchedule!;
            GraphLiveValidation.RequestProcessing =
                _previousRequestProcessing!;

            var folderCreated = false;
            if (!AssetDatabase.IsValidFolder(TempDir))
            {
                AssetDatabase.CreateFolder(
                    "Assets", "GraphLiveValidationTests");
                folderCreated = true;
            }
            if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
            {
                AssetDatabase.DeleteAsset(TempPath);
            }
            var graph = GraphDatabase.CreateGraph<OpWeaveGraph>(TempPath);
            Assert.IsNotNull(
                graph, "GraphDatabase.CreateGraph must return a graph");
            try
            {
                var (producer, consumer) = WireMismatch(graph!);
                graph!.OnEnable();
                graph.OnGraphChanged(null!);

                // The real debounce is 300 ms of editor time and the
                // delayCall pump drains on editor updates — real time
                // must pass while editor frames run.
                yield return new WaitForSecondsRealtime(0.8f);
                yield return null;
                yield return null;

                Assert.IsTrue(
                    graph.latestDiagnostics.Any(
                        d => d.Code == "OW-TYPE-002"),
                    "the production trigger must deliver OW-TYPE-002 "
                    + "through the live GraphLogger pass");
                Assert.IsNotNull(
                    graph.lastEmission,
                    "the deferred pass must emit into a live sink, "
                    + "not a null sink");
                Assert.IsTrue(
                    graph.lastEmissionUsedLoggerSink,
                    "the deferred pass must emit through the real "
                    + "LoggerSink → GraphLogger delivery");
                Assert.IsNotNull(
                    graph.lastProcessingLogger,
                    "the production callback must have received a live "
                    + "GraphLogger from a real processing pass");
                Assert.IsFalse(
                    graph.validationLog.Entries.Any(),
                    "the validation log must stay empty — its use would "
                    + "mean the toolkit pass was unreachable and the "
                    + "degraded fallback emitted");
                var typeErrors = ToolkitErrors(graph)
                    .Where(e => e.description.Contains("OW-TYPE-002"))
                    .ToList();
                Assert.AreEqual(1, typeErrors.Count,
                    "the toolkit's processing pass must record exactly "
                    + "one OW-TYPE-002 in its errorsAndWarnings feed");
                Assert.AreEqual(
                    NodeModelGuid(consumer), typeErrors[0].sourceGuid,
                    "OW-TYPE-002 must be recorded against the consumer "
                    + "node's model — the state the badge binds to");

                Assert.IsTrue(graph.Disconnect(
                    producer.GetOutputPortByName("ref"),
                    consumer.GetInputPortByName("target")));
                graph.OnGraphChanged(null!);
                yield return new WaitForSecondsRealtime(0.8f);
                yield return null;
                yield return null;

                Assert.IsFalse(
                    graph.latestDiagnostics.Any(
                        d => d.Code == "OW-TYPE-002"),
                    "fixing the connection must clear the badge through "
                    + "the same production chain");
                Assert.IsNotNull(
                    graph.lastEmission,
                    "the clearing pass must also emit into a live sink");
                Assert.IsTrue(
                    graph.lastEmissionUsedLoggerSink,
                    "the clearing pass must emit through the real "
                    + "LoggerSink → GraphLogger delivery");
                Assert.IsFalse(
                    ToolkitErrors(graph).Any(
                        e => e.description.Contains("OW-TYPE-002")),
                    "OW-TYPE-002 must be gone from the toolkit's "
                    + "errorsAndWarnings feed (the badge clears)");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempPath);
                if (folderCreated)
                {
                    AssetDatabase.DeleteAsset(TempDir);
                }
            }
        }

        [Test]
        public void Disable_ThenReenable_DoesNotReEmitStaleDiagnostics()
        {
            // R2: latestDiagnostics survives a disable today — a
            // re-enabled graph's first change would re-emit badges the
            // new lifecycle never proved.  OnDisable must drop the set.
            WithGraph(graph =>
            {
                graph.OnEnable();
                var start = new StartNode { nodeId = "start" };
                var producer = new OperationNode
                {
                    operationId = "test.lv.producer",
                    version = 1,
                    nodeId = "nProducer",
                };
                var consumer = new OperationNode
                {
                    operationId = "test.lv.consumer",
                    version = 1,
                    nodeId = "nConsumer",
                };
                graph.AddNode(start);
                graph.AddNode(producer);
                graph.AddNode(consumer);
                graph.Connect(
                    start.GetOutputPortByName(StartNode.NextOutputName),
                    consumer.GetInputPortByName(OperationNode.ControlInputName));
                graph.Connect(
                    producer.GetOutputPortByName("ref"),
                    consumer.GetInputPortByName("target"));

                GraphLiveValidation.OnGraphChanged(graph, _sink);
                _now += GraphLiveValidation.DebounceSeconds;
                _deferred!();
                Assert.IsTrue(graph.latestDiagnostics.Any(),
                    "precondition: diagnostics were proven this lifecycle");

                graph.OnDisable();
                Assert.IsEmpty(graph.latestDiagnostics,
                    "OnDisable must drop the stale badge set");
                graph.OnEnable();
                _sink.Clear();
                GraphLiveValidation.OnGraphChanged(graph, _sink);
                Assert.IsEmpty(_sink.Entries,
                    "a re-enabled graph must not re-emit unproven badges");
            });
        }

        [Test]
        public void ToolkitProcessing_DeliversOwType002ToTargetNodeModel()
        {
            // R1 (r4): drive the deferred pass through the REAL
            // GraphProcessingHelper.ProcessGraph — the identical call a
            // GraphViewEditorWindow makes when it consumes the
            // empty-undo-record poke.  That is the real toolkit
            // processing pass: it builds the live GraphLogger, invokes
            // OpWeaveGraph.OnGraphChanged(GraphLogger) — the production
            // callback — and our Emit delivers through the real
            // LoggerSink into the logger's errorsAndWarnings, which is
            // the exact feed the view turns into node badges.  The
            // toolkit's own received diagnostic state
            // (GraphProcessingError.Description / SourceModelGuid) is
            // asserted on the consumer node's model.
            WithGraph(graph =>
            {
                graph.OnEnable();
                var (producer, consumer) = WireMismatch(graph);

                GraphLiveValidation.RequestProcessing = ProcessGraphViaToolkit;

                GraphLiveValidation.OnGraphChanged(graph, _sink);
                Assert.IsNotNull(_deferred, "a change must arm the debounce");
                _now += GraphLiveValidation.DebounceSeconds;
                _deferred!();

                Assert.IsFalse(
                    graph.validationPokePending,
                    "the toolkit processing pass must consume the poke");
                Assert.IsNotNull(
                    graph.lastProcessingLogger,
                    "the deferred pass must run through the production "
                    + "OnGraphChanged(GraphLogger) callback");
                var errors = ToolkitErrors(graph).ToList();
                var typeError = errors
                    .Where(e => e.description.Contains("OW-TYPE-002"))
                    .ToList();
                Assert.AreEqual(1, typeError.Count,
                    "the toolkit's processing pass must record exactly "
                    + "one OW-TYPE-002");
                Assert.AreEqual(
                    NodeModelGuid(consumer), typeError[0].sourceGuid,
                    "OW-TYPE-002 must target the consumer node's model");

                // Fixing the wire clears the toolkit diagnostic through
                // the same real processing chain.
                Assert.IsTrue(graph.Disconnect(
                    producer.GetOutputPortByName("ref"),
                    consumer.GetInputPortByName("target")));
                GraphLiveValidation.OnGraphChanged(graph, _sink);
                _now += GraphLiveValidation.DebounceSeconds;
                _deferred!();
                errors = ToolkitErrors(graph).ToList();
                Assert.IsFalse(
                    errors.Any(e => e.description.Contains("OW-TYPE-002")),
                    "the toolkit's diagnostic state must clear OW-TYPE-002 "
                    + "after the wire is removed");
            });
        }

        // Reflection plumbing for ToolkitProcessing_*: invokes the real
        // GraphProcessingHelper.ProcessGraph(graphModel, changeset,
        // options) — the call the toolkit window issues — and reads the
        // toolkit-internal errorsAndWarnings feed the view materializes
        // badges from.  Toolkit internals are not public API, so the
        // test reaches them by reflection; the assertions bind only the
        // observable contract (Description text + SourceModelGuid).
        private static void ProcessGraphViaToolkit(OpWeaveGraph graph)
        {
            var gtAsm = typeof(Unity.GraphToolkit.Editor.Graph).Assembly;
            var model = ImplementationField(graph.GetType())!.GetValue(graph);
            var changeset = Activator.CreateInstance(
                gtAsm.GetType(
                    "Unity.GraphToolkit.Editor.GraphModelStateComponent+Changeset")!);
            var optType = gtAsm.GetType(
                "Unity.GraphToolkit.Editor.RequestGraphProcessingOptions")!;
            var options = optType.GetProperty(
                    "Default",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null) ?? Activator.CreateInstance(optType, true);
            gtAsm.GetType("Unity.GraphToolkit.Editor.GraphProcessingHelper")!
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .First(m => m.Name == "ProcessGraph")
                .Invoke(null, new[] { model, changeset, options });
        }

        private static IEnumerable<(string description, string sourceGuid)>
            ToolkitErrors(OpWeaveGraph graph)
        {
            var logger = graph.lastProcessingLogger;
            if (logger is null)
            {
                yield break;
            }
            var feed = logger.GetType().GetProperty(
                    "errorsAndWarnings",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(logger);
            var errors = feed?.GetType().GetProperty("Errors")?.GetValue(feed)
                as IEnumerable;
            if (errors is null)
            {
                yield break;
            }
            foreach (var e in errors)
            {
                var et = e.GetType();
                var description = et.GetProperty("Description")?.GetValue(e)?.ToString() ?? "";
                var guid = et.GetProperty("SourceModelGuid")?.GetValue(e);
                yield return (description, guid?.ToString() ?? "");
            }
        }

        private static string NodeModelGuid(INode node)
        {
            var model = ImplementationField(node.GetType())?.GetValue(node);
            var guid = model?.GetType().GetProperty("guid")?.GetValue(model)
                ?? model?.GetType().GetProperty("Guid")?.GetValue(model)
                ?? model?.GetType().GetField("guid")?.GetValue(model)
                ?? model?.GetType().GetField("m_Guid")?.GetValue(model);
            return guid?.ToString() ?? "";
        }

        // The m_Implementation field lives on the toolkit base types —
        // walk the hierarchy for it.
        private static FieldInfo? ImplementationField(Type type)
        {
            for (var t = type; t is not null; t = t.BaseType)
            {
                var f = t.GetField(
                    "m_Implementation",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (f is not null)
                {
                    return f;
                }
            }
            return null;
        }

        // ── fixtures ────────────────────────────────────────────────────

        private sealed class Entry
        {
            internal string Level = "";
            internal string Message = "";
            internal object? Context;
        }

        private sealed class RecordingSink : IErrorsAndWarnings
        {
            internal readonly List<Entry> Entries = new List<Entry>();

            internal IEnumerable<Entry> Errors =>
                Entries.Where(e => e.Level == "error");

            internal void Clear() => Entries.Clear();

            public void LogError(object message, object context) =>
                Entries.Add(new Entry
                {
                    Level = "error", Message = message.ToString() ?? "", Context = context,
                });

            public void LogWarning(object message, object context) =>
                Entries.Add(new Entry
                {
                    Level = "warning", Message = message.ToString() ?? "", Context = context,
                });

            public void Log(object message, object context) =>
                Entries.Add(new Entry
                {
                    Level = "info", Message = message.ToString() ?? "", Context = context,
                });
        }

        // Builds the start → consumer control edge plus the mismatched
        // producer.ref → consumer.target OwValue wire every live-
        // validation fixture needs; returns (producer, consumer) so the
        // caller can re-fetch the ports for a later Disconnect.
        private static (OperationNode producer, OperationNode consumer)
            WireMismatch(OpWeaveGraph graph)
        {
            var start = new StartNode { nodeId = "start" };
            var producer = new OperationNode
            {
                operationId = "test.lv.producer",
                version = 1,
                nodeId = "nProducer",
            };
            var consumer = new OperationNode
            {
                operationId = "test.lv.consumer",
                version = 1,
                nodeId = "nConsumer",
            };
            graph.AddNode(start);
            graph.AddNode(producer);
            graph.AddNode(consumer);
            graph.Connect(
                start.GetOutputPortByName(StartNode.NextOutputName),
                consumer.GetInputPortByName(OperationNode.ControlInputName));
            graph.Connect(
                producer.GetOutputPortByName("ref"),
                consumer.GetInputPortByName("target"));
            return (producer, consumer);
        }

        private static void WithGraph(Action<OpWeaveGraph> body)
        {
            var folderCreated = false;
            var assetCreated = false;
            try
            {
                if (!AssetDatabase.IsValidFolder(TempDir))
                {
                    AssetDatabase.CreateFolder("Assets", "GraphLiveValidationTests");
                    folderCreated = true;
                }
                if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
                {
                    AssetDatabase.DeleteAsset(TempPath);
                }
                var graph = GraphDatabase.CreateGraph<OpWeaveGraph>(TempPath);
                assetCreated = graph is not null;
                Assert.IsNotNull(graph, "GraphDatabase.CreateGraph must return a graph");
                body(graph!);
            }
            finally
            {
                if (assetCreated)
                {
                    AssetDatabase.DeleteAsset(TempPath);
                }
                if (folderCreated)
                {
                    AssetDatabase.DeleteAsset(TempDir);
                }
            }
        }

        private static RegistrySnapshot BuildSnapshot()
        {
            var descriptors = new[]
            {
                // Emits a PrefabAssetRef — not assignable to a GameObjectRef
                // input, so a wire between the two must surface OW-TYPE-002.
                Op.Define("test.lv.producer", 1)
                    .Summary("Producer fixture emitting a PrefabAssetRef.")
                    .Output("ref", "unity.PrefabAssetRef")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                Op.Define("test.lv.consumer", 1)
                    .Summary("Consumer fixture requiring a GameObjectRef.")
                    .Input("target", "unity.GameObjectRef", required: true)
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
            };
            var operations = descriptors
                .Select((d, i) => new RegisteredOperation(
                    d,
                    OperationOrigin.Custom,
                    "OpWeave.UnityAdapter.Tests",
                    null,
                    new string((char)('a' + i), 64),
                    new string((char)('A' + i), 64))
                {
                    // OperationValidationPhase refuses a snapshot entry
                    // without an implementation instance; the fixture ops
                    // need a no-op one (OW-TYPE-002 comes from the
                    // data-binding phase, not ValidateStatic).
                    Implementation = new NoopOperation(d),
                })
                .ToArray();
            return new RegistrySnapshot(
                "reg_01J00000000000000000000000",
                new string('f', 64),
                operations,
                Types,
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());
        }

        private sealed class NoopOperation : IOperation
        {
            internal NoopOperation(OperationDescriptor descriptor) =>
                Descriptor = descriptor;

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        private static ITypeRegistry? s_types;

        private static ITypeRegistry Types =>
            s_types ??= TypeRegistryBuilder.Build(
                    new[]
                    {
                        new TypeProviderCandidate(
                            CoreTypeProvider.ProviderId,
                            new CoreTypeProvider(),
                            OperationOrigin.Standard),
                        new TypeProviderCandidate(
                            UnityTypeProvider.ProviderId,
                            new UnityTypeProvider(),
                            OperationOrigin.Standard),
                    },
                    new UnityClassRelationProvider())
                .Registry;
    }
}
#endif
