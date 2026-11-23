#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.3-08 [Edit] tests — the "Save Graph JSON" command: the explicit
// human entry that runs the recovery and save seams (first-link
// create-only, revision-bound merge, three-choice conflict) through the
// real menu item, plus the unsynced-edits notifications at the verified
// reopen, import and quit events (SPEC §H7 item 3).
//
// The command's tests drive the real [MenuItem] entry —
// EditorApplication.ExecuteMenuItem("Tools/OpWeave/Save Graph JSON")
// with the .opweavegraph selected — never the internal save (owner
// decision 2026-11-14, option (a)). The conflict choices ride the
// same real prompt seam the open check uses,
// GraphJsonSync.OpenChoiceProvider.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using OpWeave.GraphToolkit;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed partial class GraphJsonSyncTests
    {
        [Test]
        public void Command_UnlinkedGraph_FirstSaveWritesJsonAndLinks()
        {
            WithGraph(graph =>
            {
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);

                Assert.IsTrue(InvokeSaveJsonCommand(),
                    "the menu entry must run for a selected .opweavegraph");

                Assert.AreEqual(SiblingJsonPath, graph.jsonPath,
                    "the command adopts the sibling .opweave.json link");
                Assert.IsTrue(File.Exists(SiblingJsonPath),
                    "the command writes the JSON source of truth");
                var written = WorkflowSerializer.Read(
                    File.ReadAllText(SiblingJsonPath));
                Assert.IsTrue(written.Ok);
                AssertSynced(graph, SiblingJsonPath, written.Value!);
                Assert.AreEqual(
                    GraphJsonSyncState.InSync, GraphJsonSync.CheckOnOpen(graph).State);
            });
        }

        // Idempotency through the command: the output already on disk
        // reconciles without a write — bytes and mtime untouched.
        [Test]
        public void Command_SecondRunNoChange_WritesNothing()
        {
            WithGraph(graph =>
            {
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);
                Assert.IsTrue(InvokeSaveJsonCommand());

                var bytes = File.ReadAllBytes(SiblingJsonPath);
                var marker = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(SiblingJsonPath, marker);

                Assert.IsTrue(InvokeSaveJsonCommand());
                Assert.AreEqual(marker,
                    File.GetLastWriteTimeUtc(SiblingJsonPath),
                    "an unchanged output reconciles without a write");
                CollectionAssert.AreEqual(
                    bytes, File.ReadAllBytes(SiblingJsonPath));
            });
        }

        // A metadata-only external change merges under the command with
        // no prompt — the file's GUI-owned projection still equals B.
        [Test]
        public void Command_MetadataOnlyExternalChange_MergesWithoutPrompt()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("command-meta.opweave.json");
                ImportAndLink(graph, path);
                File.WriteAllText(path, FixtureSync
                    .Replace("Sync fixture", "Edited outside")
                    .Replace("\"reviewed\": true", "\"reviewed\": false"));

                var prompts = 0;
                GraphJsonSync.OpenChoiceProvider = _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.Cancel;
                };
                try
                {
                    Assert.IsTrue(InvokeSaveJsonCommand());
                }
                finally
                {
                    GraphJsonSync.OpenChoiceProvider = null;
                }

                Assert.AreEqual(0, prompts,
                    "a metadata-only external change merges without a prompt");
                var written = WorkflowSerializer.Read(File.ReadAllText(path));
                Assert.IsTrue(written.Ok);
                Assert.AreEqual("Edited outside", written.Value!.Title,
                    "the external metadata survives the merge");
                AssertSynced(graph, path, written.Value!);
            });
        }

        // The three-choice prompt bound to the presented revision, driven
        // through the real prompt seam: each of cancel, reload and
        // overwrite on a graph-owned conflict.
        [Test]
        public void Command_GraphOwnedConflict_AppliesEachChoice()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("command-conflict.opweave.json");
                var baseline = ImportAndLink(graph, path);
                var external = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"External\"");

                var prompts = 0;
                var answer = GraphJsonSyncChoice.Cancel;
                GraphJsonSync.OpenChoiceProvider = check =>
                {
                    prompts++;
                    Assert.AreEqual(
                        GraphJsonSyncState.ExternalModification, check.State,
                        "the conflict reaches the prompt as the §H7.3 choice");
                    return answer;
                };
                try
                {
                    // Cancel: the file and the baseline stay untouched.
                    File.WriteAllText(path, external);
                    Assert.IsTrue(InvokeSaveJsonCommand());
                    Assert.AreEqual(1, prompts);
                    Assert.AreEqual(external, File.ReadAllText(path),
                        "cancel leaves the externally edited file untouched");
                    AssertSynced(graph, path, baseline,
                        "cancel keeps the previous sync baseline");

                    // Reload: the graph is rebuilt from the JSON — file
                    // untouched, baseline moved to it.
                    answer = GraphJsonSyncChoice.ReloadJson;
                    Assert.IsTrue(InvokeSaveJsonCommand());
                    Assert.AreEqual(2, prompts);
                    Assert.AreEqual(external, File.ReadAllText(path),
                        "reload must not touch the file");
                    var externalDoc = WorkflowSerializer.Read(external).Value!;
                    AssertSynced(graph, path, externalDoc);
                    var exported = GraphJsonConverter.Export(graph, externalDoc);
                    Assert.IsTrue(exported.Ok);
                    Assert.AreEqual(
                        externalDoc.Canonical.CanonicalText,
                        exported.Value!.Canonical.CanonicalText,
                        "after reload the graph models the JSON document");

                    // Overwrite on a NEW external revision: the graph's
                    // owned content wins, the baseline moves to the write.
                    var external2 = FixtureSync.Replace(
                        "\"name\": \"Alpha\"", "\"name\": \"Other\"");
                    File.WriteAllText(path, external2);
                    answer = GraphJsonSyncChoice.OverwriteJson;
                    Assert.IsTrue(InvokeSaveJsonCommand());
                    Assert.AreEqual(3, prompts);
                    var text = File.ReadAllText(path);
                    Assert.IsTrue(text.Contains("\"name\": \"External\""),
                        "the graph's owned content wins the explicit overwrite");
                    var written = WorkflowSerializer.Read(text);
                    Assert.IsTrue(written.Ok);
                    AssertSynced(graph, path, written.Value!);
                }
                finally
                {
                    GraphJsonSync.OpenChoiceProvider = null;
                }
            });
        }

        // Consent is bound to the presented revision R: the file changed
        // inside the prompt invalidates the choice and the command
        // re-presents the NEW revision — the shown one is never written.
        [Test]
        public void Command_ConsentBoundToShownRevision()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("command-consent.opweave.json");
                ImportAndLink(graph, path);
                var external1 = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Racer1\"");
                var external2 = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Racer2\"");
                File.WriteAllText(path, external1);
                var racer2Doc = WorkflowSerializer.Read(external2).Value!;

                var prompts = 0;
                GraphJsonSync.OpenChoiceProvider = check =>
                {
                    prompts++;
                    if (prompts == 1)
                    {
                        File.WriteAllText(path, external2);
                        return GraphJsonSyncChoice.OverwriteJson;
                    }
                    Assert.AreEqual(
                        racer2Doc.Canonical.WorkflowHash,
                        check.Document!.Canonical.WorkflowHash,
                        "the new prompt presents the raced revision");
                    return GraphJsonSyncChoice.Cancel;
                };
                try
                {
                    Assert.IsTrue(InvokeSaveJsonCommand());
                }
                finally
                {
                    GraphJsonSync.OpenChoiceProvider = null;
                }

                Assert.AreEqual(2, prompts,
                    "a file changed inside the prompt invalidates the consent");
                Assert.AreEqual(external2, File.ReadAllText(path),
                    "the revision the consent was bound to is never overwritten");
            });
        }

        // The validate function disables the item when the selection
        // holds no .opweavegraph asset.
        [Test]
        public void Command_NoGraphSelection_ItemDisabled()
        {
            Selection.objects = new UnityEngine.Object[0];
            Assert.IsFalse(
                EditorApplication.ExecuteMenuItem(
                    OpWeaveGraphJsonCommands.ToolsMenuPath),
                "the menu entry must be disabled with no graph selected");
        }

        // Verified notification gate (1): the reopen's single open-check
        // drain notifies once per (GUID, export hash) while the JSON is
        // behind the graph — and never changes B or the JSON.
        [UnityTest]
        public IEnumerator Open_UnsyncedEdits_NotifiesOncePerRevision()
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

                // Link through the explicit command, then edit the graph
                // in memory: the JSON still holds exactly the recorded
                // revision while the export moved — the unsynced state
                // the reopen reports.
                Assert.IsTrue(GraphJsonConverter.Import(graph, FixtureSync).Ok);
                Assert.IsTrue(InvokeSaveJsonCommand(),
                    "the command links the graph first");
                Assert.IsTrue(GraphJsonConverter.Import(
                    graph,
                    FixtureSync.Replace(
                        "\"name\": \"Alpha\"", "\"name\": \"Unsynced\"")).Ok);
                GraphDatabase.SaveGraph(graph);
                var json = File.ReadAllBytes(SiblingJsonPath);
                var baseline = WorkflowSerializer.Read(
                    System.Text.Encoding.UTF8.GetString(json)).Value!;

                var warnings = new List<string>();
                Application.LogCallback listener = (message, stackTrace, type) =>
                {
                    if (type == LogType.Warning &&
                        message == OpWeaveGraphJsonCommands.UnsyncedEditsWarning)
                    {
                        warnings.Add(message);
                    }
                };
                Application.logMessageReceived += listener;
                try
                {
                    // First reopen: the drain notifies once.
                    var assetGuid = AssetDatabase.AssetPathToGUID(TempPath);
                    OpenGraphAt(TempPath);
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(assetGuid,
                            PerformedOf(assetGuid), DroppedOf(assetGuid));
                        headless?.AssertOnlyExpected();
                    }
                    Assert.AreEqual(1, warnings.Count,
                        "the reopen notifies once per (GUID, export hash)");

                    // Second reopen: the same (GUID, export hash) is not
                    // reported again.
                    OpenGraphAt(TempPath);
                    using (var headless = HeadlessWindowErrorCapture.Begin())
                    {
                        yield return DrainOpenChecks(assetGuid,
                            PerformedOf(assetGuid), DroppedOf(assetGuid));
                        headless?.AssertOnlyExpected();
                    }
                    Assert.AreEqual(1, warnings.Count,
                        "the same unsynced revision is never reported twice");
                }
                finally
                {
                    Application.logMessageReceived -= listener;
                }
                CollectionAssert.AreEqual(
                    json, File.ReadAllBytes(SiblingJsonPath),
                    "the notification never writes the JSON");
                AssertSynced(graph, SiblingJsonPath, baseline,
                    "the notification never moves the baseline");
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

        // Verified notification gate (3): a real import of the saved
        // .opweavegraph — the verified OnPostprocessAllAssets event — of a
        // graph with a live, unsynced instance notifies exactly once per
        // (GUID, export hash) and never changes B or the JSON.
        [Test]
        public void Import_UnsyncedEdits_PostprocessNotifiesOnce()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("postprocess.opweave.json");
                var baseline = ImportAndLink(graph, path);
                Assert.IsTrue(GraphJsonConverter.Import(
                    graph,
                    FixtureSync.Replace(
                        "\"name\": \"Alpha\"", "\"name\": \"Unsynced\"")).Ok);
                var bytes = File.ReadAllBytes(path);

                var warnings = 0;
                Application.LogCallback listener = (message, stackTrace, type) =>
                {
                    if (type == LogType.Warning &&
                        message == OpWeaveGraphJsonCommands.UnsyncedEditsWarning)
                    {
                        warnings++;
                    }
                };
                Application.logMessageReceived += listener;
                try
                {
                    GraphDatabase.SaveGraph(graph);
                    AssetDatabase.ImportAsset(
                        TempPath, ImportAssetOptions.ForceUpdate);
                    AssetDatabase.ImportAsset(
                        TempPath, ImportAssetOptions.ForceUpdate);
                }
                finally
                {
                    Application.logMessageReceived -= listener;
                }

                Assert.AreEqual(1, warnings,
                    "the postprocess notification fires exactly once per (GUID, export hash)");
                CollectionAssert.AreEqual(
                    bytes, File.ReadAllBytes(path),
                    "the notification never writes the JSON");
                AssertSynced(graph, path, baseline,
                    "the notification never moves the baseline");
            });
        }

        // Verified notification gate (4): the registered wantsToQuit
        // handler lists the live unsynced graphs and its modal offers
        // "Save JSON for these graphs / Quit without saving JSON /
        // Cancel quit" — driven here through the modal seam.
        [Test]
        public void QuitGuard_ListsUnsyncedGraphs_AndAppliesChoice()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("quit.opweave.json");
                var baseline = ImportAndLink(graph, path);
                Assert.IsTrue(GraphJsonConverter.Import(
                    graph,
                    FixtureSync.Replace(
                        "\"name\": \"Alpha\"", "\"name\": \"Unsynced\"")).Ok);
                var bytes = File.ReadAllBytes(path);

                var answer = OpWeaveGraphJsonCommands.QuitGuardChoice.CancelQuit;
                var calls = new List<IReadOnlyList<OpWeaveGraph>>();
                OpWeaveGraphJsonCommands.QuitGuardChoiceProvider = graphs =>
                {
                    calls.Add(graphs);
                    return answer;
                };
                try
                {
                    // Cancel quit vetoes the quit and writes nothing.
                    Assert.IsFalse(
                        OpWeaveGraphJsonCommands.WantsToQuit(),
                        "Cancel quit must veto the quit");
                    Assert.AreEqual(1, calls.Count);
                    Assert.AreEqual(1, calls[0].Count,
                        "the modal lists the one unsynced graph");
                    Assert.AreSame(graph, calls[0][0]);
                    CollectionAssert.AreEqual(
                        bytes, File.ReadAllBytes(path),
                        "cancel quit writes nothing");

                    // Quit without saving proceeds and touches nothing.
                    answer = OpWeaveGraphJsonCommands
                        .QuitGuardChoice.QuitWithoutSavingJson;
                    Assert.IsTrue(OpWeaveGraphJsonCommands.WantsToQuit());
                    CollectionAssert.AreEqual(
                        bytes, File.ReadAllBytes(path));
                    AssertSynced(graph, path, baseline,
                        "quit without saving keeps the baseline");

                    // "Save JSON for these graphs" runs the real command:
                    // the JSON is written and the baseline moves.
                    answer = OpWeaveGraphJsonCommands.QuitGuardChoice.SaveJson;
                    Assert.IsTrue(OpWeaveGraphJsonCommands.WantsToQuit());
                    var text = File.ReadAllText(path);
                    Assert.IsTrue(text.Contains("\"name\": \"Unsynced\""),
                        "the quit guard's Save JSON ran the real command");
                    var written = WorkflowSerializer.Read(text);
                    Assert.IsTrue(written.Ok);
                    AssertSynced(graph, path, written.Value!);

                    // Nothing unsynced remains: the modal is not asked.
                    var asked = calls.Count;
                    Assert.IsTrue(OpWeaveGraphJsonCommands.WantsToQuit());
                    Assert.AreEqual(asked, calls.Count,
                        "a clean state never shows the quit modal");
                }
                finally
                {
                    OpWeaveGraphJsonCommands.QuitGuardChoiceProvider = null;
                }
            });
        }

        // A requested Save JSON that did not complete aborts the quit —
        // closing must never discard the unsynced state a refused or
        // cancelled save left behind (SPEC §H7 item 3). Here the file
        // moves between the guard's unsynced listing and the save it
        // runs: first a real conflict the prompt cancels, then an
        // unparseable file refused outright. Each time WantsToQuit
        // returns false, the one fixed warning names the graph and the
        // file is untouched.
        [Test]
        public void QuitGuard_SaveJsonNotCompleted_AbortsQuit()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("quit-incomplete.opweave.json");
                var baseline = ImportAndLink(graph, path);
                Assert.IsTrue(GraphJsonConverter.Import(
                    graph,
                    FixtureSync.Replace(
                        "\"name\": \"Alpha\"", "\"name\": \"Unsynced\"")).Ok);

                var external = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"External\"");
                var prompts = 0;
                GraphJsonSync.OpenChoiceProvider = check =>
                {
                    prompts++;
                    Assert.AreEqual(
                        GraphJsonSyncState.ExternalModification, check.State,
                        "the quit-time save surfaces the raced conflict");
                    return GraphJsonSyncChoice.Cancel;
                };
                var warnings = new List<string>();
                Application.LogCallback listener = (message, stackTrace, type) =>
                {
                    if (type == LogType.Warning &&
                        message.StartsWith(
                            OpWeaveGraphJsonCommands.QuitSaveIncompleteWarning,
                            StringComparison.Ordinal))
                    {
                        warnings.Add(message);
                    }
                };
                Application.logMessageReceived += listener;
                try
                {
                    // A cancelled conflict: the out-of-band edit lands
                    // between the guard's unsynced listing and the save
                    // the guard runs — the save's real prompt is
                    // cancelled and the write never happens.
                    OpWeaveGraphJsonCommands.QuitGuardChoiceProvider = graphs =>
                    {
                        Assert.AreEqual(1, graphs.Count,
                            "the modal lists the one unsynced graph");
                        Assert.AreSame(graph, graphs[0]);
                        File.WriteAllText(path, external);
                        return OpWeaveGraphJsonCommands.QuitGuardChoice.SaveJson;
                    };
                    Assert.IsFalse(
                        OpWeaveGraphJsonCommands.WantsToQuit(),
                        "a requested save that did not complete must veto the quit");
                    Assert.AreEqual(1, prompts,
                        "the raced save's conflict reached the real prompt seam");
                    Assert.AreEqual(external, File.ReadAllText(path),
                        "the cancelled save writes nothing");
                    AssertSynced(graph, path, baseline,
                        "the baseline is untouched");
                    Assert.AreEqual(1, warnings.Count,
                        "the aborted quit logs its one fixed warning");
                    Assert.IsTrue(
                        warnings[0].Contains(TempPath),
                        "the warning names the graph whose save did not complete");

                    // A refused save — an unparseable file landing the
                    // same way — aborts the quit too and never reaches
                    // the prompt.
                    File.WriteAllText(path, FixtureSync);
                    prompts = 0;
                    OpWeaveGraphJsonCommands.QuitGuardChoiceProvider = graphs =>
                    {
                        File.WriteAllText(path, "{ not valid json");
                        return OpWeaveGraphJsonCommands.QuitGuardChoice.SaveJson;
                    };
                    Assert.IsFalse(
                        OpWeaveGraphJsonCommands.WantsToQuit(),
                        "a refused save must veto the quit");
                    Assert.AreEqual(0, prompts,
                        "an unparseable file never reaches the prompt");
                    Assert.AreEqual("{ not valid json", File.ReadAllText(path),
                        "the refused save writes nothing");
                    AssertSynced(graph, path, baseline);
                    Assert.AreEqual(2, warnings.Count,
                        "the second abort logs the same fixed warning");
                    Assert.IsTrue(
                        warnings[1].Contains(TempPath),
                        "the warning names the graph whose save did not complete");
                }
                finally
                {
                    Application.logMessageReceived -= listener;
                    OpWeaveGraphJsonCommands.QuitGuardChoiceProvider = null;
                    GraphJsonSync.OpenChoiceProvider = null;
                }
            });
        }

        // The surface-refused-results rule (SPEC §H7 item 3): every
        // Refused, Busy, IoError or Pending command result is reported
        // once with a fixed, actionable warning naming the graph — never
        // silent and never as success. An invalid first-link JSON, a busy
        // path lock, an I/O failure mid-write and a sealed intent slot
        // each warn once through the real menu entry; the sealed slot's
        // report names its record path, and a choice dialog the save
        // already showed is never repeated here.
        [Test]
        public void Command_RefusedOrFailedSave_SurfacedOnce()
        {
            WithGraph(graph =>
            {
                var warnings = new List<string>();
                Application.LogCallback listener = (message, stackTrace, type) =>
                {
                    if (type == LogType.Warning &&
                        message.StartsWith(
                            OpWeaveGraphJsonCommands.CommandSaveIncompleteWarning,
                            StringComparison.Ordinal))
                    {
                        warnings.Add(message);
                    }
                };
                Application.logMessageReceived += listener;
                try
                {
                    // An invalid first-link JSON refuses the create-only
                    // write before any prompt — reported once, the file
                    // untouched.
                    Assert.IsTrue(
                        GraphJsonConverter.Import(graph, FixtureSync).Ok);
                    File.WriteAllText(SiblingJsonPath, "{ not valid json");
                    Assert.IsTrue(InvokeSaveJsonCommand());
                    Assert.AreEqual(1, warnings.Count,
                        "the invalid-JSON refusal is reported once");
                    Assert.IsTrue(warnings[0].Contains(TempPath),
                        "the report names the graph asset");
                    Assert.AreEqual("{ not valid json",
                        File.ReadAllText(SiblingJsonPath),
                        "the refused save writes nothing");
                    Assert.AreEqual(0, graph.jsonPath.Length,
                        "the refused first link stays unlinked");

                    // A busy path lock — the JSON is being written by
                    // another OpWeave process — refuses the save; the
                    // report carries the lock's own cause.
                    var path = JsonPath("command-failed.opweave.json");
                    ImportAndLink(graph, path);
                    Assert.IsTrue(GraphJsonConverter.Import(
                        graph,
                        FixtureSync.Replace(
                            "\"name\": \"Alpha\"",
                            "\"name\": \"Unsynced\"")).Ok);
                    var coordinator = TestCoordinator();
                    var resolved =
                        coordinator.ResolveOwnedPath(path).ResolvedPath!;
                    var held = coordinator.TryAcquire(resolved);
                    Assert.AreEqual(AcquireStatus.Acquired, held.Status);
                    try
                    {
                        Assert.IsTrue(InvokeSaveJsonCommand());
                    }
                    finally
                    {
                        held.Lock!.Dispose();
                    }
                    Assert.AreEqual(2, warnings.Count,
                        "the busy-lock refusal is reported once");
                    Assert.IsTrue(warnings[1].Contains(TempPath));

                    // An I/O failure mid-write — the intent record cannot
                    // be published — refuses the save; the report carries
                    // the failure's cause.
                    JsonSaveCoordinator.PublishReplaceForTesting =
                        (temp, target) => new IOException(
                            "simulated publish failure");
                    try
                    {
                        Assert.IsTrue(InvokeSaveJsonCommand());
                    }
                    finally
                    {
                        JsonSaveCoordinator.PublishReplaceForTesting = null;
                    }
                    Assert.AreEqual(3, warnings.Count,
                        "the I/O refusal is reported once");
                    Assert.IsTrue(warnings[2].Contains(TempPath));

                    // A sealed intent slot — the record a crashed writer
                    // left — refuses every later save; the report names
                    // the record path itself.
                    var guid = AssetDatabase.AssetPathToGUID(TempPath);
                    var intentPath = coordinator.IntentPathFor(guid);
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(intentPath)!);
                    File.WriteAllText(intentPath, "{ not valid intent");
                    Assert.IsTrue(InvokeSaveJsonCommand());
                    Assert.AreEqual(4, warnings.Count,
                        "the sealed slot's refusal is reported once");
                    Assert.IsTrue(warnings[3].Contains(TempPath));
                    Assert.IsTrue(warnings[3].Contains(intentPath),
                        "the sealed slot's report names the intent " +
                            "record path");
                }
                finally
                {
                    Application.logMessageReceived -= listener;
                }
            });
        }

        // ── helpers ─────────────────────────────────────────────────────────

        // The explicit Save Graph JSON command through its real menu
        // entry: the test's .opweavegraph selected, the production
        // [MenuItem] invoked through EditorApplication.ExecuteMenuItem —
        // never the internal GraphJsonSync.Save. Returns what the menu
        // dispatch reports.
        private static bool InvokeSaveJsonCommand()
        {
            Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(TempPath) };
            return EditorApplication.ExecuteMenuItem(
                OpWeaveGraphJsonCommands.ToolsMenuPath);
        }
    }
}
#endif
