#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.2-01 [Edit] tests (SPEC §H6 items 1–4): Descriptor-driven port
// generation for the generic OperationNode, the StartNode shape, WF inputs
// as OwValue variables (H6.4) and the H6.2 serialized carrier's persistence
// across SaveGraph/LoadGraph. T-W5.2-03 adds the §H6.1 assertion below:
// CreateGraph writes the asset at whatever path it is given, so the
// [Graph("opweavegraph")] extension itself needs a direct check. Below
// 6000.5 the OpWeave.GraphToolkit asmdef is excluded and this whole file
// compiles to nothing.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.GraphToolkit;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class GraphToolkitPortGenerationTests
    {
        private const string TempDir = "Assets/GraphToolkitPortTests";
        private const string TempPath = TempDir + "/graph." + OpWeaveGraph.AssetExtension;

        private Func<RegistrySnapshot?>? _previousSource;
        private RegistrySnapshot? _snapshot;

        [SetUp]
        public void SetUp()
        {
            _snapshot = BuildSnapshot();
            _previousSource = GraphToolkitRegistry.SnapshotSource;
            var snapshot = _snapshot;
            GraphToolkitRegistry.SnapshotSource = () => snapshot;
        }

        [TearDown]
        public void TearDown()
        {
            if (_previousSource is not null)
            {
                GraphToolkitRegistry.SnapshotSource = _previousSource;
                _previousSource = null;
            }
            if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
            {
                AssetDatabase.DeleteAsset(TempPath);
            }
            if (AssetDatabase.IsValidFolder(TempDir))
            {
                AssetDatabase.DeleteAsset(TempDir);
            }
        }

        // ── SPEC §H6.1: [Graph("opweavegraph")] on OpWeaveGraph : Graph ─────

        [Test]
        public void OpWeaveGraph_DeclaresTheOpWeaveGraphExtension()
        {
            Assert.IsTrue(typeof(Graph).IsAssignableFrom(typeof(OpWeaveGraph)),
                "OpWeaveGraph must be a Graph Toolkit Graph (SPEC §H6.1)");
            var attribute = (GraphAttribute?)Attribute.GetCustomAttribute(
                typeof(OpWeaveGraph), typeof(GraphAttribute));
            Assert.IsNotNull(attribute,
                "OpWeaveGraph must carry [Graph(\"opweavegraph\")] (SPEC §H6.1)");
            var extension = attribute!.Extension;
            Assert.AreEqual("opweavegraph", extension);
            Assert.AreEqual(OpWeaveGraph.AssetExtension, extension,
                "the declared importer extension and the test asset suffix must agree");
        }

        // ── SPEC §H6.2: ports equal the Descriptors exactly ─────────────────

        [Test]
        public void OperationNode_PortsMatchRegisteredDescriptors()
        {
            WithGraph(graph =>
            {
                foreach (var registered in _snapshot!.Operations)
                {
                    var descriptor = registered.Descriptor;
                    var node = new OperationNode
                    {
                        operationId = descriptor.Id,
                        version = descriptor.Version,
                        nodeId = "n-" + descriptor.Id,
                    };
                    graph.AddNode(node);
                    AssertPortsMatchDescriptor(node, descriptor);
                }
            });
        }

        [Test]
        public void OperationNode_UnknownOperation_DefinesOnlyControlInput()
        {
            WithGraph(graph =>
            {
                var node = new OperationNode
                {
                    operationId = "test.gui.absent",
                    version = 1,
                    nodeId = "n-absent",
                };
                graph.AddNode(node);
                var inputs = node.GetInputPorts().ToList();
                Assert.AreEqual(1, inputs.Count, "an unregistered Operation keeps only the control input");
                Assert.AreEqual("in", inputs[0].Name);
                Assert.AreEqual(typeof(OwControl), inputs[0].DataType);
                Assert.IsEmpty(node.GetOutputPorts().ToList(),
                    "an unregistered Operation defines no outputs");
            });
        }

        [Test]
        public void StartNode_HasOnlyNextControlOutput()
        {
            WithGraph(graph =>
            {
                var node = new StartNode { nodeId = "start" };
                graph.AddNode(node);
                Assert.IsEmpty(node.GetInputPorts().ToList(), "StartNode has no inputs");
                var outputs = node.GetOutputPorts().ToList();
                Assert.AreEqual(1, outputs.Count);
                Assert.AreEqual("next", outputs[0].Name);
                Assert.AreEqual(typeof(OwControl), outputs[0].DataType);
                Assert.AreEqual(PortDirection.Output, outputs[0].Direction);
            });
        }

        // ── SPEC §H6.2: the serialized carrier survives SaveGraph/LoadGraph ─

        [Test]
        public void CarrierFields_AndPorts_PersistAcrossSaveLoad()
        {
            WithGraph(graph =>
            {
                var node = new OperationNode
                {
                    operationId = "test.gui.immediate",
                    version = 1,
                    nodeId = "n-persist",
                };
                graph.AddNode(node);
                GraphDatabase.SaveGraph(graph);

                var reloaded = GraphDatabase.LoadGraph<OpWeaveGraph>(TempPath);
                Assert.IsNotNull(reloaded, "LoadGraph must return the saved graph");
                var rnode = reloaded!.GetNodes().OfType<OperationNode>().Single();
                Assert.AreEqual("test.gui.immediate", rnode.operationId);
                Assert.AreEqual(1, rnode.version);
                Assert.AreEqual("n-persist", rnode.nodeId);
                AssertPortsMatchDescriptor(
                    rnode, _snapshot!.Find("test.gui.immediate", 1)!.Descriptor);
            });
        }

        // ── SPEC §H6.4: WF inputs as OwValue variables ──────────────────────

        [Test]
        public void WorkflowInputs_AreOwValueVariables()
        {
            WithGraph(graph =>
            {
                var variable = graph.CreateVariable(
                    "steps", typeof(OwValue), new OwValue(), VariableKind.Input);
                var variableNode = graph.AddVariableNode(variable, new Vector2(0, 0));

                Assert.IsNotNull(variable);
                Assert.AreEqual("steps", variable.Name);
                Assert.AreEqual(typeof(OwValue), variable.DataType);
                Assert.AreEqual(VariableKind.Input, variable.VariableKind);
                Assert.IsInstanceOf<IVariableNode>(variableNode);
                Assert.AreSame(variable, ((IVariableNode)variableNode).Variable);
            });
        }

        // ── fixtures ────────────────────────────────────────────────────────

        private static void WithGraph(Action<OpWeaveGraph> body)
        {
            var folderCreated = false;
            var assetCreated = false;
            try
            {
                if (!AssetDatabase.IsValidFolder(TempDir))
                {
                    AssetDatabase.CreateFolder("Assets", "GraphToolkitPortTests");
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

        private static void AssertPortsMatchDescriptor(
            OperationNode node, OperationDescriptor descriptor)
        {
            var inputs = node.GetInputPorts().ToList();
            var expectedInputs = new[] { "in" }
                .Concat(descriptor.Inputs.Select(p => p.Name))
                .ToArray();
            CollectionAssert.AreEqual(expectedInputs,
                inputs.Select(p => p.Name).ToArray(),
                $"input port names of {descriptor.Id}");
            foreach (var port in inputs)
            {
                Assert.AreEqual(PortDirection.Input, port.Direction);
                var expected = port.Name == "in" ? typeof(OwControl) : typeof(OwValue);
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
            var descriptors = new[]
            {
                // Immediate, covering Ref/Value/List/Json port types.
                Op.Define("test.gui.immediate", 1)
                    .Summary("Immediate fixture covering the data port kinds.")
                    .Input("target", "unity.GameObjectRef", required: true)
                    .Input("name", "core.String", required: false)
                    .Input("position", "unity.Vector3", required: false)
                    .Input("tags", "core.List<core.String>", required: false)
                    .Input("payload", "core.Json", required: false)
                    .Output("result", "unity.GameObjectRef")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                // Waitable.
                Op.Define("test.gui.waitable", 1)
                    .Summary("Waitable fixture.")
                    .Input("path", "unity.AssetPath", required: true)
                    .Output("asset", "unity.AssetRef<unity.Object>")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build() with { ExecutionMode = ExecutionMode.Waitable },
                // OutputRefinement (unity.component.add shape).
                Op.Define("test.gui.refined", 1)
                    .Summary("OutputRefinement fixture.")
                    .Input("componentType", "unity.TypeName", required: true)
                    .Output("component", "unity.ComponentRef<unity.Component>")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build() with
                    {
                        OutputRefinement = new TypeRefinement(
                            "componentType", "component", "unity.ComponentRef<{0}>"),
                    },
            };
            var operations = descriptors
                .Select((d, i) => new RegisteredOperation(
                    d,
                    OperationOrigin.Custom,
                    "OpWeave.UnityAdapter.Tests",
                    null,
                    new string((char)('a' + i), 64),
                    new string((char)('A' + i), 64)))
                .ToArray();
            return new RegistrySnapshot(
                "reg_01J00000000000000000000000",
                new string('f', 64),
                operations,
                Types,
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());
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
