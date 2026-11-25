#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.3-07 [Edit] tests (SPEC §H7 item 3, SPEC §0.6 SG-17): a linked
// graph's JSON is checked when the graph opens through the real Graph
// Toolkit lifecycle — the [OnOpenAsset] request and OnEnable only queue
// the check, the deferred delayCall drain performs it — exactly one
// check and at most one dialog per reopen, for the cached and the
// uncached instance alike, including the open-time reconcile of an
// interrupted save. Below 6000.5 the OpWeave.GraphToolkit asmdef is
// excluded and this file compiles to nothing.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using OpWeave.GraphToolkit;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed partial class GraphJsonSyncTests
    {
        // ── local:T-W5.3-07:json-open-check (SPEC §H7 test 2, §0.6 SG-17) ──

        [Test]
        public void Open_UnchangedJson_NoPrompt()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("unchanged.opweave.json");
                var baseline = ImportAndLink(graph, path);

                var prompts = 0;
                var result = GraphJsonSync.ResolveOnOpen(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreEqual(GraphJsonSyncOutcome.NoAction, result.Outcome);
                Assert.AreEqual(GraphJsonSyncState.InSync, result.Check.State);
                Assert.IsFalse(result.PromptShown);
                Assert.AreEqual(0, prompts, "an unchanged JSON must open without a prompt");
                AssertSynced(graph, path, baseline);
            });
        }

        [Test]
        public void Open_MetadataOnlyExternalChange_NoPrompt()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("metaonly.opweave.json");
                var baseline = ImportAndLink(graph, path);

                // Only non-GUI-owned members changed: the canonical hash is
                // unchanged, so no prompt (SPEC §0.6 SG-17).
                File.WriteAllText(path, FixtureSync
                    .Replace("Sync fixture", "Edited outside")
                    .Replace("\"reviewed\": true", "\"reviewed\": false"));

                var prompts = 0;
                var result = GraphJsonSync.ResolveOnOpen(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreEqual(GraphJsonSyncOutcome.NoAction, result.Outcome);
                Assert.AreEqual(GraphJsonSyncState.InSync, result.Check.State);
                Assert.AreEqual(0, prompts);
                AssertSynced(graph, path, baseline);
            });
        }

        [Test]
        public void Open_ExternalEdit_PromptsAndReloadApplies()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("reloaded.opweave.json");
                var baseline = ImportAndLink(graph, path);

                // An out-of-band canonical change, as an Agent edit would be.
                var external = FixtureSync.Replace("\"name\": \"Alpha\"", "\"name\": \"External\"");
                File.WriteAllText(path, external);
                var externalDoc = WorkflowSerializer.Read(external).Value!;
                Assert.AreNotEqual(
                    baseline.Canonical.WorkflowHash, externalDoc.Canonical.WorkflowHash);

                var prompts = 0;
                GraphJsonSyncCheck? seen = null;
                var result = GraphJsonSync.ResolveOnOpen(graph, check =>
                {
                    prompts++;
                    seen = check;
                    return GraphJsonSyncChoice.ReloadJson;
                });

                Assert.AreEqual(1, prompts, "the external edit must produce the choice");
                Assert.IsTrue(result.PromptShown);
                Assert.AreEqual(GraphJsonSyncChoice.ReloadJson, result.Choice);
                Assert.AreEqual(GraphJsonSyncOutcome.Reloaded, result.Outcome);
                Assert.AreEqual(GraphJsonSyncState.ExternalModification, seen!.State);
                Assert.AreEqual(
                    externalDoc.Canonical.WorkflowHash, seen.Document!.Canonical.WorkflowHash,
                    "the prompt receives the conflicting on-disk document");
                Assert.AreEqual(external, File.ReadAllText(path),
                    "reload must not touch the file");

                // The graph now models the external document: a re-export is
                // canonically identical and the baseline moved.
                var exported = GraphJsonConverter.Export(graph, externalDoc);
                Assert.IsTrue(exported.Ok);
                Assert.AreEqual(
                    externalDoc.Canonical.CanonicalText,
                    exported.Value!.Canonical.CanonicalText,
                    "after reload the graph models the external document");
                AssertSynced(graph, path, externalDoc);

                // A second open sees an in-sync file: no prompt.
                var again = GraphJsonSync.ResolveOnOpen(graph, _ => GraphJsonSyncChoice.Cancel);
                Assert.AreEqual(GraphJsonSyncOutcome.NoAction, again.Outcome);
                Assert.AreEqual(GraphJsonSyncState.InSync, again.Check.State);
            });
        }

        [Test]
        public void Open_ExternalEdit_OverwriteApplies()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("overwritten.opweave.json");
                var baseline = ImportAndLink(graph, path);

                var external = FixtureSync.Replace("\"name\": \"Alpha\"", "\"name\": \"External\"");
                File.WriteAllText(path, external);

                var result = GraphJsonSync.ResolveOnOpen(
                    graph, _ => GraphJsonSyncChoice.OverwriteJson);

                Assert.IsTrue(result.PromptShown);
                Assert.AreEqual(GraphJsonSyncOutcome.Overwritten, result.Outcome);
                Assert.AreEqual(GraphJsonSaveStatus.Saved, result.SaveStatus);

                // The graph's owned content won; the file now matches the
                // graph, not the external edit.
                var written = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(written.Ok);
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    written.Value!.Canonical.CanonicalText,
                    "the graph's owned keys overwrite the file");
                AssertSynced(graph, path, written.Value!);
            });
        }

        [Test]
        public void Open_ExternalEdit_CancelOverwritesNothing()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("cancelled.opweave.json");
                var baseline = ImportAndLink(graph, path);

                var external = FixtureSync.Replace("\"name\": \"Alpha\"", "\"name\": \"External\"");
                File.WriteAllText(path, external);

                var result = GraphJsonSync.ResolveOnOpen(graph, _ => GraphJsonSyncChoice.Cancel);

                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, result.Outcome);
                Assert.AreEqual(external, File.ReadAllText(path),
                    "cancel must leave the externally edited file untouched");
                AssertSynced(graph, path, baseline,
                    "cancel must keep the previous sync baseline");

                // The graph still models the baseline, not the external
                // document — nothing was silently applied.
                var exported = GraphJsonConverter.Export(graph, baseline);
                Assert.IsTrue(exported.Ok);
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    exported.Value!.Canonical.CanonicalText);

                // The divergence persists: the next check reports it again.
                var recheck = GraphJsonSync.CheckOnOpen(graph);
                Assert.AreEqual(GraphJsonSyncState.ExternalModification, recheck.State);
            });
        }

        [Test]
        public void Open_DeletedJson_PromptsAndOverwriteRecreates()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("deleted.opweave.json");
                var baseline = ImportAndLink(graph, path);
                File.Delete(path);

                var cancelled = GraphJsonSync.ResolveOnOpen(graph, check =>
                {
                    Assert.AreEqual(GraphJsonSyncState.Missing, check.State);
                    return GraphJsonSyncChoice.Cancel;
                });
                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, cancelled.Outcome);
                Assert.IsFalse(File.Exists(path), "cancel must not recreate the file");

                var reloaded = GraphJsonSync.ResolveOnOpen(graph, _ => GraphJsonSyncChoice.ReloadJson);
                Assert.AreEqual(GraphJsonSyncOutcome.ReloadFailed, reloaded.Outcome,
                    "there is no document to reload from a missing file");
                Assert.IsFalse(File.Exists(path));

                var result = GraphJsonSync.ResolveOnOpen(
                    graph, _ => GraphJsonSyncChoice.OverwriteJson);
                Assert.AreEqual(GraphJsonSyncOutcome.Overwritten, result.Outcome);
                Assert.IsTrue(File.Exists(path),
                    "an explicit human overwrite recreates the file");
                var written = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(written.Ok);
                Assert.AreEqual(
                    baseline.Canonical.CanonicalText,
                    written.Value!.Canonical.CanonicalText);
            });
        }

        [Test]
        public void Open_InvalidJson_PromptsAndNeverOverwrites()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("invalid.opweave.json");
                var baseline = ImportAndLink(graph, path);
                File.WriteAllText(path, "{ this is not valid json");

                var reloaded = GraphJsonSync.ResolveOnOpen(graph, check =>
                {
                    Assert.AreEqual(GraphJsonSyncState.InvalidJson, check.State);
                    Assert.IsNotEmpty(check.Diagnostics.ToList());
                    return GraphJsonSyncChoice.ReloadJson;
                });
                Assert.AreEqual(GraphJsonSyncOutcome.ReloadFailed, reloaded.Outcome,
                    "an unparseable file cannot be reloaded");

                var result = GraphJsonSync.ResolveOnOpen(
                    graph, _ => GraphJsonSyncChoice.OverwriteJson);
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, result.Outcome);
                Assert.AreEqual(GraphJsonSaveStatus.InvalidJson, result.SaveStatus,
                    "overwriting unparseable JSON is refused even explicitly");
                Assert.AreEqual("{ this is not valid json", File.ReadAllText(path),
                    "the unparseable file is never destroyed");
                AssertSynced(graph, path, baseline);
            });
        }

        [Test]
        public void Open_UnlinkedGraph_NoPrompt()
        {
            WithGraph(graph =>
            {
                var result = GraphJsonSync.ResolveOnOpen(graph, _ =>
                {
                    Assert.Fail("an unlinked graph must never prompt");
                    return GraphJsonSyncChoice.Cancel;
                });
                Assert.AreEqual(GraphJsonSyncOutcome.NoAction, result.Outcome);
                Assert.AreEqual(GraphJsonSyncState.Unlinked, result.Check.State);
            });
        }

        // ── SPEC §H7.3 acceptance: external edit between sessions ───────
        //
        // The mandatory external-edit scenario end to end: the link is
        // established through the explicit production save — GraphJsonSync.
        // Save adopts the sibling .opweave.json — and the link is persisted
        // through a real graph-asset save (GraphDatabase.SaveGraph, the
        // public API the graph window's Save calls). The JSON is edited on
        // disk between sessions and the graph is reopened through the real
        // user-facing open API — AssetDatabase.OpenAsset drives the
        // editor's documented [OnOpenAsset] dispatch, the same chain that
        // opens the graph window — and the queued check drains through the
        // real EditorApplication.delayCall subscription the shared
        // DrainOpenChecks wait pumps until the check lands (an in-flight
        // asset refresh can hold the drain past a single frame). The
        // conflict choice is observed through the injectable provider. No
        // test invokes a lifecycle hook, drains the queue itself or drops
        // a cache.
        //
        // One invariant on both reopen paths — exactly one performed
        // check per reopen. The CACHED path keeps the graph instance
        // loaded, so the reopen produces no OnEnable at all and the
        // open-asset hook must request the check; the UNCACHED path opens
        // a never-loaded asset — the persisted .opweavegraph copied to a
        // fresh GUID — so the open deserializes a new instance whose
        // single OnEnable requests it. A copy only constructs the
        // uncached precondition; the open itself is the production flow
        // on both paths.

        [UnityTest]
        public IEnumerator Open_ExternalEditBetweenSessions_OpenHookAppliesEachChoice() =>
            ExternalEditBetweenSessionsOpenHookAppliesEachChoice(uncached: false);

        [UnityTest]
        public IEnumerator Open_ExternalEditBetweenSessions_UncachedOpenHookAppliesEachChoice() =>
            ExternalEditBetweenSessionsOpenHookAppliesEachChoice(uncached: true);

        private IEnumerator ExternalEditBetweenSessionsOpenHookAppliesEachChoice(bool uncached)
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
                var created = GraphDatabase.CreateGraph<OpWeaveGraph>(TempPath);
                assetCreated = created is not null;
                Assert.IsNotNull(created, "GraphDatabase.CreateGraph must return a graph");
                var graph = created!;

                // (1) The initial link through the production save flow:
                // the explicit GUI save adopts the sibling .opweave.json
                // and writes the JSON source of truth, then a real
                // GraphDatabase.SaveGraph persists the link into the
                // .opweavegraph. The .opweavegraph's own serialization
                // performs no JSON write — SPEC §H7 item 3 binds the
                // write to the explicit save path only.
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);
                var linkedSave = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Saved, linkedSave.Outcome,
                    "the explicit first save links and writes the JSON");
                GraphDatabase.SaveGraph(graph);

                Assert.AreEqual(SiblingJsonPath, graph.jsonPath,
                    "the asset save must establish the sibling .opweave.json link");
                Assert.IsTrue(File.Exists(SiblingJsonPath),
                    "the first save writes the JSON source of truth");
                var linked = WorkflowSerializer.Read(File.ReadAllText(SiblingJsonPath));
                Assert.IsTrue(linked.Ok);
                var baseline = linked.Value!;
                AssertSynced(graph, SiblingJsonPath, baseline,
                    "the save records the path and baseline in the graph");
                Assert.IsTrue(
                    File.ReadAllText(TempPath).Contains(SiblingJsonPath),
                    "the link is serialized into the .opweavegraph itself");

                var prompts = 0;
                GraphJsonSyncCheck? seen = null;
                var answer = GraphJsonSyncChoice.Cancel;
                GraphJsonSync.OpenChoiceProvider = check =>
                {
                    prompts++;
                    seen = check;
                    return answer;
                };
                try
                {
                    // The reopen vehicle for every step below: the cached
                    // path must return the already-loaded instance and the
                    // uncached path a freshly deserialized one — the two
                    // production edges the open check hangs on.
                    var assetGuid = AssetDatabase.AssetPathToGUID(TempPath);
                    var previous = graph;
                    var sourcePath = TempPath;
                    var copyIndex = 0;
                    var performed = 0;
                    var dialogs = 0;
                    OpWeaveGraph Reopen(out string openedGuid)
                    {
                        if (!uncached)
                        {
                            var enablesBefore = EnablesOf(assetGuid);
                            var requestsBefore = RequestsOf(assetGuid);
                            var next = OpenGraphAt(TempPath);
                            Assert.AreSame(previous, next,
                                "a cached reopen must return the already-loaded instance");
                            Assert.AreEqual(enablesBefore, EnablesOf(assetGuid),
                                "a cached reopen produces no OnEnable at all");
                            Assert.AreEqual(requestsBefore + 1, RequestsOf(assetGuid),
                                "the open-asset hook requests exactly one check");
                            previous = next;
                            openedGuid = assetGuid;
                            return next;
                        }
                        // A never-loaded asset — the persisted
                        // .opweavegraph bytes under a fresh GUID — so the
                        // production open must deserialize a new
                        // instance: no live instance and an enable count
                        // of zero before the open, exactly one OnEnable
                        // after it.
                        var copyPath = TempDir + "/reopen" + (++copyIndex) +
                            "." + OpWeaveGraph.AssetExtension;
                        Assert.IsTrue(
                            AssetDatabase.CopyAsset(sourcePath, copyPath),
                            "the uncached reopen copies the persisted .opweavegraph");
                        var copyGuid = AssetDatabase.AssetPathToGUID(copyPath);
                        Assert.IsNull(GraphJsonSync.FindLiveGraph(copyPath),
                            "the uncached precondition: no live instance for the fresh asset");
                        Assert.AreEqual(0, EnablesOf(copyGuid),
                            "the uncached precondition: the fresh asset was never enabled");
                        var fresh = OpenGraphAt(copyPath);
                        Assert.AreNotSame(previous, fresh,
                            "an uncached reopen must deserialize a fresh instance");
                        Assert.AreEqual(1, EnablesOf(copyGuid),
                            "an uncached reopen runs OnEnable exactly once");
                        Assert.GreaterOrEqual(RequestsOf(copyGuid), 1,
                            "the fresh instance's OnEnable requested the open check");
                        previous = fresh;
                        openedGuid = copyGuid;
                        // The next uncached reopen inherits the latest
                        // persisted state: a resolved choice persisted
                        // the copy, a cancelled one left it as copied.
                        sourcePath = copyPath;
                        return fresh;
                    }

                    // (2) The link — path and baseline — is persisted in
                    // the .opweavegraph: the reopen reads it back through
                    // the real open path, and the file still matches the
                    // baseline — the drain performs its one check and
                    // shows no prompt.
                    var reopened = Reopen(out var reopenedGuid);
                    Assert.AreEqual(SiblingJsonPath, reopened.jsonPath,
                        "the JSON link persists in the .opweavegraph");
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(reopenedGuid,
                            PerformedOf(reopenedGuid), DroppedOf(reopenedGuid));
                        headless?.AssertOnlyExpected();
                    }
                    Assert.AreEqual(++performed, TotalPerformed(),
                        "exactly one performed check per reopen — never a duplicate");
                    Assert.AreEqual(0, prompts,
                        "an unchanged JSON opens without a prompt");
                    Assert.AreEqual(dialogs, TotalDialogs(),
                        "an unchanged JSON opens without a dialog");

                    // (3) An out-of-band canonical edit between sessions.
                    var external = FixtureSync.Replace(
                        "\"name\": \"Alpha\"", "\"name\": \"External\"");
                    File.WriteAllText(SiblingJsonPath, external);
                    var externalDoc = WorkflowSerializer.Read(external).Value!;

                    // (4)+(6a) Reopen → the queued check reports the
                    // divergence; Cancel applies nothing.
                    var cancelled = Reopen(out var cancelledGuid);
                    Assert.AreEqual(SiblingJsonPath, cancelled.jsonPath);
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(cancelledGuid,
                            PerformedOf(cancelledGuid), DroppedOf(cancelledGuid));
                        headless?.AssertOnlyExpected();
                    }
                    Assert.AreEqual(++performed, TotalPerformed(),
                        "exactly one performed check per reopen — never a duplicate");
                    Assert.AreEqual(1, prompts,
                        "the open check asks the injected provider");
                    Assert.AreEqual(++dialogs, TotalDialogs(),
                        "at most one dialog per decided revision");
                    Assert.AreEqual(
                        GraphJsonSyncState.ExternalModification, seen!.State);
                    Assert.AreEqual(external, File.ReadAllText(SiblingJsonPath),
                        "cancel must leave the externally edited file untouched");
                    AssertSynced(cancelled, SiblingJsonPath, baseline,
                        "cancel keeps the baseline persisted in the .opweavegraph");

                    // (6a') The choice is exactly once PER REOPEN: a
                    // separate later reopen of the same unchanged
                    // conflict shows the prompt again — a remembered
                    // Cancel never carries into it — and a repeated
                    // Cancel still touches nothing.
                    prompts = 0;
                    seen = null;
                    var cancelledAgain = Reopen(out var cancelledAgainGuid);
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(cancelledAgainGuid,
                            PerformedOf(cancelledAgainGuid),
                            DroppedOf(cancelledAgainGuid));
                        headless?.AssertOnlyExpected();
                    }
                    Assert.AreEqual(++performed, TotalPerformed(),
                        "the drain still performs its one check");
                    Assert.AreEqual(1, prompts,
                        "a later reopen of the unchanged conflict prompts again");
                    Assert.AreEqual(++dialogs, TotalDialogs(),
                        "the choice shows exactly once per reopen");
                    Assert.AreEqual(
                        GraphJsonSyncState.ExternalModification, seen!.State,
                        "the new prompt presents the same unchanged conflict");
                    Assert.AreEqual(external, File.ReadAllText(SiblingJsonPath),
                        "a repeated cancel still touches nothing");
                    AssertSynced(cancelledAgain, SiblingJsonPath, baseline,
                        "a repeated cancel keeps the previous baseline");

                    // (6a'') ... and the same unchanged conflict can still
                    // be completed on a later reopen: Overwrite writes
                    // the graph's owned content over the external edit.
                    prompts = 0;
                    seen = null;
                    answer = GraphJsonSyncChoice.OverwriteJson;
                    var completed = Reopen(out var completedGuid);
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(completedGuid,
                            PerformedOf(completedGuid), DroppedOf(completedGuid));
                        headless?.AssertOnlyExpected();
                    }
                    Assert.AreEqual(++performed, TotalPerformed());
                    Assert.AreEqual(1, prompts,
                        "the completing reopen prompts again");
                    Assert.AreEqual(++dialogs, TotalDialogs());
                    var completedText = File.ReadAllText(SiblingJsonPath);
                    Assert.IsTrue(completedText.Contains("\"name\": \"Alpha\""),
                        "the graph's owned content wins over the external edit");
                    var completedDoc = WorkflowSerializer.Read(completedText);
                    Assert.IsTrue(completedDoc.Ok);
                    AssertSynced(completed, SiblingJsonPath, completedDoc.Value!);

                    // (6b) A further out-of-band edit is a NEW revision:
                    // the reopen prompts again, and Overwrite replaces
                    // the file and moves the baseline.
                    File.WriteAllText(SiblingJsonPath, FixtureSync
                        .Replace("\"name\": \"Alpha\"", "\"name\": \"External2\""));
                    prompts = 0;
                    seen = null;
                    answer = GraphJsonSyncChoice.OverwriteJson;
                    var overwritten = Reopen(out var overwrittenGuid);
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(overwrittenGuid,
                            PerformedOf(overwrittenGuid), DroppedOf(overwrittenGuid));
                        headless?.AssertOnlyExpected();
                    }
                    Assert.AreEqual(++performed, TotalPerformed());
                    Assert.AreEqual(1, prompts);
                    Assert.AreEqual(++dialogs, TotalDialogs());
                    var text = File.ReadAllText(SiblingJsonPath);
                    Assert.IsTrue(text.Contains("\"name\": \"Alpha\""),
                        "the graph's owned content wins over the external edit");
                    Assert.IsTrue(text.Contains("\"id\": \"test.sync.main\""),
                        "the file's non-owned members are preserved");
                    var written = WorkflowSerializer.Read(text);
                    Assert.IsTrue(written.Ok);
                    AssertSynced(overwritten, SiblingJsonPath, written.Value!);

                    // (6b') The overwrite's publish stays pending-persist
                    // until the .opweavegraph provably carries it: EITHER
                    // the persisted file — not just the live instance —
                    // already records the newly published baseline, OR
                    // the coordinator still holds an actionable intent
                    // for exactly this published revision — the
                    // pending-persist state the next entry's recovery
                    // reconciles (SPEC §H7 item 3's intent → publish →
                    // persist → clear).
                    Assert.AreEqual(
                        JsonSaveCoordinator.Sha256Hex(
                            File.ReadAllBytes(SiblingJsonPath)),
                        overwritten.syncedBytesHash,
                        "the recorded baseline is the published file's byte revision");
                    Assert.IsTrue(
                        File.ReadAllText(sourcePath).Contains(
                            overwritten.syncedBytesHash) ||
                        string.Equals(
                            ActionableIntentOutputHash(overwritten),
                            overwritten.syncedBytesHash,
                            StringComparison.Ordinal),
                        "the published baseline is either already in the " +
                        "persisted .opweavegraph or held pending-persist " +
                        "by an actionable intent");

                    // (6b'') A retained intent is never a prompt or a
                    // write by itself: re-entering the open check on the
                    // SAME graph takes the existing reconcile branch —
                    // no prompt, no dialog and no JSON write — whether
                    // the baseline already persisted or still waits on
                    // the intent (SPEC §H7 item 3's re-execution).
                    var beforeReenter = File.ReadAllBytes(SiblingJsonPath);
                    prompts = 0;
                    seen = null;
                    var reenterEnables = EnablesOf(overwrittenGuid);
                    var reenterRequests = RequestsOf(overwrittenGuid);
                    var reentered = OpenGraphAt(sourcePath);
                    Assert.AreSame(overwritten, reentered,
                        "re-entering the same graph returns the live instance");
                    Assert.AreEqual(reenterEnables, EnablesOf(overwrittenGuid),
                        "re-entering the same graph produces no OnEnable");
                    Assert.AreEqual(reenterRequests + 1, RequestsOf(overwrittenGuid),
                        "the open-asset hook requests the re-entry check");
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(overwrittenGuid,
                            PerformedOf(overwrittenGuid), DroppedOf(overwrittenGuid));
                        headless?.AssertOnlyExpected();
                    }
                    Assert.AreEqual(++performed, TotalPerformed(),
                        "the re-entry performs its one check");
                    Assert.AreEqual(0, prompts,
                        "a pending-persist intent never prompts by itself");
                    Assert.AreEqual(dialogs, TotalDialogs(),
                        "a pending-persist intent shows no dialog");
                    CollectionAssert.AreEqual(beforeReenter,
                        File.ReadAllBytes(SiblingJsonPath),
                        "re-entry writes nothing");

                    // (6c) Reopen after another external edit → Reload
                    // rebuilds the graph from the JSON source of truth.
                    prompts = 0;
                    seen = null;
                    File.WriteAllText(SiblingJsonPath, external);
                    answer = GraphJsonSyncChoice.ReloadJson;
                    var reloaded = Reopen(out var reloadedGuid);
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(reloadedGuid,
                            PerformedOf(reloadedGuid), DroppedOf(reloadedGuid));
                        headless?.AssertOnlyExpected();
                    }
                    Assert.AreEqual(++performed, TotalPerformed());
                    Assert.AreEqual(1, prompts);
                    Assert.AreEqual(++dialogs, TotalDialogs());
                    Assert.AreEqual(external, File.ReadAllText(SiblingJsonPath),
                        "reload must not touch the file");
                    AssertSynced(reloaded, SiblingJsonPath, externalDoc);
                    var exported = GraphJsonConverter.Export(reloaded, externalDoc);
                    Assert.IsTrue(exported.Ok);
                    Assert.AreEqual(
                        externalDoc.Canonical.CanonicalText,
                        exported.Value!.Canonical.CanonicalText,
                        "after reload the graph models the external document");

                    // (6d) Batch mode with no provider resolves to Cancel:
                    // the check runs, never blocks and never overwrites.
                    if (Application.isBatchMode)
                    {
                        var other = FixtureSync.Replace(
                            "\"name\": \"Alpha\"", "\"name\": \"Other\"");
                        File.WriteAllText(SiblingJsonPath, other);
                        GraphJsonSync.OpenChoiceProvider = null;
                        var unattended = Reopen(out var unattendedGuid);
                        using (var headless = HeadlessWindowErrorCapture.Begin())
                        {
                            yield return DrainOpenChecks(unattendedGuid,
                                PerformedOf(unattendedGuid), DroppedOf(unattendedGuid));
                            headless?.AssertOnlyExpected();
                        }
                        Assert.AreEqual(++performed, TotalPerformed(),
                            "batch mode still performs the single check");
                        Assert.AreEqual(++dialogs, TotalDialogs(),
                            "batch mode with no provider must resolve to Cancel");
                        Assert.AreEqual(other, File.ReadAllText(SiblingJsonPath),
                            "no open path may silently overwrite the file");
                        // The reopened instance's baseline is what the
                        // last applied choice left — the reload moved it
                        // to the external document, and a cancelled open
                        // must leave it untouched.
                        AssertSynced(unattended, SiblingJsonPath, externalDoc,
                            "a cancelled open must leave the baseline untouched");
                    }
                }
                finally
                {
                    GraphJsonSync.OpenChoiceProvider = null;
                }
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

        // Reload consent is bound to the presented revision R: a file that
        // changed between the prompt and the apply invalidates the choice
        // and prompts anew — the reload then applies to the NEW revision
        // (SPEC §0.6 SG-17).
        [Test]
        public void Open_ReloadConsentBoundToShownRevision()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("reload-bound.opweave.json");
                ImportAndLink(graph, path);
                var external1 = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Change1\"");
                var external2 = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Change2\"");
                File.WriteAllText(path, external1);
                var doc2 = WorkflowSerializer.Read(external2).Value!;

                var prompts = 0;
                var result = GraphJsonSync.ResolveOnOpen(graph, check =>
                {
                    prompts++;
                    if (prompts == 1)
                    {
                        File.WriteAllText(path, external2);
                        return GraphJsonSyncChoice.ReloadJson;
                    }
                    Assert.AreEqual(
                        doc2.Canonical.WorkflowHash,
                        check.Document!.Canonical.WorkflowHash);
                    return GraphJsonSyncChoice.ReloadJson;
                });

                Assert.AreEqual(2, prompts);
                Assert.AreEqual(GraphJsonSyncOutcome.Reloaded, result.Outcome);
                Assert.AreEqual(external2, File.ReadAllText(path));
                var exported = GraphJsonConverter.Export(graph, doc2);
                Assert.IsTrue(exported.Ok);
                Assert.AreEqual(
                    doc2.Canonical.CanonicalText,
                    exported.Value!.Canonical.CanonicalText,
                    "the reload applied to the new revision, not the shown one");
            });
        }

        // Overwrite consent is bound the same way: a raced edit
        // invalidates the shown revision and the new file is presented
        // before anything is written (SPEC §0.6 SG-17).
        [Test]
        public void Open_OverwriteConsentBoundToShownRevision()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("overwrite-bound.opweave.json");
                ImportAndLink(graph, path);
                var external1 = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Change1\"")
                    .Replace("Sync fixture", "Title One");
                var external2 = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Change2\"")
                    .Replace("Sync fixture", "Title Two");
                File.WriteAllText(path, external1);
                var doc2 = WorkflowSerializer.Read(external2).Value!;

                var prompts = 0;
                var result = GraphJsonSync.ResolveOnOpen(graph, check =>
                {
                    prompts++;
                    if (prompts == 1)
                    {
                        File.WriteAllText(path, external2);
                        return GraphJsonSyncChoice.OverwriteJson;
                    }
                    Assert.AreEqual(
                        doc2.Canonical.WorkflowHash,
                        check.Document!.Canonical.WorkflowHash);
                    return GraphJsonSyncChoice.OverwriteJson;
                });

                Assert.AreEqual(2, prompts);
                Assert.AreEqual(GraphJsonSyncOutcome.Overwritten, result.Outcome);
                var text = File.ReadAllText(path);
                Assert.IsTrue(text.Contains("\"name\": \"Alpha\""),
                    "the graph's owned content wins the explicit overwrite");
                Assert.IsTrue(text.Contains("Title Two"),
                    "the overwrite merged over the NEW revision's non-owned members");
            });
        }

        // The pending-persist save on a model-clean graph (SPEC §H7 item
        // 3): the explicit Save JSON publishes the output while the
        // baseline's durability is proved, not assumed — either the
        // persisted .opweavegraph already carries it or the coordinator
        // holds an actionable intent for exactly the published revision.
        // The fresh open check then takes the existing reconcile branch:
        // no prompt, no write, no rebuild — a retained intent never
        // prompts by itself.
        [Test]
        public void Save_ModelClean_PendingPersistReconcilesOnOpenCheck()
        {
            WithGraph(graph =>
            {
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);
                GraphDatabase.SaveGraph(graph);

                var save = GraphJsonSync.Save(graph, null);
                Assert.AreEqual(GraphJsonSyncOutcome.Saved, save.Outcome);
                var bytes = File.ReadAllBytes(SiblingJsonPath);
                var written = WorkflowSerializer.Read(
                    System.Text.Encoding.UTF8.GetString(bytes));
                Assert.IsTrue(written.Ok);
                AssertSynced(graph, SiblingJsonPath, written.Value!);
                var guiHash = graph.syncedGuiOwnedHash;
                var exportedBefore = GraphJsonConverter.Export(graph, written.Value!);
                Assert.IsTrue(exportedBefore.Ok);
                var canonicalBefore = exportedBefore.Value!.Canonical.CanonicalText;

                // Pending-persist: EITHER the persisted .opweavegraph
                // already carries the link and baseline, OR an
                // actionable intent for exactly this revision stands.
                Assert.IsTrue(
                    File.ReadAllText(TempPath).Contains(graph.syncedBytesHash) ||
                    string.Equals(
                        ActionableIntentOutputHash(graph),
                        graph.syncedBytesHash, StringComparison.Ordinal),
                    "the published baseline is either already in the " +
                    "persisted .opweavegraph or held pending-persist " +
                    "by an actionable intent");

                var mtime = File.GetLastWriteTimeUtc(SiblingJsonPath);
                var prompts = 0;
                var result = GraphJsonSync.ResolveOnOpen(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreEqual(0, prompts,
                    "a pending-persist intent never prompts by itself");
                Assert.IsFalse(result.PromptShown);
                Assert.IsTrue(
                    result.Outcome == GraphJsonSyncOutcome.NoAction ||
                        result.Outcome == GraphJsonSyncOutcome.Saved ||
                        result.Outcome == GraphJsonSyncOutcome.PersistPending,
                    "re-entry takes the existing reconcile branch, got " +
                        result.Outcome);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(SiblingJsonPath),
                    "the open check writes nothing");
                Assert.AreEqual(mtime, File.GetLastWriteTimeUtc(SiblingJsonPath),
                    "the open check never touches the file");
                Assert.AreEqual(guiHash, graph.syncedGuiOwnedHash,
                    "the GUI-owned hash is unchanged");
                var exportedAfter = GraphJsonConverter.Export(graph, written.Value!);
                Assert.IsTrue(exportedAfter.Ok);
                Assert.AreEqual(canonicalBefore,
                    exportedAfter.Value!.Canonical.CanonicalText,
                    "the open check does not rebuild the graph");
            });
        }

        // The interrupted first link at the real open (SPEC §H7 item 3):
        // publication precedes graph persistence, so the persisted graph
        // is unlinked while its GUID-keyed intent slot still holds the
        // record — the open queues the one check and the deferred drain
        // reconciles it through the same recovery the command runs.
        [UnityTest]
        public IEnumerator Open_InterruptedFirstLink_OpenCheckReconciles() =>
            InterruptedFirstLinkOpenCheck(differing: false);

        [UnityTest]
        public IEnumerator Open_InterruptedFirstLinkDiffers_OpenCheckPrompts() =>
            InterruptedFirstLinkOpenCheck(differing: true);

        // Both cases share the arrange: a persisted graph with no
        // recorded link, the sibling JSON the crashed create published
        // and its standing create intent. The matching record carries
        // the projection this graph still emits — the drain records the
        // link and baseline with no prompt, no rebuild and no write. The
        // differing record carries the published document's own
        // projection — the graph moved since the write — so the drain
        // offers the revision-bound recovery choice exactly once, and
        // the Cancel answered here keeps the record, the file and the
        // graph exactly as found.
        private IEnumerator InterruptedFirstLinkOpenCheck(bool differing)
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
                var created = GraphDatabase.CreateGraph<OpWeaveGraph>(TempPath);
                assetCreated = created is not null;
                Assert.IsNotNull(created, "GraphDatabase.CreateGraph must return a graph");
                var graph = created!;
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);
                GraphDatabase.SaveGraph(graph);
                Assert.AreEqual(0, graph.jsonPath.Length,
                    "the interrupted first link leaves the persisted graph unlinked");
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var coordinator = TestCoordinator();
                var resolved = coordinator.ResolveOwnedPath(
                    SiblingJsonPath).ResolvedPath!;

                // The crashed create's state on disk: the published JSON
                // plus the intent record it wrote first. The matching
                // record's outputGuiHash is the projection this graph
                // still emits over the published document; the differing
                // record's is the published document's own — the
                // projection a graph that moved no longer emits.
                var published = differing
                    ? FixtureSync.Replace(
                        "\"name\": \"Alpha\"", "\"name\": \"Published\"")
                    : FixtureSync;
                File.WriteAllText(SiblingJsonPath, published);
                var diskBytes = File.ReadAllBytes(SiblingJsonPath);
                var diskDoc = WorkflowSerializer.Read(published).Value!;
                var projected = GraphJsonConverter.Export(graph, diskDoc);
                Assert.IsTrue(projected.Ok && projected.Value is not null);
                var outputGuiHash = differing
                    ? GraphJsonFile.GuiOwnedHash(diskDoc)
                    : GraphJsonFile.GuiOwnedHash(projected.Value!);
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindCreate,
                    JsonSaveCoordinator.IntentBaseAbsent,
                    JsonSaveCoordinator.Sha256Hex(diskBytes),
                    outputGuiHash)).Ok);
                var intentBytes = File.ReadAllBytes(
                    coordinator.IntentPathFor(guid));
                var projectionBefore = projected.Value!.Canonical.CanonicalText;

                var prompts = 0;
                string? presented = null;
                GraphJsonSync.OpenChoiceProvider = check =>
                {
                    prompts++;
                    presented = check.PresentedRevision;
                    return GraphJsonSyncChoice.Cancel;
                };
                try
                {
                    var requestsBefore = RequestsOf(guid);
                    var performedBefore = PerformedOf(guid);
                    var droppedBefore = DroppedOf(guid);
                    var reopened = OpenGraphAt(TempPath);
                    Assert.AreSame(graph, reopened,
                        "the reopen returns the live persisted instance");
                    Assert.AreEqual(requestsBefore + 1, RequestsOf(guid),
                        "the unlinked reopen with a standing intent " +
                            "requests exactly one open check");
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(
                            guid, performedBefore, droppedBefore);
                        headless?.AssertOnlyExpected();
                    }
                    if (differing)
                    {
                        Assert.AreEqual(1, prompts,
                            "a moved graph gets the recovery choice exactly once");
                        Assert.AreEqual(
                            JsonSaveCoordinator.Sha256Hex(diskBytes), presented,
                            "the recovery prompt is bound to the published revision");
                        CollectionAssert.AreEqual(
                            intentBytes,
                            File.ReadAllBytes(coordinator.IntentPathFor(guid)),
                            "Cancel keeps the pending intent byte-identical");
                        Assert.AreEqual(0, reopened.jsonPath.Length,
                            "Cancel leaves the graph unlinked");
                    }
                    else
                    {
                        Assert.AreEqual(0, prompts,
                            "a matching graph reconciles without a prompt");
                        Assert.AreEqual(SiblingJsonPath, reopened.jsonPath,
                            "the open reconcile records the first link");
                        Assert.AreEqual(
                            JsonSaveCoordinator.Sha256Hex(diskBytes),
                            reopened.syncedBytesHash,
                            "the reconcile moves the baseline to the published revision");
                        // The same outcome the restart verifier accepts:
                        // the record is cleared after the durable persist
                        // proof, or it stands as the pending-persist of
                        // exactly the published revision while the
                        // persisted .opweavegraph does not yet carry it.
                        var publishedHash = JsonSaveCoordinator.Sha256Hex(diskBytes);
                        Assert.IsTrue(
                            !File.Exists(coordinator.IntentPathFor(guid)) ||
                                (string.Equals(
                                    ActionableIntentOutputHash(reopened),
                                    publishedHash, StringComparison.Ordinal) &&
                                 !File.ReadAllText(TempPath).Contains(publishedHash)),
                            "the reconciled intent record is cleared, or " +
                                "stands as the pending-persist of exactly " +
                                "the published revision");
                    }
                    CollectionAssert.AreEqual(
                        diskBytes, File.ReadAllBytes(SiblingJsonPath),
                        "the open check writes nothing to the JSON");
                    var projectedAfter = GraphJsonConverter.Export(reopened, diskDoc);
                    Assert.IsTrue(projectedAfter.Ok);
                    Assert.AreEqual(
                        projectionBefore,
                        projectedAfter.Value!.Canonical.CanonicalText,
                        "the open check never rebuilds the graph");
                }
                finally
                {
                    GraphJsonSync.OpenChoiceProvider = null;
                }
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

        // ── helpers ─────────────────────────────────────────────────────

        // The pending intent the coordinator still holds for this graph's
        // publish — the pending-persist state (SPEC §H7 item 3) — or null
        // when none stands. The production read path validates the record
        // against the graph's GUID and its recorded link or sibling path;
        // an actionable record's outputHash is exactly the published
        // revision its persist still owes.
        private static string? ActionableIntentOutputHash(OpWeaveGraph graph)
        {
            var assetPath = GraphDatabase.GetGraphAssetPath(graph);
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (guid.Length == 0)
            {
                return null;
            }
            var read = TestCoordinator().ReadIntent(
                guid, graph.jsonPath,
                Path.ChangeExtension(assetPath, "opweave.json"));
            return read.State == IntentReadState.Valid
                ? read.Intent!.OutputHash
                : null;
        }

        // The user-facing open of one graph asset: AssetDatabase.OpenAsset
        // drives the editor's documented [OnOpenAsset] dispatch — the
        // open-check request rides the same chain as Graph Toolkit's own
        // OpenGraphAsset handler, which loads the graph and creates its
        // window — then LoadGraph hands back the instance the open
        // resolved to (the already-loaded one on a cached reopen, a fresh
        // deserialization on an uncached one). The test never invokes a
        // production hook.
        private OpWeaveGraph OpenGraphAt(string assetPath)
        {
            var main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            Assert.IsNotNull(main, "the .opweavegraph asset must be on disk");
            var before = GraphToolkitWindows();
            using (var headless = HeadlessWindowErrorCapture.Begin())
            {
                AssetDatabase.OpenAsset(main!);
                var created = GraphToolkitWindows()
                    .Where(w => !before.Contains(w))
                    .ToList();
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                {
                    Assert.IsTrue(created.Count > 0,
                        "with a graphics device the open must create its window");
                    // The graphics-enabled run counts only with the
                    // window's creation in the log — one fixed line per
                    // open records the created window's real type, the
                    // device and the asset.
                    Debug.Log(
                        "OPWEAVE-W53-WINDOW created type=" +
                        created[0].GetType().FullName +
                        " assembly=" +
                        created[0].GetType().Assembly.GetName().Name +
                        " graphics=" + SystemInfo.graphicsDeviceType +
                        " asset=" + assetPath);
                }
                else
                {
                    Debug.Log("OPWEAVE-W53-WINDOW skipped graphics=Null");
                }
                // Closing the window the open created logs the same
                // headless device errors — it stays inside the capture.
                foreach (var window in created)
                {
                    window.Close();
                }
                headless?.AssertOnlyExpected();
            }
            var graph = GraphDatabase.LoadGraph<OpWeaveGraph>(assetPath);
            Assert.IsNotNull(graph, "the open must leave a loaded graph");
            return graph!;
        }

        // Under -nographics the window the real open creates reports the
        // headless device errors: "initialize the view" as its view comes
        // up and again for each deferred retry, and "show the window" when
        // the window itself is created. How many of each Graph Toolkit
        // emits is internal to it — but the Test Framework records an
        // unexpected error before a logMessageReceived callback could ever
        // register an expectation, so LogAssert.Expect inside a callback is
        // always too late. Instead, only while no graphics device exists
        // at all, the OpenAsset call, the close of the window it created
        // and the real delayCall drain run under
        // LogAssert.ignoreFailingMessages — restored in the using
        // disposal — while every Error, Exception and Assert line is
        // captured; AssertOnlyExpected then requires each captured line to
        // be one of the two headless messages and nothing else. With a
        // device nothing is captured or ignored: the window must open, and
        // none of these errors may appear.
        private sealed class HeadlessWindowErrorCapture : IDisposable
        {
            private static readonly Regex s_expected = new Regex(
                @"^No graphic device is available to " +
                @"(show the window|initialize the view)\.$");

            private readonly List<string> _messages = new List<string>();
            private readonly Application.LogCallback _listener;
            private readonly bool _previousIgnore;

            private HeadlessWindowErrorCapture()
            {
                _listener = (message, stackTrace, type) =>
                {
                    if (type == LogType.Error || type == LogType.Exception ||
                        type == LogType.Assert)
                    {
                        _messages.Add(message);
                    }
                };
                _previousIgnore = LogAssert.ignoreFailingMessages;
                LogAssert.ignoreFailingMessages = true;
                Application.logMessageReceived += _listener;
            }

            // Begins the capture — only with no graphics device at all.
            // Null (a no-op scope) otherwise: with a device the window must
            // open and the headless errors must never appear.
            internal static HeadlessWindowErrorCapture? Begin() =>
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                    ? new HeadlessWindowErrorCapture()
                    : null;

            public void Dispose()
            {
                Application.logMessageReceived -= _listener;
                LogAssert.ignoreFailingMessages = _previousIgnore;
            }

            // Every captured line must be one of the two headless window
            // errors — any other error fails the test with its text.
            internal void AssertOnlyExpected()
            {
                foreach (var message in _messages)
                {
                    Assert.IsTrue(
                        s_expected.IsMatch(message),
                        "an unexpected error reached the open path: " + message);
                }
            }
        }

        // The Graph Toolkit editor windows currently open — the open
        // flow creates one per fresh open under any graphics device. The
        // public Unity.GraphToolkit namespace prefix identifies them; no
        // internal type name is relied on.
        private static List<EditorWindow> GraphToolkitWindows() =>
            Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(w => w != null &&
                    w.GetType().FullName is string name &&
                    name.StartsWith("Unity.GraphToolkit.", StringComparison.Ordinal))
                .ToList();

        // The open-check instrumentation a test reads per GUID: how many
        // requests the hooks made, how many OnEnable hooks ran, how many
        // checks the drain performed and how many a drain or a disable
        // dropped, plus the totals performed and the dialogs shown.
        private static int RequestsOf(string guid) =>
            GraphJsonSync.OpenCheckRequests.TryGetValue(guid, out var n) ? n : 0;

        private static int EnablesOf(string guid) =>
            GraphJsonSync.OpenEnableCount.TryGetValue(guid, out var n) ? n : 0;

        private static int PerformedOf(string guid) =>
            GraphJsonSync.OpenChecksPerformed.TryGetValue(guid, out var n) ? n : 0;

        private static int DroppedOf(string guid) =>
            GraphJsonSync.OpenChecksDropped.TryGetValue(guid, out var n) ? n : 0;

        private static string? LastDropReasonOf(string guid) =>
            GraphJsonSync.OpenCheckDropReason.TryGetValue(guid, out var r) ? r : null;

        private static int TotalPerformed() =>
            GraphJsonSync.OpenChecksPerformed.Values.Sum();

        private static int TotalDialogs() =>
            GraphJsonSync.OpenDialogsShown.Values.Sum();

        // Pumps editor frames until the deferred drain performs — or
        // drops — the one open check the last reopen queued for `guid`.
        // The drain rides the real EditorApplication.delayCall, which an
        // in-flight asset refresh (the JSON write or the graph save
        // immediately before the reopen) can hold past a single
        // "yield return null": the wait is therefore bounded by a 10 s
        // deadline, not a frame count. A drop is never acceptable — the
        // check a reopen requested must run — so a changed drop count
        // fails with the recorded reason, and a deadline miss fails with
        // the full counter state. Three extra frames after the performed
        // check prove no duplicate follows.
        private static IEnumerator DrainOpenChecks(
            string guid, int performedBefore, int droppedBefore)
        {
            var deadline = EditorApplication.timeSinceStartup + 10.0;
            while (PerformedOf(guid) == performedBefore &&
                DroppedOf(guid) == droppedBefore)
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "the open check for " + guid + " was never performed: " +
                        "requested " + RequestsOf(guid) + ", performed " +
                        PerformedOf(guid) + ", dropped " + DroppedOf(guid) +
                        ", last drop reason " +
                        (LastDropReasonOf(guid) ?? "<none>"));
                }
                yield return null;
            }
            if (DroppedOf(guid) != droppedBefore)
            {
                Assert.Fail("the open check was dropped: " +
                    (LastDropReasonOf(guid) ?? "<unknown>"));
            }
            yield return null;
            yield return null;
            yield return null;
            Assert.AreEqual(performedBefore + 1, PerformedOf(guid),
                "exactly one performed check per reopen — never a duplicate");
            Assert.AreEqual(droppedBefore, DroppedOf(guid),
                "a requested open check must never be dropped");
        }
    }
}
#endif
