#nullable enable

using System;
using OpWeave.Core.Persistence;

namespace OpWeave.GraphToolkit
{
    internal static partial class GraphJsonSync
    {
        // Test-only probe for the intent re-read under the path lock
        // (intent-reread-under-lock): invoked inside RecoverPendingIntent
        // while the target path's exclusion is held, after each re-read
        // and revalidation of the intent record — the window in which an
        // uncoordinated writer can replace the record the clear must
        // never act on from the stale snapshot. The argument is the
        // record's own path. Production callers leave it null; tests
        // reset it explicitly.
        internal static Action<string>? IntentRecordProbe { get; set; }

        // The crash-recovery hook T-W5.3-06 implements (SPEC §H7 item 3),
        // declared in GraphJsonSync.cs: invoked at every save entry
        // before the normal flow, it reconciles the graph's pending
        // intent record — finishing a crash-interrupted publish when it
        // provably can. A produced <paramref name="result"/> returns
        // directly; a null result with <paramref name="handled"/> set
        // means "the record is cleared or gone — run the normal flow";
        // a null result with handled unset means the record was not
        // dealt with and GuardHeldIntent's refusal covers it.
        static partial void RecoverPendingIntent(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string? guid,
            string recordedPath,
            string? siblingPath,
            Func<GraphJsonSyncCheck, GraphJsonSyncChoice>? prompt,
            ref GraphJsonSyncResult? result,
            ref bool handled)
        {
            if (guid is null)
            {
                return;
            }
            result = TryRecoverIntent(
                coordinator, graph, guid, recordedPath, siblingPath, prompt,
                out handled);
        }

