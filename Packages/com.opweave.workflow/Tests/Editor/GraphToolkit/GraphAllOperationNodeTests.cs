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
using System.Reflection;
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

        // The resumable [UnityTest]'s snapshot source and node-population
        // feeds: statics are the deliberate shape — a lambda over a
        // resumable-body local lands the local in a display class the
        // domain-reload serializer restores as null (the <>8__N hole),
        // and on a resume the body re-assigns these before anything
        // dereferences them.
        private static RegistrySnapshot? s_nodeSnapshot;
        private static List<string>? s_nodePopulation;
        private static Dictionary<string, RegisteredOperation>? s_nodeRegistered;

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

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the quiet
        // drain reaches it through reflection (the At01AgentStepsTests
        // probe, T-W2.4-07). A reload already armed behind the runner's
        // reload lock surfaces here while isCompiling is false — the
        // deferred state an isCompiling-only check misses.
        private const string ScriptReloadProbeType =
            "UnityEditorInternal.InternalEditorUtility";
        private const string ScriptReloadProbeMethod =
            "IsScriptReloadRequested";

        private static readonly MethodInfo? s_scriptReloadRequested =
            typeof(EditorApplication).Assembly
                .GetType(ScriptReloadProbeType)
                ?.GetMethod(
                    ScriptReloadProbeMethod,
                    BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

        // Non-null and shaped as OneTimeSetUp proved — the fixture does
        // not run at all on a Unity version whose probe is absent.
        private static bool ScriptReloadRequested() =>
            (bool)s_scriptReloadRequested!.Invoke(null, null)!;

        // The state a queued compile/reload reports while the runner's
        // lock holds it: a running script compile always ends in a reload
        // queued behind the lock, and a requested reload is already
        // pending — either is one the drain must cross, never ride into
        // the registry build below.
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        // The pending probe the quiet drain trusts is only as strong as
        // its shape: prove it once up front (the At01AgentStepsTests
        // pattern, T-W2.4-07) so the fixture fails here — before any
        // test — instead of silently degrading to an isCompiling-only
        // check that misses a requested reload armed behind the lock.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var probe = s_scriptReloadRequested;
            if (probe is null ||
                !probe.IsStatic ||
                probe.GetParameters().Length != 0 ||
                probe.ReturnType != typeof(bool))
            {
                Assert.Fail(
                    "the domain-reload pending probe is unavailable on " +
                    "this Unity version — the quiet drain cannot prove a " +
                    "deferred reload is drained");
            }
        }

        // This test never writes a script file, so the only work it
        // can meet here is a leftover handed over by an earlier fixture:
        // a script compile still in flight or queued behind the runner's
        // lock, or the asset import in front of one. The sanctioned
        // bounded conditional drain (the At01AgentStepsTests
        // DrainEditorState idiom, T-W2.4-07) replaces the unconditional
        // `yield return new RecompileScripts(false, false)` residual:
        // that instruction's Perform requests a compile and unlocks the
        // runner's reload lock even when nothing is queued — and with
        // ExpectDomainReload=false UTF keeps no resume state, so a
        // still-pending predecessor reload crossing the unlock abandons
        // the test on cleared locals. Here a pending compile or
        // requested reload is crossed only at DrainedScriptReload — the
        // bounded ExpectDomainReload boundary the caller's relay hands
        // to UTF at the test body's own top level, where the captured PC
        // is correct — and everything else settles on bounded plain
        // frames: a queued compile surfaces as isCompiling a frame or
        // two after Refresh returns, so the wait only trusts a sustained
        // quiet spell and a periodic Refresh flushes the import queue so
        // a queued compile surfaces inside the window, never behind it.
        private static IEnumerator WaitForQuietEditor()
        {
            AssetDatabase.Refresh();
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var nextRefreshAt = double.NegativeInfinity;
            var quietAt = -1.0;
            for (;;)
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "GraphAllOperationNodeTests.WaitForQuietEditor " +
                        "timed out after " + WaitTimeoutSec +
                        " s draining the armed editor state " +
                        "(scriptReloadRequested: " + ScriptReloadRequested() +
                        ", isCompiling: " + EditorApplication.isCompiling +
                        ", isUpdating: " + EditorApplication.isUpdating + ")");
                }
                if (ReloadPending())
                {
                    // A running or requested compile ends in a reload
                    // queued behind the runner's lock — cross it at the
                    // bounded sanctioned boundary, never ride it into
                    // the registry build.
                    quietAt = -1.0;
                    yield return new DrainedScriptReload(
                        "WaitForQuietEditor", WaitTimeoutSec);
                    continue;
                }
                if (EditorApplication.isUpdating)
                {
                    // An import still in flight may surface a compile —
                    // no quiet counts while it runs.
                    quietAt = -1.0;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - quietAt >=
                         QuietSettleSec)
                {
                    break;
                }
                if (EditorApplication.timeSinceStartup >= nextRefreshAt)
                {
                    AssetDatabase.Refresh();
                    nextRefreshAt = EditorApplication.timeSinceStartup + 1.0;
                }
                yield return null;
            }
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the live registry cannot build over a failed script compile");
            Assert.IsFalse(EditorApplication.isCompiling,
                "the live registry cannot build while a compile is in flight");
            Assert.IsFalse(ScriptReloadRequested(),
                "the live registry cannot build while a script reload is pending");
        }

        // The bounded reload-crossing instruction — the
        // WaitForDomainReload.Perform wait with a deadline, run by UTF
        // through the IEditModeTestYieldInstruction interface so
        // ExpectDomainReload arms the runner's reload bookkeeping
        // exactly as with UTF's own instructions. It is yielded only
        // after ReloadPending() proved a real reload is armed, so the
        // unlock below always meets a queued reload and can never strand
        // the lock released on an idle Editor — the failure mode of the
        // unconditional RecompileScripts(false, false) unlock it
        // replaces. The one path where no reload can follow — a failed
        // compile — re-locks before failing.
        private sealed class DrainedScriptReload : IEditModeTestYieldInstruction
        {
            private readonly string m_phase;
            private readonly double m_timeoutSec;

            internal DrainedScriptReload(string phase, double timeoutSec)
            {
                m_phase = phase;
                m_timeoutSec = timeoutSec;
            }

            public bool ExpectDomainReload => true;
            public bool ExpectedPlaymodeState => false;

            public IEnumerator Perform()
            {
                // UTF yields the same first frame: the runner must see
                // the yielded instruction and arm its expected-domain-
                // reload bookkeeping before the reload can start.
                yield return null;

                var deadline =
                    EditorApplication.timeSinceStartup + m_timeoutSec;

                AssetDatabase.Refresh();
                EditorApplication.UnlockReloadAssemblies();

                // Wait out the armed state as WaitForDomainReload.Perform
                // does — while a script reload stays requested or a
                // compile runs — but bounded: the reload itself kills
                // the domain mid-wait, so a deadline expiry can only
                // mean the proven pending state never crossed.
                while (ReloadPending())
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        EditorApplication.LockReloadAssemblies();
                        Assert.Fail(
                            "GraphAllOperationNodeTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "GraphAllOperationNodeTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        [UnityTest]
        public IEnumerator EveryStandardAndAt01Operation_GetsADescriptorExactNode()
        {
            // UTF resumes a reload-crossed [UnityTest] enumerator at its
            // yield with every local reset — a null waiter is that
            // resume, not a fresh start: rebuild the idempotent wait
            // and relay on instead of touching the dead reference (the
            // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07).
            IEnumerator? waiter = null;
            for (;;)
            {
                waiter ??= WaitForQuietEditor();
                if (!waiter.MoveNext())
                {
                    break;
                }
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
            s_nodeSnapshot = snapshot;
            GraphToolkitRegistry.SnapshotSource = static () => s_nodeSnapshot;

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

            s_nodePopulation = population;
            s_nodeRegistered = registered;
            WithGraph(static graph =>
            {
                var created = 0;
                foreach (var id in s_nodePopulation!)
                {
                    var descriptor = s_nodeRegistered![id].Descriptor;
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
