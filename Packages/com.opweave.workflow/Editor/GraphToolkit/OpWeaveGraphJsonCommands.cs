#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The explicit "Save Graph JSON" command of SPEC §H7 item 3 (owner
    /// decision 2026-11-14, option (a)): the only new JSON writer is a
    /// human action reached three ways — the Project browser context menu
    /// <c>Assets &gt; OpWeave &gt; Save Graph JSON</c>, the main menu
    /// <c>Tools &gt; OpWeave &gt; Save Graph JSON</c> and a
    /// <see cref="ShortcutAttribute"/> binding with no default chord (so it
    /// can never collide with Unity's Ctrl+S, which keeps saving only the
    /// <c>.opweavegraph</c> asset). Only public Unity editor API is used:
    /// Graph Toolkit 6000.5.11f1's public surface has no toolbar or
    /// window-menu extension point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each entry acts on the selected <c>*.opweavegraph</c> assets through
    /// <see cref="GraphDatabase.LoadGraph{T}"/> — the same in-memory
    /// instance an open window edits — and hands each to the single
    /// <see cref="GraphJsonSync.Save"/> path: the first-link create-only
    /// write, the revision-bound linked update, the existing-file and
    /// graph-owned conflicts' three-choice prompt, and the intent-record
    /// recovery every explicit entry runs first. A validate function
    /// disables the menu items when the selection holds no graph. A save
    /// that does not complete — a refused, busy, I/O-failed or pending
    /// result — is reported once with a fixed, actionable warning; a
    /// cancelled or failed save that leaves the JSON behind the graph
    /// then fires the command event's unsynced-edits notification once
    /// per (GUID, export hash).
    /// </para>
    /// <para>
    /// This file also carries the notify-only halves of the unsynced-edits
    /// surfacing (design review D4): the <see cref="AssetPostprocessor"/>
    /// that reports a real import of a graph with unsaved JSON changes —
    /// never a writer — and the <see cref="EditorApplication.wantsToQuit"/>
    /// guard that offers to run this command for every live unsynced graph
    /// before the editor closes.
    /// </para>
    /// </remarks>
    internal static class OpWeaveGraphJsonCommands
    {
        /// <summary>The main-menu path the tests drive through <see cref="EditorApplication.ExecuteMenuItem"/>.</summary>
        internal const string ToolsMenuPath = "Tools/OpWeave/Save Graph JSON";

        // The Project browser context-menu path of the same command.
        private const string AssetsMenuPath = "Assets/OpWeave/Save Graph JSON";

        // The Shortcut Manager binding — declared with no default chord so
        // it never collides with Unity's Ctrl+S window Save.
        private const string ShortcutName = "OpWeave/Save Graph JSON";

        /// <summary>
        /// The one fixed English warning every verified notification event
        /// emits once per (GUID, export hash) while the JSON is behind the
        /// graph (SPEC §H7 item 3): the reopen drain, the command event,
        /// the asset-postprocess notification and the batch-mode quit
        /// guard share it. It names the explicit command — the only write
        /// path.
        /// </summary>
        internal const string UnsyncedEditsWarning =
            "Graph has changes not saved to JSON; use OpWeave > Save Graph JSON.";

        /// <summary>
        /// The one fixed English warning the quit guard emits when a
        /// requested Save JSON did not complete — a cancelled conflict, a
        /// refused write or an I/O failure: the quit is aborted and the
        /// message names the graph(s) whose save did not finish.
        /// </summary>
        internal const string QuitSaveIncompleteWarning =
            "Save Graph JSON did not complete; the quit was aborted for:";

        /// <summary>
        /// The one fixed English warning the command emits for each
        /// selected graph whose save did not complete (SPEC §H7 item 3):
        /// a refused, busy, I/O-failed or pending result is reported once
        /// — never as success — and the message names the graph and the
        /// actionable cause; a sealed intent slot's cause names the record
        /// path. A choice dialog the save already showed is never repeated:
        /// a Cancelled result is the human's own answer, not a report.
        /// </summary>
        internal const string CommandSaveIncompleteWarning =
            "Save Graph JSON did not complete for ";

        // The (GUID, export hash) pairs the import postprocessor already
        // notified about this session — one warning per pair per event,
        // whatever triggers the import.
        private static readonly HashSet<string> s_postprocessNotified =
            new HashSet<string>(StringComparer.Ordinal);

        // The (GUID, export hash) pairs the command already warned about
        // this session — the command event's own one-warning scope,
        // independent of the reopen and import events' sets.
        private static readonly HashSet<string> s_commandNotified =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly GUIContent s_notification = new GUIContent(
            "Graph has changes not saved to JSON — use OpWeave > Save Graph JSON.");

        /// <summary>The answer the quit guard's modal asks for.</summary>
        internal enum QuitGuardChoice
        {
            /// <summary>Run the explicit command for each unsynced graph, then quit.</summary>
            SaveJson = 0,

            /// <summary>Quit, keeping the unsynced edits in memory only.</summary>
            QuitWithoutSavingJson = 1,

            /// <summary>Abort the quit.</summary>
            CancelQuit = 2,
        }

        /// <summary>
        /// The quit guard's modal, or null for the production default —
        /// <see cref="EditorUtility.DisplayDialogComplex"/>. Tests install
        /// a scripted answer and restore null afterwards; the installed
        /// seam answers first — only with no provider does batch mode
        /// skip the choice and log instead of blocking.
        /// </summary>
        internal static Func<IReadOnlyList<OpWeaveGraph>, QuitGuardChoice>? QuitGuardChoiceProvider
        {
            get;
            set;
        }

        // ── The explicit command ──────────────────────────────────────

        /// <summary>The <c>Tools &gt; OpWeave &gt; Save Graph JSON</c> menu entry.</summary>
        [MenuItem(ToolsMenuPath)]
        private static void SaveGraphJsonMenu() => SaveSelectedGraphJson();

        // The validate function that disables the item when the selection
        // holds no .opweavegraph asset.
        [MenuItem(ToolsMenuPath, true)]
        private static bool SaveGraphJsonMenuValidate() => HasGraphSelection();

        /// <summary>The <c>Assets &gt; OpWeave &gt; Save Graph JSON</c> context entry.</summary>
        [MenuItem(AssetsMenuPath)]
        private static void SaveGraphJsonContextMenu() => SaveSelectedGraphJson();

        [MenuItem(AssetsMenuPath, true)]
        private static bool SaveGraphJsonContextValidate() => HasGraphSelection();

        /// <summary>
        /// The <see cref="ShortcutAttribute"/> binding — no default chord,
        /// so nothing collides with Ctrl+S. It resolves to the same
        /// selected-graph command as the menu entries.
        /// </summary>
        [Shortcut(ShortcutName)]
        private static void SaveGraphJsonShortcut() => SaveSelectedGraphJson();

        // The command body every entry shares (SPEC §H7 item 3): for each
        // selected .opweavegraph, hand the same in-memory instance the open
        // window edits to the single JsonSaveCoordinator/GraphJsonSync
        // save path — first-link create-only, linked revision-bound,
        // three-choice conflict prompt bound to the presented revision,
        // intent recovery first. A save that does not complete is reported
        // once (never as success); a graph the attempt still leaves
        // unsynced fires the command event's notification.
        private static void SaveSelectedGraphJson()
        {
            foreach (var path in SelectedGraphPaths())
            {
                var graph = GraphDatabase.LoadGraph<OpWeaveGraph>(path);
                if (graph is null)
                {
                    continue;
                }
                var result = SaveJsonForGraph(graph);
                ReportIfSaveIncomplete(graph, result);
                NotifyCommandUnsyncedEdits(graph);
            }
        }

        // The per-graph command: the explicit human save with the existing
        // three-choice prompt — the real prompt seam tests drive, the modal
        // dialog in production.
        internal static GraphJsonSyncResult SaveJsonForGraph(OpWeaveGraph graph) =>
            GraphJsonSync.Save(
                graph,
                GraphJsonSync.OpenChoiceProvider ??
                    GraphJsonSyncDialog.ShowConflictPrompt);

        // The .opweavegraph assets of the current selection — the command's
        // operand set for all three entries.
        private static List<string> SelectedGraphPaths()
        {
            var paths = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(
                    "." + OpWeaveGraph.AssetExtension,
                    StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }
            return paths;
        }

        private static bool HasGraphSelection()
        {
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(
                    "." + OpWeaveGraph.AssetExtension,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // The surface-refused-results rule (SPEC §H7 item 3): every
        // unsuccessful save is reported — a refused write (an unresolved
        // conflict, unparseable JSON, a busy path lock, an I/O failure or
        // a sealed intent slot), a chosen reload that found nothing
        // readable, an unreadable baseline and a published-but-unproven
        // pending persist each produce one fixed, actionable warning
        // naming the graph — never silence and never a success report.
        // Saved, Overwritten, Reloaded, NoAction and the human's own
        // Cancel are not reported: Cancel's choice dialog already showed.
        private static void ReportIfSaveIncomplete(
            OpWeaveGraph graph, GraphJsonSyncResult result)
        {
            switch (result.Outcome)
            {
                case GraphJsonSyncOutcome.Refused:
                case GraphJsonSyncOutcome.ReloadFailed:
                case GraphJsonSyncOutcome.BaselineUnavailable:
                case GraphJsonSyncOutcome.PersistPending:
                    break;
                default:
                    return;
            }
            Debug.LogWarning(
                CommandSaveIncompleteWarning + GraphAssetPathOrFallback(graph) +
                ": " + SaveIncompleteReason(result) +
                ". Resolve the cause, then run OpWeave > Save Graph JSON again.");
        }

        // The actionable cause a failed save report carries: the check's
        // own message when it has one — the busy lock, the I/O failure
        // and the sealed intent slot's record path all travel there —
        // else a fixed reason per outcome or disk state.
        private static string SaveIncompleteReason(GraphJsonSyncResult result)
        {
            var message = result.Check.ErrorMessage;
            if (!string.IsNullOrEmpty(message))
            {
                return message!;
            }
            if (result.Outcome == GraphJsonSyncOutcome.PersistPending)
            {
                return "the JSON was written but the graph's sync baseline " +
                    "could not be persisted; the next save finishes it";
            }
            if (result.Outcome == GraphJsonSyncOutcome.ReloadFailed)
            {
                return "the JSON document chosen for reload could not be read";
            }
            if (result.Outcome == GraphJsonSyncOutcome.BaselineUnavailable)
            {
                return "the graph's recorded sync baseline could not be read";
            }
            if (result.Check.State == GraphJsonSyncState.InvalidJson)
            {
                return "the JSON file does not parse as a workflow " +
                    "document; repair or remove it";
            }
            if (result.Check.State == GraphJsonSyncState.UnemittableGraph)
            {
                return "the graph carries content it cannot re-emit " +
                    "unambiguously";
            }
            return "the JSON's conflicting revision was not resolved";
        }

        // The command event's unsynced-edits notification (SPEC §H7 item
        // 3): after the command's save attempt the JSON may still be
        // behind the graph — a cancelled conflict or a refused write —
        // and the command is one of the verified events that report it,
        // once per (GUID, export hash), never a write.
        private static void NotifyCommandUnsyncedEdits(OpWeaveGraph graph)
        {
            var key = GraphJsonSync.UnsyncedExportHash(graph);
            if (key is null)
            {
                return;
            }
            var guid = AssetGuidOf(graph);
            if (!s_commandNotified.Add((guid ?? "") + "|" + key))
            {
                return;
            }
            EmitUnsyncedEditsNotification();
        }

        // The graph asset's GUID for the (GUID, export hash) dedupe key —
        // null when the instance no longer resolves to an asset.
        private static string? AssetGuidOf(OpWeaveGraph graph)
        {
            try
            {
                var assetPath = GraphDatabase.GetGraphAssetPath(graph);
                if (string.IsNullOrEmpty(assetPath))
                {
                    return null;
                }
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                return string.IsNullOrEmpty(guid) ? null : guid;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // One notification per (GUID, export hash): the transient bar on
        // the focused window when there is one, plus the fixed Console
        // warning — the only durable record a batch run keeps.
        internal static void NotifyUnsyncedEdits(string dedupeKey)
        {
            if (!s_postprocessNotified.Add(dedupeKey))
            {
                return;
            }
            EmitUnsyncedEditsNotification();
        }

        // The bar-plus-warning emission every notification event shares.
        private static void EmitUnsyncedEditsNotification()
        {
            if (EditorWindow.focusedWindow is not null)
            {
                EditorWindow.focusedWindow.ShowNotification(s_notification);
            }
            Debug.LogWarning(UnsyncedEditsWarning);
        }

        // ── The quit guard ──────────────────────────────────────────────

        // The registered wantsToQuit handler (public API): when the editor
        // is asked to close with live graphs the JSON is behind, one modal
        // offers "Save JSON for these graphs / Quit without saving JSON /
        // Cancel quit". "Save JSON" runs the explicit command for each —
        // conflict prompts included — and the quit proceeds only when
        // every requested save completed: a cancelled, refused or failed
        // save aborts it with one fixed warning naming the graph(s).
        // With no seam installed batch mode
        // never blocks: it logs the same fixed warning and lets the quit
        // proceed.
        [InitializeOnLoadMethod]
        private static void RegisterQuitGuard() =>
            EditorApplication.wantsToQuit += WantsToQuit;

        // The registered handler itself — internal so the gate test drives
        // it directly with the modal seam installed.
        internal static bool WantsToQuit()
        {
            var unsynced = new List<OpWeaveGraph>();
            foreach (var graph in GraphJsonSync.LiveGraphs())
            {
                if (GraphJsonSync.UnsyncedExportHash(graph) is not null)
                {
                    unsynced.Add(graph);
                }
            }
            if (unsynced.Count == 0)
            {
                return true;
            }
            // The installed seam answers first — the batch-mode no-block
            // fallback covers only the case where no provider is installed.
            if (QuitGuardChoiceProvider is null && Application.isBatchMode)
            {
                Debug.LogWarning(UnsyncedEditsWarning);
                return true;
            }
            var choice = QuitGuardChoiceProvider is not null
                ? QuitGuardChoiceProvider(unsynced)
                : PromptQuitChoice(unsynced);
            switch (choice)
            {
                case QuitGuardChoice.SaveJson:
                    var incomplete = new List<OpWeaveGraph>();
                    foreach (var graph in unsynced)
                    {
                        if (!RequestedSaveCompleted(SaveJsonForGraph(graph)))
                        {
                            incomplete.Add(graph);
                        }
                    }
                    if (incomplete.Count == 0)
                    {
                        return true;
                    }
                    var incompleteMessage =
                        new StringBuilder(QuitSaveIncompleteWarning);
                    foreach (var graph in incomplete)
                    {
                        incompleteMessage
                            .Append("\n  ")
                            .Append(GraphAssetPathOrFallback(graph));
                    }
                    Debug.LogWarning(incompleteMessage.ToString());
                    return false;
                case QuitGuardChoice.QuitWithoutSavingJson:
                    return true;
                default:
                    return false;
            }
        }

        // Whether one requested Save JSON completed: the JSON was written
        // (Saved / Overwritten) or the graph was deliberately rebuilt
        // from it (Reloaded) — either way no unsynced state remains.
        // A cancelled conflict, a refusal and every other outcome leave
        // the state exactly as found: the quit must not proceed.
        private static bool RequestedSaveCompleted(GraphJsonSyncResult result) =>
            result.Outcome == GraphJsonSyncOutcome.Saved ||
            result.Outcome == GraphJsonSyncOutcome.Overwritten ||
            result.Outcome == GraphJsonSyncOutcome.Reloaded;

        private static QuitGuardChoice PromptQuitChoice(
            IReadOnlyList<OpWeaveGraph> unsynced)
        {
            var message = new StringBuilder(
                unsynced.Count == 1
                    ? "1 graph has changes not saved to JSON:"
                    : unsynced.Count + " graphs have changes not saved to JSON:");
            foreach (var graph in unsynced)
            {
                message.Append("\n  ").Append(GraphAssetPathOrFallback(graph));
            }
            var answer = EditorUtility.DisplayDialogComplex(
                "OpWeave JSON not saved",
                message.ToString(),
                "Save JSON for these graphs",
                "Cancel quit",
                "Quit without saving JSON");
            return answer switch
            {
                0 => QuitGuardChoice.SaveJson,
                2 => QuitGuardChoice.QuitWithoutSavingJson,
                _ => QuitGuardChoice.CancelQuit,
            };
        }

        // The graph's asset path for a human-facing listing, or a fixed
        // placeholder when it can no longer be resolved — the listing is
        // informational, so any lookup failure still names the graph.
        private static string GraphAssetPathOrFallback(OpWeaveGraph graph)
        {
            try
            {
                return GraphDatabase.GetGraphAssetPath(graph);
            }
            catch (Exception)
            {
                return "<unsaved graph>";
            }
        }

        // Test-only reset: the postprocessor's and the command's
        // (GUID, export hash) dedupes are session state a fixture must
        // not leak across tests — cleared from
        // GraphJsonSync.ClearSyncTestState through the ClearNotifyTestState
        // seam alongside the drain's own notification set.
        internal static void ClearNotificationState()
        {
            s_postprocessNotified.Clear();
            s_commandNotified.Clear();
            QuitGuardChoiceProvider = null;
        }
    }

    /// <summary>
    /// The notify-only import observation of SPEC §H7 item 3 (design
    /// review D4): the verified <see cref="AssetPostprocessor"/> event that
    /// fires on the main thread after Graph Toolkit's window Save writes
    /// the asset. For an imported <c>.opweavegraph</c> whose live instance
    /// holds edits the JSON does not — the file still holds exactly the
    /// recorded revision while the graph's export moved — one notification
    /// per (GUID, export hash): a focused-window notification plus the
    /// fixed warning. It never writes, never syncs, never changes B or the
    /// JSON. A top-level type so Unity's postprocessor discovery always
    /// finds it.
    /// </summary>
    internal sealed class OpWeaveGraphJsonPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            foreach (var path in importedAssets)
            {
                if (!path.EndsWith(
                    "." + OpWeaveGraph.AssetExtension,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var graph = GraphJsonSync.FindLiveGraph(path);
                if (graph is null)
                {
                    continue;
                }
                var key = GraphJsonSync.UnsyncedExportHash(graph);
                if (key is null)
                {
                    continue;
                }
                var guid = AssetDatabase.AssetPathToGUID(path);
                OpWeaveGraphJsonCommands.NotifyUnsyncedEdits(guid + "|" + key);
            }
        }
    }
}
