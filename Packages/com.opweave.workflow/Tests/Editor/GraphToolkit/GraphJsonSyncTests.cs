#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.3-05 [Edit] tests (SPEC §H7 item 3, SPEC §0.6 SG-17): the
// .opweavegraph stores the JSON path and the last synced revisions;
// GraphJsonSync.Save writes the JSON through the coordinator under the
// SG-17 rules — revision-bound consent, create-only first link, the
// narrowed three-way merge and the intent-guarded publish. Below 6000.5
// the OpWeave.GraphToolkit asmdef is excluded and this file compiles to
// nothing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.GraphToolkit;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed partial class GraphJsonSyncTests
    {
        private const string TempDir = "Assets/GraphJsonSyncTests";
        private const string TempPath = TempDir + "/graph." + OpWeaveGraph.AssetExtension;

        // The sibling .opweave.json the first-save link adopts for the
        // graph created at TempPath (SPEC §H7.3).
        private const string SiblingJsonPath = TempDir + "/graph.opweave.json";

        private const string FixtureSync = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.sync.main"",
  ""title"": ""Sync fixture"",
  ""inputs"": [
    { ""id"": ""scene"", ""type"": ""unity.SceneRef"", ""required"": true }
  ],
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""alpha"", ""operation"": ""test.conv.alpha"", ""version"": 1,
      ""parameters"": { ""name"": ""Alpha"" } },
    { ""id"": ""beta"", ""operation"": ""test.conv.beta"", ""version"": 1 }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""alpha"" },
    { ""from"": ""alpha"", ""to"": ""beta"" }
  ],
  ""inputBindings"": [
    { ""fromInput"": ""scene"", ""to"": { ""node"": ""alpha"", ""port"": ""target"" } }
  ],
  ""x-owner"": { ""reviewed"": true }
}";

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
                "Temp", "OpWeaveGraphSyncTests");
            Directory.CreateDirectory(_jsonDir);
            // Fixture isolation (T-W5.3-05): intent records are keyed by
            // the graph asset GUID, and the fixture reuses one GUID across
            // re-creations — a record a previous test left (a pending
            // persist, a crashed link) would sit in the same slot and
            // refuse the next save. Sweep it before the test and prove
            // the slot starts empty.
            ClearIntentRecords();
            Assert.AreEqual(0, IntentRecordCount(),
                "the intent record slot must be empty after SetUp cleanup");
        }

        [TearDown]
        public void TearDown()
        {
            GraphJsonSync.ClearSyncTestState();
            ClearIntentRecords();
            GraphToolkitRegistry.SnapshotSource = _previousSource;
            _previousSource = null;
            DeleteSiblingJson();
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

        // ── SPEC §H7.3: the linked save under the revision rules ────────

        [Test]
        public void Save_LinkedGraph_WritesJsonAndMovesBaseline()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("save.opweave.json");
                var baseline = ImportAndLink(graph, path);
                File.Delete(path);

                // The file the graph was synced to is gone: an automatic
                // save is refused — the delete is an external change.
                var refused = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, refused.Outcome);
                Assert.IsFalse(File.Exists(path));

                // The same save with the human prompt can recreate it.
                var result = GraphJsonSync.Save(graph, _ => GraphJsonSyncChoice.OverwriteJson);
                Assert.AreEqual(GraphJsonSyncOutcome.Overwritten, result.Outcome);
                var written = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(written.Ok);
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    written.Value!.Canonical.CanonicalText);
                AssertSynced(graph, path, written.Value!);
            });
        }

        [Test]
        public void Save_ExternalChange_AutomaticSaveRefusedPromptAppliesChoice()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("saveconflict.opweave.json");
                var baseline = ImportAndLink(graph, path);

                var external = FixtureSync.Replace("\"name\": \"Alpha\"", "\"name\": \"External\"");
                File.WriteAllText(path, external);

                // An automatic (Agent/autosave) write is refused outright —
                // no prompt, file untouched (SPEC §0.6 SG-17).
                var automatic = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, automatic.Outcome);
                Assert.IsFalse(automatic.PromptShown);
                Assert.AreEqual(external, File.ReadAllText(path));

                // The GUI save offers the choice; cancel leaves everything.
                var cancelled = GraphJsonSync.Save(graph, _ => GraphJsonSyncChoice.Cancel);
                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, cancelled.Outcome);
                Assert.AreEqual(external, File.ReadAllText(path));
                AssertSynced(graph, path, baseline);

                // Overwrite applies the graph's content.
                var overwritten = GraphJsonSync.Save(
                    graph, _ => GraphJsonSyncChoice.OverwriteJson);
                Assert.AreEqual(GraphJsonSyncOutcome.Overwritten, overwritten.Outcome);
                var written = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(written.Ok);
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    written.Value!.Canonical.CanonicalText);
            });
        }

        [Test]
        public void Save_ExternalChange_PromptReloadDiscardsGraphEdits()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("savereload.opweave.json");
                ImportAndLink(graph, path);

                var external = FixtureSync
                    .Replace("\"name\": \"Alpha\"", "\"name\": \"External\"")
                    .Replace("Sync fixture", "External Title");
                File.WriteAllText(path, external);
                var externalDoc = WorkflowSerializer.Read(external).Value!;

                var result = GraphJsonSync.Save(graph, _ => GraphJsonSyncChoice.ReloadJson);

                Assert.AreEqual(GraphJsonSyncOutcome.Reloaded, result.Outcome);
                Assert.AreEqual(external, File.ReadAllText(path),
                    "reload must not touch the file");
                AssertSynced(graph, path, externalDoc);

                // After reload the graph models the external document and
                // the next save is clean.
                var after = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Saved, after.Outcome);
                var written = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(written.Ok);
                Assert.AreEqual(
                    externalDoc.Canonical.CanonicalText,
                    written.Value!.Canonical.CanonicalText);
                Assert.AreEqual("External Title", written.Value!.Title,
                    "external metadata survives the save after reload");
            });
        }

        // ── SPEC §H7.3: the first save establishes the JSON link ─────────
        //
        // A graph that was never synced records no JSON path. Its first
        // save adopts the sibling .opweave.json of the .opweavegraph asset
        // as the source of truth: the JSON is created when absent, and a
        // file already there links only through the §H7.3 choice — never
        // silently (SPEC §0.6 SG-17).

        [Test]
        public void Save_UnlinkedGraph_FirstSaveWritesJsonAndLinks()
        {
            WithGraph(graph =>
            {
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);

                // The initial link through the production save: nothing
                // exists to overwrite, so no prompt is needed — an
                // automatic save may link too.
                var result = GraphJsonSync.Save(graph, null);

                Assert.AreEqual(GraphJsonSyncOutcome.Saved, result.Outcome);
                Assert.AreEqual(SiblingJsonPath, graph.jsonPath,
                    "the graph adopts the sibling .opweave.json of its asset");
                Assert.IsTrue(File.Exists(SiblingJsonPath),
                    "the first save writes the JSON source of truth");
                var written = WorkflowSerializer.Read(File.ReadAllText(SiblingJsonPath));
                Assert.IsTrue(written.Ok);
                Assert.AreEqual("graph", written.Value!.Id,
                    "a new document's id derives from the graph asset name");
                AssertSynced(graph, SiblingJsonPath, written.Value!);
                Assert.AreEqual(
                    GraphJsonSyncState.InSync, SyncStateOf(graph));

                // The next save is the ordinary linked save.
                var again = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Saved, again.Outcome);
            });
        }

        [Test]
        public void Save_UnlinkedGraph_ExistingJson_NeedsHumanLink()
        {
            WithGraph(graph =>
            {
                File.WriteAllText(SiblingJsonPath, FixtureSync);
                var disk = WorkflowSerializer.Read(FixtureSync).Value!;

                // An automatic save can neither rebase the graph on the
                // JSON nor overwrite it silently (SPEC §0.6 SG-17):
                // refused, file untouched, still unlinked.
                var automatic = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, automatic.Outcome);
                Assert.AreEqual(
                    GraphJsonSyncState.ExternalModification, automatic.Check.State);
                Assert.AreEqual(FixtureSync, File.ReadAllText(SiblingJsonPath));
                Assert.AreEqual(0, graph.jsonPath.Length);

                // Cancel leaves everything — the file and the missing link.
                var cancelled = GraphJsonSync.Save(graph, _ => GraphJsonSyncChoice.Cancel);
                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, cancelled.Outcome);
                Assert.AreEqual(FixtureSync, File.ReadAllText(SiblingJsonPath));
                Assert.AreEqual(0, graph.jsonPath.Length);

                // "Reload from JSON" links the graph: the document is the
                // source of truth and the graph is rebuilt from it.
                var linked = GraphJsonSync.Save(graph, _ => GraphJsonSyncChoice.ReloadJson);
                Assert.AreEqual(GraphJsonSyncOutcome.Reloaded, linked.Outcome);
                Assert.AreEqual(FixtureSync, File.ReadAllText(SiblingJsonPath));
                AssertSynced(graph, SiblingJsonPath, disk);
                var exported = GraphJsonConverter.Export(graph, disk);
                Assert.IsTrue(exported.Ok);
                Assert.AreEqual(
                    disk.Canonical.CanonicalText, exported.Value!.Canonical.CanonicalText,
                    "after linking by reload the graph models the JSON document");
                Assert.AreEqual(
                    GraphJsonSyncState.InSync, SyncStateOf(graph));

                var after = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Saved, after.Outcome);
            });
        }

        [Test]
        public void Save_UnlinkedGraph_ExistingJson_OverwriteLinks()
        {
            WithGraph(graph =>
            {
                var mine = FixtureSync.Replace("\"name\": \"Alpha\"", "\"name\": \"Mine\"");
                Assert.IsTrue(GraphJsonConverter.Import(graph, mine).Ok);
                File.WriteAllText(SiblingJsonPath, FixtureSync);

                var result = GraphJsonSync.Save(graph, _ => GraphJsonSyncChoice.OverwriteJson);

                Assert.AreEqual(GraphJsonSyncOutcome.Overwritten, result.Outcome);
                var text = File.ReadAllText(SiblingJsonPath);
                Assert.IsTrue(text.Contains("\"name\": \"Mine\""),
                    "the graph's owned content wins the explicit overwrite");
                Assert.IsTrue(text.Contains("\"id\": \"test.sync.main\""),
                    "the file's non-owned members are preserved");
                var written = WorkflowSerializer.Read(text);
                Assert.IsTrue(written.Ok);
                AssertSynced(graph, SiblingJsonPath, written.Value!);
            });
        }

        [Test]
        public void Save_UnlinkedGraph_InvalidJson_RefusedWithoutPrompt()
        {
            WithGraph(graph =>
            {
                File.WriteAllText(SiblingJsonPath, "{ this is not valid json");

                var result = GraphJsonSync.Save(graph, _ =>
                {
                    Assert.Fail("an unparseable file never reaches the prompt");
                    return GraphJsonSyncChoice.OverwriteJson;
                });

                Assert.AreEqual(GraphJsonSyncOutcome.Refused, result.Outcome);
                Assert.AreEqual(GraphJsonSyncState.InvalidJson, result.Check.State);
                Assert.AreEqual("{ this is not valid json", File.ReadAllText(SiblingJsonPath),
                    "unparseable content is never destroyed");
                Assert.AreEqual(0, graph.jsonPath.Length,
                    "an unparseable file is never linked");
            });
        }

        // A file created between the first save's absence check and the
        // publication of its .opweave.json is the §H7.3 existing-file
        // conflict — surfaced for an explicit choice, never silently
        // overwritten (SPEC §0.6 SG-17).
        [Test]
        public void Save_UnlinkedGraph_JsonAppearedInPublishGap_NeverOverwrites()
        {
            WithGraph(graph =>
            {
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);

                // The concurrent creator: a different, valid document the
                // probe plants only while the sibling path is absent —
                // the file another process could create between the
                // first save's absence check and publication.
                var racer = FixtureSync
                    .Replace("\"id\": \"test.sync.main\"", "\"id\": \"racer.appeared\"")
                    .Replace("Sync fixture", "Racer document")
                    .Replace("\"name\": \"Alpha\"", "\"name\": \"Racer\"");
                GraphJsonSync.PublishProbe = path =>
                {
                    if (Path.GetFullPath(path).Equals(
                            Path.GetFullPath(SiblingJsonPath),
                            StringComparison.OrdinalIgnoreCase) &&
                        !File.Exists(path))
                    {
                        File.WriteAllText(path, racer);
                    }
                };
                try
                {
                    // An automatic save refuses the appeared file —
                    // untouched, and the graph stays unlinked.
                    var automatic = GraphJsonSync.Save(graph, null);
                    Assert.AreEqual(GraphJsonSyncOutcome.Refused, automatic.Outcome);
                    Assert.AreEqual(
                        GraphJsonSyncState.ExternalModification, automatic.Check.State);
                    Assert.AreEqual(racer, File.ReadAllText(SiblingJsonPath),
                        "a concurrently created file is never overwritten");
                    Assert.AreEqual(0, graph.jsonPath.Length);

                    // With the human surface, the appeared file arrives
                    // through the §H7.3 prompt — Cancel keeps it and the
                    // missing link.
                    File.Delete(SiblingJsonPath);
                    var cancelled = GraphJsonSync.Save(graph, check =>
                    {
                        Assert.AreEqual(
                            GraphJsonSyncState.ExternalModification, check.State,
                            "the appeared file surfaces as the existing-file conflict");
                        return GraphJsonSyncChoice.Cancel;
                    });
                    Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, cancelled.Outcome);
                    Assert.AreEqual(racer, File.ReadAllText(SiblingJsonPath));
                    Assert.AreEqual(0, graph.jsonPath.Length);

                    // The explicit human overwrite applies to the
                    // appeared file: the graph's owned content wins and
                    // the racer's non-owned members survive the merge.
                    File.Delete(SiblingJsonPath);
                    var overwritten = GraphJsonSync.Save(
                        graph, _ => GraphJsonSyncChoice.OverwriteJson);
                    Assert.AreEqual(GraphJsonSyncOutcome.Overwritten, overwritten.Outcome);
                    var text = File.ReadAllText(SiblingJsonPath);
                    Assert.IsTrue(text.Contains("\"name\": \"Alpha\""),
                        "the graph's owned content wins the explicit overwrite");
                    Assert.IsTrue(text.Contains("\"id\": \"racer.appeared\""),
                        "the appeared file's non-owned members are preserved");
                    var written = WorkflowSerializer.Read(text);
                    Assert.IsTrue(written.Ok);
                    AssertSynced(graph, SiblingJsonPath, written.Value!);

                    // Reload applies to the appeared file too: unlink and
                    // let the racer appear once more — the graph is
                    // rebuilt from it and links.
                    graph.jsonPath = "";
                    File.Delete(SiblingJsonPath);
                    var reloaded = GraphJsonSync.Save(
                        graph, _ => GraphJsonSyncChoice.ReloadJson);
                    Assert.AreEqual(GraphJsonSyncOutcome.Reloaded, reloaded.Outcome);
                    var racerDoc = WorkflowSerializer.Read(racer).Value!;
                    AssertSynced(graph, SiblingJsonPath, racerDoc);
                    Assert.AreEqual(racer, File.ReadAllText(SiblingJsonPath),
                        "reload must not touch the appeared file");
                }
                finally
                {
                    GraphJsonSync.PublishProbe = null;
                }
            });
        }

        [Test]
        public void Save_UnlinkedGraphWithoutAsset_NoAction()
        {
            // A graph not backed by an asset has no sibling path to adopt:
            // the first-save link cannot apply and the save stays a no-op.
            var graph = new OpWeaveGraph();
            var result = GraphJsonSync.Save(graph, _ =>
            {
                Assert.Fail("an unlinked graph must never prompt");
                return GraphJsonSyncChoice.Cancel;
            });
            Assert.AreEqual(GraphJsonSyncOutcome.NoAction, result.Outcome);
            Assert.AreEqual(GraphJsonSyncState.Unlinked, result.Check.State);
        }

        // ── SPEC §H7.3: path and hash are stored in the .opweavegraph ────

        [Test]
        public void Sync_SurvivesAssetReload()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("persist.opweave.json");
                var baseline = ImportAndLink(graph, path);

                // A session boundary: the .opweavegraph is written and a new
                // object is built from its serialized state — the path and
                // the synced hash must be stored in the asset.
                GraphDatabase.SaveGraph(graph);
                var reloaded = GraphDatabase.LoadGraph<OpWeaveGraph>(TempPath);
                Assert.IsNotNull(reloaded);
                Assert.AreEqual(path, reloaded!.jsonPath,
                    "the JSON path is stored in the .opweavegraph");
                Assert.AreEqual(
                    baseline.Canonical.WorkflowHash, reloaded.syncedWorkflowHash,
                    "the last synced canonical hash is stored in the .opweavegraph");

                // An unchanged JSON reconciles with no prompt — the
                // stored baseline drives the save's clean path.
                var inSync = GraphJsonSync.Save(reloaded, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Saved, inSync.Outcome,
                    "an unchanged JSON saves clean after reopen");

                // Edit the JSON externally "between sessions": the stored
                // baseline still detects the divergence and the bound
                // choice loop applies the revision-checked reload.
                var external = FixtureSync.Replace("\"name\": \"Alpha\"", "\"name\": \"External\"");
                File.WriteAllText(path, external);

                var prompts = 0;
                var result = GraphJsonSync.Save(reloaded, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.ReloadJson;
                });
                Assert.AreEqual(1, prompts,
                    "the external edit between sessions produces the prompt");
                Assert.AreEqual(GraphJsonSyncOutcome.Reloaded, result.Outcome);
                AssertSynced(reloaded, path,
                    WorkflowSerializer.Read(external).Value!);
            });
        }

        // ── SPEC §H7.3: the narrowed three-way merge (§0.6 SG-17) ──────

        [Test]
        public void Save_MetadataOnlyExternalChange_MergesWithoutPrompt()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("merge-meta.opweave.json");
                var baseline = ImportAndLink(graph, path);

                var external = FixtureSync
                    .Replace("Sync fixture", "Renamed outside")
                    .Replace("\"reviewed\": true", "\"reviewed\": false");
                File.WriteAllText(path, external);
                var externalDoc = WorkflowSerializer.Read(external).Value!;
                Assert.AreEqual(
                    GraphJsonFile.GuiOwnedHash(baseline),
                    GraphJsonFile.GuiOwnedHash(externalDoc),
                    "title and x-* are not GUI-owned — the projection still matches");

                var result = GraphJsonSync.Save(graph, _ =>
                {
                    Assert.Fail("a metadata-only change must merge without a prompt");
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreEqual(GraphJsonSyncOutcome.Saved, result.Outcome);
                var text = File.ReadAllText(path);
                Assert.IsTrue(text.Contains("Renamed outside"),
                    "the external title survives the merge");
                Assert.IsTrue(text.Contains("\"reviewed\": false"),
                    "the external x-* member survives the merge");
                var written = WorkflowSerializer.Read(text);
                Assert.IsTrue(written.Ok);
                Assert.AreEqual(
                    baseline.Canonical.WorkflowHash, written.Value!.Canonical.WorkflowHash,
                    "the graph's owned content is unchanged by the merge");
                AssertSynced(graph, path, written.Value!);
            });
        }

        // A layout change is GUI-owned: the projection differs and the
        // file is a §H7.3 conflict — never silently merged or overwritten.
        [Test]
        public void Save_LayoutExternalChange_Conflicts()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("conflict-layout.opweave.json");
                ImportAndLink(graph, path);

                var external = FixtureSync.Replace(
                    "\n  \"x-owner\"",
                    "\n  \"layout\": { \"nodes\": { \"alpha\": { \"x\": 1, \"y\": 2 } }," +
                        " \"notes\": [] },\n  \"x-owner\"");
                File.WriteAllText(path, external);
                Assert.IsTrue(WorkflowSerializer.Read(external).Ok,
                    "the layout edit must remain a valid document");

                var automatic = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, automatic.Outcome);
                Assert.AreEqual(external, File.ReadAllText(path));

                var prompts = 0;
                var result = GraphJsonSync.Save(graph, check =>
                {
                    prompts++;
                    Assert.AreEqual(GraphJsonSyncState.ExternalModification, check.State);
                    return GraphJsonSyncChoice.Cancel;
                });
                Assert.AreEqual(1, prompts);
                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, result.Outcome);
                Assert.AreEqual(external, File.ReadAllText(path),
                    "a layout conflict leaves the file untouched");
            });
        }

        // A workflow identity change is canonical: the projection differs
        // and the file is a §H7.3 conflict.
        [Test]
        public void Save_IdExternalChange_Conflicts()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("conflict-id.opweave.json");
                ImportAndLink(graph, path);

                var external = FixtureSync.Replace(
                    "\"id\": \"test.sync.main\"", "\"id\": \"test.other.id\"");
                File.WriteAllText(path, external);

                var automatic = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, automatic.Outcome);
                Assert.AreEqual(external, File.ReadAllText(path));

                var prompts = 0;
                var result = GraphJsonSync.Save(graph, check =>
                {
                    prompts++;
                    Assert.AreEqual(GraphJsonSyncState.ExternalModification, check.State);
                    return GraphJsonSyncChoice.Cancel;
                });
                Assert.AreEqual(1, prompts);
                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, result.Outcome);
                Assert.AreEqual(external, File.ReadAllText(path));
            });
        }

        // A schemaVersion change is never merged silently — it is either a
        // canonical conflict or an unreadable version, and the file stays
        // untouched either way (SPEC §0.6 SG-17).
        [Test]
        public void Save_SchemaVersionExternalChange_NeverMerged()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("conflict-schema.opweave.json");
                ImportAndLink(graph, path);

                var external = FixtureSync.Replace(
                    "\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"1.1\"");
                File.WriteAllText(path, external);

                var automatic = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, automatic.Outcome,
                    "a schemaVersion change is never silently merged");
                Assert.AreEqual(external, File.ReadAllText(path));

                var prompts = 0;
                var result = GraphJsonSync.Save(graph, check =>
                {
                    prompts++;
                    Assert.IsTrue(
                        check.State == GraphJsonSyncState.ExternalModification ||
                            check.State == GraphJsonSyncState.InvalidJson,
                        "the schemaVersion change surfaces as a conflict or unparseable, " +
                            "never a merge");
                    return GraphJsonSyncChoice.Cancel;
                });
                Assert.IsTrue(
                    result.Outcome == GraphJsonSyncOutcome.Cancelled ||
                        result.Outcome == GraphJsonSyncOutcome.Refused);
                Assert.AreEqual(external, File.ReadAllText(path),
                    "the file is never overwritten");
            });
        }

        // Unparseable JSON is never overwritten — even by an explicit
        // human Overwrite (SPEC §0.6 SG-17). The choice is never offered:
        // there is no valid document to reload and nothing may be
        // destroyed.
        [Test]
        public void Save_LinkedInvalidJson_NeverOverwrittenEvenWithConsent()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("linked-invalid.opweave.json");
                var baseline = ImportAndLink(graph, path);
                File.WriteAllText(path, "{ this is not valid json");

                var prompts = 0;
                var result = GraphJsonSync.Save(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.OverwriteJson;
                });

                Assert.AreEqual(GraphJsonSyncOutcome.Refused, result.Outcome);
                Assert.AreEqual(GraphJsonSyncState.InvalidJson, result.Check.State);
                Assert.AreEqual(0, prompts,
                    "an unparseable file is refused before any prompt");
                Assert.AreEqual("{ this is not valid json", File.ReadAllText(path),
                    "unparseable content is never destroyed — even with consent");
                AssertSynced(graph, path, baseline);
            });
        }

        // A deleted file is surfaced as a conflict, not silently recreated.
        [Test]
        public void Save_LinkedMissing_SurfacedAsConflict()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("linked-missing.opweave.json");
                var baseline = ImportAndLink(graph, path);
                File.Delete(path);

                var prompts = 0;
                var result = GraphJsonSync.Save(graph, check =>
                {
                    prompts++;
                    Assert.AreEqual(GraphJsonSyncState.Missing, check.State,
                        "the delete is surfaced, not silently recreated");
                    return GraphJsonSyncChoice.Cancel;
                });
                Assert.AreEqual(1, prompts);
                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, result.Outcome);
                Assert.IsFalse(File.Exists(path));
                AssertSynced(graph, path, baseline);
            });
        }

        // Create-only race point 1: a file that appears after the absence
        // check — an uncoordinated writer drops a valid document into the
        // gap; the create-only publication must report it instead of
        // replacing (SPEC §0.6 SG-17).
        [Test]
        public void Save_FirstLink_JsonAppearsAfterAbsenceCheck_NeverOverwritten()
        {
            WithGraph(graph =>
            {
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);
                var racer = FixtureSync
                    .Replace("\"id\": \"test.sync.main\"", "\"id\": \"racer.appeared\"");
                GraphJsonSync.AbsenceProbe = path =>
                {
                    if (Path.GetFullPath(path).Equals(
                            Path.GetFullPath(SiblingJsonPath),
                            StringComparison.OrdinalIgnoreCase) &&
                        !File.Exists(path))
                    {
                        File.WriteAllText(path, racer);
                    }
                };
                try
                {
                    var automatic = GraphJsonSync.Save(graph, null);
                    Assert.AreEqual(GraphJsonSyncOutcome.Refused, automatic.Outcome);
                    Assert.AreEqual(
                        GraphJsonSyncState.ExternalModification, automatic.Check.State);
                    Assert.AreEqual(racer, File.ReadAllText(SiblingJsonPath),
                        "the raced create is surfaced, never overwritten");
                    Assert.AreEqual(0, graph.jsonPath.Length);
                }
                finally
                {
                    GraphJsonSync.AbsenceProbe = null;
                }
            });
        }

        // Create-only race point 2: the file changes inside the prompt —
        // the presented consent is bound to the shown revision, so a file
        // that moved underneath the human's choice is never written over:
        // the decision is invalidated and a new prompt shows the new
        // revision (SPEC §0.6 SG-17).
        [Test]
        public void Save_FirstLink_JsonChangedInsidePrompt_Reprompts()
        {
            WithGraph(graph =>
            {
                var racer1 = FixtureSync
                    .Replace("\"id\": \"test.sync.main\"", "\"id\": \"racer.one\"");
                var racer2 = FixtureSync
                    .Replace("\"id\": \"test.sync.main\"", "\"id\": \"racer.two\"");
                File.WriteAllText(SiblingJsonPath, racer1);
                var racer2Doc = WorkflowSerializer.Read(racer2).Value!;

                var prompts = 0;
                var result = GraphJsonSync.Save(graph, check =>
                {
                    prompts++;
                    if (prompts == 1)
                    {
                        File.WriteAllText(SiblingJsonPath, racer2);
                        return GraphJsonSyncChoice.OverwriteJson;
                    }
                    Assert.AreEqual(
                        racer2Doc.Canonical.WorkflowHash,
                        check.Document!.Canonical.WorkflowHash,
                        "the new prompt presents the new revision");
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreEqual(2, prompts,
                    "a file changed inside the prompt invalidates the consent");
                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, result.Outcome);
                Assert.AreEqual(racer2, File.ReadAllText(SiblingJsonPath),
                    "the revision the consent was bound to is never overwritten");
            });
        }

        // The replace race (F3): a file changed between the bound check and
        // the atomic publication's final byte comparison is reported, never
        // overwritten — and the human is asked again about the new revision.
        [Test]
        public void Save_LinkedOverwriteRacedBeforePublish_Reprompts()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("raced-overwrite.opweave.json");
                ImportAndLink(graph, path);
                var external1 = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Racer1\"");
                var external2 = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Racer2\"");
                File.WriteAllText(path, external1);
                var racer2Doc = WorkflowSerializer.Read(external2).Value!;

                var planted = false;
                GraphJsonSync.PublishProbe = probePath =>
                {
                    if (!planted &&
                        Path.GetFullPath(probePath).Equals(
                            Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    {
                        planted = true;
                        File.WriteAllText(path, external2);
                    }
                };
                try
                {
                    var prompts = 0;
                    var result = GraphJsonSync.Save(graph, check =>
                    {
                        prompts++;
                        if (prompts == 1)
                        {
                            return GraphJsonSyncChoice.OverwriteJson;
                        }
                        Assert.AreEqual(
                            racer2Doc.Canonical.WorkflowHash,
                            check.Document!.Canonical.WorkflowHash,
                            "the new prompt presents the raced revision");
                        return GraphJsonSyncChoice.Cancel;
                    });

                    Assert.AreEqual(2, prompts,
                        "the final byte comparison catches the raced write");
                    Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, result.Outcome);
                    Assert.AreEqual(external2, File.ReadAllText(path),
                        "the raced revision is never overwritten");
                }
                finally
                {
                    GraphJsonSync.PublishProbe = null;
                }
            });
        }

        // A second run with no change writes nothing — the already-applied
        // output reconciles without a write or an mtime change.
        [Test]
        public void Save_AlreadyApplied_NoWriteNoMtimeChange()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("idempotent.opweave.json");
                ImportAndLink(graph, path);
                var first = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Saved, first.Outcome);

                var bytes = File.ReadAllBytes(path);
                var marker = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, marker);

                var again = GraphJsonSync.Save(graph, null);

                Assert.AreEqual(GraphJsonSyncOutcome.Saved, again.Outcome);
                Assert.AreEqual(
                    marker, File.GetLastWriteTimeUtc(path),
                    "an already-applied output writes nothing — mtime untouched");
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            });
        }

        // The already-applied reconcile still proves the baseline durable
        // (SPEC §H7 item 3): a persist that reports unproven moves the
        // in-memory baseline only — the outcome is pending, never Saved,
        // and the stored .opweavegraph keeps the old baseline.
        [Test]
        public void Save_AlreadyApplied_PersistUnproven_ReportsPending()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("reconcile-pending.opweave.json");
                ImportAndLink(graph, path);
                // The already-applied reconcile runs only inside the
                // recorded projection: the merge's output always carries
                // the export's layout member, which a baseline synced to
                // the raw fixture lacks — the arranged file would be a
                // projection conflict, never the reconcile branch. A
                // first real save moves the baseline to the exported
                // document the fixture link produced.
                var first = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Saved, first.Outcome);
                // The stored baseline the reconcile must leave untouched.
                GraphDatabase.SaveGraph(graph);
                var storedHash = graph.syncedBytesHash;
                var storedText = File.ReadAllText(TempPath);

                // A file holding an already-applied output over a
                // metadata-changed source: the GUI-owned projection still
                // matches the baseline (title is not GUI-owned) and the
                // bytes are exactly what the merge would emit — the
                // reconcile branch, never a write.
                var externalText = File.ReadAllText(path)
                    .Replace("Sync fixture", "Renamed outside");
                var externalDoc = WorkflowSerializer.Read(externalText).Value!;
                var merged = GraphJsonConverter.Export(graph, externalDoc);
                Assert.IsTrue(merged.Ok);
                var applied = GraphJsonFile.BuildOutput(merged.Value!, externalText);
                File.WriteAllBytes(path, applied.Bytes);
                var appliedHash = JsonSaveCoordinator.Sha256Hex(applied.Bytes);
                var marker = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, marker);

                GraphJsonSync.PersistFault = _ =>
                    throw new InvalidOperationException("injected persist failure");
                try
                {
                    var result = GraphJsonSync.Save(graph, null);

                    Assert.AreEqual(GraphJsonSyncOutcome.PersistPending, result.Outcome,
                        "an unproven persist reports pending, never Saved");
                    Assert.AreEqual(marker, File.GetLastWriteTimeUtc(path),
                        "the reconcile writes nothing");
                    Assert.AreEqual(appliedHash, graph.syncedBytesHash,
                        "the in-memory baseline moved to the reconciled revision");
                    StringAssert.Contains("Renamed outside", graph.syncedJson,
                        "the in-memory baseline document moved");
                    Assert.AreEqual(storedText, File.ReadAllText(TempPath),
                        "an unproven persist leaves the stored .opweavegraph untouched");
                    StringAssert.Contains(storedHash, storedText,
                        "the stored baseline is unchanged");
                }
                finally
                {
                    GraphJsonSync.PersistFault = null;
                }
            });
        }

        // "Reload from JSON" proves the reloaded baseline durable before
        // it reports done (SPEC §H7 item 3): a persist that reports
        // unproven moves the in-memory baseline only — the outcome is
        // pending, never Reloaded, and the stored .opweavegraph keeps
        // the old baseline.
        [Test]
        public void Save_ReloadChoice_PersistUnproven_ReportsPending()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("reload-pending.opweave.json");
                ImportAndLink(graph, path);
                GraphDatabase.SaveGraph(graph);
                var storedHash = graph.syncedBytesHash;
                var storedText = File.ReadAllText(TempPath);

                var external = FixtureSync
                    .Replace("\"name\": \"Alpha\"", "\"name\": \"External\"");
                File.WriteAllText(path, external);
                var externalHash =
                    JsonSaveCoordinator.Sha256Hex(File.ReadAllBytes(path));

                GraphJsonSync.PersistFault = _ =>
                    throw new InvalidOperationException("injected persist failure");
                try
                {
                    var result = GraphJsonSync.Save(
                        graph, _ => GraphJsonSyncChoice.ReloadJson);

                    Assert.AreEqual(GraphJsonSyncOutcome.PersistPending, result.Outcome,
                        "an unproven persist reports pending, never Reloaded");
                    Assert.AreEqual(external, File.ReadAllText(path),
                        "reload must not touch the file");
                    Assert.AreEqual(externalHash, graph.syncedBytesHash,
                        "the in-memory baseline moved to the reloaded revision");
                    Assert.AreEqual(storedText, File.ReadAllText(TempPath),
                        "an unproven persist leaves the stored .opweavegraph untouched");
                    StringAssert.Contains(storedHash, storedText,
                        "the stored baseline is unchanged");
                }
                finally
                {
                    GraphJsonSync.PersistFault = null;
                }
            });
        }

        // The model-clean persist (pending-persist-outcome): the explicit
        // save publishes the JSON, the sync-state-only baseline change is
        // made durable through the supported dirty-and-flush path, and the
        // intent record clears only on that proof.
        [Test]
        public void Save_ModelCleanGraph_BaselineDurableAndIntentCleared()
        {
            WithGraph(graph =>
            {
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);
                // The graph model is already saved: the baseline move the
                // first link performs is a sync-state-only change.
                GraphDatabase.SaveGraph(graph);

                var save = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Saved, save.Outcome);
                var written = WorkflowSerializer.Read(File.ReadAllText(SiblingJsonPath));
                Assert.IsTrue(written.Ok);
                AssertSynced(graph, SiblingJsonPath, written.Value!);

                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var coordinator = TestCoordinator();
                Assert.IsFalse(
                    File.Exists(coordinator.IntentPathFor(guid)),
                    "a durable baseline clears the write's intent record");

                var persisted = File.ReadAllText(TempPath);
                StringAssert.Contains(graph.syncedBytesHash, persisted,
                    "the persisted .opweavegraph carries the new baseline");
                Assert.IsTrue(
                    persisted.Contains(graph.jsonPath) ||
                        persisted.Contains(graph.jsonPath.Replace("\\", "\\\\")) ||
                        persisted.Contains(graph.jsonPath.Replace("\\", "/")),
                    "the persisted .opweavegraph carries the link");
            });
        }

        // The interim guard: while the graph's GUID-keyed intent slot
        // holds a record no recovery handled, the save refuses with a
        // message naming the record path and the record is left exactly
        // as found (SPEC §H7 item 3). A well-formed record naming a path
        // that is neither the graph's recorded link nor its sibling is
        // such a record: ReadIntent reports it Invalid, the recovery
        // leaves it untouched, and the guard's refusal applies.
        [Test]
        public void Save_SlotHoldsRecord_RefusedRecordUntouched()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("slot-held.opweave.json");
                ImportAndLink(graph, path);
                var coordinator = TestCoordinator();
                var other = coordinator.ResolveOwnedPath(
                    JsonPath("slot-held-other.opweave.json")).ResolvedPath!;
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var bytes = File.ReadAllBytes(path);
                var diskDoc = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    other, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    GraphJsonFile.GuiOwnedHash(diskDoc))).Ok);
                var recordPath = coordinator.IntentPathFor(guid);
                Assert.IsTrue(File.Exists(recordPath));
                var recordBytes = File.ReadAllBytes(recordPath);

                var prompts = 0;
                var result = GraphJsonSync.Save(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.OverwriteJson;
                });

                Assert.AreEqual(GraphJsonSyncOutcome.Refused, result.Outcome);
                Assert.AreEqual(0, prompts,
                    "a held intent slot refuses before any prompt");
                StringAssert.Contains(recordPath, result.Check.ErrorMessage ?? "",
                    "the refusal names the record path");
                Assert.IsTrue(File.Exists(recordPath),
                    "the record stays in place");
                CollectionAssert.AreEqual(recordBytes, File.ReadAllBytes(recordPath),
                    "the record's bytes are untouched");
                Assert.AreEqual(FixtureSync, File.ReadAllText(path),
                    "the JSON is untouched");
            });
        }

        // A decision a previous prompt made for the exact (P, R, output,
        // B) request is reused by an automatic attempt — bound to the
        // presented revision, without a dialog.
        [Test]
        public void Save_RememberedDecision_AutomaticAttemptReusesIt()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("coalesced.opweave.json");
                var baseline = ImportAndLink(graph, path);
                var external = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"External\"");
                File.WriteAllText(path, external);

                var prompts = 0;
                var cancelled = GraphJsonSync.Save(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.Cancel;
                });
                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, cancelled.Outcome);
                Assert.AreEqual(1, prompts, "the explicit save asks once");

                var automatic = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, automatic.Outcome);
                Assert.IsFalse(automatic.PromptShown,
                    "the remembered decision applies with no dialog");
                Assert.AreEqual(external, File.ReadAllText(path),
                    "the remembered cancel still touches nothing");
                AssertSynced(graph, path, baseline);
            });
        }

        // Graph Toolkit's own Save — Ctrl+S / the window Save, driven here
        // through GraphDatabase.SaveGraph — writes only the .opweavegraph:
        // it never writes the JSON, never links, never moves B (SPEC §H7
        // item 3, owner decision 2026-11-14).
        [Test]
        public void SaveGraph_WritesNoJson()
        {
            WithGraph(graph =>
            {
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);
                GraphDatabase.SaveGraph(graph);
                Assert.IsFalse(File.Exists(SiblingJsonPath),
                    "the asset save writes no sibling JSON");
                Assert.AreEqual(0, graph.jsonPath.Length,
                    "the asset save records no link");

                var path = JsonPath("savegraph.opweave.json");
                var baseline = ImportAndLink(graph, path);
                var bytes = File.ReadAllBytes(path);
                Assert.IsTrue(GraphJsonConverter.Import(
                    graph,
                    FixtureSync.Replace(
                        "\"name\": \"Alpha\"", "\"name\": \"Unsynced\"")).Ok);
                GraphDatabase.SaveGraph(graph);
                CollectionAssert.AreEqual(
                    bytes, File.ReadAllBytes(path),
                    "the asset save never writes the linked JSON");
                AssertSynced(graph, path, baseline,
                    "the asset save never moves the baseline");
            });
        }

        // ── helpers ─────────────────────────────────────────────────────────

        // The test-side coordinator over the real project root: intent
        // records are crafted through the production write path so the
        // save reads exactly what a real writer left.
        private static JsonSaveCoordinator TestCoordinator() =>
            new JsonSaveCoordinator(Path.GetFullPath(
                Path.Combine(Application.dataPath, "..")));

        // Fixture isolation: every intent record under the test project's
        // jsonsync directory. The fixture graph reuses one asset GUID
        // across re-creations, so a record a previous test left — the
        // pending persist of an interrupted publish — would sit under the
        // same slot and refuse the next save. Swept in SetUp and TearDown.
        private static void ClearIntentRecords()
        {
            var dir = TestCoordinator().IntentDirectory;
            if (!Directory.Exists(dir))
            {
                return;
            }
            foreach (var file in Directory.GetFiles(dir, "graph-*.intent.json"))
            {
                File.Delete(file);
            }
        }

        private static int IntentRecordCount()
        {
            var dir = TestCoordinator().IntentDirectory;
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "graph-*.intent.json").Length
                : 0;
        }

        private string JsonPath(string name) => Path.Combine(_jsonDir, name);

        // The standalone sync relation the linked state bears to the file
        // — the test-side stand-in for the open check (T-W5.3-07 owns the
        // production CheckOnOpen): the file parses and its GUI-owned
        // projection equals the stored sync hash.
        private static GraphJsonSyncState SyncStateOf(OpWeaveGraph graph)
        {
            if (graph.jsonPath.Length == 0)
            {
                return GraphJsonSyncState.Unlinked;
            }
            var disk = GraphJsonFile.Read(graph.jsonPath);
            if (disk.ErrorMessage is not null)
            {
                return GraphJsonSyncState.IoError;
            }
            if (!disk.Exists)
            {
                return GraphJsonSyncState.Missing;
            }
            if (disk.Document is not { Ok: true } read)
            {
                return GraphJsonSyncState.InvalidJson;
            }
            return string.Equals(
                GraphJsonFile.GuiOwnedHash(read.Value!),
                graph.syncedGuiOwnedHash, StringComparison.Ordinal)
                ? GraphJsonSyncState.InSync
                : GraphJsonSyncState.ExternalModification;
        }

        // The sibling JSON the first-save link writes next to the graph
        // asset. DeleteAsset covers the imported case; the file may not be
        // imported yet, so a direct delete sweeps the rest.
        private static void DeleteSiblingJson()
        {
            AssetDatabase.DeleteAsset(SiblingJsonPath);
            if (File.Exists(SiblingJsonPath))
            {
                File.Delete(SiblingJsonPath);
            }
            var meta = SiblingJsonPath + ".meta";
            if (File.Exists(meta))
            {
                File.Delete(meta);
            }
        }

        // Imports the fixture, writes it to disk and records it as the
        // graph's synced state — the equivalent of a GUI "import JSON"
        // followed by a save. The recorded byte revision is the exact
        // file the sync was made against — what a real link observes.
        private static WorkflowDocument ImportAndLink(OpWeaveGraph graph, string path)
        {
            var read = GraphJsonConverter.Import(graph, FixtureSync);
            Assert.IsTrue(read.Ok, "the fixture must import");
            var document = read.Value!;
            File.WriteAllText(path, FixtureSync);
            GraphJsonSync.MarkSynced(
                graph, path, document,
                JsonSaveCoordinator.Sha256Hex(File.ReadAllBytes(path)));
            return document;
        }

        private static void AssertSynced(
            OpWeaveGraph graph, string path, WorkflowDocument document,
            string? message = null)
        {
            Assert.AreEqual(path, graph.jsonPath, message);
            Assert.AreEqual(
                document.Canonical.WorkflowHash, graph.syncedWorkflowHash,
                message ?? "the stored hash must be the synced document's canonical hash");
        }

        private static void WithGraph(Action<OpWeaveGraph> body)
        {
            var folderCreated = false;
            var assetCreated = false;
            try
            {
                if (!AssetDatabase.IsValidFolder(TempDir))
                {
                    AssetDatabase.CreateFolder("Assets", "GraphJsonSyncTests");
                    folderCreated = true;
                }
                if (AssetDatabase.LoadMainAssetAtPath(TempPath) is not null)
                {
                    AssetDatabase.DeleteAsset(TempPath);
                }
                DeleteSiblingJson();
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
                DeleteSiblingJson();
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
                Op.Define("test.conv.alpha", 1)
                    .Summary("Sync fixture alpha.")
                    .Input("target", "unity.GameObjectRef", required: true)
                    .Input("name", "core.String", required: false)
                    .Output("result", "unity.GameObjectRef")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                Op.Define("test.conv.beta", 1)
                    .Summary("Sync fixture beta.")
                    .Input("source", "unity.GameObjectRef", required: false)
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
                    new string((char)('A' + i), 64))
                {
                    // OperationValidationPhase refuses a snapshot entry
                    // without an implementation instance: a published §C3
                    // snapshot always carries the instantiated Operation
                    // (step 6), so the fixture ops need a no-op one for any
                    // validation path that compiles against this snapshot.
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

        // The no-op IOperation every snapshot entry carries: a live
        // validation reaches it only through the D3 phase's
        // ValidateStatic — it is no IEffectResolver for the D6 phase to
        // consult, and the Runner lifecycle members never run on a
        // fixture op.
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
