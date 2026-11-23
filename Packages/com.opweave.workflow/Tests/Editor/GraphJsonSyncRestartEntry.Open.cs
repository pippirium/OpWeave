#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.3-07 [Edit] restart entries for the JSON sync intent protocol's
// open-time half (SPEC §H7 item 3): the production open —
// AssetDatabase.OpenAsset and the real delayCall drain — replaces the
// explicit save the save-side verifies drive. A claimed scenario
// arranges its own exit: the arrange/edit scenarios exit 0 when their
// synchronous work finishes (Run defers every claimed scenario); the
// verify scenarios install an EditorApplication.update subscription
// that waits for the real drain — OpenChecksPerformed for the GUID
// must grow by exactly one — bounded by a 120 s deadline, then asserts
// and exits. OWP_W53_CHOICE carries the verify-reopen answer
// ("cancel", "reload" or "overwrite"). In -batchmode -nographics the
// open's window device errors are expected; only the exact two
// headless messages stay out of the verdict.
//
//   verify-a-open  session 2+: the production open reconciles the
//                  intent the crash left — no prompt, no rebuild, no
//                  write. An unlinked persisted graph whose intent slot
//                  still holds the record queues the check too; a
//                  differing graph gets the recovery prompt (answered
//                  Cancel here).
//   arrange-reopen linked graph, GUI-edited and asset-saved: the JSON
//                  is now behind the graph. Session 2's edit-json
//                  moves D, then verify-reopen/<choice> opens the
//                  ORIGINAL asset uncached in a fresh session.
//   edit-json      changes one CanonicalWorkflow value in the linked
//                  JSON — D diverges from the recorded baseline.
//   verify-reopen  session 2+ on arrange-reopen + edit-json: the real
//                  production open runs exactly one OnEnable, one
//                  performed check and one dialog bound to hash(D),
//                  then applies OWP_W53_CHOICE.
//   arrange-clean-save
//                  session 1: link a graph, run Save JSON on a
//                  model-clean graph and exit normally — the publish's
//                  baseline either already persisted or stays
//                  pending-persist on the retained intent (no crash).
//   verify-clean-open
//                  session 2: the real OpenAsset path drains the one
//                  open check — no prompt, no JSON write (bytes and
//                  mtime), no rebuild: the existing reconcile branch
//                  takes a standing intent — recording the first link
//                  when the persisted graph never carried it — the
//                  in-sync check a carried baseline.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using OpWeave.GraphToolkit;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    public static partial class GraphJsonSyncRestartEntry
    {
        // T-W5.3-06's dispatch seam: the partial claims a scenario by
        // setting handled — a claimed scenario arranges its own exit
        // because Run defers it. The arrange/edit scenarios finish
        // synchronously and exit 0 directly; the verify scenarios
        // install the update subscription AwaitOpenCheck pumps until
        // the real delayCall drain performs the check, then assert and
        // exit — never a synchronous exit past the drain.
        static partial void RunOpenScenario(
            string scenario, string jobDir, ref bool handled)
        {
            switch (scenario)
            {
                case "arrange-reopen":
                    handled = true;
                    ArrangeReopen();
                    EditorApplication.Exit(0);
                    break;
                case "edit-json":
                    handled = true;
                    EditJson();
                    EditorApplication.Exit(0);
                    break;
                case "verify-a-open":
                    handled = true;
                    VerifyOpenReconcile(jobDir);
                    break;
                case "verify-reopen":
                    handled = true;
                    VerifyReopen(
                        jobDir,
                        Environment.GetEnvironmentVariable(ChoiceEnvVar) ??
                            "overwrite");
                    break;
                case "arrange-clean-save":
                    handled = true;
                    ArrangeCleanSave();
                    EditorApplication.Exit(0);
                    break;
                case "verify-clean-open":
                    handled = true;
                    VerifyCleanOpen(jobDir);
                    break;
            }
        }

        // ── arrange phases ────────────────────────────────────────────

        // The uncached-reopen arrangement: the ORIGINAL asset is created,
        // imported V1 and linked through the real save seam — the
        // first-link create writes the JSON — then one GUI edit is
        // asset-saved so the JSON falls behind the graph while the file
        // still holds exactly the recorded revision. A later session's
        // edit-json moves D and verify-reopen opens this same asset with
        // nothing cached.
        private static void ArrangeReopen()
        {
            var graph = NewGraph(FixtureV1);
            var first = SaveJson(graph);
            Require(
                first.SaveStatus == GraphJsonSaveStatus.Saved &&
                File.Exists(JsonPath),
                "the first-link save must create " + JsonPath +
                    " (status " + first.SaveStatus + ")");
            Import(graph, FixtureV2);
            GraphDatabase.SaveGraph(graph);
        }

        // The clean-save arrangement: link the graph through the real
        // save seam (the dirty model carries the first baseline into the
        // persisted asset), persist an edited model so the next publish
        // is baseline-only, then run Save JSON on the model-clean graph
        // and exit normally — the update's intent stands whenever the
        // baseline-only persist could not carry it; no crash needed.
        private static void ArrangeCleanSave()
        {
            var graph = NewGraph(FixtureV1);
            var first = SaveJson(graph);
            Require(
                first.SaveStatus == GraphJsonSaveStatus.Saved &&
                File.Exists(JsonPath),
                "the first-link save must create " + JsonPath +
                    " (status " + first.SaveStatus + ")");
            Import(graph, FixtureV2);
            GraphDatabase.SaveGraph(graph);
            var second = SaveJson(graph);
            Require(
                second.SaveStatus == GraphJsonSaveStatus.Saved,
                "the model-clean save must publish its update (status " +
                    second.SaveStatus + ")");
        }

        // The external between-sessions edit for verify-reopen: one
        // CanonicalWorkflow value in the linked JSON changes — D diverges
        // from the recorded baseline while staying a valid document.
        private static void EditJson()
        {
            var text = File.ReadAllText(JsonPath);
            var edited = text.Replace("\"Alpha\"", "\"Alpha3\"");
            Require(
                !string.Equals(edited, text, StringComparison.Ordinal),
                "the linked JSON must hold the fixture value to edit");
            var read = WorkflowSerializer.Read(edited);
            Require(
                read.Ok && read.Value is not null,
                "the edited JSON must still parse");
            File.WriteAllText(JsonPath, edited);
        }

        // ── verify phases ─────────────────────────────────────────────

        // Recovery (A) through the production open: AssetDatabase.
        // OpenAsset drives the documented [OnOpenAsset] chain — the same
        // user-facing flow a double-click rides — the load's OnEnable
        // queues the one open check and the real delayCall drain
        // reconciles the intent: no prompt, no rebuild, no write. The
        // interrupted first link is recovered the same way: publication
        // precedes graph persistence, so the persisted graph has no
        // recorded link while its GUID-keyed intent slot still holds the
        // record — the open queues the check and the drain reconciles it
        // (matching graph) or offers the revision-bound recovery choice
        // (differing graph, answered Cancel here: the record, the file
        // and the graph stay exactly as found).
        private static void VerifyOpenReconcile(string jobDir)
        {
            var guid = AssetDatabase.AssetPathToGUID(AssetPath);
            Require(guid.Length != 0, "the graph asset must have a GUID");
            Require(
                GraphJsonSync.FindLiveGraph(AssetPath) is null,
                "a fresh session must hold no live instance of the arranged graph");
            Require(
                EnablesOf(guid) == 0,
                "a fresh session ran no OnEnable for the arranged graph");
            var diskBefore = File.Exists(JsonPath)
                ? File.ReadAllBytes(JsonPath)
                : throw new InvalidOperationException(
                    "the published JSON must be on disk: " + JsonPath);
            var diskHash = JsonSaveCoordinator.Sha256Hex(diskBefore);
            var prompts = 0;
            string? presented = null;
            GraphJsonSync.OpenChoiceProvider = check =>
            {
                prompts++;
                presented = check.PresentedRevision;
                return GraphJsonSyncChoice.Cancel;
            };
            var gate = new HeadlessErrorGate();
            var performedBefore = PerformedOf(guid);
            var main = AssetDatabase.LoadMainAssetAtPath(AssetPath);
            Require(
                main is not null,
                "the .opweavegraph asset must be on disk");
            AssetDatabase.OpenAsset(main!);
            var graph = LoadGraph();
            Require(
                EnablesOf(guid) == 1,
                "the fresh open runs exactly one OnEnable");
            var linkBefore = graph.jsonPath;
            var intent = ValidIntentOf(graph);
            Require(
                intent is not null,
                "the arranged intent must validate for this graph");
            var graphBefore = ProjectionOf(graph, diskBefore);
            if (!string.Equals(
                intent!.OutputGuiHash, graphBefore, StringComparison.Ordinal))
            {
                // The persisted graph moved since the interrupted write:
                // the open check offers the revision-bound recovery
                // choice exactly once; Cancel keeps the record, the file
                // and the graph exactly as found.
                AwaitOpenCheck(jobDir, guid, performedBefore, () =>
                {
                    try
                    {
                        Require(
                            prompts == 1,
                            "the differing graph's recovery prompts " +
                                "exactly once (saw " + prompts + ")");
                        Require(
                            presented == diskHash,
                            "the recovery prompt is bound to R = hash(D)");
                        Require(
                            File.ReadAllBytes(JsonPath).SequenceEqual(diskBefore),
                            "Cancel must not write the JSON");
                        Require(
                            ValidIntentOf(graph) is not null,
                            "Cancel keeps the pending intent");
                        Require(
                            graph.jsonPath == linkBefore,
                            "Cancel keeps the graph's recorded link state");
                        Require(
                            graphBefore == ProjectionOf(graph, diskBefore),
                            "Cancel preserves the graph");
                        gate.AssertOnlyExpected();
                        Verdict(
                            jobDir,
                            "open recovery prompt bound to hash(D) -> " +
                                "cancel kept intent");
                    }
                    finally
                    {
                        gate.Dispose();
                    }
                });
                return;
            }
            AwaitOpenCheck(jobDir, guid, performedBefore, () =>
            {
                try
                {
                    Require(
                        prompts == 0,
                        "recovery must not prompt (saw " + prompts + ")");
                    Require(
                        File.ReadAllBytes(JsonPath).SequenceEqual(diskBefore),
                        "reconcile must not write the JSON");
                    Require(
                        graph.jsonPath == JsonPath,
                        "the reconcile records the link " + JsonPath +
                            " (jsonPath: " + graph.jsonPath + ")");
                    Require(
                        graphBefore == ProjectionOf(graph, diskBefore),
                        "reconcile must not rebuild the graph");
                    Require(
                        graph.syncedBytesHash == diskHash,
                        "the reconcile moves the baseline to the published revision");
                    Require(
                        IntentFileOf(graph) is null ||
                            (string.Equals(
                                IntentOutputHashOf(graph), diskHash,
                                StringComparison.Ordinal) &&
                             !File.ReadAllText(AssetPath).Contains(diskHash)),
                        "the intent record must be cleared or stand as the " +
                            "pending-persist of exactly the published revision");
                    gate.AssertOnlyExpected();
                    Verdict(
                        jobDir,
                        "open reconciled" +
                            (linkBefore.Length == 0
                                ? " and recorded the first link" : "") +
                            " with no prompt, no rebuild, no write");
                }
                finally
                {
                    gate.Dispose();
                }
            });
        }

        // The clean-save open (arrange-clean-save → verify-clean-open):
        // the fresh session's real open — AssetDatabase.OpenAsset and
        // the real delayCall drain — meets either the persisted
        // baseline or the retained pending-persist intent; the existing
        // reconcile branch (or the plain in-sync check) finishes with
        // exactly one performed check, no prompt, no JSON write — bytes
        // and mtime — and no rebuild. The link itself is a
        // baseline-only field, so a persisted file that never carried it
        // opens unlinked — its standing intent still queues the one
        // check and the drain reconciles the first link without a
        // prompt, exactly as on the linked path.
        private static void VerifyCleanOpen(string jobDir)
        {
            var guid = AssetDatabase.AssetPathToGUID(AssetPath);
            Require(guid.Length != 0, "the graph asset must have a GUID");
            Require(
                GraphJsonSync.FindLiveGraph(AssetPath) is null,
                "a fresh session must hold no live instance of the arranged graph");
            Require(
                EnablesOf(guid) == 0,
                "a fresh session ran no OnEnable for the arranged graph");
            var diskBefore = File.Exists(JsonPath)
                ? File.ReadAllBytes(JsonPath)
                : throw new InvalidOperationException(
                    "the published JSON must be on disk: " + JsonPath);
            var mtimeBefore = File.GetLastWriteTimeUtc(JsonPath);
            var prompts = 0;
            GraphJsonSync.OpenChoiceProvider = check =>
            {
                prompts++;
                return GraphJsonSyncChoice.Cancel;
            };
            var gate = new HeadlessErrorGate();
            var performedBefore = PerformedOf(guid);
            var main = AssetDatabase.LoadMainAssetAtPath(AssetPath);
            Require(
                main is not null,
                "the .opweavegraph asset must be on disk");
            AssetDatabase.OpenAsset(main!);
            var graph = LoadGraph();
            var wasLinked = graph.jsonPath.Length != 0;
            Require(
                EnablesOf(guid) == 1,
                "the fresh open runs exactly one OnEnable");
            var graphBefore = ProjectionOf(graph, diskBefore);
            AwaitOpenCheck(jobDir, guid, performedBefore, () =>
            {
                try
                {
                    Require(
                        prompts == 0,
                        "a clean open must not prompt (saw " + prompts + ")");
                    Require(
                        File.ReadAllBytes(JsonPath).SequenceEqual(diskBefore),
                        "the open writes nothing to the JSON");
                    Require(
                        File.GetLastWriteTimeUtc(JsonPath) == mtimeBefore,
                        "the open never touches the JSON's mtime");
                    Require(
                        graph.jsonPath == JsonPath,
                        "the open " +
                            (wasLinked ? "keeps the recorded link "
                                : "reconciled the interrupted first link ") +
                            JsonPath + " (jsonPath: " + graph.jsonPath + ")");
                    Require(
                        graphBefore == ProjectionOf(graph, diskBefore),
                        "the open does not rebuild the graph");
                    var intentOutput = IntentOutputHashOf(graph);
                    Require(
                        intentOutput is null ||
                            string.Equals(
                                intentOutput,
                                JsonSaveCoordinator.Sha256Hex(diskBefore),
                                StringComparison.Ordinal),
                        "a standing intent must be the pending-persist " +
                            "of this exact revision");
                    gate.AssertOnlyExpected();
                    Verdict(
                        jobDir,
                        "clean open: one check, no prompt, no write, no rebuild" +
                            (wasLinked ? "" : "; first link recorded"));
                }
                finally
                {
                    gate.Dispose();
                }
            });
        }

        // The uncached reopen of the ORIGINAL asset across sessions
        // (arrange-reopen → edit-json → verify-reopen/<choice>): the
        // fresh session holds nothing, so the production open
        // deserializes the persisted .opweavegraph — exactly one
        // OnEnable, one performed check and one dialog bound to hash(D)
        // through the OpenChoiceProvider seam, then OWP_W53_CHOICE:
        // Cancel keeps the file and the unsynced edit, Reload rebuilds
        // the graph from D, Overwrite makes D the graph's export.
        private static void VerifyReopen(string jobDir, string choice)
        {
            var guid = AssetDatabase.AssetPathToGUID(AssetPath);
            Require(guid.Length != 0, "the graph asset must have a GUID");
            Require(
                GraphJsonSync.FindLiveGraph(AssetPath) is null,
                "a fresh session must hold no live instance of the original asset");
            Require(
                EnablesOf(guid) == 0,
                "a fresh session ran no OnEnable for the original GUID");
            var diskBefore = File.Exists(JsonPath)
                ? File.ReadAllBytes(JsonPath)
                : throw new InvalidOperationException(
                    "the edited JSON must be on disk: " + JsonPath);
            var diskHash = JsonSaveCoordinator.Sha256Hex(diskBefore);
            var answer = ChoiceOf(choice);
            var prompts = 0;
            string? presented = null;
            GraphJsonSync.OpenChoiceProvider = check =>
            {
                prompts++;
                presented = check.PresentedRevision;
                return answer;
            };
            var gate = new HeadlessErrorGate();
            var performedBefore = PerformedOf(guid);
            var main = AssetDatabase.LoadMainAssetAtPath(AssetPath);
            Require(
                main is not null,
                "the .opweavegraph asset must be on disk");
            AssetDatabase.OpenAsset(main!);
            Require(
                EnablesOf(guid) == 1,
                "the uncached open runs exactly one OnEnable");
            AwaitOpenCheck(jobDir, guid, performedBefore, () =>
            {
                try
                {
                    var graph = LoadGraph();
                    Require(
                        prompts == 1,
                        "exactly one dialog through the open seam (saw " +
                            prompts + ")");
                    Require(
                        presented == diskHash,
                        "the dialog must be bound to hash(D)");
                    Require(
                        graph.jsonPath == JsonPath,
                        "the link is kept: " + JsonPath +
                            " (jsonPath: " + graph.jsonPath + ")");
                    if (choice == "cancel")
                    {
                        Require(
                            File.ReadAllBytes(JsonPath).SequenceEqual(diskBefore),
                            "Cancel leaves the JSON bytes untouched");
                        var exported = GraphJsonConverter.Export(
                            graph, ParseDoc(diskBefore));
                        Require(
                            exported.Ok &&
                            exported.Value!.Canonical.CanonicalText.Contains(
                                "\"Alpha2\""),
                            "Cancel keeps the graph's unsynced edit");
                        Require(
                            graph.syncedJson.Contains("\"Alpha\""),
                            "Cancel keeps the recorded baseline");
                    }
                    else if (choice == "reload")
                    {
                        Require(
                            File.ReadAllBytes(JsonPath).SequenceEqual(diskBefore),
                            "Reload must not write the JSON");
                        var diskDoc = ParseDoc(diskBefore);
                        Require(
                            graph.syncedBytesHash == diskHash,
                            "Reload moves the baseline to D");
                        var exported = GraphJsonConverter.Export(graph, diskDoc);
                        Require(
                            exported.Ok &&
                            exported.Value!.Canonical.CanonicalText ==
                                diskDoc.Canonical.CanonicalText,
                            "after Reload the graph models D");
                    }
                    else
                    {
                        var written = File.ReadAllBytes(JsonPath);
                        Require(
                            !written.SequenceEqual(diskBefore),
                            "Overwrite rewrites the file");
                        Require(
                            System.Text.Encoding.UTF8.GetString(written)
                                .Contains("\"Alpha2\""),
                            "Overwrite makes D equal the graph's export");
                        Require(
                            graph.syncedBytesHash ==
                                JsonSaveCoordinator.Sha256Hex(written),
                            "Overwrite moves the baseline to the written revision");
                    }
                    gate.AssertOnlyExpected();
                    Verdict(
                        jobDir,
                        "uncached reopen: one check, one dialog bound to hash(D) → " +
                            choice);
                }
                finally
                {
                    gate.Dispose();
                }
            });
        }

        // ── the deferred open drain ───────────────────────────────────
        //
        // An open scenario never exits synchronously: the update
        // subscription waits until the real delayCall drain has performed
        // the one queued check — OpenChecksPerformed for the GUID grew by
        // exactly one — or the 120 s deadline from
        // EditorApplication.timeSinceStartup turns a hung drain into
        // fatal.txt + exit 3.
        private static void AwaitOpenCheck(
            string jobDir, string guid, int performedBefore, Action finish)
        {
            var deadline = EditorApplication.timeSinceStartup + 120.0;
            EditorApplication.CallbackFunction tick = null!;
            tick = () =>
            {
                try
                {
                    var performed = PerformedOf(guid);
                    if (performed == performedBefore &&
                        EditorApplication.timeSinceStartup < deadline)
                    {
                        return;
                    }
                    EditorApplication.update -= tick;
                    Require(
                        performed == performedBefore + 1,
                        "the open check must drain exactly once within " +
                            "120 s (performed " +
                            (performed - performedBefore) + ")");
                    finish();
                    EditorApplication.Exit(0);
                }
                catch (Exception exception)
                {
                    EditorApplication.update -= tick;
                    Fail(jobDir, exception);
                }
            };
            EditorApplication.update += tick;
        }

        // In a -batchmode -nographics child the real open logs Graph
        // Toolkit's window device errors; only the two documented
        // messages are tolerated — anything else is a verdict failure.
        // The in-process tests gate the same two messages through
        // LogAssert; the child has no runner, so a listener collects and
        // the finish asserts.
        private sealed class HeadlessErrorGate : IDisposable
        {
            private static readonly Regex s_expected = new Regex(
                @"^No graphic device is available to " +
                @"(show the window|initialize the view)\.$");

            private readonly List<string> _messages = new List<string>();
            private readonly Application.LogCallback _listener;

            internal HeadlessErrorGate()
            {
                _listener = (message, stackTrace, type) =>
                {
                    if (type == LogType.Error || type == LogType.Exception ||
                        type == LogType.Assert)
                    {
                        _messages.Add(message);
                    }
                };
                Application.logMessageReceived += _listener;
            }

            public void Dispose() =>
                Application.logMessageReceived -= _listener;

            internal void AssertOnlyExpected()
            {
                foreach (var message in _messages)
                {
                    Require(
                        s_expected.IsMatch(message),
                        "an unexpected error reached the open path: " + message);
                }
            }
        }

        private static WorkflowDocument ParseDoc(byte[] bytes)
        {
            var read = WorkflowSerializer.Read(
                System.Text.Encoding.UTF8.GetString(bytes));
            Require(
                read.Ok && read.Value is not null,
                "the disk JSON must parse");
            return read.Value!;
        }

        // The open-check instrumentation the verify scenarios read per
        // GUID: OnEnable hooks run and checks the deferred drain
        // performed.
        private static int EnablesOf(string guid) =>
            GraphJsonSync.OpenEnableCount.TryGetValue(guid, out var n) ? n : 0;

        private static int PerformedOf(string guid) =>
            GraphJsonSync.OpenChecksPerformed.TryGetValue(guid, out var n) ? n : 0;
    }
}
#endif
