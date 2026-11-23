#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.3-01 [Edit] tests (SPEC §H7 items 1–2, §0.6 SG-17, §A3, §A4, §A8):
// JSON → graph → JSON keeps the §A4 canonical form and every non-GUI-owned
// member; the GUI writes only its seven owned keys; an out-of-band change
// blocks automatic overwrite until explicit human reload/overwrite, and an
// unparseable file is never overwritten. Below 6000.5 the
// OpWeave.GraphToolkit asmdef is excluded and this file compiles to nothing.
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.GraphToolkit;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class GraphJsonConversionTests
    {
        private const string TempDir = "Assets/GraphJsonConversionTests";
        private const string TempPath = TempDir + "/graph." + OpWeaveGraph.AssetExtension;

        private const string FixtureFull = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.conv.full"",
  ""title"": ""Full conversion fixture"",
  ""description"": ""Covers every owned key."",
  ""tags"": [""conv"", ""full""],
  ""inputs"": [
    { ""id"": ""scene"", ""type"": ""unity.SceneRef"", ""required"": true, ""description"": ""the scene"" },
    { ""id"": ""count"", ""type"": ""core.String"", ""required"": false, ""default"": ""3"" }
  ],
  ""outputs"": [
    { ""id"": ""result"", ""from"": { ""node"": ""beta"", ""port"": ""info"" }, ""description"": ""info out"" }
  ],
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1, ""label"": ""S"" },
    { ""id"": ""alpha"", ""operation"": ""test.conv.alpha"", ""version"": 1,
      ""parameters"": { ""name"": ""Alpha"", ""config"": {""k"": 1, ""s"": ""v""}, ""opt"": null },
      ""label"": ""A"", ""policy"": { ""timeoutSeconds"": 30, ""maxAttempts"": 2 } },
    { ""id"": ""beta"", ""operation"": ""test.conv.beta"", ""version"": 1,
      ""parameters"": { ""items"": [""x"", ""y""] } }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""alpha"" },
    { ""from"": ""alpha"", ""to"": ""beta"", ""fromPort"": ""next"" }
  ],
  ""dataEdges"": [
    { ""from"": { ""node"": ""alpha"", ""port"": ""result"" }, ""to"": { ""node"": ""beta"", ""port"": ""source"" } }
  ],
  ""inputBindings"": [
    { ""fromInput"": ""scene"", ""to"": { ""node"": ""alpha"", ""port"": ""target"" } },
    { ""fromInput"": ""count"", ""to"": { ""node"": ""beta"", ""port"": ""count"" } }
  ],
  ""layout"": {
    ""nodes"": {
      ""start"": { ""x"": 0, ""y"": 0 },
      ""alpha"": { ""x"": 220, ""y"": -40 },
      ""beta"": { ""x"": 440, ""y"": 80 },
      ""ghost"": { ""x"": 9, ""y"": 9 }
    },
    ""notes"": [ { ""id"": ""note1"", ""x"": 5, ""y"": 6, ""text"": ""keep me"" } ]
  },
  ""x-owner"": { ""reviewed"": true },
  ""x-notes"": ""external metadata""
}";

        private const string FixtureFanout = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.conv.fanout"",
  ""inputs"": [
    { ""id"": ""scene"", ""type"": ""unity.SceneRef"", ""required"": true }
  ],
  ""outputs"": [
    { ""id"": ""done"", ""from"": { ""node"": ""gamma"", ""port"": ""done"" } }
  ],
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""alpha"", ""operation"": ""test.conv.alpha"", ""version"": 1 },
    { ""id"": ""beta"", ""operation"": ""test.conv.beta"", ""version"": 1 },
    { ""id"": ""gamma"", ""operation"": ""test.conv.gamma"", ""version"": 1 }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""alpha"" },
    { ""from"": ""alpha"", ""to"": ""beta"" },
    { ""from"": ""beta"", ""to"": ""gamma"" }
  ],
  ""dataEdges"": [
    { ""from"": { ""node"": ""alpha"", ""port"": ""result"" }, ""to"": { ""node"": ""beta"", ""port"": ""source"" } },
    { ""from"": { ""node"": ""alpha"", ""port"": ""result"" }, ""to"": { ""node"": ""gamma"", ""port"": ""source"" } }
  ],
  ""inputBindings"": [
    { ""fromInput"": ""scene"", ""to"": { ""node"": ""beta"", ""port"": ""target"" } },
    { ""fromInput"": ""scene"", ""to"": { ""node"": ""gamma"", ""port"": ""target"" } }
  ]
}";

        private const string FixtureMinimal = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.conv.min"",
  ""nodes"": [ { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 } ],
  ""controlEdges"": []
}";

        // SPEC §0.6 SG-17: "ghost" runs an Operation absent from the test
        // Registry (no descriptor → only the control input exists), "alpha"
        // carries an unknown parameter and is the target of wires to ports
        // no descriptor declares. Every unmapped member must be carried and
        // re-emitted verbatim — never silently dropped.
        private const string FixtureLossy = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.conv.lossy"",
  ""inputs"": [
    { ""id"": ""scene"", ""type"": ""unity.SceneRef"", ""required"": true }
  ],
  ""outputs"": [
    { ""id"": ""result"", ""from"": { ""node"": ""ghost"", ""port"": ""info"" } }
  ],
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""ghost"", ""operation"": ""test.conv.missing"", ""version"": 1,
      ""parameters"": { ""p1"": ""kept"", ""deep"": {""k"": [1, 2]} },
      ""label"": ""G"" },
    { ""id"": ""alpha"", ""operation"": ""test.conv.alpha"", ""version"": 1,
      ""parameters"": { ""name"": ""A"", ""unknownPort"": ""kept"" } }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""ghost"" },
    { ""from"": ""ghost"", ""to"": ""alpha"" }
  ],
  ""dataEdges"": [
    { ""from"": { ""node"": ""ghost"", ""port"": ""result"" }, ""to"": { ""node"": ""alpha"", ""port"": ""source"" } },
    { ""from"": { ""node"": ""alpha"", ""port"": ""info"" }, ""to"": { ""node"": ""ghost"", ""port"": ""unknownIn"" } }
  ],
  ""inputBindings"": [
    { ""fromInput"": ""scene"", ""to"": { ""node"": ""ghost"", ""port"": ""target"" } },
    { ""fromInput"": ""scene"", ""to"": { ""node"": ""alpha"", ""port"": ""noPort"" } }
  ]
}";

        private static readonly string[] s_ownedKeys =
        {
            "nodes", "controlEdges", "dataEdges", "inputs", "inputBindings", "outputs", "layout",
        };

        private static readonly string[] s_knownNonOwnedKeys =
        {
            "schemaVersion", "id", "title", "description", "tags",
        };

        private Func<RegistrySnapshot?>? _previousSource;
        private RegistrySnapshot? _snapshot;
        private string _jsonDir = null!;

        [SetUp]
        public void SetUp()
        {
            _snapshot = BuildSnapshot();
            _previousSource = GraphToolkitRegistry.SnapshotSource;
            var snapshot = _snapshot;
            GraphToolkitRegistry.SnapshotSource = () => snapshot;
            _jsonDir = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Temp", "OpWeaveGraphJsonTests");
            Directory.CreateDirectory(_jsonDir);
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
            if (Directory.Exists(_jsonDir))
            {
                Directory.Delete(_jsonDir, true);
            }
        }

        // ── local:T-W5.3-01:gui-json-roundtrip (SPEC §H7 test 1) ────────────

        [Test]
        public void RoundTrip_FullFixture_PreservesCanonicalFormAndMetadata()
        {
            WithGraph(graph =>
            {
                var read = GraphJsonConverter.Import(graph, FixtureFull);
                Assert.IsTrue(read.Ok, "the fixture must read");
                var baseline = read.Value!;

                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok, "the export must produce a document");
                var doc = exported.Value!;

                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    doc.Canonical.CanonicalText,
                    "JSON → graph → JSON must preserve the A4 canonical form");
                AssertNonOwnedPreserved(baseline, doc);

                // layout is GUI-owned and must round-trip the node positions
                // the fixture carries (the ghost entry is dropped, see below).
                Assert.AreEqual(3, doc.Layout!.Nodes.Count);
                Assert.AreEqual(new LayoutNodePosition(220, -40), doc.Layout.Nodes["alpha"]);
                Assert.AreEqual(new LayoutNodePosition(440, 80), doc.Layout.Nodes["beta"]);
                Assert.AreEqual(1, doc.Layout.Notes.Count);
                Assert.AreEqual("keep me", doc.Layout.Notes[0].Text);

                // SPEC §A8: the layout entry for the missing node was dropped.
                Assert.IsFalse(doc.Layout.Nodes.ContainsKey("ghost"));
            });
        }

        [Test]
        public void RoundTrip_FanOutFixture_PreservesCanonicalForm()
        {
            WithGraph(graph =>
            {
                var read = GraphJsonConverter.Import(graph, FixtureFanout);
                Assert.IsTrue(read.Ok);
                var baseline = read.Value!;
                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    exported.Value!.Canonical.CanonicalText,
                    "data-edge and input-binding fan-out must survive the round-trip");
            });
        }

        [Test]
        public void RoundTrip_MinimalFixture_PreservesCanonicalForm()
        {
            WithGraph(graph =>
            {
                var read = GraphJsonConverter.Import(graph, FixtureMinimal);
                Assert.IsTrue(read.Ok);
                var baseline = read.Value!;
                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    exported.Value!.Canonical.CanonicalText,
                    "a minimal document must survive the round-trip");
                AssertNonOwnedPreserved(baseline, exported.Value!);
            });
        }

        // ── SPEC §0.6 SG-17: unmapped content is carried, never dropped ────

        [Test]
        public void RoundTrip_LossyImport_PreservesUnmappedContent()
        {
            WithGraph(graph =>
            {
                var read = GraphJsonConverter.Import(graph, FixtureLossy);
                Assert.IsTrue(read.Ok, "the lossy fixture must read");
                var baseline = read.Value!;

                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok, "the export must produce a document");
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    exported.Value!.Canonical.CanonicalText,
                    "parameters, wires and outputs the graph cannot represent must " +
                    "round-trip verbatim through the SG-17 carrier");
            });
        }

        [Test]
        public void Save_LossyImport_WritesUnmappedContentBack()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("lossy.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureLossy).Value!;
                File.WriteAllText(path, FixtureLossy);

                var result = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, result.Status,
                    "error: " + result.ErrorMessage);

                var reread = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(reread.Ok);
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    reread.Value!.Canonical.CanonicalText,
                    "the written file must keep the unmapped content");
            });
        }

        // ── SPEC §0.6 SG-17: a rename is not a delete — carried wires follow ─

        [Test]
        public void Export_NodeIdRename_RewritesCarriedWiresToCurrentId()
        {
            WithGraph(graph =>
            {
                var read = GraphJsonConverter.Import(graph, FixtureLossy);
                Assert.IsTrue(read.Ok, "the lossy fixture must read");
                var baseline = read.Value!;

                // The inspector-editable nodeId is only the node's display
                // id: renaming it must not orphan the carried wires that
                // reference the element (SPEC §0.6 SG-17).
                var alpha = graph.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == "alpha");
                alpha.nodeId = "renamed";

                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                var doc = exported.Value!;

                Assert.IsTrue(doc.Nodes.Any(n => n.Id == "renamed"));
                Assert.IsFalse(doc.Nodes.Any(n => n.Id == "alpha"));
                Assert.IsTrue(
                    doc.ControlEdges.Contains(new ControlEdge("ghost", "renamed")),
                    "the carried control edge must follow the renamed node");
                Assert.IsTrue(
                    doc.DataEdges.Contains(new DataEdge(
                        new PortRef("ghost", "result"), new PortRef("renamed", "source"))),
                    "the carried data edge into the renamed node must follow it");
                Assert.IsTrue(
                    doc.DataEdges.Contains(new DataEdge(
                        new PortRef("renamed", "info"), new PortRef("ghost", "unknownIn"))),
                    "the carried data edge out of the renamed node must follow it");
                Assert.IsTrue(
                    doc.InputBindings.Contains(
                        new InputBinding("scene", new PortRef("renamed", "noPort"))),
                    "the carried input binding must follow the renamed node");
                Assert.IsFalse(
                    doc.ControlEdges.Any(e => e.From == "alpha" || e.To == "alpha") ||
                    doc.DataEdges.Any(e => e.From.Node == "alpha" || e.To.Node == "alpha") ||
                    doc.InputBindings.Any(b => b.To.Node == "alpha"),
                    "no emitted wire may still name the old id");
            });
        }

        [Test]
        public void Export_VariableRename_RewritesCarriedReferences()
        {
            WithGraph(graph =>
            {
                var read = GraphJsonConverter.Import(graph, FixtureLossy);
                Assert.IsTrue(read.Ok, "the lossy fixture must read");
                var baseline = read.Value!;

                var scene = graph.GetVariables().Single(v => v.Name == "scene");
                scene.Name = "sceneRenamed";
                var result = graph.GetVariables().Single(v => v.Name == "result");
                result.Name = "resultRenamed";

                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                var doc = exported.Value!;

                Assert.IsTrue(doc.Inputs.Any(i => i.Id == "sceneRenamed"));
                Assert.IsTrue(
                    doc.InputBindings.Contains(
                        new InputBinding("sceneRenamed", new PortRef("ghost", "target"))),
                    "the carried input binding must follow the renamed input");
                Assert.IsTrue(
                    doc.InputBindings.Contains(
                        new InputBinding("sceneRenamed", new PortRef("alpha", "noPort"))));
                Assert.IsFalse(
                    doc.InputBindings.Any(b => b.FromInput == "scene"),
                    "no emitted binding may still name the old input id");
                Assert.IsTrue(
                    doc.Outputs.Any(o => o.Id == "resultRenamed"),
                    "the carried output must follow the renamed output variable");
            });
        }

        [Test]
        public void Export_DeletedNode_DropsItsCarriedWires()
        {
            WithGraph(graph =>
            {
                var read = GraphJsonConverter.Import(graph, FixtureLossy);
                Assert.IsTrue(read.Ok, "the lossy fixture must read");
                var baseline = read.Value!;

                // Deleting a node is an explicit act: the carried wires that
                // touched it die with it — but the wires that never touched
                // it must survive.
                var alpha = graph.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == "alpha");
                graph.RemoveNode(alpha);

                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                var doc = exported.Value!;

                Assert.IsFalse(doc.Nodes.Any(n => n.Id == "alpha"));
                CollectionAssert.AreEqual(
                    new[] { new ControlEdge("start", "ghost") },
                    doc.ControlEdges.ToList(),
                    "only the live start→ghost edge remains");
                Assert.IsEmpty(doc.DataEdges,
                    "both carried data edges touched the deleted node");
                CollectionAssert.AreEqual(
                    new[] { new InputBinding("scene", new PortRef("ghost", "target")) },
                    doc.InputBindings.ToList(),
                    "the carried binding to the surviving ghost remains");
                Assert.IsTrue(
                    doc.Outputs.Any(o => o.Id == "result"),
                    "the carried output through the surviving ghost remains");
            });
        }

        [Test]
        public void Export_ExplicitNullParameterOnUnknownPort_PreservesJsonNull()
        {
            WithGraph(graph =>
            {
                // "unknownNull" names no port of test.conv.alpha: an explicit
                // JSON null there is real content to carry, not a missing
                // parameter (SPEC §0.1 F-07, §A0.1).
                const string fixture = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.conv.nullparam"",
  ""nodes"": [
    { ""id"": ""alpha"", ""operation"": ""test.conv.alpha"", ""version"": 1,
      ""parameters"": { ""unknownNull"": null, ""unknownKept"": ""v"" } }
  ],
  ""controlEdges"": []
}";
                var read = GraphJsonConverter.Import(graph, fixture);
                Assert.IsTrue(read.Ok, "the fixture must read");
                var exported = GraphJsonConverter.Export(graph, read.Value!);
                Assert.IsTrue(exported.Ok);

                var parameters = exported.Value!.Nodes
                    .Single(n => n.Id == "alpha")
                    .Parameters;
                Assert.IsTrue(
                    parameters.ContainsKey("unknownNull"),
                    "an explicit null on an undeclared port must be carried, not dropped");
                Assert.AreEqual("null", parameters["unknownNull"].CanonicalText,
                    "the carried parameter stays an explicit JSON null, never Missing");
                Assert.AreEqual("\"v\"", parameters["unknownKept"].CanonicalText);
            });
        }

        // ── SPEC §0.6 SG-17: the GUI writes only its seven owned keys ───────

        [Test]
        public void Export_WritesOnlyOwnedKeys_AndPreservesNonOwned()
        {
            WithGraph(graph =>
            {
                var baseline = GraphJsonConverter.Import(graph, FixtureFull).Value!;
                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                var written = JObject.Parse(WorkflowSerializer.Write(exported.Value!));

                foreach (var key in s_ownedKeys)
                {
                    Assert.IsTrue(written.ContainsKey(key), "owned key missing: " + key);
                }
                foreach (var property in written.Properties())
                {
                    var isOwned = s_ownedKeys.Contains(property.Name);
                    var isNonOwned = s_knownNonOwnedKeys.Contains(property.Name) ||
                        property.Name.StartsWith("x-", StringComparison.Ordinal);
                    Assert.IsTrue(isOwned || isNonOwned,
                        "export must not invent top-level members: " + property.Name);
                }

                var reread = WorkflowSerializer.Read(written.ToString(Formatting.None));
                Assert.IsTrue(reread.Ok, "the written JSON must re-read");
                AssertNonOwnedPreserved(baseline, reread.Value!);
            });
        }

        // ── SPEC §0.6 SG-17: save-time overwrite discipline ─────────────────

        [Test]
        public void Save_UnchangedFile_WritesOwnedAndPreservesNonOwned()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("unchanged.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureFull).Value!;
                File.WriteAllText(path, FixtureFull);

                var result = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, result.Status,
                    "error: " + result.ErrorMessage);

                var reread = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(reread.Ok);
                AssertNonOwnedPreserved(baseline, reread.Value!);
            });
        }

        [Test]
        public void Save_ExplicitEmptyTags_StaysEmptyArray()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("tagsempty.opweave.json");
                const string fixture = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.conv.tagsempty"",
  ""tags"": [],
  ""nodes"": [ { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 } ],
  ""controlEdges"": []
}";
                var baseline = GraphJsonConverter.Import(graph, fixture).Value!;
                File.WriteAllText(path, fixture);

                var result = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, result.Status,
                    "error: " + result.ErrorMessage);

                // tags is not GUI-owned (SPEC §0.6 SG-17): an explicit
                // "tags": [] in the current file stays an explicit empty
                // array — never dropped and never turned into null.
                var written = JObject.Parse(File.ReadAllText(path));
                Assert.IsTrue(written.ContainsKey("tags"),
                    "explicit \"tags\": [] must be kept as a member");
                Assert.AreEqual(JTokenType.Array, written["tags"]!.Type);
                Assert.AreEqual(0, ((JArray)written["tags"]!).Count,
                    "the empty array stays empty");

                var reread = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(reread.Ok);
                Assert.AreEqual(0, reread.Value!.Tags.Count);
            });
        }

        [Test]
        public void Save_AbsentTags_StaysAbsent()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("tagsabsent.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureMinimal).Value!;
                File.WriteAllText(path, FixtureMinimal);

                var result = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, result.Status,
                    "error: " + result.ErrorMessage);

                // A missing member is never materialized: absent tags stays
                // absent — not "[]" and never null (SPEC §0.6 SG-17).
                var written = JObject.Parse(File.ReadAllText(path));
                Assert.IsFalse(written.ContainsKey("tags"),
                    "an absent tags member must stay absent");

                var reread = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(reread.Ok);
                Assert.AreEqual(0, reread.Value!.Tags.Count);
            });
        }

        [Test]
        public void Save_ExplicitNullTags_StaysNull()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("tagsnull.opweave.json");
                const string fixture = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.conv.tagsnull"",
  ""tags"": null,
  ""nodes"": [ { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 } ],
  ""controlEdges"": []
}";
                var baseline = GraphJsonConverter.Import(graph, fixture).Value!;
                File.WriteAllText(path, fixture);

                var result = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, result.Status,
                    "error: " + result.ErrorMessage);

                // tags is not GUI-owned (SPEC §0.6 SG-17): an explicit
                // "tags": null in the current file keeps the member and its
                // null value — never dropped to absent and never turned
                // into "[]".
                var written = JObject.Parse(File.ReadAllText(path));
                Assert.IsTrue(written.ContainsKey("tags"),
                    "explicit \"tags\": null must keep the member");
                Assert.AreEqual(JTokenType.Null, written["tags"]!.Type,
                    "the explicit null value stays null");

                var reread = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(reread.Ok);
                Assert.AreEqual(0, reread.Value!.Tags.Count);
            });
        }

        [Test]
        public void Save_ExternalChange_BlocksUntilExplicitReloadOrOverwrite()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("conflict.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureFull).Value!;
                File.WriteAllText(path, FixtureFull);

                // An out-of-band edit: a canonical-affecting parameter change
                // plus a metadata change, written while the GUI is open.
                var external = FixtureFull
                    .Replace("\"name\": \"Alpha\"", "\"name\": \"External\"")
                    .Replace("Full conversion fixture", "External Title");
                File.WriteAllText(path, external);

                var first = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.ExternalModification, first.Status,
                    "an out-of-band change must block automatic overwrite; " +
                    "error: " + first.ErrorMessage);
                Assert.AreEqual(external, File.ReadAllText(path),
                    "the refused save must leave the file untouched");

                var retry = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.ExternalModification, retry.Status,
                    "the block persists until an explicit human choice; " +
                    "error: " + retry.ErrorMessage);

                // Path A: explicit human "Overwrite JSON" — the graph's owned
                // keys win; the external non-owned metadata is kept.
                var overwrite = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: true);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, overwrite.Status,
                    "error: " + overwrite.ErrorMessage);
                var afterOverwrite = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                Assert.AreEqual("External Title", afterOverwrite.Title,
                    "external metadata survives even an explicit overwrite");
                Assert.AreEqual(baseline.Canonical.CanonicalText,
                    afterOverwrite.Canonical.CanonicalText,
                    "the graph's owned content is what was written");
            });
        }

        [Test]
        public void Save_ExternalChange_ExplicitReloadUnblocks()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("reload.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureFull).Value!;
                File.WriteAllText(path, FixtureFull);

                var external = FixtureFull
                    .Replace("\"name\": \"Alpha\"", "\"name\": \"External\"");
                File.WriteAllText(path, external);

                var conflict = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.ExternalModification, conflict.Status,
                    "error: " + conflict.ErrorMessage);

                // Path B: explicit human "Reload JSON" — the graph is rebuilt
                // from the file (unsaved GUI edits are discarded) and the new
                // document becomes the baseline, so the next save is clean.
                var reloaded = GraphJsonConverter.Import(graph, File.ReadAllText(path));
                Assert.IsTrue(reloaded.Ok);
                var saved = GraphJsonFile.SaveToFile(
                    graph, path, reloaded.Value!, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, saved.Status,
                    "after reload the baseline matches and the save proceeds; " +
                    "error: " + saved.ErrorMessage);
            });
        }

        [Test]
        public void Save_ExternalMetadataOnlyChange_SavesAndKeepsExternalMetadata()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("metadata.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureFull).Value!;
                File.WriteAllText(path, FixtureFull);

                // Only non-owned members changed: the canonical hash is the
                // same, so the save proceeds — and the externally edited
                // metadata is preserved, not silently reverted.
                var external = FixtureFull
                    .Replace("Full conversion fixture", "External Title")
                    .Replace("\"x-notes\": \"external metadata\"", "\"x-notes\": \"edited outside\"");
                File.WriteAllText(path, external);

                var result = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, result.Status,
                    "a metadata-only external change must not block the save; " +
                    "error: " + result.ErrorMessage);
                var reread = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                Assert.AreEqual("External Title", reread.Title);
                Assert.AreEqual(
                    JsonValue.From(new JValue("edited outside")).CanonicalText,
                    reread.Extensions["x-notes"].CanonicalText,
                    "externally updated x-* metadata is kept on save");
            });
        }

        [Test]
        public void Save_InvalidJsonOnDisk_RefusedForAutomaticAndExplicitWrites()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("invalid.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureFull).Value!;
                File.WriteAllText(path, "{ this is not valid json");

                var automatic = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.InvalidJson, automatic.Status,
                    "error: " + automatic.ErrorMessage);
                Assert.IsNotEmpty(automatic.Diagnostics.ToList(),
                    "the refusal carries the read diagnostics");

                var explicitResult = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: true);
                Assert.AreEqual(GraphJsonSaveStatus.InvalidJson, explicitResult.Status,
                    "overwriting unparseable JSON is refused even explicitly; " +
                    "error: " + explicitResult.ErrorMessage);
                Assert.AreEqual("{ this is not valid json", File.ReadAllText(path),
                    "the unparseable file is never destroyed");
            });
        }

        [Test]
        public void Save_FileDeletedOutsideGUI_ReportsExternalChange()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("deleted.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureFull).Value!;
                File.WriteAllText(path, FixtureFull);
                File.Delete(path);

                var result = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.ExternalModification, result.Status,
                    "a file deleted outside the GUI is an external change, not a silent recreate; " +
                    "error: " + result.ErrorMessage);
                Assert.IsFalse(File.Exists(path), "the refused save must not recreate the file");

                var overwrite = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: true);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, overwrite.Status,
                    "an explicit human overwrite may recreate the file; " +
                    "error: " + overwrite.ErrorMessage);
                Assert.IsTrue(File.Exists(path));
            });
        }

        // ── SPEC §0.6 SG-17: carried wires never silently rewire,
        //    resurrect or drop — ambiguous or unprovable identities refuse ──

        [Test]
        public void Save_DuplicatedNode_RefusesCarriedRewireAndOverwrite()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("dup.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureLossy).Value!;
                File.WriteAllText(path, FixtureLossy);

                // A GUI duplicate copies the serialized carrier verbatim —
                // "importedAs" and the carried wires alike — so the copy
                // claims the same import identity.
                var alpha = graph.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == "alpha");
                var copy = new OperationNode
                {
                    operationId = alpha.operationId,
                    version = alpha.version,
                    nodeId = "beta",
                    extrasJson = alpha.extrasJson,
                };
                graph.AddNode(copy);

                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.IsNotEmpty(exported.Diagnostics.ToList(),
                    "the ambiguous identity must be diagnosed, never guessed");
                Assert.IsTrue(
                    exported.Diagnostics.All(d =>
                        d.Code == ErrorCodes.OW_UNITY_002 && d.Severity == Severity.Error),
                    "a carried refusal is OW-UNITY-002 at its SPEC §J default severity");
                Assert.IsTrue(
                    exported.Diagnostics.Any(d => d.Message.Contains("alpha")),
                    "the diagnostics must name the ambiguous reference");
                var doc = exported.Value!;
                Assert.IsFalse(
                    doc.ControlEdges.Any(e => e.From == "beta" || e.To == "beta") ||
                    doc.DataEdges.Any(e => e.From.Node == "beta" || e.To.Node == "beta") ||
                    doc.InputBindings.Any(b => b.To.Node == "beta"),
                    "no carried wire may be rewired to the copy");
                CollectionAssert.AreEqual(
                    new[] { new ControlEdge("start", "ghost") },
                    doc.ControlEdges.ToList(),
                    "wires referencing the ambiguous identity are not emitted");

                var refused = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.InvalidGraph, refused.Status,
                    "a save that would silently drop refused carried items is blocked; " +
                    "error: " + refused.ErrorMessage);
                Assert.AreEqual(FixtureLossy, File.ReadAllText(path),
                    "the refused save must not touch the file");
                Assert.IsNotEmpty(refused.Diagnostics.ToList(),
                    "the refusal carries the per-item diagnostics");

                var overwrite = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: true);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, overwrite.Status,
                    "an explicit human overwrite drops the refused items by choice; " +
                    "error: " + overwrite.ErrorMessage);
            });
        }

        [Test]
        public void Save_DuplicateThenDeleteOriginal_RefusesResurrection()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("dupdel.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureLossy).Value!;
                File.WriteAllText(path, FixtureLossy);

                var alpha = graph.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == "alpha");
                var copy = new OperationNode
                {
                    operationId = alpha.operationId,
                    version = alpha.version,
                    nodeId = "beta",
                    extrasJson = alpha.extrasJson,
                };
                graph.AddNode(copy);
                graph.RemoveNode(alpha);

                // The surviving copy is not the import-created claimant:
                // resolving "alpha" through it would resurrect the deleted
                // node's carried wires on the copy.
                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.IsNotEmpty(exported.Diagnostics.ToList(),
                    "an unprovable claimant must be diagnosed, never trusted");
                var doc = exported.Value!;
                Assert.IsFalse(
                    doc.ControlEdges.Any(e => e.From == "beta" || e.To == "beta") ||
                    doc.DataEdges.Any(e => e.From.Node == "beta" || e.To.Node == "beta") ||
                    doc.InputBindings.Any(b => b.To.Node == "beta"),
                    "the deleted node's carried wires must not come back on the copy");

                var refused = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.InvalidGraph, refused.Status,
                    "error: " + refused.ErrorMessage);
                Assert.AreEqual(FixtureLossy, File.ReadAllText(path));
            });
        }

        [Test]
        public void Save_DeleteThenRecreateNodeId_RefusesNameFallback()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("recreate.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureLossy).Value!;
                File.WriteAllText(path, FixtureLossy);

                var alpha = graph.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == "alpha");
                graph.RemoveNode(alpha);
                graph.AddNode(new OperationNode
                {
                    operationId = "test.conv.alpha",
                    version = 1,
                    nodeId = "alpha",
                });

                // The fresh "alpha" is not the imported element: the
                // carried wires must not re-attach by name alone.
                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.IsNotEmpty(exported.Diagnostics.ToList(),
                    "a name-only re-attach to a post-import element must be refused");
                var doc = exported.Value!;
                CollectionAssert.AreEqual(
                    new[] { new ControlEdge("start", "ghost") },
                    doc.ControlEdges.ToList(),
                    "the carried control edge must not re-attach to the recreated node");
                Assert.IsEmpty(doc.DataEdges);
                CollectionAssert.AreEqual(
                    new[] { new InputBinding("scene", new PortRef("ghost", "target")) },
                    doc.InputBindings.ToList(),
                    "only the wire that never touched the old alpha remains");

                var refused = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.InvalidGraph, refused.Status,
                    "error: " + refused.ErrorMessage);
                Assert.AreEqual(FixtureLossy, File.ReadAllText(path));
            });
        }

        [Test]
        public void Save_DeleteThenDuplicateOtherNodeToSameId_RefusesNameFallback()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("reclaim.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureLossy).Value!;
                File.WriteAllText(path, FixtureLossy);

                // A new element named "alpha" that carries a *different*
                // import identity: the name fallback must not attach to an
                // element claiming another identity.
                var alpha = graph.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == "alpha");
                graph.RemoveNode(alpha);
                var ghost = graph.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == "ghost");
                var reclaimed = new OperationNode
                {
                    operationId = ghost.operationId,
                    version = ghost.version,
                    nodeId = "alpha",
                    extrasJson = ghost.extrasJson,
                };
                graph.AddNode(reclaimed);

                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.IsNotEmpty(exported.Diagnostics.ToList());
                var doc = exported.Value!;
                CollectionAssert.AreEqual(
                    new[] { new ControlEdge("start", "ghost") },
                    doc.ControlEdges.ToList(),
                    "the carried control edge must not re-attach to the claimant of 'ghost'");

                var refused = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.InvalidGraph, refused.Status,
                    "error: " + refused.ErrorMessage);
                Assert.AreEqual(FixtureLossy, File.ReadAllText(path));
            });
        }

        [Test]
        public void Save_VariableMarkerLostThenRenamed_RefusesCarriedBindings()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("lostid.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureLossy).Value!;
                File.WriteAllText(path, FixtureLossy);

                // Editing the variable payload drops the "importedAs"
                // marker; after the rename nothing proves whether the
                // "scene" claimant was deleted or is this very variable —
                // the carried bindings must refuse, not drop silently.
                var scene = graph.GetVariables().Single(v => v.Name == "scene");
                Assert.IsTrue(
                    scene.TrySetDefaultValue(new OwValue { json = "" }),
                    "wiping the variable payload must succeed");
                scene.Name = "sceneRenamed";

                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.IsNotEmpty(exported.Diagnostics.ToList(),
                    "an unprovable delete must be diagnosed, never a silent drop");
                Assert.IsFalse(
                    exported.Value!.InputBindings.Any(b => b.FromInput == "sceneRenamed"),
                    "the carried bindings must not rewire to the renamed variable");

                var refused = GraphJsonFile.SaveToFile(graph, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.InvalidGraph, refused.Status,
                    "error: " + refused.ErrorMessage);
                Assert.AreEqual(FixtureLossy, File.ReadAllText(path));
            });
        }

        // ── SPEC §0.6 SG-17: provenance is serialized — it survives an
        //    asset reopen (and likewise a domain reload or undo/redo) ──────

        [Test]
        public void Save_AfterAssetReload_SavesCarriedItems()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("reloadsave.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureLossy).Value!;
                File.WriteAllText(path, FixtureLossy);

                // Reopening the asset rebuilds every element instance from
                // the graph's serialized state; the carried items' import
                // provenance is serialized with them.
                var reloaded = ReloadGraph(graph);

                var exported = GraphJsonConverter.Export(reloaded, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.IsEmpty(exported.Diagnostics.ToList(),
                    "a reload must not orphan the carried items' provenance");
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    exported.Value!.Canonical.CanonicalText,
                    "carried items resolve after the element instances are rebuilt");

                var saved = GraphJsonFile.SaveToFile(
                    reloaded, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, saved.Status,
                    "a save after a reload writes instead of refusing; " +
                    "error: " + saved.ErrorMessage);

                // A rename after the reload still follows the import identity.
                var alpha = reloaded.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == "alpha");
                alpha.nodeId = "renamed";
                var renamed = GraphJsonConverter.Export(reloaded, baseline);
                Assert.IsTrue(renamed.Ok);
                Assert.IsTrue(
                    renamed.Value!.ControlEdges.Contains(new ControlEdge("ghost", "renamed")),
                    "a rename after reload rewrites the carried reference as before");
            });
        }

        [Test]
        public void Save_AfterAssetReload_DuplicateStillRefuses()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("reloaddup.opweave.json");
                var baseline = GraphJsonConverter.Import(graph, FixtureLossy).Value!;
                File.WriteAllText(path, FixtureLossy);

                var reloaded = ReloadGraph(graph);
                var alpha = reloaded.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == "alpha");
                var copy = new OperationNode
                {
                    operationId = alpha.operationId,
                    version = alpha.version,
                    nodeId = "beta",
                    extrasJson = alpha.extrasJson,
                };
                reloaded.AddNode(copy);

                var exported = GraphJsonConverter.Export(reloaded, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.IsNotEmpty(exported.Diagnostics.ToList(),
                    "a duplicate after reload still claims an ambiguous identity");
                var doc = exported.Value!;
                Assert.IsFalse(
                    doc.ControlEdges.Any(e => e.From == "beta" || e.To == "beta") ||
                    doc.DataEdges.Any(e => e.From.Node == "beta" || e.To.Node == "beta") ||
                    doc.InputBindings.Any(b => b.To.Node == "beta"),
                    "no carried wire may be rewired to the post-reload copy");

                var refused = GraphJsonFile.SaveToFile(
                    reloaded, path, baseline, explicitOverwrite: false);
                Assert.AreEqual(GraphJsonSaveStatus.InvalidGraph, refused.Status,
                    "error: " + refused.ErrorMessage);
                Assert.AreEqual(FixtureLossy, File.ReadAllText(path));
            });
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private string JsonPath(string name) => Path.Combine(_jsonDir, name);

        // Simulates an asset reopen (the same element-instance rebuild a
        // domain reload or undo/redo performs): the graph is serialized to
        // its asset and re-instantiated from that state, so only serialized
        // provenance survives.
        private static OpWeaveGraph ReloadGraph(OpWeaveGraph graph)
        {
            GraphDatabase.SaveGraph(graph);
            var reloaded = GraphDatabase.LoadGraph<OpWeaveGraph>(TempPath);
            Assert.IsNotNull(reloaded, "the graph asset must reload from disk");
            return reloaded!;
        }

        private static void AssertNonOwnedPreserved(WorkflowDocument expected, WorkflowDocument actual)
        {
            Assert.AreEqual(expected.Title, actual.Title, "title must be preserved");
            Assert.AreEqual(expected.Description, actual.Description, "description must be preserved");
            CollectionAssert.AreEqual(expected.Tags.ToList(), actual.Tags.ToList(),
                "tags must be preserved");
            CollectionAssert.AreEquivalent(
                expected.Extensions.Keys.ToList(), actual.Extensions.Keys.ToList(),
                "every x-* member must be preserved");
            foreach (var pair in expected.Extensions)
            {
                Assert.AreEqual(pair.Value.CanonicalText, actual.Extensions[pair.Key].CanonicalText,
                    "x-* value differs: " + pair.Key);
            }
        }

        // Internal so the T-W5.3-09 retry tests reuse the same graph host
        // and fixture registry snapshot.
        internal static void WithGraph(Action<OpWeaveGraph> body)
        {
            var folderCreated = false;
            var assetCreated = false;
            try
            {
                if (!AssetDatabase.IsValidFolder(TempDir))
                {
                    AssetDatabase.CreateFolder("Assets", "GraphJsonConversionTests");
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

        internal static RegistrySnapshot BuildSnapshot()
        {
            var descriptors = new[]
            {
                Op.Define("test.conv.alpha", 1)
                    .Summary("Conversion fixture alpha.")
                    .Input("target", "unity.GameObjectRef", required: true)
                    .Input("name", "core.String", required: false)
                    .Input("config", "core.Json", required: false)
                    .Input("opt", "core.Json", required: false)
                    .Output("result", "unity.GameObjectRef")
                    .Output("info", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                Op.Define("test.conv.beta", 1)
                    .Summary("Conversion fixture beta.")
                    .Input("source", "unity.GameObjectRef", required: false)
                    .Input("count", "core.String", required: false)
                    .Input("items", "core.List<core.String>", required: false)
                    .Output("info", "core.String")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                Op.Define("test.conv.gamma", 1)
                    .Summary("Conversion fixture gamma.")
                    .Input("target", "unity.GameObjectRef", required: false)
                    .Input("source", "unity.GameObjectRef", required: false)
                    .Input("count", "core.String", required: false)
                    .Output("done", "core.Bool")
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
