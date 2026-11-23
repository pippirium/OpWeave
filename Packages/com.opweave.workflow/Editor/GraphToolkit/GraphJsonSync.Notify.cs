#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;

namespace OpWeave.GraphToolkit
{
    internal static partial class GraphJsonSync
    {
        // ── Unsynced-edits notification (SPEC §H7 item 3) ──────────────
        // T-W5.3-08's half of the "changes not saved to JSON" surfacing:
        // the unsynced test itself plus the verified reopen event's
        // implementation of the AfterOpenCheck seam declared in
        // GraphJsonSync.cs. The import postprocessor and the quit guard
        // live with the command in OpWeaveGraphJsonCommands.cs.

        // The unsynced-edits warnings the open-check drain already
        // emitted, keyed by "GUID | export projection hash": the verified
        // reopen event notifies exactly once per (GUID, export hash)
        // (SPEC §H7 item 3, decision 2026-11-14-t-w5-3-02-explicit-save-json).
        private static readonly HashSet<string> s_unsyncedNotified =
            new HashSet<string>(StringComparer.Ordinal);

        // The unsynced-edits test of SPEC §H7 item 3's verified events: the
        // graph is linked, the file still holds exactly the recorded
        // revision (D == B — nothing external moved it), and the graph's
        // own GUI-owned export no longer matches the baseline — the JSON
        // is behind the graph. The returned key is the graph's export
        // projection hash, the (GUID, export hash) dedupe part.
        // Null for every other state: the file moved, went missing or is
        // unreadable — those are the open check's conflict states, not
        // unsynced edits.
        internal static string? UnsyncedExportHash(
            OpWeaveGraph graph, WorkflowDocument diskDocument, string diskBytesHash)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (graph.syncedBytesHash.Length == 0 ||
                !string.Equals(
                    diskBytesHash, graph.syncedBytesHash, StringComparison.Ordinal))
            {
                return null;
            }
            var baselineHash = BaselineGuiOwnedHash(graph);
            if (baselineHash is null)
            {
                return null;
            }
            var projected = ProjectedGuiHash(graph, diskDocument);
            return projected is not null &&
                !string.Equals(projected, baselineHash, StringComparison.Ordinal)
                ? projected
                : null;
        }

        // The disk-side half of UnsyncedExportHash for callers that hold
        // a path rather than a fresh read (the command, the asset-import
        // and quit events): the file must exist and parse — a missing,
        // moved or unreadable JSON is not an unsynced-edits warning.
        internal static string? UnsyncedExportHash(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (graph.jsonPath.Length == 0)
            {
                return null;
            }
            var coordinator = Coordinator();
            var owned = coordinator.ResolveOwnedPath(graph.jsonPath);
            if (owned.Status != PathResolveStatus.Resolved)
            {
                return null;
            }
            var disk = GraphJsonFile.Read(owned.ResolvedPath!);
            var diskDoc = DiskDocument(disk);
            if (diskDoc is null || disk.BytesHash is null)
            {
                return null;
            }
            return UnsyncedExportHash(graph, diskDoc, disk.BytesHash);
        }

        // T-W5.3-08's implementation of the AfterOpenCheck seam declared
        // in GraphJsonSync.cs — the verified reopen notification (SPEC
        // §H7 item 3): the open check found the file still holding
        // exactly the recorded revision (InSync = its own GUI-owned
        // projection unchanged) and the graph's export moved — one fixed
        // warning per (GUID, export hash) naming the explicit save,
        // never a write.
        static partial void AfterOpenCheck(
            string guid, OpWeaveGraph graph, GraphJsonSyncCheck check)
        {
            if (check.State != GraphJsonSyncState.InSync ||
                check.Document is null || check.PresentedRevision is null)
            {
                return;
            }
            var key = UnsyncedExportHash(
                graph, check.Document, check.PresentedRevision);
            if (key is not null && s_unsyncedNotified.Add(guid + "|" + key))
            {
                UnityEngine.Debug.LogWarning(
                    OpWeaveGraphJsonCommands.UnsyncedEditsWarning);
            }
        }

        // T-W5.3-08's implementation of the ClearNotifyTestState seam
        // declared in GraphJsonSync.cs: the reopen dedupe and the
        // command-side notification state (the import set, the command
        // set and the quit guard's installed seam) are test state a
        // fixture must not leak across tests.
        static partial void ClearNotifyTestState()
        {
            s_unsyncedNotified.Clear();
            OpWeaveGraphJsonCommands.ClearNotificationState();
        }
    }
}
