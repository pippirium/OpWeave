#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.2-04 [Edit] tests (SPEC §H6 item 6, §H7 item 3): the
// Assets > Create > OpWeave > Graph registration — exactly one menu item,
// bound to OpWeaveGraph through the supported public
// GraphDatabase.PromptInProjectBrowserToCreateNewAsset — the
// noninteractive create → save → reload regression, the
// no-companion-.opweave.json guarantee and the unchanged sibling menus
// (Operation create, Save Graph JSON). The interactive Project Browser
// naming UI is never driven headless; the ticket's manual GUI step covers
// it. Below 6000.5 the OpWeave.GraphToolkit asmdef is excluded and this
// file compiles to nothing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using NUnit.Framework;
using OpWeave.GraphToolkit;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The SPEC §H6 item 6 acceptance tests of
    /// local:T-W5.2-04:graph-create-menu and
    /// local:T-W5.2-04:graph-create-regression.
    /// </summary>
    internal sealed class OpWeaveGraphCreateMenuTests
    {
        // The dedicated folder every asset-creating check uses — created
        // under Assets only and removed with its contents afterwards.
        private const string TempDir = "Assets/OpWeaveGraphCreateMenuTests";
        private const string TempPath =
            TempDir + "/graph." + OpWeaveGraph.AssetExtension;

        // ── SPEC §H6 item 6: menu registration ─────────────────────────

        // The Graph create item registers exactly once across the package's
        // production assemblies — every place a menu item could legitimately
        // live: a second [MenuItem] on the same path would surface as a
        // duplicate menu entry. (OpWeave.Fixture.* test assemblies are not
        // scanned; their types are intentionally malformed fixtures.)
        private static readonly string[] ProductionAssemblies =
        {
            "OpWeave.AgentApi",
            "OpWeave.Core",
            "OpWeave.GraphToolkit",
            "OpWeave.Operations",
            "OpWeave.Pipeline",
            "OpWeave.Testing",
            "OpWeave.UnityAdapter",
            "OpWeave.Viewer",
        };

        [Test]
        public void MenuItem_GraphCreate_RegisteredExactlyOnce()
        {
            Assert.That(OpWeaveGraphCreateMenu.MenuPath,
                Is.EqualTo("Assets/Create/OpWeave/Graph"));
            var registrations = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => ProductionAssemblies.Contains(
                    assembly.GetName().Name))
                .SelectMany(assembly => assembly.GetTypes())
                .SelectMany(type => type.GetMethods(
                    BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic))
                .SelectMany(method => method
                    .GetCustomAttributes(typeof(MenuItem), inherit: false)
                    .Cast<MenuItem>()
                    .Where(item =>
                        item.menuItem == OpWeaveGraphCreateMenu.MenuPath)
                    .Select(item => method))
                .ToList();
            Assert.That(registrations, Has.Count.EqualTo(1),
                "exactly one [MenuItem] may register the Graph create path");
            Assert.That(registrations[0].DeclaringType,
                Is.EqualTo(typeof(OpWeaveGraphCreateMenu)),
                "the registered entry must be the Graph create handler");
        }

        // The handler must reach the supported public creation API bound
        // to OpWeaveGraph: the IL check reads the call target's generic
        // argument, so a placeholder type or a different graph class fails
        // here. PromptInProjectBrowserToCreateNewAsset is interactive and
        // is never invoked in tests — the naming UI belongs to the ticket's
        // manual GUI step.
        [Test]
        public void MenuItem_GraphCreate_TargetsOpWeaveGraph()
        {
            var handler = typeof(OpWeaveGraphCreateMenu).GetMethod(
                "CreateGraphAsset",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(handler, Is.Not.Null,
                "the menu handler method must exist");
            var calls = CalledMembers(handler!)
                .OfType<MethodInfo>()
                .Where(member =>
                    member.DeclaringType == typeof(GraphDatabase) &&
                    member.Name == "PromptInProjectBrowserToCreateNewAsset")
                .ToList();
            Assert.That(calls, Has.Count.EqualTo(1),
                "the handler must call GraphDatabase." +
                "PromptInProjectBrowserToCreateNewAsset exactly once");
            Assert.IsTrue(calls[0].IsGenericMethod);
            Assert.That(calls[0].GetGenericArguments(),
                Is.EqualTo(new[] { typeof(OpWeaveGraph) }),
                "the prompt must create an OpWeaveGraph");
            Assert.That(OpWeaveGraphCreateMenu.DefaultAssetName,
                Is.EqualTo("New OpWeave Graph"),
                "the ticket fixes the rename placeholder text");
        }

        // The sibling entry stays untouched (SPEC §H6 item 6: the existing
        // Assets > Create > OpWeave > Operation menu is preserved).
        [Test]
        public void MenuItem_OperationCreate_StillRegistered()
        {
            var method = typeof(OperationCreateMenu).GetMethod(
                "Open", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var items = method!.GetCustomAttributes(
                typeof(MenuItem), inherit: false);
            Assert.That(items, Has.Length.EqualTo(1));
            Assert.That(((MenuItem)items[0]).menuItem,
                Is.EqualTo(OperationCreateMenu.MenuPath));
            Assert.That(OperationCreateMenu.MenuPath,
                Is.EqualTo("Assets/Create/OpWeave/Operation"));
        }

        // The explicit Save Graph JSON command keeps its registration —
        // the only JSON writer stays opt-in (SPEC §H7 item 3).
        [Test]
        public void MenuItem_SaveGraphJson_StillRegistered()
        {
            var method = typeof(OpWeaveGraphJsonCommands).GetMethod(
                "SaveGraphJsonMenu",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var items = method!.GetCustomAttributes(
                    typeof(MenuItem), inherit: false)
                .Cast<MenuItem>()
                .ToList();
            Assert.That(
                items.Exists(item =>
                    item.menuItem == OpWeaveGraphJsonCommands.ToolsMenuPath),
                Is.True,
                "the Tools > OpWeave > Save Graph JSON entry must remain");
        }

        // ── SPEC §H6 item 6: graph lifecycle, no implicit JSON ─────────

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
            {
                AssetDatabase.DeleteAsset(TempPath);
            }
            if (AssetDatabase.IsValidFolder(TempDir))
            {
                AssetDatabase.DeleteAsset(TempDir);
            }
        }

        // The noninteractive persistence regression the ticket's required
        // verification asks for: the public Graph Toolkit API creates the
        // asset at an explicit path, and the canonical empty payload saves
        // and reloads as OpWeaveGraph with the declared extension and a
        // real GUID. This checks the Graph Toolkit lifecycle only — it
        // does not prove the interactive naming UI.
        [Test]
        public void CreateGraph_PublicApi_PersistsAsOpWeaveGraphAsset()
        {
            WithGraph(graph =>
            {
                Assert.That(graph.GetNodes().Count(), Is.EqualTo(0),
                    "a new graph is the canonical empty payload");
                Assert.That(graph.GetVariables().Count(), Is.EqualTo(0),
                    "a new graph declares no variables");

                GraphDatabase.SaveGraph(graph);

                Assert.IsTrue(File.Exists(TempPath),
                    "the .opweavegraph asset must be on disk");
                Assert.That(
                    AssetDatabase.LoadMainAssetAtPath(TempPath), Is.Not.Null,
                    "Unity must import the created asset");
                Assert.IsFalse(
                    string.IsNullOrEmpty(
                        AssetDatabase.AssetPathToGUID(TempPath)),
                    "the imported asset must carry a GUID");

                var reloaded = GraphDatabase.LoadGraph<OpWeaveGraph>(TempPath);
                Assert.IsNotNull(reloaded,
                    "the saved asset must reload through the graph model");
                Assert.That(
                    GraphDatabase.GetGraphAssetPath(reloaded!),
                    Is.EqualTo(TempPath),
                    "the reloaded graph must bind to the created asset");
            });
        }

        // SPEC §H6 item 6 + §H7 item 3: graph creation and the toolkit's
        // own Save write only the .opweavegraph — no .opweave.json may
        // appear and the graph keeps its unlinked state.
        [Test]
        public void CreateGraph_AndSave_WriteNoJson()
        {
            WithGraph(graph =>
            {
                Assert.That(graph.jsonPath, Is.Empty,
                    "a freshly created graph is unlinked");
                Assert.AreEqual(0, JsonFileCount(),
                    "creation alone must not write a .opweave.json");

                GraphDatabase.SaveGraph(graph);

                Assert.IsTrue(File.Exists(TempPath),
                    "the .opweavegraph itself must be written");
                Assert.That(graph.jsonPath, Is.Empty,
                    "a native graph save never links the graph");
                Assert.AreEqual(0, JsonFileCount(),
                    "a native graph save must not write a .opweave.json");
            });
        }

        // ── helpers ─────────────────────────────────────────────────────

        // Hosts one graph at TempPath inside the dedicated TempDir and
        // always removes the created asset and folder — no writes outside
        // the probe path.
        private static void WithGraph(Action<OpWeaveGraph> body)
        {
            var folderCreated = false;
            var assetCreated = false;
            try
            {
                if (!AssetDatabase.IsValidFolder(TempDir))
                {
                    AssetDatabase.CreateFolder(
                        "Assets", "OpWeaveGraphCreateMenuTests");
                    folderCreated = true;
                }
                if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
                {
                    AssetDatabase.DeleteAsset(TempPath);
                }
                var graph = GraphDatabase.CreateGraph<OpWeaveGraph>(TempPath);
                assetCreated = graph is not null;
                Assert.IsNotNull(
                    graph, "GraphDatabase.CreateGraph must return a graph");
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

        // The companion .opweave.json files inside the probe folder — the
        // assertion target for the never-auto-write guarantee.
        private static int JsonFileCount() =>
            Directory.Exists(TempDir)
                ? Directory.GetFiles(TempDir, "*.opweave.json").Length
                : 0;

        // The members a method body references, decoded from its IL with
        // real operand widths: InlineField/InlineMethod/InlineType/
        // InlineTok operands carry a 4-byte metadata token that
        // Module.ResolveMember decodes — a call to a constructed generic
        // resolves to that instantiation, so the prompt's generic
        // argument is read back exactly.
        private static IEnumerable<MemberInfo> CalledMembers(MethodInfo method)
        {
            var body = method.GetMethodBody();
            if (body is null)
            {
                yield break;
            }
            var il = body.GetILAsByteArray();
            if (il is null)
            {
                yield break;
            }
            var oneByte = new OpCode[0x100];
            var twoByte = new OpCode[0x100];
            foreach (var field in typeof(OpCodes).GetFields(
                BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is not OpCode op)
                {
                    continue;
                }
                var value = unchecked((ushort)op.Value);
                if (value < 0x100)
                {
                    oneByte[value] = op;
                }
                else if ((value & 0xff00) == 0xfe00)
                {
                    twoByte[value & 0xff] = op;
                }
            }
            var offset = 0;
            while (offset < il.Length)
            {
                OpCode op;
                if (il[offset] == 0xfe && offset + 1 < il.Length)
                {
                    op = twoByte[il[offset + 1]];
                    offset += 2;
                }
                else
                {
                    op = oneByte[il[offset]];
                    offset += 1;
                }
                switch (op.OperandType)
                {
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineVar:
                        offset += 1;
                        break;
                    case OperandType.InlineVar:
                        offset += 2;
                        break;
                    case OperandType.InlineI:
                    case OperandType.InlineBrTarget:
                    case OperandType.ShortInlineR:
                        offset += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        offset += 8;
                        break;
                    case OperandType.InlineSwitch:
                        offset += 4 + 4 * BitConverter.ToInt32(il, offset);
                        break;
                    case OperandType.InlineString:
                    case OperandType.InlineSig:
                        offset += 4;
                        break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        var token = BitConverter.ToInt32(il, offset);
                        offset += 4;
                        MemberInfo? member = null;
                        try
                        {
                            member = method.Module.ResolveMember(token);
                        }
                        catch (Exception)
                        {
                            // An unresolvable token contributes nothing —
                            // the assert decides whether the expected call
                            // was found.
                        }
                        if (member is not null)
                        {
                            yield return member;
                        }
                        break;
                    default:
                        // InlineNone: no operand bytes.
                        break;
                }
            }
        }
    }
}
#endif
