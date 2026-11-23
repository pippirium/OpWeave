#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.4-04 [Edit] test (SPEC §H6 test 1): a generic OperationNode is created
// in an OpWeaveGraph for every one of the 36 standard Operations of the SPEC
// §I catalog and for the AT-01 custom Operation proj.transform.reset, and each
// node's ports match its live-Registry Descriptor exactly — names, C# types,
// directions and control outputs. Required, optional and nullable
// PortDescriptors each produce a port; the 1:1 name-order check proves none is
// dropped. The standard set is enumerated from the live Registry (production
// stages, same as the other live-registry tests) by provenance — the
// OpWeave.Operations standard-Operation assembly, since the package's own
// test assemblies also carry OperationOrigin.Standard — and cross-checked
// against the explicit SPEC §I list: a missing or extra standard Operation
// fails before a single node is built.
using System;
using System.Collections;
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
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class GraphAllOperationNodeTests
    {
        private const string TempDir = "Assets/GraphAllOpNodeTests";
        private const string TempPath = TempDir + "/graph." + OpWeaveGraph.AssetExtension;
        private const string At01OperationId = "proj.transform.reset";

        // The assembly every SPEC §I standard Operation is defined in —
        // core.start and the 35 unity.* Operations all live here. The
        // package's own test assemblies compile the test.* Operations with
        // the same Standard origin (their package is com.opweave.workflow),
        // so provenance, not Origin alone, identifies the catalog.
        private const string StandardOperationsAssembly = "OpWeave.Operations";

        // The explicit SPEC §I v1 standard-Operation catalog (36 IDs, all
        // version 1). Table order in §I.1–I.4; markdown escapes removed.
        private static readonly string[] StandardOperationIds =
        {
            // §I.1 common, Scene, GameObject (14)
            "core.start",
            "unity.scene.open",
            "unity.scene.create",
            "unity.scene.save",
            "unity.scene.close",
            "unity.gameobject.create",
            "unity.gameobject.find",
            "unity.gameobject.delete",
            "unity.gameobject.set_parent",
            "unity.transform.set",
            "unity.component.add",
            "unity.component.remove",
            "unity.property.get",
            "unity.property.set",
            // §I.2 Prefab (8)
            "unity.prefab.create_from_gameobject",
            "unity.prefab.instantiate",
            "unity.prefab.apply_overrides",
            "unity.prefab.revert_overrides",
            "unity.prefab.unpack",
            "unity.prefab.create_variant",
            "unity.prefab.open_contents",
            "unity.prefab.save_contents",
            // §I.3 Asset (9)
            "unity.asset.import",
            "unity.asset.create_folder",
            "unity.asset.create_material",
            "unity.asset.create_scriptable_object",
            "unity.asset.find",
            "unity.asset.move",
            "unity.asset.copy",
            "unity.asset.delete",
            "unity.asset.save_all",
            // §I.4 Compile & Package (5)
            "unity.script.create_from_template",
            "unity.script.create",
            "unity.script.compile",
            "unity.package.add",
            "unity.package.remove",
        };

        private Func<RegistrySnapshot?>? _previousSource;

        [SetUp]
        public void SetUp()
        {
            _previousSource = GraphToolkitRegistry.SnapshotSource;
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

        private static IEnumerator WaitForQuietEditor()
        {
            yield return new RecompileScripts(false, false);
            var guard = 0;
            while (EditorApplication.isCompiling && guard++ < 600)
            {
                yield return null;
            }
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the live registry cannot build over a failed script compile");
            Assert.IsFalse(EditorApplication.isCompiling,
                "the live registry cannot build while a compile is in flight");
        }

        [UnityTest]
        public IEnumerator EveryStandardAndAt01Operation_GetsADescriptorExactNode()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            // The live Registry over the production stages: the real standard
            // catalog of OpWeave.Operations and the AT-01 custom Operation of
            // the project's OpWeave.Custom assembly.
            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale, "the live registry build must not be stale");
            Assert.IsNotNull(build.Snapshot, "the live registry must publish a snapshot");
            var snapshot = build.Snapshot!;
            GraphToolkitRegistry.SnapshotSource = () => snapshot;

            // Cross-check the registered standard set against the explicit
            // SPEC §I list — a missing or extra standard Operation fails
            // here. Provenance selects the set: inside the TestProject the
            // package's test assemblies also register their test.*
            // Operations with OperationOrigin.Standard, so only the
            // standard-Operation assembly counts.
            var registeredStandard = snapshot.Operations
                .Where(o => o.Origin == OperationOrigin.Standard &&
                            o.AssemblyName == StandardOperationsAssembly)
                .Select(o => o.Descriptor.Id)
                .ToList();
            CollectionAssert.AreEquivalent(StandardOperationIds, registeredStandard,
                "the live Registry's standard set must equal the SPEC §I catalog");

            // The 37-node population: 36 standard + the AT-01 custom Operation.
            var population = new List<string>(StandardOperationIds) { At01OperationId };
            var registered = new Dictionary<string, RegisteredOperation>();
            foreach (var id in population)
            {
                var operation = snapshot.Find(id, 1);
                Assert.IsNotNull(operation,
                    id + "@1 must register in the live Registry");
                registered[id] = operation!;
            }

            WithGraph(graph =>
            {
                var created = 0;
                foreach (var id in population)
                {
                    var descriptor = registered[id].Descriptor;
                    var node = new OperationNode
                    {
                        operationId = descriptor.Id,
                        version = descriptor.Version,
                        nodeId = "n-" + descriptor.Id,
                    };
                    graph.AddNode(node);
                    created++;
                    AssertPortsMatchDescriptor(node, descriptor);
                }
                Assert.AreEqual(37, created,
                    "exactly 37 OperationNodes must be created (SPEC §H6 test 1)");
                Assert.AreEqual(37, graph.GetNodes().OfType<OperationNode>().Count(),
                    "the graph must hold exactly the 37 created OperationNodes");
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
                    AssetDatabase.CreateFolder("Assets", "GraphAllOpNodeTests");
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

            // Every PortDescriptor — required, optional or nullable — produces
            // exactly one node port; the name-order check above already proves
            // the 1:1 mapping, and this names the offender when one is absent.
            var dataInputs = inputs.Skip(1).Select(p => p.Name).ToList();
            foreach (var port in descriptor.Inputs)
            {
                Assert.IsTrue(dataInputs.Contains(port.Name),
                    (port.Required ? "required" : "optional") +
                    (port.Nullable ? " nullable" : "") + " input " + port.Name +
                    " must produce a port on " + descriptor.Id);
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
    }
}
#endif
