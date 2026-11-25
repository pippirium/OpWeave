#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.2-02 [Edit] tests (SPEC §H6 item 5, §C6, §E8): the add-Operation
// picker runs the §C6 search — Japanese and English queries alike — over
// the published snapshot and every result carries its origin; a pick
// creates an OperationNode whose ports equal the Descriptor. On
// RegistryChanged every live graph's OperationNode ports are redefined
// and a node whose Operation left the Registry surfaces OW-OP-001 as its
// error display without the graph or node being lost. Below 6000.5 the
// OpWeave.GraphToolkit asmdef is excluded and this whole file compiles to
// nothing.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.Core.Search;
using OpWeave.GraphToolkit;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class GraphOperationSearchRefreshTests
    {
        private const string TempDir = "Assets/GraphSearchRefreshTests";
        private const string TempPath = TempDir + "/graph." + OpWeaveGraph.AssetExtension;

        private Func<RegistrySnapshot?>? _previousSource;
        private Func<string?>? _previousLanguage;
        private Func<IRunEvents?>? _previousEventsSource;
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

            _previousLanguage = GraphOperationPicker.DefaultLanguage;
            GraphOperationPicker.DefaultLanguage = () => "en";
            _previousEventsSource = GraphRegistryRefresh.EventsSource;
            GraphRegistryRefresh.EventsSource = () => null;

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

            // Graph objects another suite's test enabled and forgot to
            // disable stay in the live set — drain them so a
            // RegistryChanged refresh touches only this suite's graphs.
            foreach (var leaked in OpWeaveGraphTracker.Live)
            {
                OpWeaveGraphTracker.Unregister(leaked);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (_previousSource is not null)
            {
                GraphToolkitRegistry.SnapshotSource = _previousSource;
            }
            if (_previousLanguage is not null)
            {
                GraphOperationPicker.DefaultLanguage = _previousLanguage;
            }
            GraphRegistryRefresh.Unsubscribe();
            if (_previousEventsSource is not null)
            {
                GraphRegistryRefresh.EventsSource = _previousEventsSource;
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
            _previousLanguage = null;
            _previousEventsSource = null;
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

        // ── SPEC §H6.5 / ticket acceptance: searching 「プレハブ」 offers
        //    the Prefab Operations with their origin ───────────────────────

        [Test]
        public void Search_JapaneseQuery_ListsPrefabOperationWithOrigin()
        {
            var page = GraphOperationPicker.Search(new OperationSearchRequest
            {
                Query = "プレハブ",
            }).Value!;

            var hit = page.Items
                .SingleOrDefault(i => i.Operation.Descriptor.Id == "test.gui.prefab_create");
            Assert.IsNotNull(hit,
                "「プレハブ」 must offer the Prefab Operation (SPEC §H6.5)");
            // The origin the picker row displays (SPEC §H6.5 origin 表示).
            Assert.AreEqual(OperationOrigin.Standard, hit!.Operation.Origin);
        }

        [Test]
        public void Search_JapaneseQuery_SelectsJapaneseText()
        {
            var page = GraphOperationPicker.Search(new OperationSearchRequest
            {
                Query = "プレハブ",
                Lang = "ja",
            }).Value!;

            var hit = page.Items
                .Single(i => i.Operation.Descriptor.Id == "test.gui.prefab_create");
            Assert.AreEqual("プレハブ作成", hit.DisplayName);
        }

        [Test]
        public void Search_EnglishQuery_ListsTheSameOperation()
        {
            var page = GraphOperationPicker.Search(new OperationSearchRequest
            {
                Query = "prefab",
            }).Value!;

            Assert.IsTrue(
                page.Items.Any(i => i.Operation.Descriptor.Id == "test.gui.prefab_create"),
                "the English query must offer the Prefab Operation too");
        }

        [Test]
        public void Search_NoPublishedSnapshot_ReturnsEmptyPage()
        {
            GraphToolkitRegistry.SnapshotSource = () => null;
            var page = GraphOperationPicker.Search(new OperationSearchRequest
            {
                Query = "prefab",
            }).Value!;
            Assert.IsEmpty(page.Items,
                "before the first §C3 publication the picker lists no candidates");
        }

        // ── Adding a pick: a node with Descriptor ports ───────────────────

        [Test]
        public void Add_PickedResult_CreatesNodeWithDescriptorPorts()
        {
            WithGraph(graph =>
            {
                var registered = _snapshot!.Find("test.gui.prefab_create", 1)!;
                var node = GraphOperationPicker.Add(
                    graph, registered, Vector2.zero, "nPrefab");

                Assert.AreEqual("test.gui.prefab_create", node.operationId);
                Assert.AreEqual(1, node.version);
                Assert.AreEqual("nPrefab", node.nodeId);
                AssertPortsMatchDescriptor(node, registered.Descriptor);
            });
        }

        [Test]
        public void Add_GeneratedNodeId_MatchesGrammarAndIsUnique()
        {
            WithGraph(graph =>
            {
                var registered = _snapshot!.Find("test.gui.prefab_create", 1)!;
                var first = GraphOperationPicker.Add(
                    graph, registered, Vector2.zero);
                var second = GraphOperationPicker.Add(
                    graph, registered, Vector2.zero);

                Assert.AreNotEqual(first.nodeId, second.nodeId);
                foreach (var node in new[] { first, second })
                {
                    Assert.IsNotEmpty(node.nodeId);
                    Assert.IsTrue(char.IsLetter(node.nodeId[0]));
                    Assert.IsTrue(node.nodeId.All(c =>
                        char.IsLetterOrDigit(c) || c == '_'),
                        "generated node IDs must match the §A4 grammar");
                    Assert.LessOrEqual(node.nodeId.Length, 64);
                }
            });
        }

        // ── RegistryChanged refresh (SPEC §H6.5, §E8) ─────────────────────

        [Test]
        public void Tracker_EnableRegisters_DisableDeregisters()
        {
            WithGraph(graph =>
            {
                graph.OnEnable();
                Assert.IsTrue(OpWeaveGraphTracker.Live.Contains(graph),
                    "OnEnable must register the graph as live");
                graph.OnDisable();
                Assert.IsFalse(OpWeaveGraphTracker.Live.Contains(graph),
                    "OnDisable must drop the graph from the live set");
                graph.OnEnable();
            });
        }

        [Test]
        public void RegistryChanged_RemovedOperation_MarksNodeAsError_KeepingGraph()
        {
            WithGraph(graph =>
            {
                graph.OnEnable();
                var registered = _snapshot!.Find("test.gui.removable", 1)!;
                var node = GraphOperationPicker.Add(
                    graph, registered, Vector2.zero, "nRemoved");
                Assert.Greater(node.GetInputPorts().Count(), 1,
                    "precondition: the node carried Descriptor ports");

                // The Registry rebuild no longer registers the Operation;
                // the diff reports it removed.
                GraphToolkitRegistry.SnapshotSource = BuildSnapshotWithoutRemovable;
                var events = new RunEvents();
                GraphRegistryRefresh.EventsSource = () => events;
                GraphRegistryRefresh.Subscribe();
                events.PublishRegistryChanges(new[]
                {
                    new RegistryChange(
                        RegistryChangeKinds.Removed,
                        "test.gui.removable",
                        1,
                        "Assets/OpWeave/Removable.cs",
                        Array.Empty<Diagnostic>()),
                });

                // Ports are redefined against the new snapshot: only the
                // fixed control input remains — while the node and the
                // graph itself are kept (SPEC §H6.5).
                var inputs = node.GetInputPorts().ToList();
                Assert.AreEqual(1, inputs.Count,
                    "a removed Operation's node keeps only the control input");
                Assert.AreEqual("in", inputs[0].Name);
                Assert.IsEmpty(node.GetOutputPorts().ToList());
                Assert.IsTrue(graph.GetNodes().Contains(node),
                    "the removed Operation's node must stay on the graph");

                // The deferred validation pass turns the removal into the
                // node's error display: OW-OP-001 lands on the node.
                Assert.IsNotNull(_deferred,
                    "the refresh must arm the §H8 quiet window");
                _now += GraphLiveValidation.DebounceSeconds;
                _deferred!();

                var error = _sink.Errors
                    .Where(e => e.Message.Contains("OW-OP-001"))
                    .ToList();
                Assert.AreEqual(1, error.Count,
                    "exactly one OW-OP-001 must be reported");
                Assert.AreSame(node, error[0].Context,
                    "OW-OP-001 must land on the removed Operation's node");
            });
        }

        [Test]
        public void RegistryChanged_DescriptorChange_RedefinesPorts()
        {
            WithGraph(graph =>
            {
                graph.OnEnable();
                var registered = _snapshot!.Find("test.gui.evolving", 1)!;
                var node = GraphOperationPicker.Add(
                    graph, registered, Vector2.zero, "nEvolving");
                CollectionAssert.AreEqual(
                    new[] { "in", "alpha" },
                    node.GetInputPorts().Select(p => p.Name).ToArray(),
                    "precondition: the node carries the old Descriptor's ports");

                // The same (id, version) is re-registered with an added
                // input — the published snapshot now reads the new
                // Descriptor.
                GraphToolkitRegistry.SnapshotSource = BuildSnapshotEvolved;
                GraphRegistryRefresh.OnRegistryChanged(new[]
                {
                    new RegistryChange(
                        RegistryChangeKinds.DescriptorChanged,
                        "test.gui.evolving",
                        1,
                        "Assets/OpWeave/Evolving.cs",
                        Array.Empty<Diagnostic>()),
                });

                CollectionAssert.AreEqual(
                    new[] { "in", "alpha", "beta" },
                    node.GetInputPorts().Select(p => p.Name).ToArray(),
                    "the node must pick up the new Descriptor's ports");
                Assert.IsNotNull(_deferred,
                    "the refresh must arm the §H8 quiet window");
            });
        }

        [Test]
        public void RegistryChanged_KeepsUnaffectedNodesAlone()
        {
            WithGraph(graph =>
            {
                graph.OnEnable();
                var registered = _snapshot!.Find("test.gui.prefab_create", 1)!;
                var node = GraphOperationPicker.Add(
                    graph, registered, Vector2.zero, "nKept");

                GraphRegistryRefresh.OnRegistryChanged(new[]
                {
                    new RegistryChange(
                        RegistryChangeKinds.Removed,
                        "test.gui.absent",
                        1,
                        "Assets/OpWeave/Absent.cs",
                        Array.Empty<Diagnostic>()),
                });

                // The unaffected node keeps its Descriptor ports.
                AssertPortsMatchDescriptor(node, registered.Descriptor);
            });
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

        private static void WithGraph(Action<OpWeaveGraph> body)
        {
            var folderCreated = false;
            OpWeaveGraph? graph = null;
            try
            {
                if (!AssetDatabase.IsValidFolder(TempDir))
                {
                    AssetDatabase.CreateFolder("Assets", "GraphSearchRefreshTests");
                    folderCreated = true;
                }
                if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
                {
                    AssetDatabase.DeleteAsset(TempPath);
                }
                graph = GraphDatabase.CreateGraph<OpWeaveGraph>(TempPath);
                Assert.IsNotNull(graph, "GraphDatabase.CreateGraph must return a graph");
                body(graph!);
            }
            finally
            {
                if (graph is not null)
                {
                    OpWeaveGraphTracker.Unregister(graph);
                }
                if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
                {
                    AssetDatabase.DeleteAsset(TempPath);
                }
                if (folderCreated)
                {
                    AssetDatabase.DeleteAsset(TempDir);
                }
            }
        }

        private static void AssertPortsMatchDescriptor(
            OperationNode node, OperationDescriptor descriptor)
        {
            var inputs = node.GetInputPorts().ToList();
            var expectedInputs = new[] { OperationNode.ControlInputName }
                .Concat(descriptor.Inputs.Select(p => p.Name))
                .ToArray();
            CollectionAssert.AreEqual(expectedInputs,
                inputs.Select(p => p.Name).ToArray(),
                $"input port names of {descriptor.Id}");
            foreach (var port in inputs)
            {
                Assert.AreEqual(PortDirection.Input, port.Direction);
                var expected = port.Name == OperationNode.ControlInputName
                    ? typeof(OwControl)
                    : typeof(OwValue);
                Assert.AreEqual(expected, port.DataType,
                    $"data type of input port {port.Name} on {descriptor.Id}");
            }

            var outputs = node.GetOutputPorts().ToList();
            var expectedOutputs = descriptor.ControlOutputs
                .Concat(descriptor.Outputs.Select(p => p.Name))
                .ToArray();
            CollectionAssert.AreEqual(expectedOutputs,
                outputs.Select(p => p.Name).ToArray(),
                $"output port names of {descriptor.Id}");
            foreach (var port in outputs)
            {
                Assert.AreEqual(PortDirection.Output, port.Direction);
                var expected = descriptor.ControlOutputs.Contains(port.Name)
                    ? typeof(OwControl)
                    : typeof(OwValue);
                Assert.AreEqual(expected, port.DataType,
                    $"data type of output port {port.Name} on {descriptor.Id}");
            }
        }

        private static RegistrySnapshot BuildSnapshot()
        {
            return SnapshotOf(
                "reg_01J00000000000000000000000",
                (
                    // The Prefab Operation the 「プレハブ」 query must
                    // offer, standing in for the standard catalog's — the
                    // origin the picker row displays.
                    Op.Define("test.gui.prefab_create", 1)
                        .Summary("Creates a prefab asset from a GameObject.")
                        .Ja("プレハブ作成", "GameObject からプレハブを作成する。", "プレハブ")
                        .Input("source", "unity.GameObjectRef", required: true)
                        .Input("path", "unity.AssetPath", required: true)
                        .Output("prefab", "unity.PrefabAssetRef")
                        .Effects(EffectNames.AssetCreate)
                        .Idempotency(Idempotency.Reconcilable)
                        .Build() with { DisplayName = "Create Prefab" },
                    OperationOrigin.Standard
                ),
                (
                    // Removed by the refresh fixture's second snapshot.
                    Op.Define("test.gui.removable", 1)
                        .Summary("Custom Operation the refresh removes.")
                        .Input("target", "unity.GameObjectRef", required: true)
                        .Output("result", "unity.GameObjectRef")
                        .Effects(EffectNames.SceneModify)
                        .Idempotency(Idempotency.Idempotent)
                        .Build(),
                    OperationOrigin.Custom
                ),
                (
                    // Re-registered with an added input by the
                    // descriptor-change fixture's second snapshot.
                    Op.Define("test.gui.evolving", 1)
                        .Summary("Operation whose Descriptor changes.")
                        .Input("alpha", "core.String", required: false)
                        .Effects(EffectNames.Read)
                        .Idempotency(Idempotency.Idempotent)
                        .Build(),
                    OperationOrigin.Custom
                ));
        }

        // The rebuilt snapshot without test.gui.removable — the custom
        // Operation the fixture deletes.
        private static RegistrySnapshot BuildSnapshotWithoutRemovable()
        {
            return SnapshotOf(
                "reg_01J00000000000000000000001",
                (
                    Op.Define("test.gui.prefab_create", 1)
                        .Summary("Creates a prefab asset from a GameObject.")
                        .Ja("プレハブ作成", "GameObject からプレハブを作成する。", "プレハブ")
                        .Input("source", "unity.GameObjectRef", required: true)
                        .Input("path", "unity.AssetPath", required: true)
                        .Output("prefab", "unity.PrefabAssetRef")
                        .Effects(EffectNames.AssetCreate)
                        .Idempotency(Idempotency.Reconcilable)
                        .Build() with { DisplayName = "Create Prefab" },
                    OperationOrigin.Standard
                ),
                (
                    Op.Define("test.gui.evolving", 1)
                        .Summary("Operation whose Descriptor changes.")
                        .Input("alpha", "core.String", required: false)
                        .Effects(EffectNames.Read)
                        .Idempotency(Idempotency.Idempotent)
                        .Build(),
                    OperationOrigin.Custom
                ));
        }

        // The rebuilt snapshot where test.gui.evolving gained an input.
        private static RegistrySnapshot BuildSnapshotEvolved()
        {
            return SnapshotOf(
                "reg_01J00000000000000000000002",
                (
                    Op.Define("test.gui.prefab_create", 1)
                        .Summary("Creates a prefab asset from a GameObject.")
                        .Ja("プレハブ作成", "GameObject からプレハブを作成する。", "プレハブ")
                        .Input("source", "unity.GameObjectRef", required: true)
                        .Input("path", "unity.AssetPath", required: true)
                        .Output("prefab", "unity.PrefabAssetRef")
                        .Effects(EffectNames.AssetCreate)
                        .Idempotency(Idempotency.Reconcilable)
                        .Build() with { DisplayName = "Create Prefab" },
                    OperationOrigin.Standard
                ),
                (
                    Op.Define("test.gui.removable", 1)
                        .Summary("Custom Operation the refresh removes.")
                        .Input("target", "unity.GameObjectRef", required: true)
                        .Output("result", "unity.GameObjectRef")
                        .Effects(EffectNames.SceneModify)
                        .Idempotency(Idempotency.Idempotent)
                        .Build(),
                    OperationOrigin.Custom
                ),
                (
                    Op.Define("test.gui.evolving", 1)
                        .Summary("Operation whose Descriptor changes.")
                        .Input("alpha", "core.String", required: false)
                        .Input("beta", "core.String", required: false)
                        .Effects(EffectNames.Read)
                        .Idempotency(Idempotency.Idempotent)
                        .Build(),
                    OperationOrigin.Custom
                ));
        }

        private static RegistrySnapshot SnapshotOf(
            string registryId,
            params (OperationDescriptor Descriptor, OperationOrigin Origin)[] entries)
        {
            var operations = entries
                .Select((e, i) => new RegisteredOperation(
                    e.Descriptor,
                    e.Origin,
                    "OpWeave.UnityAdapter.Tests",
                    null,
                    new string((char)('a' + i), 64),
                    new string((char)('A' + i), 64))
                {
                    // OperationValidationPhase refuses a snapshot entry
                    // without an implementation instance; the fixture ops
                    // need a no-op one (the removed Operation's OW-OP-001
                    // comes from the resolve phase, not ValidateStatic).
                    Implementation = new NoopOperation(e.Descriptor),
                })
                .ToArray();
            return new RegistrySnapshot(
                registryId,
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