        // The pending-intent reconciliation every save entry runs before
        // its normal flow: find the graph's record, prove it names this
        // graph and this path, then under the path's lock — re-read and
        // revalidate it, since a record may have been replaced since the
        // first read — and reconcile: the published output already on
        // disk reconciles to the recorded baseline when the graph's own
        // projection still matches, a graph that moved prompts the §H7.3
        // choice, a record whose publish never reached the disk is
        // cleared and the normal flow runs. Every clear goes through
        // ClearIntentIfUnchanged against the re-read snapshot — a record
        // that changed after the lock was taken is never cleared from
        // the stale snapshot; one retry absorbs a single replacement,
        // then the save refuses. An invalid record is reported once,
        // left untouched and never acted upon. Null means "nothing
        // pending — run the normal flow".
        private static GraphJsonSyncResult? TryRecoverIntent(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string guid,
            string recordedPath,
            string? siblingPath,
            Func<GraphJsonSyncCheck, GraphJsonSyncChoice>? prompt,
            out bool handled)
        {
            handled = false;
            var read = coordinator.ReadIntent(guid, recordedPath, siblingPath);
            if (read.State == IntentReadState.Absent)
            {
                return null;
            }
            if (read.State == IntentReadState.Invalid)
            {
                WarnInvalidIntentOnce(coordinator, guid, read.Warning);
                return null;
            }
            var acquired = coordinator.TryAcquire(read.Intent!.Path);
            if (acquired.Status != AcquireStatus.Acquired)
            {
                // The path's lock is unreachable — the record is held
                // and the interim guard's refusal reports it.
                return null;
            }
            GraphJsonSyncCheck? conflict = null;
            JsonSyncIntent? presented = null;
            using (acquired.Lock)
            {
                // Under the lock the record is re-read and revalidated —
                // one retry when a compare-and-clear finds it changed,
                // then the save refuses (intent-reread-under-lock).
                for (var attempt = 0; attempt < 2 && conflict is null; attempt++)
                {
                    var current = coordinator.ReadIntent(
                        guid, recordedPath, siblingPath);
                    if (current.State == IntentReadState.Absent)
                    {
                        // The record went away — nothing left pending.
                        handled = true;
                        return null;
                    }
                    if (current.State == IntentReadState.Invalid)
                    {
                        WarnInvalidIntentOnce(
                            coordinator, guid, current.Warning);
                        return null;
                    }
                    var intent = current.Intent!;
                    IntentRecordProbe?.Invoke(coordinator.IntentPathFor(guid));
                    var disk = GraphJsonFile.Read(intent.Path);
                    var diskDoc = DiskDocument(disk);
                    if (disk.Exists && diskDoc is not null &&
                        string.Equals(
                            disk.BytesHash, intent.OutputHash,
                            StringComparison.Ordinal))
                    {
                        // The publish reached the disk; the question is
                        // whether the graph's own GUI-owned projection
                        // still matches the output the intent recorded.
                        var projected = ProjectedGuiHash(graph, diskDoc);
                        if (projected is null ||
                            !string.Equals(
                                projected, intent.OutputGuiHash,
                                StringComparison.Ordinal))
                        {
                            // The graph moved since the intent — the
                            // §H7.3 choice decides below the lock:
                            // Cancel keeps the record, Reload adopts the
                            // published JSON and clears it, Overwrite
                            // replaces the pending intent with its own
                            // revision-bound update.
                            conflict = new GraphJsonSyncCheck(
                                GraphJsonSyncState.ExternalModification,
                                diskDoc, disk.Document?.Diagnostics, null,
                                disk.BytesHash);
                            presented = intent;
                            break;
                        }
                        // Reconcile: record the link and baseline — no
                        // rebuild, no prompt, no write.
                        MarkSynced(
                            graph,
                            DisplayPath(graph, recordedPath, siblingPath),
                            diskDoc, disk.BytesHash);
                        // The record clears only once the persisted file
                        // provably carries the reconciled baseline — and
                        // only while it is still the record that was
                        // re-read under this lock; kept, it is exactly
                        // this publish's pending-persist — the next entry
                        // reaches this same branch, still without a
                        // prompt or a write.
                        var persisted = PersistGraph(graph);
                        if (persisted)
                        {
                            var cleared = coordinator.ClearIntentIfUnchanged(
                                guid, current.RecordHash!);
                            if (!cleared.Ok)
                            {
                                return new GraphJsonSyncResult(
                                    new GraphJsonSyncCheck(
                                        GraphJsonSyncState.IoError, null,
                                        null, cleared.Error),
                                    GraphJsonSyncOutcome.Refused, false,
                                    GraphJsonSyncChoice.Cancel, null);
                            }
                            if (!cleared.Cleared)
                            {
                                // The record changed under the lock —
                                // never cleared from the stale snapshot;
                                // retry the re-read once.
                                continue;
                            }
                        }
                        ForgetDecisions(intent.Path);
                        return new GraphJsonSyncResult(
                            new GraphJsonSyncCheck(
                                GraphJsonSyncState.InSync, diskDoc,
                                disk.Document?.Diagnostics, null,
                                disk.BytesHash),
                            persisted
                                ? GraphJsonSyncOutcome.Saved
                                : GraphJsonSyncOutcome.PersistPending,
                            false, GraphJsonSyncChoice.Cancel,
                            GraphJsonSaveStatus.Saved);
                    }
                    else if ((!disk.Exists &&
                            intent.BaseHash == JsonSaveCoordinator.IntentBaseAbsent) ||
                        (disk.Exists && diskDoc is not null &&
                            string.Equals(
                                disk.BytesHash, intent.BaseHash,
                                StringComparison.Ordinal)))
                    {
                        // The disk still holds exactly the revision the
                        // intent was bound to (or the expected absence):
                        // the publish never happened — the record is
                        // stale, the normal flow decides. The clear is
                        // bound to the re-read snapshot only.
                        var cleared = coordinator.ClearIntentIfUnchanged(
                            guid, current.RecordHash!);
                        if (!cleared.Ok)
                        {
                            return new GraphJsonSyncResult(
                                new GraphJsonSyncCheck(
                                    GraphJsonSyncState.IoError, null,
                                    null, cleared.Error),
                                GraphJsonSyncOutcome.Refused, false,
                                GraphJsonSyncChoice.Cancel, null);
                        }
                        if (cleared.Cleared)
                        {
                            handled = true;
                            return null;
                        }
                        continue;
                    }
                    else
                    {
                        // The disk changed past both the intent's base
                        // and its output — an external revision nobody
                        // consented over: the stale record clears and the
                        // normal conflict path applies (SPEC §0.6 SG-17),
                        // still bound to the re-read snapshot only.
                        var cleared = coordinator.ClearIntentIfUnchanged(
                            guid, current.RecordHash!);
                        if (!cleared.Ok)
                        {
                            return new GraphJsonSyncResult(
                                new GraphJsonSyncCheck(
                                    GraphJsonSyncState.IoError, null,
                                    null, cleared.Error),
                                GraphJsonSyncOutcome.Refused, false,
                                GraphJsonSyncChoice.Cancel, null);
                        }
                        if (cleared.Cleared)
                        {
                            handled = true;
                            return null;
                        }
                        continue;
                    }
                }
            }
            if (conflict is null)
            {
                // Every attempt found the record changed — it was never
                // cleared or reconciled from a stale snapshot; the save
                // is refused until the record is resolved.
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.IoError, null, null,
                        "the graph's pending JSON sync intent record at " +
                        coordinator.IntentPathFor(guid) +
                        " changed while it was being reconciled; the save " +
                        "is refused until it is resolved"),
                    GraphJsonSyncOutcome.Refused, false,
                    GraphJsonSyncChoice.Cancel, null);
            }
            if (prompt is null)
            {
                // The graph moved since the interrupted write — an
                // automatic save cannot resolve the recovery conflict;
                // the record stays for a later explicit retry.
                return new GraphJsonSyncResult(
                    conflict, GraphJsonSyncOutcome.Refused, false,
                    GraphJsonSyncChoice.Cancel, null);
            }
            return RunRecoveryChoiceLoop(
                coordinator, graph, guid, presented!, prompt, conflict,
                DisplayPath(graph, recordedPath, siblingPath));
        }

        // The choice loop for the recovery conflict: like RunChoiceLoop,
        // but the base file is the intent's published output — Reload
        // adopts it, Overwrite replaces the pending intent with its own
        // revision-bound update, Cancel leaves the record in place.
        private static GraphJsonSyncResult RunRecoveryChoiceLoop(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string guid,
            JsonSyncIntent intent,
            Func<GraphJsonSyncCheck, GraphJsonSyncChoice> prompt,
            GraphJsonSyncCheck check,
            string displayPath)
        {
            var promptShown = false;
            var current = check;
            for (;;)
            {
                var choice = prompt(current);
                promptShown = true;
                switch (choice)
                {
                    case GraphJsonSyncChoice.ReloadJson:
                        var reloaded = TryReloadBound(
                            coordinator, graph, intent.Path,
                            displayPath, current, guid);
                        if (reloaded is not null)
                        {
                            return new GraphJsonSyncResult(
                                reloaded.Check, reloaded.Outcome, promptShown,
                                reloaded.Choice, reloaded.SaveStatus);
                        }
                        break;

                    case GraphJsonSyncChoice.OverwriteJson:
                        var overwritten = TryOverwriteBound(
                            coordinator, graph, intent.Path,
                            displayPath, current, guid,
                            null, null, promptShown);
                        if (overwritten is not null)
                        {
                            return overwritten;
                        }
                        break;

                    default:
                        // Cancel: the intent record stays — a later
                        // explicit retry asks again.
                        return new GraphJsonSyncResult(
                            current, GraphJsonSyncOutcome.Cancelled, promptShown,
                            choice, null);
                }
                // The bound attempt invalidated: re-evaluate under the
                // path's lock — the same re-check RunChoiceLoop gets
                // through EvaluateForPrompt. A conflict that evaporated
                // — the published output gone or the disk now matching
                // the graph's baseline — has nothing left to decide
                // (SPEC §0.6 SG-17); the record stays for the next
                // entry's recovery.
                var acquired = coordinator.TryAcquire(intent.Path);
                if (acquired.Status != AcquireStatus.Acquired)
                {
                    return new GraphJsonSyncResult(
                        new GraphJsonSyncCheck(
                            GraphJsonSyncState.IoError, null, null,
                            acquired.Status == AcquireStatus.Busy
                                ? "the JSON file is being written by another OpWeave process"
                                : acquired.ErrorMessage),
                        GraphJsonSyncOutcome.Refused, promptShown,
                        GraphJsonSyncChoice.Cancel, null);
                }
                using (acquired.Lock)
                {
                    current = CheckFromDisk(
                        GraphJsonFile.Read(intent.Path), graph);
                }
                if (current.State == GraphJsonSyncState.InSync ||
                    current.State == GraphJsonSyncState.Missing)
                {
                    return new GraphJsonSyncResult(
                        current, GraphJsonSyncOutcome.NoAction, promptShown,
                        GraphJsonSyncChoice.Cancel, null);
                }
            }
        }

        // The warn-once report every invalid record shares: the record's
        // own bytes when they can be read — a different invalid record at
        // the same path is a new problem worth one report — falling back
        // to the fixed warning when the bytes cannot be read.
        private static void WarnInvalidIntentOnce(
            JsonSaveCoordinator coordinator, string guid, string? warning)
        {
            var intentPath = coordinator.IntentPathFor(guid);
            if (s_warnedIntents.Add(InvalidIntentKey(intentPath, warning)))
            {
                UnityEngine.Debug.LogWarning(warning);
            }
        }
    }
}
