#if UNITY_6000_5_OR_NEWER
#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using OpWeave.GraphToolkit;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed partial class GraphJsonSyncTests
    {
        // The crash-recovery contract (SPEC §H7 item 3, T-W5.3-06): a
        // record whose publish provably finished reconciles to its own
        // output — no rebuild, no prompt, no write — an equal projection
        // clears the record; a differing graph keeps its state and gets
        // exactly one prompt bound to the published revision; Cancel
        // keeps the record for a later explicit retry; a recovery
        // Overwrite supersedes the record with its own revision-bound
        // intent; a record the graph cannot act on is left untouched
        // and the save is refused.

        [Test]
        public void Intent_PublishDoneProjectionEqual_Reconciles()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("recover-eq.opweave.json");
                var baseline = ImportAndLink(graph, path);
                var coordinator = TestCoordinator();
                var resolved = coordinator.ResolveOwnedPath(path).ResolvedPath!;
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var intentPath = coordinator.IntentPathFor(guid);
                var bytes = File.ReadAllBytes(path);
                var diskDoc = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                var exported = GraphJsonConverter.Export(graph, diskDoc);
                Assert.IsTrue(exported.Ok);
                // The publish reached the disk exactly; the graph was not
                // touched — the recorded output is what the graph projects.
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    GraphJsonFile.GuiOwnedHash(exported.Value!))).Ok);

                var result = GraphJsonSync.Save(graph, _ =>
                {
                    Assert.Fail("a reconciled intent must not prompt");
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreEqual(GraphJsonSyncOutcome.Saved, result.Outcome);
                Assert.IsFalse(File.Exists(intentPath),
                    "the reconciled intent clears");
                AssertSynced(graph, path, diskDoc);
            });
        }

        [Test]
        public void Intent_PublishDoneProjectionDiffers_CancelThenReloadRetry()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("recover-conflict.opweave.json");
                var baseline = ImportAndLink(graph, path);
                var baselineBytes = File.ReadAllBytes(path);
                var coordinator = TestCoordinator();
                var resolved = coordinator.ResolveOwnedPath(path).ResolvedPath!;
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var intentPath = coordinator.IntentPathFor(guid);

                // The interrupted write published this output; the graph's
                // own persisted model still models the baseline — the
                // projection differs from the intent's outputGuiHash, the
                // recovery conflict that needs consent.
                var output = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Published\"");
                File.WriteAllText(path, output);
                var published = File.ReadAllBytes(path);
                var outputDoc = WorkflowSerializer.Read(output).Value!;
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(baselineBytes),
                    JsonSaveCoordinator.Sha256Hex(published),
                    GraphJsonFile.GuiOwnedHash(outputDoc))).Ok);

                var prompts = 0;
                var first = GraphJsonSync.Save(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.Cancel;
                });
                Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, first.Outcome);
                Assert.AreEqual(1, prompts);
                Assert.IsTrue(File.Exists(intentPath),
                    "Cancel keeps the intent — a later retry asks again");
                AssertSynced(graph, path, baseline,
                    "Cancel leaves the graph's state untouched");
                CollectionAssert.AreEqual(
                    published, File.ReadAllBytes(path),
                    "Cancel leaves the published JSON untouched");

                var retry = GraphJsonSync.Save(graph, check =>
                {
                    prompts++;
                    Assert.AreEqual(
                        outputDoc.Canonical.WorkflowHash,
                        check.Document!.Canonical.WorkflowHash,
                        "the prompt presents the published revision");
                    return GraphJsonSyncChoice.ReloadJson;
                });
                Assert.AreEqual(GraphJsonSyncOutcome.Reloaded, retry.Outcome);
                Assert.AreEqual(2, prompts);
                Assert.IsFalse(File.Exists(intentPath),
                    "the accepted reload clears the intent");
                CollectionAssert.AreEqual(
                    published, File.ReadAllBytes(path),
                    "the published JSON is never rewritten");
                AssertSynced(graph, path, outputDoc);
            });
        }

        [Test]
        public void Intent_InvalidRecord_LeftUntouchedAndSaveRefused()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("recover-invalid.opweave.json");
                ImportAndLink(graph, path);
                // A graph edit the save must publish — the sealed slot
                // refuses it.
                Assert.IsTrue(GraphJsonConverter.Import(
                    graph,
                    FixtureSync.Replace(
                        "\"name\": \"Alpha\"", "\"name\": \"Unsynced\"")).Ok);
                var coordinator = TestCoordinator();
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var intentPath = coordinator.IntentPathFor(guid);
                Directory.CreateDirectory(Path.GetDirectoryName(intentPath)!);
                File.WriteAllText(intentPath, "{ not valid intent");
                var record = File.ReadAllBytes(intentPath);
                var jsonBefore = File.ReadAllBytes(path);

                var warnings = 0;
                void OnLog(string condition, string stack, LogType type)
                {
                    if (type == LogType.Warning && condition.Contains("intent")) warnings++;
                }
                Application.logMessageReceived += OnLog;
                try
                {
                    var result = GraphJsonSync.Save(graph, _ =>
                    {
                        Assert.Fail("an unreadable intent must not prompt");
                        return GraphJsonSyncChoice.Cancel;
                    });
                    Assert.AreNotEqual(GraphJsonSyncOutcome.Saved, result.Outcome,
                        "a save that must publish is refused past an unreadable record");
                    CollectionAssert.AreEqual(
                        jsonBefore, File.ReadAllBytes(path),
                        "the refused save writes nothing");
                    // The next save is refused again — the warning fires
                    // once, the record is never touched.
                    var again = GraphJsonSync.Save(graph, _ => GraphJsonSyncChoice.Cancel);
                    Assert.AreNotEqual(GraphJsonSyncOutcome.Saved, again.Outcome);
                }
                finally
                {
                    Application.logMessageReceived -= OnLog;
                }
                Assert.AreEqual(1, warnings, "an invalid intent warns exactly once");
                CollectionAssert.AreEqual(
                    record, File.ReadAllBytes(intentPath),
                    "an invalid intent is never cleared");
            });
        }

        [Test]
        public void Intent_ForeignRecord_LeftUntouchedAndSaveRefused()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("recover-foreign.opweave.json");
                ImportAndLink(graph, path);
                var coordinator = TestCoordinator();
                var resolved = coordinator.ResolveOwnedPath(path).ResolvedPath!;
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var bytes = File.ReadAllBytes(path);
                var diskDoc = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                var foreignPath = coordinator.IntentPathFor(
                    "ffffffffffffffffffffffffffffffff");
                Directory.CreateDirectory(Path.GetDirectoryName(foreignPath)!);
                // A well-formed record naming another graph, dropped into
                // this graph's record slot.
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    resolved, "ffffffffffffffffffffffffffffffff",
                    JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    GraphJsonFile.GuiOwnedHash(diskDoc))).Ok);
                var intentPath = coordinator.IntentPathFor(guid);
                if (File.Exists(intentPath))
                {
                    File.Delete(intentPath);
                }
                File.Move(foreignPath, intentPath);
                var record = File.ReadAllBytes(intentPath);

                var result = GraphJsonSync.Save(graph, _ =>
                {
                    Assert.Fail("a foreign intent must not prompt");
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreNotEqual(GraphJsonSyncOutcome.Saved, result.Outcome);
                CollectionAssert.AreEqual(
                    record, File.ReadAllBytes(intentPath),
                    "a foreign record is never cleared");
            });
        }

        [Test]
        public void Intent_OutsideProjectRecord_LeftUntouchedAndSaveRefused()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("recover-outside.opweave.json");
                ImportAndLink(graph, path);
                var coordinator = TestCoordinator();
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var bytes = File.ReadAllBytes(path);
                var diskDoc = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                var outsidePath = Path.Combine(
                    Path.GetFullPath(TempDir), "..", "..", "..", "outside-project.opweave.json");
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    outsidePath, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    GraphJsonFile.GuiOwnedHash(diskDoc))).Ok);
                var intentPath = coordinator.IntentPathFor(guid);
                var record = File.ReadAllBytes(intentPath);

                var result = GraphJsonSync.Save(graph, _ =>
                {
                    Assert.Fail("an outside-project intent must not prompt");
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreNotEqual(GraphJsonSyncOutcome.Saved, result.Outcome);
                CollectionAssert.AreEqual(
                    record, File.ReadAllBytes(intentPath),
                    "an outside-project record is never cleared");
                // A save that would choose Reload is refused by the
                // sealed slot before any prompt — a record the recovery
                // cannot act on is never cleared by any entry.
                File.WriteAllText(
                    path,
                    FixtureSync.Replace("\"name\": \"Alpha\"", "\"name\": \"External\""));
                var afterEdit = File.ReadAllBytes(path);
                var prompts = 0;
                var refused = GraphJsonSync.Save(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.ReloadJson;
                });
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, refused.Outcome);
                Assert.AreEqual(0, prompts,
                    "the sealed slot refuses before any prompt");
                CollectionAssert.AreEqual(
                    record, File.ReadAllBytes(intentPath),
                    "an invalid record is never cleared");
                CollectionAssert.AreEqual(
                    afterEdit, File.ReadAllBytes(path),
                    "a refused save never writes the JSON");
            });
        }

        [Test]
        public void Intent_PathMismatchRecord_LeftUntouchedAndSaveRefused()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("recover-mismatch.opweave.json");
                ImportAndLink(graph, path);
                var coordinator = TestCoordinator();
                var resolved = coordinator.ResolveOwnedPath(path).ResolvedPath!;
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var bytes = File.ReadAllBytes(path);
                var diskDoc = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                var other = coordinator.ResolveOwnedPath(
                    JsonPath("recover-mismatch-other.opweave.json")).ResolvedPath!;
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    other, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    GraphJsonFile.GuiOwnedHash(diskDoc))).Ok);
                var intentPath = coordinator.IntentPathFor(guid);
                var record = File.ReadAllBytes(intentPath);

                var result = GraphJsonSync.Save(graph, _ =>
                {
                    Assert.Fail("a path-mismatch intent must not prompt");
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreNotEqual(GraphJsonSyncOutcome.Saved, result.Outcome);
                StringAssert.Contains(
                    "was not resolved; the save is refused until it is resolved",
                    result.Check!.ErrorMessage!,
                    "the held-record refusal names why the record held the save");
                StringAssert.Contains(
                    intentPath,
                    result.Check!.ErrorMessage!,
                    "the refusal names the record path");
                CollectionAssert.AreEqual(
                    record, File.ReadAllBytes(intentPath),
                    "a path-mismatch record is never cleared");
                // The sealed slot refuses every later save on the same
                // record, before any prompt.
                File.WriteAllText(
                    path,
                    FixtureSync.Replace("\"name\": \"Alpha\"", "\"name\": \"External\""));
                var afterEdit = File.ReadAllBytes(path);
                var prompts = 0;
                var refused = GraphJsonSync.Save(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.ReloadJson;
                });
                Assert.AreEqual(GraphJsonSyncOutcome.Refused, refused.Outcome);
                Assert.AreEqual(0, prompts,
                    "the sealed slot refuses before any prompt");
                CollectionAssert.AreEqual(
                    record, File.ReadAllBytes(intentPath),
                    "an invalid record is never cleared");
                CollectionAssert.AreEqual(
                    afterEdit, File.ReadAllBytes(path),
                    "a refused save never writes the JSON");
            });
        }

        // intent-reread-under-lock: a record replaced after the path lock
        // was taken is never cleared from the stale snapshot — the
        // compare-and-clear reports the change, the re-read retries once
        // and the save resolves against the NEWER record alone.
        [Test]
        public void Intent_NewerRecordAfterLock_NeverClearedFromStaleSnapshot()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("raced-record.opweave.json");
                var baseline = ImportAndLink(graph, path);
                var coordinator = TestCoordinator();
                var resolved = coordinator.ResolveOwnedPath(path).ResolvedPath!;
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var intentPath = coordinator.IntentPathFor(guid);
                var bytes = File.ReadAllBytes(path);
                var diskDoc = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                // The stale record of a publish that never reached the
                // disk: the first read under the lock would clear it and
                // run the normal flow.
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    JsonSaveCoordinator.Sha256Hex(
                        JsonSaveCoordinator.Utf8Bytes("the output that never landed")),
                    GraphJsonFile.GuiOwnedHash(diskDoc))).Ok);

                // After each re-read under the held lock, an uncoordinated
                // writer replaces the record once — with a published
                // output whose gui hash the unchanged graph does not
                // project, so the recovery must ask once.
                var newerGuiHash = JsonSaveCoordinator.Sha256Hex(
                    JsonSaveCoordinator.Utf8Bytes("not the graph's projection"));
                var probes = 0;
                byte[]? newerRecord = null;
                GraphJsonSync.IntentRecordProbe = _ =>
                {
                    probes++;
                    if (newerRecord is null)
                    {
                        Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                            resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                            JsonSaveCoordinator.Sha256Hex(bytes),
                            JsonSaveCoordinator.Sha256Hex(bytes),
                            newerGuiHash)).Ok);
                        newerRecord = File.ReadAllBytes(intentPath);
                    }
                };
                try
                {
                    var prompts = 0;
                    var result = GraphJsonSync.Save(graph, _ =>
                    {
                        prompts++;
                        return GraphJsonSyncChoice.Cancel;
                    });

                    Assert.AreEqual(GraphJsonSyncOutcome.Cancelled, result.Outcome,
                        "the retry resolves against the newer record's conflict");
                    Assert.AreEqual(1, prompts,
                        "the newer record's conflict prompts exactly once");
                    Assert.AreEqual(2, probes,
                        "the changed record is re-read and revalidated exactly once");
                    Assert.IsTrue(File.Exists(intentPath),
                        "the newer record is never cleared from the stale snapshot");
                    CollectionAssert.AreEqual(
                        newerRecord, File.ReadAllBytes(intentPath),
                        "the newer record's bytes are untouched");
                    CollectionAssert.AreEqual(
                        bytes, File.ReadAllBytes(path),
                        "the cancelled recovery writes nothing");
                    AssertSynced(graph, path, baseline,
                        "the cancelled recovery moves no baseline");
                }
                finally
                {
                    GraphJsonSync.IntentRecordProbe = null;
                }
            });
        }

        // sibling-intent-unlinked-only (SPEC §H7 item 3): the sibling path
        // validates an intent only for an unlinked first link — a record
        // naming it on a LINKED graph matches neither the recorded link
        // nor the sibling rule, so it is never acted on: the save is
        // refused and the record, the linked JSON and the sibling file
        // all stay exactly as found.
        [Test]
        public void Intent_LinkedGraphSiblingRecord_BothFilesAndRecordUntouched()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("linked-sibling.opweave.json");
                ImportAndLink(graph, path);
                var coordinator = TestCoordinator();
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var intentPath = coordinator.IntentPathFor(guid);
                File.WriteAllText(SiblingJsonPath, FixtureSync);
                var sibling = coordinator.ResolveOwnedPath(SiblingJsonPath);
                Assert.AreEqual(PathResolveStatus.Resolved, sibling.Status,
                    "the sibling path resolves inside the project");
                var bytes = File.ReadAllBytes(path);
                var siblingBytes = File.ReadAllBytes(SiblingJsonPath);
                var diskDoc = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    sibling.ResolvedPath!, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    GraphJsonFile.GuiOwnedHash(diskDoc))).Ok);
                var record = File.ReadAllBytes(intentPath);

                var prompts = 0;
                var result = GraphJsonSync.Save(graph, _ =>
                {
                    prompts++;
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreNotEqual(GraphJsonSyncOutcome.Saved, result.Outcome,
                    "a record the linked graph cannot act on refuses the save");
                Assert.AreEqual(0, prompts,
                    "a record the graph cannot act on never prompts");
                CollectionAssert.AreEqual(
                    record, File.ReadAllBytes(intentPath),
                    "the sibling-named record is never cleared");
                CollectionAssert.AreEqual(
                    bytes, File.ReadAllBytes(path),
                    "the linked JSON is untouched");
                CollectionAssert.AreEqual(
                    siblingBytes, File.ReadAllBytes(SiblingJsonPath),
                    "the sibling file is untouched");
            });
        }

        // pending-persist-outcome: a recovery whose persist could not be
        // proved keeps its outcome pending and the intent record stands as
        // the pending-persist of exactly that publish — the next save
        // entry's recovery repeats the reconcile and, once the persist is
        // durable, clears the record.
        [Test]
        public void Intent_PendingPersist_RepeatedRecoveryBecomesDurable()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("recover-pending.opweave.json");
                ImportAndLink(graph, path);
                var coordinator = TestCoordinator();
                var resolved = coordinator.ResolveOwnedPath(path).ResolvedPath!;
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var intentPath = coordinator.IntentPathFor(guid);
                var bytes = File.ReadAllBytes(path);
                var diskDoc = WorkflowSerializer.Read(File.ReadAllText(path)).Value!;
                var exported = GraphJsonConverter.Export(graph, diskDoc);
                Assert.IsTrue(exported.Ok);
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    JsonSaveCoordinator.Sha256Hex(bytes),
                    GraphJsonFile.GuiOwnedHash(exported.Value!))).Ok);

                GraphJsonSync.PersistFault = _ =>
                    throw new InvalidOperationException("injected persist failure");
                try
                {
                    var first = GraphJsonSync.Save(graph, _ =>
                    {
                        Assert.Fail("a reconciled intent must not prompt");
                        return GraphJsonSyncChoice.Cancel;
                    });
                    Assert.AreEqual(GraphJsonSyncOutcome.PersistPending, first.Outcome,
                        "an unproven persist reports pending, never Saved");
                }
                finally
                {
                    GraphJsonSync.PersistFault = null;
                }
                Assert.IsTrue(File.Exists(intentPath),
                    "the pending persist keeps the intent record");

                var second = GraphJsonSync.Save(graph, _ =>
                {
                    Assert.Fail("a repeated reconcile must not prompt");
                    return GraphJsonSyncChoice.Cancel;
                });

                Assert.AreEqual(GraphJsonSyncOutcome.Saved, second.Outcome,
                    "the repeated recovery's persist proves durable");
                Assert.IsFalse(File.Exists(intentPath),
                    "the durable reconcile clears the intent");
                AssertSynced(graph, path, diskDoc);
            });
        }

        [Test]
        public void Intent_RecoveryOverwrite_ReplacesRecordAndPublishes()
        {
            WithGraph(graph =>
            {
                var path = JsonPath("recover-overwrite.opweave.json");
                var baseline = ImportAndLink(graph, path);
                var baselineBytes = File.ReadAllBytes(path);
                var coordinator = TestCoordinator();
                var resolved = coordinator.ResolveOwnedPath(path).ResolvedPath!;
                var guid = AssetDatabase.AssetPathToGUID(TempPath);
                var intentPath = coordinator.IntentPathFor(guid);

                // The interrupted write published this output while the
                // graph still models the baseline — the recovery conflict
                // the Overwrite resolves with its own revision-bound update.
                var output = FixtureSync.Replace(
                    "\"name\": \"Alpha\"", "\"name\": \"Published\"");
                File.WriteAllText(path, output);
                var outputBytes = File.ReadAllBytes(path);
                var outputDoc = WorkflowSerializer.Read(output).Value!;
                Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.Sha256Hex(baselineBytes),
                    JsonSaveCoordinator.Sha256Hex(outputBytes),
                    GraphJsonFile.GuiOwnedHash(outputDoc))).Ok);

                var result = GraphJsonSync.Save(graph, _ =>
                    GraphJsonSyncChoice.OverwriteJson);

                Assert.AreEqual(GraphJsonSyncOutcome.Overwritten, result.Outcome);
                Assert.IsTrue(File.Exists(path));
                var written = File.ReadAllText(path);
                StringAssert.Contains("\"name\": \"Alpha\"", written,
                    "the graph's own projection lands");
                Assert.IsFalse(File.Exists(intentPath),
                    "the recovery overwrite supersedes the record");
                AssertSynced(graph, path,
                    WorkflowSerializer.Read(File.ReadAllText(path)).Value!);
            });
        }
    }
}
#endif
