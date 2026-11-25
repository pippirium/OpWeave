#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using OpWeave.Core;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using Unity.GraphToolkit.Editor;
using UnityEditor;

namespace OpWeave.GraphToolkit
{
    /// <summary>The relation between a graph's recorded sync state and the
    /// <c>.opweave.json</c> file on disk (SPEC §H7.3).</summary>
    internal enum GraphJsonSyncState
    {
        /// <summary>The graph records no JSON path; there is nothing to compare.</summary>
        Unlinked = 0,

        /// <summary>The file's GUI-owned projection (canonical workflow + layout) equals the stored sync projection.</summary>
        InSync = 1,

        /// <summary>The file parses but its GUI-owned projection differs from the stored sync projection — an out-of-band change.</summary>
        ExternalModification = 2,

        /// <summary>The file the graph was synced to no longer exists — an out-of-band delete.</summary>
        Missing = 3,

        /// <summary>The file does not parse as a workflow document, so no hash can be compared; treated as a divergence.</summary>
        InvalidJson = 4,

        /// <summary>The file could not be read; <see cref="GraphJsonSyncCheck.ErrorMessage"/> carries the exception message.</summary>
        IoError = 5,

        /// <summary>
        /// Not a disk divergence: the graph's own export refused to re-emit
        /// carried content whose references no longer resolve uniquely — only
        /// an explicit human overwrite drops the affected items (SPEC §0.6
        /// SG-17). <see cref="GraphJsonSyncCheck.Diagnostics"/> carries the
        /// export's reasons.
        /// </summary>
        UnemittableGraph = 6,
    }

    /// <summary>The result of comparing the on-disk JSON with the stored sync state.</summary>
    /// <remarks>
    /// <see cref="Document"/> carries the parsed on-disk document whenever it
    /// was read — the payload a "reload from JSON" choice imports and the
    /// conflicting document a save-time refusal names.
    /// <see cref="PresentedRevision"/> is the disk revision the presented
    /// conflict is bound to — lowercase-hex SHA-256 of the file's exact
    /// bytes, or <c>absent</c> when the file is missing: a human's Reload or
    /// Overwrite applies to exactly this revision, and a file that changed
    /// since invalidates the decision and prompts anew (SPEC §0.6 SG-17).
    /// </remarks>
    internal sealed class GraphJsonSyncCheck
    {
        internal GraphJsonSyncCheck(
            GraphJsonSyncState state,
            WorkflowDocument? document,
            IReadOnlyList<Diagnostic>? diagnostics,
            string? errorMessage,
            string? presentedRevision = null)
        {
            State = state;
            Document = document;
            Diagnostics = diagnostics ?? Array.Empty<Diagnostic>();
            ErrorMessage = errorMessage;
            PresentedRevision = presentedRevision;
        }

        internal GraphJsonSyncState State { get; }

        internal WorkflowDocument? Document { get; }

        internal IReadOnlyList<Diagnostic> Diagnostics { get; }

        internal string? ErrorMessage { get; }

        /// <summary>The on-disk revision this check presents — the consent R an explicit choice is bound to.</summary>
        internal string? PresentedRevision { get; }
    }

    /// <summary>The human's answer to the SPEC §H7.3 conflict prompt.</summary>
    internal enum GraphJsonSyncChoice
    {
        /// <summary>Rebuild the graph from the JSON on disk, discarding unsaved GUI changes (SPEC §0.6 SG-17).</summary>
        ReloadJson = 0,

        /// <summary>Write the graph's JSON form over the file — an explicit human overwrite only (SPEC §0.6 SG-17).</summary>
        OverwriteJson = 1,

        /// <summary>Apply nothing: the graph stays open, the file stays untouched and the divergence remains.</summary>
        Cancel = 2,
    }

    /// <summary>What a <see cref="GraphJsonSync"/> open or save operation did.</summary>
    internal enum GraphJsonSyncOutcome
    {
        /// <summary>Nothing diverged (or nothing is linked); no prompt was shown and nothing was applied.</summary>
        NoAction = 0,

        /// <summary>The prompt was shown and the human chose Cancel; nothing was applied.</summary>
        Cancelled = 1,

        /// <summary>"Reload from JSON" was applied: the graph was rebuilt from the on-disk document and the sync baseline moved to it.</summary>
        Reloaded = 2,

        /// <summary>"Overwrite JSON" was applied: the graph's JSON form was written over the file and the sync baseline moved to it.</summary>
        Overwritten = 3,

        /// <summary>A GUI save wrote the JSON without a conflict — or reconciled an already-applied output without writing; the sync baseline moved to it.</summary>
        Saved = 4,

        /// <summary>
        /// The write was refused — a conflict nobody resolved, an unparseable
        /// target, a refused export or an I/O failure. The file is untouched.
        /// </summary>
        Refused = 5,

        /// <summary>"Reload from JSON" was chosen but there is no parseable document to reload (the file is missing or invalid).</summary>
        ReloadFailed = 6,

        /// <summary>
        /// The graph records a JSON path but no usable baseline document
        /// (<see cref="OpWeaveGraph.syncedJson"/> is empty or unparseable),
        /// so the SG-17 overwrite check cannot run — fail closed.
        /// </summary>
        BaselineUnavailable = 7,

        /// <summary>
        /// An applied action moved the graph's sync baseline but cannot
        /// yet prove it durable in the persisted <c>.opweavegraph</c>:
        /// on a publish the write's intent record stands as the
        /// pending-persist state the next entry's recovery reconciles
        /// (SPEC §H7 item 3). Reported instead of <see cref="Saved"/> —
        /// the baseline moved in memory only.
        /// </summary>
        PersistPending = 8,
    }

    /// <summary>The result of a <see cref="GraphJsonSync"/> open or save operation.</summary>
    internal sealed class GraphJsonSyncResult
    {
        internal GraphJsonSyncResult(
            GraphJsonSyncCheck check,
            GraphJsonSyncOutcome outcome,
            bool promptShown,
            GraphJsonSyncChoice choice,
            GraphJsonSaveStatus? saveStatus)
        {
            Check = check;
            Outcome = outcome;
            PromptShown = promptShown;
            Choice = choice;
            SaveStatus = saveStatus;
        }

        internal GraphJsonSyncCheck Check { get; }

        internal GraphJsonSyncOutcome Outcome { get; }

        internal bool PromptShown { get; }

        internal GraphJsonSyncChoice Choice { get; }

        /// <summary>The <see cref="GraphJsonFile"/> status behind the outcome when a write was attempted.</summary>
        internal GraphJsonSaveStatus? SaveStatus { get; }
    }

    /// <summary>
    /// The JSON source-of-truth sync of SPEC §H7 item 3: the
    /// <c>.opweavegraph</c> stores the JSON path and the last synced
    /// revisions (<see cref="OpWeaveGraph.jsonPath"/>,
    /// <see cref="OpWeaveGraph.syncedWorkflowHash"/>,
    /// <see cref="OpWeaveGraph.syncedBytesHash"/>,
    /// <see cref="OpWeaveGraph.syncedGuiOwnedHash"/>,
    /// <see cref="OpWeaveGraph.syncedJson"/>); an explicit save writes the
    /// JSON under the SG-17 rules; on open, a diverging GUI-owned
    /// projection offers the user "reload from JSON" or "overwrite JSON
    /// with graph". Nothing is overwritten without the choice, and every
    /// choice applies to exactly the disk revision it was shown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every OpWeave-controlled write to a <c>.opweave.json</c> funnels
    /// through the <see cref="JsonSaveCoordinator"/>: the path resolves
    /// inside the project (junctions and reparse points included), the
    /// per-path OS exclusion serializes all writers across processes, a
    /// publishing write carries an intent record
    /// (intent → publish → persist → clear), a create is always
    /// create-only, and a replace is bound to the exact disk revision it
    /// consented to — the final byte comparison immediately before the
    /// atomic same-directory replace.
    /// </para>
    /// <para>
    /// A graph that was never synced establishes the link on its first
    /// save: <see cref="Save"/> adopts the sibling <c>.opweave.json</c> of
    /// the <c>.opweavegraph</c> asset as the source of truth — creating it
    /// from the graph's export when absent, linking through the §H7.3
    /// choice when it already holds a document — and
    /// <see cref="MarkSynced"/> records the path and baseline in the
    /// serialized graph state being saved.
    /// </para>
    /// <para>
    /// The prompt is an injected delegate: production passes the modal
    /// dialog of <see cref="GraphJsonSyncDialog.ShowConflictPrompt"/>; tests
    /// pass a scripted answer. A null prompt on the save path means the
    /// write is automatic — a conflict is refused outright (SG-17).
    /// Graph Toolkit's own Save (Ctrl+S,
    /// <see cref="GraphDatabase.SaveGraph"/>) writes only the
    /// <c>.opweavegraph</c> — the JSON is written by the explicit save
    /// paths here, never by a serialization callback.
    /// </para>
    /// </remarks>
    internal static partial class GraphJsonSync
    {
        // Remembered §H7.3 decisions, keyed by
        // "resolved P | presented revision R | output | baseline B": the
        // session memory a prompt-less (automatic) attempt consults —
        // a repeated background write with the same key applies the
        // remembered choice instead of staying refused; a changed file,
        // graph or path produces a different key (SPEC §H7 item 3's
        // revision memory). It never suppresses a dialog: the open
        // drain's duplicate suppression is its own one-reopen scope and
        // every explicit path asks anew.
        // The memory is retired per path by ForgetDecisions whenever an
        // applied action moves that path's sync state: a remembered
        // answer was made for a world that no longer exists, and a later
        // recurrence of the identical key — a fresh session may have
        // deserialized a baseline the pending-persist never carried — is
        // a new request that must prompt anew.
        private static readonly Dictionary<string, GraphJsonSyncChoice> s_remembered =
            new Dictionary<string, GraphJsonSyncChoice>(StringComparer.Ordinal);

        // Intent records already reported this session, keyed by record
        // identity — the record path and its content hash: an invalid or
        // unreadable record is left untouched, reported once and never
        // acted upon — the report must not repeat on every entry, but a
        // DIFFERENT invalid record at the same path is a new problem
        // worth its own report.
        private static readonly HashSet<string> s_warnedIntents =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        // Test-only fault injection for the restart recovery gate
        // (GraphJsonSyncRestartEntry): invoked inside PublishWithIntent
        // after the output bytes are published and before the graph's
        // baseline persist runs — the process dies there, leaving the
        // intent record and the published JSON for the next session's
        // recovery. Production callers leave it null.
        internal static Action<string>? PublishPersistFault { get; set; }

        // Test-only probe for the create-only race points (SPEC §0.6
        // SG-17): invoked after a locked evaluation found the target
        // absent and immediately before the create-only publication runs —
        // the window in which an uncoordinated writer can plant a file the
        // publication must refuse, never replace. Production leaves it
        // null.
        internal static Action<string>? AbsenceProbe { get; set; }

        // Test-only probe for the publish gap: on a create it is forwarded
        // to GraphJsonFile.PublishCreate's beforeMove seam (after the
        // absence check and the temp write, immediately before the publish
        // move); on a bound replace it runs immediately before
        // GraphJsonFile.PublishReplace's own re-read — so the raced write
        // lands before the publish's final byte comparison, which catches
        // it instead of overwriting it. Production leaves it null.
        internal static Action<string>? PublishProbe { get; set; }

        // Test-only fault injection for the baseline persist (SPEC §H7
        // item 3): invoked inside PersistGraph before the write and the
        // proof run — a fault throws and the persist reports unproven
        // with the real .opweavegraph untouched, exactly the
        // pending-persist state a store that never ran leaves. Unlike
        // PublishPersistFault it also covers the paths that persist
        // without a publish (the reconcile, the reload). Production
        // leaves it null.
        internal static Action<OpWeaveGraph>? PersistFault { get; set; }

        /// <summary>
        /// Records <paramref name="jsonPath"/> and <paramref name="document"/>
        /// as the graph's last synced state (SPEC §H7.3): the stored hashes
        /// are the document's <c>CanonicalWorkflow</c> hash, the GUI-owned
        /// projection hash (canonical + layout) and the SHA-256 of the
        /// exact bytes the file held, and the document itself is serialized
        /// into the graph so the SG-17 save baseline — including the
        /// non-GUI-owned members — survives a session.
        /// <paramref name="syncedBytesHash"/> is the lowercase-hex SHA-256
        /// of the exact on-disk bytes the sync was made against; when null
        /// (a caller that syncs to a document rather than to observed
        /// bytes) the hash of the canonical write of the document stands
        /// in — the byte-exact idempotency signal only fires when the real
        /// bytes were recorded.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal static void MarkSynced(
            OpWeaveGraph graph,
            string jsonPath,
            WorkflowDocument document,
            string? syncedBytesHash = null)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (jsonPath is null)
            {
                throw new ArgumentNullException(nameof(jsonPath));
            }
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }
            graph.jsonPath = jsonPath;
            graph.syncedWorkflowHash = document.Canonical.WorkflowHash;
            graph.syncedJson = WorkflowSerializer.Write(document);
            graph.syncedBytesHash = syncedBytesHash ??
                JsonSaveCoordinator.Sha256Hex(s_utf8.GetBytes(graph.syncedJson));
            graph.syncedGuiOwnedHash = GraphJsonFile.GuiOwnedHash(document);
        }

        // The write coordination of every entry below: one coordinator per
        // operation, rooted at the current Unity project — relative
        // .opweave.json paths resolve under it.
        internal static JsonSaveCoordinator Coordinator() =>
            new JsonSaveCoordinator(ProjectRootPath());

        private static string ProjectRootPath() =>
            Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));

        /// <summary>
        /// The explicit GUI JSON save of SPEC §H7.3: writes the graph's
        /// JSON form to the recorded path under the SG-17 rules — an
        /// automatic write is refused while the file's GUI-owned projection
        /// diverged — and moves the sync baseline to the written document.
        /// A null <paramref name="prompt"/> means the write is automatic
        /// (an Agent write or autosave): a conflict is refused without
        /// asking. A non-null prompt is the explicit human surface that may
        /// resolve the conflict. A graph that was never synced records no
        /// path yet: the save adopts the sibling <c>.opweave.json</c> of
        /// its <c>.opweavegraph</c> asset — the §H7.3 initial link of
        /// <see cref="SaveNewLink"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static GraphJsonSyncResult Save(
            OpWeaveGraph graph,
            Func<GraphJsonSyncCheck, GraphJsonSyncChoice>? prompt)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var coordinator = Coordinator();
            var path = graph.jsonPath;
            var guid = AssetGuidOf(graph);
            if (path.Length == 0)
            {
                return SaveNewLink(coordinator, graph, guid, prompt);
            }

            var sibling = SiblingPathOf(graph);
            GraphJsonSyncResult? recovered = null;
            var handled = false;
            RecoverPendingIntent(
                coordinator, graph, guid, path, sibling, prompt,
                ref recovered, ref handled);
            if (recovered is not null)
            {
                return recovered;
            }
            if (!handled)
            {
                var held = GuardHeldIntent(coordinator, guid, path, sibling);
                if (held is not null)
                {
                    return held;
                }
            }

            var baseline = ReadBaseline(graph);
            if (baseline is null)
            {
                // Without the synced document the required re-check of the
                // on-disk hash cannot run — fail closed rather than guess
                // (SPEC §0.6 SG-17).
                return new GraphJsonSyncResult(
                    SyncStateOfOpen(graph), GraphJsonSyncOutcome.BaselineUnavailable,
                    false, GraphJsonSyncChoice.Cancel, null);
            }

            var owned = coordinator.ResolveOwnedPath(path);
            if (owned.Status != PathResolveStatus.Resolved)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.IoError, null, null, owned.ErrorMessage),
                    GraphJsonSyncOutcome.Refused, false, GraphJsonSyncChoice.Cancel, null);
            }
            var resolved = owned.ResolvedPath!;
            var acquired = coordinator.TryAcquire(resolved);
            if (acquired.Status != AcquireStatus.Acquired)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.IoError, null, null,
                        acquired.Status == AcquireStatus.Busy
                            ? "the JSON file is being written by another OpWeave process"
                            : acquired.ErrorMessage),
                    GraphJsonSyncOutcome.Refused, false, GraphJsonSyncChoice.Cancel, null);
            }
            SaveEvaluation? eval;
            using (acquired.Lock)
            {
                eval = EvaluateForSave(graph, resolved, baseline, path);
                if (eval.Action == SaveAction.Merge)
                {
                    return MergeLocked(
                        coordinator, graph, resolved, path, eval, promptShown: false);
                }
            }
            // Below the lock: the bound executors and the prompt loop
            // re-acquire the path's exclusion themselves.
            if (eval.Action == SaveAction.Refused)
            {
                // A terminal refusal — unparseable JSON is never
                // overwritten, even with consent (SPEC §0.6 SG-17).
                return new GraphJsonSyncResult(
                    eval.Check!, GraphJsonSyncOutcome.Refused, false,
                    GraphJsonSyncChoice.Cancel, eval.SaveStatus);
            }
            if (prompt is null)
            {
                // An automatic save has no dialog: a decision remembered
                // for this exact (P, B, output, R) request applies, bound
                // to the presented revision; anything else stays refused.
                var remembered = TryRememberedDecision(
                    coordinator, graph, guid, resolved, path, eval.Check!,
                    DecisionCarrier(
                        eval.Check!, baseline, ChoiceContext.SaveLinked, null),
                    baseline, null);
                return remembered ?? new GraphJsonSyncResult(
                    eval.Check!, GraphJsonSyncOutcome.Refused, false,
                    GraphJsonSyncChoice.Cancel, eval.SaveStatus);
            }
            return RunChoiceLoop(
                coordinator, graph, guid, prompt, eval.Check,
                ChoiceContext.SaveLinked, resolved, path, baseline);
        }

        // What one locked evaluation of the save target says to do.
        private enum SaveAction
        {
            // The file's GUI-owned projection matches the baseline (or the
            // file reconciles byte-exactly): the merged write proceeds.
            Merge = 0,

            // A human-resolvable conflict: the check carries the disk state.
            Conflict = 1,

            // A terminal refusal — unparseable JSON is never overwritten,
            // even with consent (SPEC §0.6 SG-17).
            Refused = 2,
        }

        private sealed class SaveEvaluation
        {
            internal SaveAction Action;
            internal GraphJsonFile.DiskRead? Disk;
            internal PreparedOutput? Output;
            internal GraphJsonSyncCheck? Check;
            internal GraphJsonSaveStatus? SaveStatus;
        }

        // The locked evaluation of the save target (SPEC §0.6 SG-17):
        // invalid JSON is a terminal refusal; a missing file or a diverged
        // GUI-owned projection is a conflict; an equal projection merges —
        // unless the export itself refuses to re-emit carried items, which
        // is the UnemittableGraph conflict only a human overwrite resolves.
        private static SaveEvaluation EvaluateForSave(
            OpWeaveGraph graph,
            string resolvedPath,
            WorkflowDocument baseline,
            string displayPath)
        {
            var disk = GraphJsonFile.Read(resolvedPath);
            if (disk.ErrorMessage is not null)
            {
                return new SaveEvaluation
                {
                    Action = SaveAction.Refused,
                    Check = new GraphJsonSyncCheck(
                        GraphJsonSyncState.IoError, null, null, disk.ErrorMessage),
                };
            }
            if (!disk.Exists)
            {
                return new SaveEvaluation
                {
                    Action = SaveAction.Conflict,
                    Disk = disk,
                    Check = new GraphJsonSyncCheck(
                        GraphJsonSyncState.Missing, null, null, null,
                        JsonSaveCoordinator.IntentBaseAbsent),
                };
            }
            var diskDoc = DiskDocument(disk);
            if (diskDoc is null)
            {
                return new SaveEvaluation
                {
                    Action = SaveAction.Refused,
                    Disk = disk,
                    Check = new GraphJsonSyncCheck(
                        GraphJsonSyncState.InvalidJson, null,
                        disk.Document?.Diagnostics, null, disk.BytesHash),
                    SaveStatus = GraphJsonSaveStatus.InvalidJson,
                };
            }
            var guiMatches = string.Equals(
                GraphJsonFile.GuiOwnedHash(diskDoc),
                BaselineGuiOwnedHash(graph) ?? "",
                StringComparison.Ordinal);
            if (!guiMatches)
            {
                return new SaveEvaluation
                {
                    Action = SaveAction.Conflict,
                    Disk = disk,
                    Check = new GraphJsonSyncCheck(
                        GraphJsonSyncState.ExternalModification,
                        diskDoc, disk.Document?.Diagnostics, null, disk.BytesHash),
                };
            }
            var built = BuildPrepared(graph, diskDoc, disk.Text);
            if (!built.Ok || built.Diagnostics.Count != 0)
            {
                return new SaveEvaluation
                {
                    Action = SaveAction.Conflict,
                    Disk = disk,
                    Check = new GraphJsonSyncCheck(
                        GraphJsonSyncState.UnemittableGraph, diskDoc,
                        built.Diagnostics, null, disk.BytesHash),
                };
            }
            return new SaveEvaluation
            {
                Action = SaveAction.Merge,
                Disk = disk,
                Output = built.Value,
            };
        }

        // The merged write of an in-projection file (SPEC §0.6 SG-17): an
        // already-applied output reconciles without a write or mtime
        // change; otherwise the merge publishes through the intent
        // protocol bound to the disk's current revision — a publish the
        // final byte comparison finds raced is a new conflict, never an
        // overwrite.
        private static GraphJsonSyncResult MergeLocked(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string resolvedPath,
            string displayPath,
            SaveEvaluation eval,
            bool promptShown)
        {
            var disk = eval.Disk!;
            var output = eval.Output!;
            if (string.Equals(
                disk.BytesHash,
                JsonSaveCoordinator.Sha256Hex(output.Output.Bytes),
                StringComparison.Ordinal))
            {
                MarkSynced(graph, displayPath, DiskDocument(disk)!, disk.BytesHash);
                var persisted = PersistGraph(graph);
                ForgetDecisions(resolvedPath);
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.InSync, DiskDocument(disk),
                        disk.Document?.Diagnostics, null, disk.BytesHash),
                    persisted
                        ? GraphJsonSyncOutcome.Saved
                        : GraphJsonSyncOutcome.PersistPending,
                    promptShown, GraphJsonSyncChoice.Cancel,
                    GraphJsonSaveStatus.Saved);
            }
            var published = PublishWithIntent(
                coordinator, graph, resolvedPath,
                JsonSaveCoordinator.IntentKindUpdate, disk.BytesHash!, output,
                () =>
                {
                    MarkSynced(graph, displayPath, output.Document,
                        JsonSaveCoordinator.Sha256Hex(output.Output.Bytes));
                    return PersistGraph(graph);
                });
            if (published.Status == IntentPublishStatus.Published)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.InSync, output.Document, null, null,
                        JsonSaveCoordinator.Sha256Hex(output.Output.Bytes)),
                    GraphJsonSyncOutcome.Saved, promptShown, GraphJsonSyncChoice.Cancel,
                    GraphJsonSaveStatus.Saved);
            }
            if (published.Status == IntentPublishStatus.PersistPending)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.InSync, output.Document, null, null,
                        JsonSaveCoordinator.Sha256Hex(output.Output.Bytes)),
                    GraphJsonSyncOutcome.PersistPending, promptShown,
                    GraphJsonSyncChoice.Cancel, GraphJsonSaveStatus.Saved);
            }
            if (published.Status == IntentPublishStatus.Changed)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.ExternalModification,
                        published.Current is { } current ? DiskDocument(current) : null,
                        published.Current?.Document?.Diagnostics, null,
                        published.Current?.BytesHash),
                    GraphJsonSyncOutcome.Refused, promptShown, GraphJsonSyncChoice.Cancel,
                    GraphJsonSaveStatus.ExternalModification);
            }
            return new GraphJsonSyncResult(
                new GraphJsonSyncCheck(
                    GraphJsonSyncState.IoError, null, null, published.ErrorMessage),
                GraphJsonSyncOutcome.Refused, promptShown, GraphJsonSyncChoice.Cancel,
                GraphJsonSaveStatus.IoError);
        }

        // The SPEC §H7.3 initial link: a graph that was never synced has no
        // recorded JSON path, so the save adopts the sibling
        // .opweave.json of its .opweavegraph asset as the source of truth.
        // A graph not backed by an asset yet (a transient in-memory copy)
        // has no path to adopt and stays unlinked. A file the adopted path
        // already holds is the JSON source of truth — the graph links to it
        // only through the §H7.3 choice (reload rebuilds the graph from it,
        // an explicit overwrite writes the graph over it — a revision-bound
        // update, never a create); an automatic save refuses because
        // neither rebasing the graph nor overwriting the file may happen
        // silently (SPEC §0.6 SG-17).
        private static GraphJsonSyncResult SaveNewLink(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string? guid,
            Func<GraphJsonSyncCheck, GraphJsonSyncChoice>? prompt)
        {
            var assetPath = AssetPathOf(graph);
            if (assetPath is null)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(GraphJsonSyncState.Unlinked, null, null, null),
                    GraphJsonSyncOutcome.NoAction, false, GraphJsonSyncChoice.Cancel, null);
            }
            var path = Path.ChangeExtension(assetPath, "opweave.json")!;

            GraphJsonSyncResult? recovered = null;
            var handled = false;
            RecoverPendingIntent(
                coordinator, graph, guid, "", path, prompt,
                ref recovered, ref handled);
            if (recovered is not null)
            {
                return recovered;
            }
            if (!handled)
            {
                var held = GuardHeldIntent(coordinator, guid, "", path);
                if (held is not null)
                {
                    return held;
                }
            }

            var owned = coordinator.ResolveOwnedPath(path);
            if (owned.Status != PathResolveStatus.Resolved)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.IoError, null, null, owned.ErrorMessage),
                    GraphJsonSyncOutcome.Refused, false, GraphJsonSyncChoice.Cancel, null);
            }
            var resolved = owned.ResolvedPath!;
            var acquired = coordinator.TryAcquire(resolved);
            if (acquired.Status != AcquireStatus.Acquired)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.IoError, null, null,
                        acquired.Status == AcquireStatus.Busy
                            ? "the JSON file is being written by another OpWeave process"
                            : acquired.ErrorMessage),
                    GraphJsonSyncOutcome.Refused, false, GraphJsonSyncChoice.Cancel, null);
            }
            GraphJsonSyncCheck? conflict = null;
            using (acquired.Lock)
            {
                var disk = GraphJsonFile.Read(resolved);
                if (disk.ErrorMessage is not null)
                {
                    return new GraphJsonSyncResult(
                        new GraphJsonSyncCheck(
                            GraphJsonSyncState.IoError, null, null, disk.ErrorMessage),
                        GraphJsonSyncOutcome.Refused, false, GraphJsonSyncChoice.Cancel, null);
                }
                var diskDoc = DiskDocument(disk);
                if (disk.Exists && diskDoc is null)
                {
                    // An unparseable file is never linked or overwritten
                    // — even by an explicit human choice (SPEC §0.6
                    // SG-17). No prompt is shown: there is no valid
                    // document to reload and nothing may be destroyed.
                    return new GraphJsonSyncResult(
                        new GraphJsonSyncCheck(
                            GraphJsonSyncState.InvalidJson, null,
                            disk.Document?.Diagnostics, null, disk.BytesHash),
                        GraphJsonSyncOutcome.Refused, false, GraphJsonSyncChoice.Cancel,
                        GraphJsonSaveStatus.InvalidJson);
                }
                if (disk.Exists)
                {
                    // The existing-file conflict: decided below the lock.
                    conflict = new GraphJsonSyncCheck(
                        GraphJsonSyncState.ExternalModification,
                        diskDoc, disk.Document?.Diagnostics, null, disk.BytesHash);
                }
                else
                {
                    // Nothing at the adopted path: the create-only first
                    // write — absent stays create-only through the
                    // prompt, the absence check and the publication
                    // (SPEC §0.6 SG-17).
                    var seed = SeedDocument(assetPath);
                    if (!seed.Ok)
                    {
                        return new GraphJsonSyncResult(
                            new GraphJsonSyncCheck(
                                GraphJsonSyncState.UnemittableGraph, null,
                                seed.Diagnostics, null,
                                JsonSaveCoordinator.IntentBaseAbsent),
                            GraphJsonSyncOutcome.Refused, false,
                            GraphJsonSyncChoice.Cancel,
                            GraphJsonSaveStatus.InvalidGraph);
                    }
                    var built = BuildPrepared(graph, seed.Value!, null);
                    if (!built.Ok || built.Diagnostics.Count != 0)
                    {
                        conflict = new GraphJsonSyncCheck(
                            GraphJsonSyncState.UnemittableGraph, null,
                            built.Diagnostics, null,
                            JsonSaveCoordinator.IntentBaseAbsent);
                    }
                    else
                    {
                        var created = CreateLocked(
                            coordinator, graph, resolved, path, built.Value!,
                            promptShown: false);
                        if (created is not null)
                        {
                            return created;
                        }
                        // A file appeared at publication — the §H7.3
                        // existing-file conflict decided below the lock.
                        var current = GraphJsonFile.Read(resolved);
                        conflict = new GraphJsonSyncCheck(
                            GraphJsonSyncState.ExternalModification,
                            DiskDocument(current), current.Document?.Diagnostics,
                            null, current.BytesHash);
                    }
                }
            }
            if (prompt is null)
            {
                // An automatic save has no dialog: a decision remembered
                // for this exact (P, B, output, R) request applies, bound
                // to the presented revision; anything else stays refused.
                var remembered = TryRememberedDecision(
                    coordinator, graph, guid, resolved, path, conflict!,
                    DecisionCarrier(
                        conflict!, null, ChoiceContext.SaveFirstLink, assetPath),
                    null, assetPath);
                if (remembered is not null)
                {
                    return remembered;
                }
                return new GraphJsonSyncResult(
                    conflict!, GraphJsonSyncOutcome.Refused, false,
                    GraphJsonSyncChoice.Cancel,
                    conflict.State == GraphJsonSyncState.UnemittableGraph
                        ? GraphJsonSaveStatus.InvalidGraph
                        : (GraphJsonSaveStatus?)null);
            }
            return RunChoiceLoop(
                coordinator, graph, guid, prompt, conflict,
                ChoiceContext.SaveFirstLink, resolved, path, null, assetPath);
        }

        // The create-only first write inside the lock: absent → intent →
        // create-only publish → persist → clear. Null when a file appeared
        // at publication — the §H7.3 existing-file conflict the caller
        // surfaces (SPEC §0.6 SG-17).
        private static GraphJsonSyncResult? CreateLocked(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string resolvedPath,
            string displayPath,
            PreparedOutput output,
            bool promptShown)
        {
            AbsenceProbe?.Invoke(resolvedPath);
            var published = PublishWithIntent(
                coordinator, graph, resolvedPath,
                JsonSaveCoordinator.IntentKindCreate,
                JsonSaveCoordinator.IntentBaseAbsent, output,
                () =>
                {
                    MarkSynced(graph, displayPath, output.Document,
                        JsonSaveCoordinator.Sha256Hex(output.Output.Bytes));
                    return PersistGraph(graph);
                });
            if (published.Status == IntentPublishStatus.Published)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(GraphJsonSyncState.Unlinked, null, null, null),
                    GraphJsonSyncOutcome.Saved, promptShown, GraphJsonSyncChoice.Cancel,
                    GraphJsonSaveStatus.Saved);
            }
            if (published.Status == IntentPublishStatus.PersistPending)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(GraphJsonSyncState.Unlinked, null, null, null),
                    GraphJsonSyncOutcome.PersistPending, promptShown,
                    GraphJsonSyncChoice.Cancel, GraphJsonSaveStatus.Saved);
            }
            if (published.Status == IntentPublishStatus.Changed)
            {
                return null;
            }
            return new GraphJsonSyncResult(
                new GraphJsonSyncCheck(
                    GraphJsonSyncState.IoError, null, null, published.ErrorMessage),
                GraphJsonSyncOutcome.Refused, promptShown, GraphJsonSyncChoice.Cancel,
                GraphJsonSaveStatus.IoError);
        }

        // ── The revision-bound choice loop (SPEC §0.6 SG-17) ────────────

        private enum ChoiceContext
        {
            Open = 0,
            SaveLinked = 1,
            SaveFirstLink = 2,
        }

        // The loop every explicit path shares: each iteration presents a
        // fresh check carrying the current disk revision R; the human's
        // answer applies only while the file still hashes to R — a changed
        // or missing file invalidates the decision and leads to a new
        // prompt for the new revision, forever fail-closed. Every prompt
        // answer is remembered under its exact
        // (P, B, output projection, R) key — in the session map an
        // automatic attempt reuses, and, when the caller supplies
        // <paramref name="rememberedPrompts"/>, in the open drain's
        // one-reopen scope: a repeated request with the same key inside
        // that scope applies the remembered choice instead of showing a
        // duplicate dialog (SPEC §H7 item 3's "at most one dialog per
        // reopen" coalescing). Explicit paths pass null: a fresh human
        // request always prompts again.
        private static GraphJsonSyncResult RunChoiceLoop(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string? guid,
            Func<GraphJsonSyncCheck, GraphJsonSyncChoice> prompt,
            GraphJsonSyncCheck? firstCheck,
            ChoiceContext context,
            string? resolvedPath = null,
            string? displayPath = null,
            WorkflowDocument? baseline = null,
            string? assetPath = null,
            Dictionary<string, GraphJsonSyncChoice>? rememberedPrompts = null)
        {
            var check = firstCheck;
            var promptShown = false;
            for (;;)
            {
                if (check is null)
                {
                    var evaluated = EvaluateForPrompt(
                        coordinator, graph, resolvedPath!, displayPath!,
                        baseline, context, assetPath);
                    if (evaluated.Result is not null)
                    {
                        evaluated.Result = new GraphJsonSyncResult(
                            evaluated.Result.Check,
                            evaluated.Result.Outcome,
                            promptShown,
                            evaluated.Result.Choice,
                            evaluated.Result.SaveStatus);
                        return evaluated.Result;
                    }
                    check = evaluated.Check!;
                }
                var choice = GraphJsonSyncChoice.Cancel;
                var key = DecisionKey(
                    graph, resolvedPath!, check,
                    DecisionCarrier(check, baseline, context, assetPath));
                var reused = rememberedPrompts is not null &&
                    rememberedPrompts.TryGetValue(key, out choice);
                if (!reused)
                {
                    choice = prompt(check);
                    promptShown = true;
                    if (rememberedPrompts is not null)
                    {
                        rememberedPrompts[key] = choice;
                    }
                    lock (s_remembered)
                    {
                        // Bounded: a decision whose key no longer recurs
                        // is dead weight — a full map sheds it all rather
                        // than growing without limit.
                        if (s_remembered.Count >= 512)
                        {
                            s_remembered.Clear();
                        }
                        s_remembered[key] = choice;
                    }
                }
                switch (choice)
                {
                    case GraphJsonSyncChoice.ReloadJson:
                        var reloaded = TryReloadBound(
                            coordinator, graph, resolvedPath!, displayPath!,
                            check, guid);
                        if (reloaded is not null)
                        {
                            reloaded = new GraphJsonSyncResult(
                                reloaded.Check, reloaded.Outcome, promptShown,
                                reloaded.Choice, reloaded.SaveStatus);
                            return reloaded;
                        }
                        break;

                    case GraphJsonSyncChoice.OverwriteJson:
                        var overwritten = TryOverwriteBound(
                            coordinator, graph, resolvedPath!, displayPath!,
                            check, guid, baseline, assetPath, promptShown);
                        if (overwritten is not null)
                        {
                            return overwritten;
                        }
                        break;

                    default:
                        return new GraphJsonSyncResult(
                            check, GraphJsonSyncOutcome.Cancelled, promptShown,
                            choice, check.State == GraphJsonSyncState.UnemittableGraph
                                ? GraphJsonSaveStatus.InvalidGraph
                                : (GraphJsonSaveStatus?)null);
                }
                check = null;
            }
        }

        private sealed class PromptEvaluation
        {
            internal GraphJsonSyncCheck? Check;
            internal GraphJsonSyncResult? Result;
        }

        // The fresh check each loop iteration presents: the disk state
        // under the path's lock, in the calling context's semantics —
        // open: disk states only; linked save: the same conflict/merge
        // evaluation the save used (a mergeable state means the conflict
        // evaporated and the merged write simply proceeds); first link:
        // missing means create, existing means the §H7.3 conflict.
        private static PromptEvaluation EvaluateForPrompt(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string resolvedPath,
            string displayPath,
            WorkflowDocument? baseline,
            ChoiceContext context,
            string? assetPath)
        {
            var acquired = coordinator.TryAcquire(resolvedPath);
            if (acquired.Status != AcquireStatus.Acquired)
            {
                return new PromptEvaluation
                {
                    Result = new GraphJsonSyncResult(
                        new GraphJsonSyncCheck(
                            GraphJsonSyncState.IoError, null, null,
                            acquired.Status == AcquireStatus.Busy
                                ? "the JSON file is being written by another OpWeave process"
                                : acquired.ErrorMessage),
                        GraphJsonSyncOutcome.Refused, false,
                        GraphJsonSyncChoice.Cancel, null),
                };
            }
            using (acquired.Lock)
            {
                switch (context)
                {
                    case ChoiceContext.Open:
                    {
                        var disk = GraphJsonFile.Read(resolvedPath);
                        var check = CheckFromDisk(disk, graph);
                        if (check.State == GraphJsonSyncState.Unlinked ||
                            check.State == GraphJsonSyncState.InSync)
                        {
                            // The presented conflict evaporated — there is
                            // nothing left to decide (SPEC §0.6 SG-17).
                            return new PromptEvaluation
                            {
                                Result = new GraphJsonSyncResult(
                                    check, GraphJsonSyncOutcome.NoAction, false,
                                    GraphJsonSyncChoice.Cancel, null),
                            };
                        }
                        return new PromptEvaluation { Check = check };
                    }
                    case ChoiceContext.SaveLinked:
                    {
                        var eval = EvaluateForSave(graph, resolvedPath, baseline!, displayPath);
                        if (eval.Action == SaveAction.Merge)
                        {
                            return new PromptEvaluation
                            {
                                Result = MergeLocked(
                                    coordinator, graph, resolvedPath, displayPath,
                                    eval, promptShown: false),
                            };
                        }
                        if (eval.Action == SaveAction.Refused)
                        {
                            return new PromptEvaluation
                            {
                                Result = new GraphJsonSyncResult(
                                    eval.Check!, GraphJsonSyncOutcome.Refused, false,
                                    GraphJsonSyncChoice.Cancel, eval.SaveStatus),
                            };
                        }
                        return new PromptEvaluation { Check = eval.Check };
                    }
                    default:
                    {
                        var disk = GraphJsonFile.Read(resolvedPath);
                        if (disk.ErrorMessage is not null)
                        {
                            return new PromptEvaluation
                            {
                                Result = new GraphJsonSyncResult(
                                    new GraphJsonSyncCheck(
                                        GraphJsonSyncState.IoError, null, null,
                                        disk.ErrorMessage),
                                    GraphJsonSyncOutcome.Refused, false,
                                    GraphJsonSyncChoice.Cancel, null),
                            };
                        }
                        var diskDoc = DiskDocument(disk);
                        if (disk.Exists && diskDoc is null)
                        {
                            return new PromptEvaluation
                            {
                                Result = new GraphJsonSyncResult(
                                    new GraphJsonSyncCheck(
                                        GraphJsonSyncState.InvalidJson, null,
                                        disk.Document?.Diagnostics, null,
                                        disk.BytesHash),
                                    GraphJsonSyncOutcome.Refused, false,
                                    GraphJsonSyncChoice.Cancel,
                                    GraphJsonSaveStatus.InvalidJson),
                            };
                        }
                        if (disk.Exists)
                        {
                            return new PromptEvaluation
                            {
                                Check = new GraphJsonSyncCheck(
                                    GraphJsonSyncState.ExternalModification,
                                    diskDoc, disk.Document?.Diagnostics, null,
                                    disk.BytesHash),
                            };
                        }
                        // Absent: the first-link create — the export's own
                        // refusal is a conflict; otherwise the create
                        // proceeds inside this same lock.
                        var seed = SeedDocument(assetPath!);
                        if (!seed.Ok)
                        {
                            return new PromptEvaluation
                            {
                                Result = new GraphJsonSyncResult(
                                    new GraphJsonSyncCheck(
                                        GraphJsonSyncState.UnemittableGraph, null,
                                        seed.Diagnostics, null,
                                        JsonSaveCoordinator.IntentBaseAbsent),
                                    GraphJsonSyncOutcome.Refused, false,
                                    GraphJsonSyncChoice.Cancel,
                                    GraphJsonSaveStatus.InvalidGraph),
                            };
                        }
                        var built = BuildPrepared(graph, seed.Value!, null);
                        if (!built.Ok || built.Diagnostics.Count != 0)
                        {
                            return new PromptEvaluation
                            {
                                Check = new GraphJsonSyncCheck(
                                    GraphJsonSyncState.UnemittableGraph, null,
                                    built.Diagnostics, null,
                                    JsonSaveCoordinator.IntentBaseAbsent),
                            };
                        }
                        var created = CreateLocked(
                            coordinator, graph, resolvedPath, displayPath,
                            built.Value!, promptShown: false);
                        if (created is not null)
                        {
                            return new PromptEvaluation { Result = created };
                        }
                        var current = GraphJsonFile.Read(resolvedPath);
                        return new PromptEvaluation
                        {
                            Check = new GraphJsonSyncCheck(
                                GraphJsonSyncState.ExternalModification,
                                DiskDocument(current), current.Document?.Diagnostics,
                                null, current.BytesHash),
                        };
                    }
                }
            }
        }

        private static GraphJsonSyncCheck CheckFromDisk(
            GraphJsonFile.DiskRead disk, OpWeaveGraph graph)
        {
            if (disk.ErrorMessage is not null)
            {
                return new GraphJsonSyncCheck(
                    GraphJsonSyncState.IoError, null, null, disk.ErrorMessage);
            }
            if (!disk.Exists)
            {
                return new GraphJsonSyncCheck(
                    GraphJsonSyncState.Missing, null, null, null,
                    JsonSaveCoordinator.IntentBaseAbsent);
            }
            var diskDoc = DiskDocument(disk);
            if (diskDoc is null)
            {
                return new GraphJsonSyncCheck(
                    GraphJsonSyncState.InvalidJson, null,
                    disk.Document?.Diagnostics, null, disk.BytesHash);
            }
            var guiHash = BaselineGuiOwnedHash(graph);
            var inSync = guiHash is not null && string.Equals(
                GraphJsonFile.GuiOwnedHash(diskDoc), guiHash,
                StringComparison.Ordinal);
            return new GraphJsonSyncCheck(
                inSync ? GraphJsonSyncState.InSync : GraphJsonSyncState.ExternalModification,
                diskDoc, disk.Document?.Diagnostics, null, disk.BytesHash);
        }

        // "Reload from JSON" bound to the presented revision R (SPEC §0.6
        // SG-17): under the lock the file must still hash to R — a changed
        // or missing file invalidates the choice and the loop presents the
        // new revision. A reload that applies rebuilds the graph from the
        // on-disk document, moves the baseline to it and persists.
        private static GraphJsonSyncResult? TryReloadBound(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string resolvedPath,
            string displayPath,
            GraphJsonSyncCheck check,
            string? guid)
        {
            if (check.Document is null)
            {
                return new GraphJsonSyncResult(
                    check, GraphJsonSyncOutcome.ReloadFailed, true,
                    GraphJsonSyncChoice.ReloadJson, null);
            }
            var acquired = coordinator.TryAcquire(resolvedPath);
            if (acquired.Status != AcquireStatus.Acquired)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.IoError, null, null,
                        acquired.Status == AcquireStatus.Busy
                            ? "the JSON file is being written by another OpWeave process"
                            : acquired.ErrorMessage),
                    GraphJsonSyncOutcome.Refused, true,
                    GraphJsonSyncChoice.ReloadJson, null);
            }
            using (acquired.Lock)
            {
                var disk = GraphJsonFile.Read(resolvedPath);
                var diskDoc = DiskDocument(disk);
                if (!disk.Exists || diskDoc is null ||
                    !string.Equals(
                        disk.BytesHash, check.PresentedRevision, StringComparison.Ordinal))
                {
                    // A changed, missing or newly-invalid file invalidates
                    // the presented decision — a new prompt shows the new
                    // revision (SPEC §0.6 SG-17).
                    return null;
                }
                GraphJsonConverter.Import(graph, diskDoc);
                MarkSynced(graph, displayPath, diskDoc, disk.BytesHash);
                // The record clears only once the persisted file
                // provably carries the reloaded baseline; kept, it is
                // exactly the pending-persist of the revision just
                // reloaded — the next entry's recovery reconciles it
                // (SPEC §H7 item 3). The outcome follows the same
                // proof: an unproven persist is pending, never done.
                var persisted = PersistGraph(graph);
                if (persisted && guid is not null &&
                    IntentSlotActionable(coordinator, graph, guid))
                {
                    coordinator.ClearIntent(guid);
                }
                ForgetDecisions(resolvedPath);
                return new GraphJsonSyncResult(
                    check,
                    persisted
                        ? GraphJsonSyncOutcome.Reloaded
                        : GraphJsonSyncOutcome.PersistPending,
                    true, GraphJsonSyncChoice.ReloadJson, null);
            }
        }

        // "Overwrite JSON" bound to the presented revision R (SPEC §0.6
        // SG-17): under the lock the file must still hash to R — a changed
        // file invalidates the choice and the loop presents the new
        // revision; unparseable JSON is a terminal refusal even with
        // consent; a still-matching file — or a still-absent one — is
        // published through the intent protocol as a revision-bound
        // update (or a create-only write), never a blind overwrite.
        private static GraphJsonSyncResult? TryOverwriteBound(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string resolvedPath,
            string displayPath,
            GraphJsonSyncCheck check,
            string? guid,
            WorkflowDocument? baseline,
            string? assetPath,
            bool promptShown)
        {
            var acquired = coordinator.TryAcquire(resolvedPath);
            if (acquired.Status != AcquireStatus.Acquired)
            {
                return new GraphJsonSyncResult(
                    new GraphJsonSyncCheck(
                        GraphJsonSyncState.IoError, null, null,
                        acquired.Status == AcquireStatus.Busy
                            ? "the JSON file is being written by another OpWeave process"
                            : acquired.ErrorMessage),
                    GraphJsonSyncOutcome.Refused, promptShown,
                    GraphJsonSyncChoice.OverwriteJson, null);
            }
            using (acquired.Lock)
            {
                var disk = GraphJsonFile.Read(resolvedPath);
                var diskDoc = DiskDocument(disk);
                if (disk.Exists && diskDoc is null)
                {
                    // Unparseable content is never destroyed — even with
                    // consent (SPEC §0.6 SG-17).
                    return new GraphJsonSyncResult(
                        new GraphJsonSyncCheck(
                            GraphJsonSyncState.InvalidJson, null,
                            disk.Document?.Diagnostics, null, disk.BytesHash),
                        GraphJsonSyncOutcome.Refused, promptShown,
                        GraphJsonSyncChoice.OverwriteJson,
                        GraphJsonSaveStatus.InvalidJson);
                }
                var revision = check.PresentedRevision;
                var currentRevision = disk.Exists
                    ? disk.BytesHash
                    : JsonSaveCoordinator.IntentBaseAbsent;
                if (!string.Equals(
                    currentRevision, revision, StringComparison.Ordinal))
                {
                    // The file changed between prompt and write — the
                    // consent was bound to the old revision; a new prompt
                    // presents the new one.
                    return null;
                }
                WorkflowDocument? nonOwnedSource;
                if (disk.Exists)
                {
                    nonOwnedSource = diskDoc!;
                }
                else
                {
                    nonOwnedSource = baseline ??
                        (assetPath is not null ? SeedDocument(assetPath).Value : null);
                    if (nonOwnedSource is null)
                    {
                        return new GraphJsonSyncResult(
                            check, GraphJsonSyncOutcome.BaselineUnavailable,
                            promptShown, GraphJsonSyncChoice.OverwriteJson, null);
                    }
                }
                var built = BuildPrepared(graph, nonOwnedSource, disk.Text);
                if (!built.Ok)
                {
                    return new GraphJsonSyncResult(
                        new GraphJsonSyncCheck(
                            GraphJsonSyncState.UnemittableGraph, diskDoc,
                            built.Diagnostics, null, disk.BytesHash),
                        GraphJsonSyncOutcome.Refused, promptShown,
                        GraphJsonSyncChoice.OverwriteJson,
                        GraphJsonSaveStatus.InvalidGraph);
                }
                var output = built.Value!;
                var kind = disk.Exists
                    ? JsonSaveCoordinator.IntentKindUpdate
                    : JsonSaveCoordinator.IntentKindCreate;
                var baseHash = disk.Exists
                    ? disk.BytesHash!
                    : JsonSaveCoordinator.IntentBaseAbsent;
                var published = PublishWithIntent(
                    coordinator, graph, resolvedPath, kind, baseHash, output,
                    () =>
                    {
                        MarkSynced(graph, displayPath, output.Document,
                            JsonSaveCoordinator.Sha256Hex(output.Output.Bytes));
                        return PersistGraph(graph);
                    });
                switch (published.Status)
                {
                    case IntentPublishStatus.Published:
                        return new GraphJsonSyncResult(
                            new GraphJsonSyncCheck(
                                GraphJsonSyncState.InSync, output.Document, null, null,
                                JsonSaveCoordinator.Sha256Hex(output.Output.Bytes)),
                            GraphJsonSyncOutcome.Overwritten, promptShown,
                            GraphJsonSyncChoice.OverwriteJson,
                            GraphJsonSaveStatus.Saved);
                    case IntentPublishStatus.PersistPending:
                        return new GraphJsonSyncResult(
                            new GraphJsonSyncCheck(
                                GraphJsonSyncState.InSync, output.Document, null, null,
                                JsonSaveCoordinator.Sha256Hex(output.Output.Bytes)),
                            GraphJsonSyncOutcome.PersistPending, promptShown,
                            GraphJsonSyncChoice.OverwriteJson,
                            GraphJsonSaveStatus.Saved);
                    case IntentPublishStatus.Changed:
                        // The file changed between the bound check and the
                        // publication's final byte comparison — invalidate
                        // and present the new revision (SPEC §0.6 SG-17).
                        return null;
                    default:
                        return new GraphJsonSyncResult(
                            new GraphJsonSyncCheck(
                                GraphJsonSyncState.IoError, null, null,
                                published.ErrorMessage),
                            GraphJsonSyncOutcome.Refused, promptShown,
                            GraphJsonSyncChoice.OverwriteJson,
                            GraphJsonSaveStatus.IoError);
                }
            }
        }

        // ── The intent protocol (SPEC §H7 item 3) ────────────────────────

        /// <summary>What a <see cref="PublishWithIntent"/> produced.</summary>
        internal enum IntentPublishStatus
        {
            /// <summary>Intent, publish, persist and clear all ran — the write is durable.</summary>
            Published = 0,

            /// <summary>The bound disk revision changed before publication — the intent was cleared and nothing was written.</summary>
            Changed = 1,

            /// <summary>The intent or the write itself failed; <see cref="IntentPublishResult.ErrorMessage"/> carries the reason.</summary>
            IoError = 2,

            /// <summary>
            /// The output was published but the persist step could not
            /// prove the new baseline durable in the .opweavegraph: the
            /// intent record stands as the pending-persist state the next
            /// entry's recovery finishes (SPEC §H7 item 3).
            /// </summary>
            PersistPending = 3,
        }

        /// <summary>The outcome of <see cref="PublishWithIntent"/>.</summary>
        internal sealed class IntentPublishResult
        {
            internal IntentPublishResult(
                IntentPublishStatus status,
                GraphJsonFile.DiskRead? current,
                string? errorMessage)
            {
                Status = status;
                Current = current;
                ErrorMessage = errorMessage;
            }

            internal IntentPublishStatus Status { get; }

            /// <summary>The fresh disk read behind a <see cref="IntentPublishStatus.Changed"/>.</summary>
            internal GraphJsonFile.DiskRead? Current { get; }

            internal string? ErrorMessage { get; }
        }

        /// <summary>
        /// One publishing write's full protocol (SPEC §H7 item 3): write the
        /// intent record, publish the output, persist the graph's link and
        /// baseline, then clear the record. The caller holds
        /// <paramref name="resolvedPath"/>'s coordinator exclusion for the
        /// whole span — the record is written and cleared inside it.
        /// <paramref name="persist"/> moves the baseline and writes the
        /// graph asset; when it reports failure the intent is kept so the
        /// next run's recovery finishes the persist, and the publish
        /// reports <see cref="IntentPublishStatus.PersistPending"/> — the
        /// outcome is pending, never durable-and-done. A record the graph's
        /// intent slot already holds that fails validation is never acted
        /// on — whatever the failing check: the slot stays sealed, the
        /// record's bytes stay exactly as found, and this write is
        /// refused rather than publishing untracked — a write the next
        /// entry could never reconcile through this graph's intent, while
        /// overwriting, clearing or superseding the record would act on
        /// one this write does not own. Only a record that still
        /// validates — the pending write's own — may be superseded by
        /// this write's intent.
        /// </summary>
        internal static IntentPublishResult PublishWithIntent(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string resolvedPath,
            string kind,
            string baseHash,
            PreparedOutput output,
            Func<bool> persist)
        {
            var guid = AssetGuidOf(graph);
            var intentWritten = false;
            if (guid is not null)
            {
                if (!IntentSlotActionable(coordinator, graph, guid))
                {
                    return new IntentPublishResult(
                        IntentPublishStatus.IoError, null,
                        "the JSON was not written: the graph's intent record at " +
                        coordinator.IntentPathFor(guid) +
                        " could not be validated");
                }
                var intent = new JsonSyncIntent(
                    resolvedPath, guid, kind, baseHash,
                    JsonSaveCoordinator.Sha256Hex(output.Output.Bytes),
                    GraphJsonFile.GuiOwnedHash(output.Document));
                var written = coordinator.WriteIntent(intent);
                if (!written.Ok)
                {
                    return new IntentPublishResult(
                        IntentPublishStatus.IoError, null, written.Error);
                }
                intentWritten = true;
            }
            GraphJsonPublishResult published;
            if (kind == JsonSaveCoordinator.IntentKindCreate)
            {
                published = GraphJsonFile.PublishCreate(
                    resolvedPath, output.Output, PublishProbe);
            }
            else
            {
                // The bound write's last word is the publish primitive's
                // own re-verification: the probe plants the raced write
                // BEFORE it, so the final byte comparison catches the
                // change — a post-verification seam would place the race
                // after the comparison that must see it.
                PublishProbe?.Invoke(resolvedPath);
                published = GraphJsonFile.PublishReplace(
                    resolvedPath, output.Output, baseHash);
            }
            if (published.Published)
            {
                // The restart recovery gate's crash point: the output is
                // durable, the intent record stands, and the baseline
                // persist has not run — exactly the state recovery (A)
                // and (B) reconcile from.
                PublishPersistFault?.Invoke(resolvedPath);
                var persisted = persist();
                if (persisted && intentWritten)
                {
                    coordinator.ClearIntent(guid!);
                }
                ForgetDecisions(resolvedPath);
                return new IntentPublishResult(
                    persisted
                        ? IntentPublishStatus.Published
                        : IntentPublishStatus.PersistPending,
                    null, null);
            }
            if (intentWritten)
            {
                // The publish provably did not happen — the record's base
                // no longer describes the disk; remove it so the normal
                // conflict path applies fresh.
                coordinator.ClearIntent(guid!);
            }
            if (published.Status == GraphJsonPublishStatus.IoError)
            {
                return new IntentPublishResult(
                    IntentPublishStatus.IoError, null, published.ErrorMessage);
            }
            return new IntentPublishResult(
                IntentPublishStatus.Changed,
                GraphJsonFile.Read(resolvedPath), null);
        }

        // The interim guard of T-W5.3-05 (SPEC §H7 item 3): while the
        // graph's GUID-keyed intent slot holds ANY record the
        // RecoverPendingIntent hook did not handle, the save is refused
        // with a fixed English message naming the record path — the
        // record is never superseded, cleared or acted on, and every
        // Invalid record stays non-actionable. Until T-W5.3-06's
        // recovery implementation lands, a leftover record — a crash
        // between publish and persist — always resolves to refusal.
        private static GraphJsonSyncResult? GuardHeldIntent(
            JsonSaveCoordinator coordinator,
            string? guid,
            string recordedPath,
            string? siblingPath)
        {
            if (guid is null)
            {
                return null;
            }
            var read = coordinator.ReadIntent(guid, recordedPath, siblingPath);
            if (read.State == IntentReadState.Absent)
            {
                return null;
            }
            var intentPath = coordinator.IntentPathFor(guid);
            if (read.State == IntentReadState.Invalid &&
                s_warnedIntents.Add(InvalidIntentKey(intentPath, read.Warning)))
            {
                UnityEngine.Debug.LogWarning(read.Warning);
            }
            return new GraphJsonSyncResult(
                new GraphJsonSyncCheck(
                    GraphJsonSyncState.IoError, null, null,
                    "the graph's pending JSON sync intent record at " + intentPath +
                    " was not resolved; the save is refused until it is resolved"),
                GraphJsonSyncOutcome.Refused, false, GraphJsonSyncChoice.Cancel, null);
        }

        // The crash-recovery hook T-W5.3-06 implements (SPEC §H7 item 3):
        // invoked at every save entry before the normal flow, it
        // reconciles the graph's pending intent record — finishing a
        // crash-interrupted publish when it provably can. It sets
        // <paramref name="handled"/> true when it dealt with the record
        // (a produced <paramref name="result"/> returns directly; a null
        // result with handled means "cleared — run the normal flow").
        // Unimplemented here, it compiles away and GuardHeldIntent's
        // interim refusal covers every leftover record.
        static partial void RecoverPendingIntent(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string? guid,
            string recordedPath,
            string? siblingPath,
            Func<GraphJsonSyncCheck, GraphJsonSyncChoice>? prompt,
            ref GraphJsonSyncResult? result,
            ref bool handled);

        // The notification state T-W5.3-08's unsynced-edit warnings keep
        // — cleared alongside the rest of the session state in
        // ClearSyncTestState. Unimplemented here, it compiles away.
        static partial void ClearNotifyTestState();

        // The open-check state T-W5.3-07's machinery keeps — the queue,
        // the dedupe set and the per-GUID instrumentation — cleared
        // alongside the rest of the session state in ClearSyncTestState.
        // Unimplemented here, it compiles away.
        static partial void ClearOpenTestState();

        // The notification seam T-W5.3-08's unsynced-edit warning
        // implements (SPEC §H7 item 3): invoked by the open-check drain
        // after every performed check with its resulting check.
        // Unimplemented here, it compiles away.
        static partial void AfterOpenCheck(
            string guid, OpWeaveGraph g, GraphJsonSyncCheck c);

        // The graph asset path the recorded link displays — the recorded
        // path when linked, the sibling when the recovery reconciled the
        // first link.
        private static string DisplayPath(
            OpWeaveGraph graph, string recordedPath, string? siblingPath)
        {
            if (recordedPath.Length != 0)
            {
                return recordedPath;
            }
            if (siblingPath is not null)
            {
                return siblingPath;
            }
            var assetPath = AssetPathOf(graph);
            return assetPath is not null
                ? Path.ChangeExtension(assetPath, "opweave.json")!
                : recordedPath;
        }

        // The GUI-owned projection hash the graph would emit over the
        // given non-owned source — the "X" of the recovery comparison.
        // Null when the graph cannot export at all (treated as differing).
        private static string? ProjectedGuiHash(
            OpWeaveGraph graph, WorkflowDocument nonOwnedSource)
        {
            var built = BuildPrepared(graph, nonOwnedSource, null);
            return built.Ok && built.Value is not null
                ? GraphJsonFile.GuiOwnedHash(built.Value!.Document)
                : null;
        }

        // Whether the graph's intent slot may be written or cleared: while
        // the record it holds validates for this graph — or none exists.
        // A record that fails any validation check is never acted on
        // (SPEC §H7 item 3): the slot stays sealed and its bytes stay
        // exactly as found — replacing, clearing or superseding it would
        // act on a record this write does not own, while publishing past
        // it would leave a write the next entry could never reconcile
        // through this graph's intent. Recovery ignores it and reports it
        // once exactly like any invalid record.
        private static bool IntentSlotActionable(
            JsonSaveCoordinator coordinator, OpWeaveGraph graph, string guid)
        {
            var read = coordinator.ReadIntent(
                guid, graph.jsonPath, SiblingPathOf(graph));
            return read.State != IntentReadState.Invalid;
        }

        // The non-owned document a write in this check would merge over:
        // the file's own document when it parses, else the context's seed
        // — the baseline document for a linked save or open, the
        // graph-derived seed for a first link. Null when nothing exists
        // to carry — the decision key then marks the output unemittable.
        private static WorkflowDocument? DecisionCarrier(
            GraphJsonSyncCheck check,
            WorkflowDocument? baseline,
            ChoiceContext context,
            string? assetPath)
        {
            if (check.Document is not null)
            {
                return check.Document;
            }
            if (context == ChoiceContext.SaveFirstLink && assetPath is not null)
            {
                var seed = SeedDocument(assetPath);
                return seed.Ok ? seed.Value : null;
            }
            return baseline;
        }

        // The identity of a §H7 item-3 save request and of the decision
        // that answers it: the resolved target P, the presented disk
        // revision R, the byte hash of the output the request would
        // publish and the baseline B the request was made against. Any
        // change to the file, the graph's projection or the path yields a
        // different key, so a remembered decision can never outlive the
        // exact request it was made for.
        private static string DecisionKey(
            OpWeaveGraph graph,
            string resolvedPath,
            GraphJsonSyncCheck check,
            WorkflowDocument? carrier)
        {
            var outputKey = "unemittable";
            if (carrier is not null)
            {
                var built = BuildPrepared(graph, carrier, null);
                if (built.Ok && built.Value is not null)
                {
                    outputKey = JsonSaveCoordinator.Sha256Hex(
                        built.Value!.Output.Bytes);
                }
            }
            return string.Concat(
                resolvedPath, "|",
                check.PresentedRevision ?? JsonSaveCoordinator.IntentBaseAbsent, "|",
                outputKey, "|",
                BaselineGuiOwnedHash(graph) ?? "none");
        }

        // Applies a remembered §H7.3 decision to an automatic attempt (an
        // Agent write or autosave has no prompt): when the exact request
        // key was decided before, the remembered choice runs — still
        // bound to R under the lock — and when it was never decided the
        // automatic attempt stays refused. An invalidated revision falls
        // back to refusal: nobody may act on a decision the disk moved
        // past.
        private static GraphJsonSyncResult? TryRememberedDecision(
            JsonSaveCoordinator coordinator,
            OpWeaveGraph graph,
            string? guid,
            string resolvedPath,
            string displayPath,
            GraphJsonSyncCheck check,
            WorkflowDocument? carrier,
            WorkflowDocument? baseline,
            string? assetPath)
        {
            var key = DecisionKey(graph, resolvedPath, check, carrier);
            GraphJsonSyncChoice choice;
            lock (s_remembered)
            {
                if (!s_remembered.TryGetValue(key, out choice))
                {
                    return null;
                }
            }
            GraphJsonSyncResult? applied;
            switch (choice)
            {
                case GraphJsonSyncChoice.ReloadJson:
                    applied = TryReloadBound(
                        coordinator, graph, resolvedPath, displayPath, check, guid);
                    break;
                case GraphJsonSyncChoice.OverwriteJson:
                    applied = TryOverwriteBound(
                        coordinator, graph, resolvedPath, displayPath, check,
                        guid, baseline, assetPath, promptShown: false);
                    break;
                default:
                    applied = null;
                    break;
            }
            // The remembered application never shows a dialog: the bound
            // executors report the prompt flag of a human-driven call —
            // normalize it before the result sees it.
            return applied is null
                ? new GraphJsonSyncResult(
                    check, GraphJsonSyncOutcome.Refused, false,
                    GraphJsonSyncChoice.Cancel, null)
                : new GraphJsonSyncResult(
                    applied.Check, applied.Outcome, false, applied.Choice,
                    applied.SaveStatus);
        }

        // Retires every remembered decision made for a path: an applied
        // action moved that path's sync state (a publish rewrote the
        // JSON, a reload or reconcile moved the baseline), so each
        // remembered answer was made for a world that no longer exists.
        // A later recurrence of the identical key — a fresh session may
        // have deserialized a baseline the pending-persist never
        // carried — is a new request and must prompt anew; a decided
        // revision asks no second time only while nothing intervened.
        // Keys begin "resolvedPath |" and '|' is not a legal path
        // character, so the prefix match is exact.
        private static void ForgetDecisions(string resolvedPath)
        {
            var prefix = resolvedPath + "|";
            lock (s_remembered)
            {
                List<string>? stale = null;
                foreach (var key in s_remembered.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        (stale ??= new List<string>()).Add(key);
                    }
                }
                if (stale is null)
                {
                    return;
                }
                foreach (var key in stale)
                {
                    s_remembered.Remove(key);
                }
            }
        }

        // The "reported once" identity of an invalid intent record: the
        // record's own bytes when they can be read — a different invalid
        // record at the same path is a new problem worth one report —
        // falling back to the fixed warning when the bytes cannot be
        // read, so an unreadable record still reports once.
        private static string InvalidIntentKey(string intentPath, string? warning)
        {
            try
            {
                if (File.Exists(intentPath))
                {
                    return intentPath + "|" +
                        JsonSaveCoordinator.Sha256Hex(
                            File.ReadAllBytes(intentPath));
                }
            }
            catch (Exception e) when (e is IOException ||
                e is UnauthorizedAccessException || e is NotSupportedException ||
                e is ArgumentException || e is System.Security.SecurityException)
            {
                // Fall through to the warning-based key.
            }
            return intentPath + "|" + (warning ?? "");
        }

        // The single isolation seam of T-W5.3-16 (research B-05): the
        // one internal reset entry point every mutable static the type
        // carries funnels through — the remembered §H7.3 decisions, the
        // warn-once and unsynced-notify sets, the queued and scheduled
        // open checks, the per-GUID instrumentation, the live-graph
        // registry, the installed open choice provider, every test-only
        // fault hook and the dirty-mark reflection cache — so a
        // domain-reload boundary and consecutive fixtures can never
        // observe a previous run's state. The clear runs under the same
        // single-threaded editor discipline every mutation of these
        // seams uses; the remembered-decision map additionally keeps
        // its own lock, which the clear honours. SyncSeamOccupancy is
        // the matching inspection surface, and the T-W5.3-16 test's
        // reflection census proves no static field escapes coverage.
        internal static void ClearSyncTestState()
        {
            lock (s_remembered)
            {
                s_remembered.Clear();
            }
            s_warnedIntents.Clear();
            AbsenceProbe = null;
            PublishProbe = null;
            PublishPersistFault = null;
            PersistFault = null;
            IntentRecordProbe = null;
            GraphDirtyMark.Reset();
            ClearNotifyTestState();
            ClearOpenTestState();
        }

        // The inspectability half of the isolation contract (T-W5.3-16):
        // one occupancy count per mutable static seam the type carries —
        // a fixture asserts "populated" and "cleared" through this single
        // view instead of reaching into private state, and the census
        // keeps the table covering every seam the reset clears.
        internal static IReadOnlyDictionary<string, int> SyncSeamOccupancy()
        {
            var seams = new Dictionary<string, int>(StringComparer.Ordinal);
            lock (s_remembered)
            {
                seams["s_remembered"] = s_remembered.Count;
            }
            seams["s_warnedIntents"] = s_warnedIntents.Count;
            seams["s_unsyncedNotified"] = s_unsyncedNotified.Count;
            seams["PublishPersistFault"] = PublishPersistFault is null ? 0 : 1;
            seams["AbsenceProbe"] = AbsenceProbe is null ? 0 : 1;
            seams["PublishProbe"] = PublishProbe is null ? 0 : 1;
            seams["PersistFault"] = PersistFault is null ? 0 : 1;
            seams["IntentRecordProbe"] = IntentRecordProbe is null ? 0 : 1;
            seams["GraphDirtyMark"] = GraphDirtyMark.Resolved ? 1 : 0;
            seams["s_scheduledOpens"] = s_scheduledOpens.Count;
            seams["s_live"] = s_live.Count;
            seams["s_pendingOpenChecks"] = s_pendingOpenChecks.Count;
            seams["OpenCheckRequests"] = OpenCheckRequests.Count;
            seams["OpenChecksPerformed"] = OpenChecksPerformed.Count;
            seams["OpenDialogsShown"] = OpenDialogsShown.Count;
            seams["OpenEnableCount"] = OpenEnableCount.Count;
            seams["OpenChecksDropped"] = OpenChecksDropped.Count;
            seams["OpenCheckDropReason"] = OpenCheckDropReason.Count;
            seams["s_openChoiceProvider"] = s_openChoiceProvider is null ? 0 : 1;
            return seams;
        }

        private static string? SiblingPathOf(OpWeaveGraph graph)
        {
            var assetPath = AssetPathOf(graph);
            return assetPath is not null
                ? Path.ChangeExtension(assetPath, "opweave.json")
                : null;
        }

        private static string? AssetPathOf(OpWeaveGraph graph)
        {
            try
            {
                var assetPath = GraphDatabase.GetGraphAssetPath(graph);
                return string.IsNullOrEmpty(assetPath) ? null : assetPath;
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
            {
                return null;
            }
        }

        private static string? AssetGuidOf(OpWeaveGraph graph)
        {
            var assetPath = AssetPathOf(graph);
            if (assetPath is null)
            {
                return null;
            }
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }

        // The persisted baseline write every applied action performs:
        // MarkSynced already moved the in-memory fields; the graph asset
        // write makes them durable. GraphDatabase.SaveGraph writes only
        // when the graph's backing object carries the toolkit's dirty
        // flag — the flag real model mutations set through the public
        // graph APIs — so a baseline-only move on a model-clean graph
        // legitimately produces no write without the mark. No public
        // member marks it (a Graph is not a UnityEngine.Object —
        // EditorUtility.SetDirty and AssetDatabase.SaveAssetIfDirty do
        // not apply), so the persist reaches the model's own dirty
        // entry point through the same bounded reflection
        // GraphLiveValidation.ToolkitPass uses for its windowless pass;
        // a miss marks nothing and the save simply no-ops. The result
        // is proved, not assumed: the persisted .opweavegraph must
        // carry the graph's current link and baseline. A false return
        // keeps the publish's intent record as the pending-persist
        // state (SPEC §H7 item 3): the next entry's recovery finishes
        // the persist — no new store, no model mutation, no forced
        // write.
        private static bool PersistGraph(OpWeaveGraph graph)
        {
            try
            {
                PersistFault?.Invoke(graph);
                GraphDirtyMark.Mark(graph);
                GraphDatabase.SaveGraph(graph);
                var assetPath = AssetPathOf(graph);
                return assetPath is not null &&
                    PersistedFileCarriesSyncState(assetPath, graph);
            }
            catch (Exception)
            {
                // A graph that cannot be persisted now keeps its in-memory
                // baseline; the pending intent — when one was written —
                // covers the persist for the next entry's recovery.
                return false;
            }
        }

        // The toolkit dirty flag GraphDatabase.SaveGraph gates the
        // write on: internal GraphModel.SetGraphObjectDirty marks the
        // internal GraphObject the save serializes, and the model
        // hangs off the public graph's m_Implementation field — the
        // same members ToolkitPass resolves for ProcessGraph. The
        // flag's entry point (or, if the member moved, the GraphObject
        // property and its Dirty setter) is resolved once; when the
        // internals are not where the current toolkit keeps them,
        // nothing is marked and the pending-persist proof decides.
        private static class GraphDirtyMark
        {
            private const BindingFlags InstanceFlags =
                BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic;

            private static FieldInfo? s_implementation;
            private static MethodInfo? s_setGraphObjectDirty;
            private static PropertyInfo? s_graphObject;
            private static PropertyInfo? s_dirty;
            private static bool s_resolved;

            internal static void Mark(OpWeaveGraph graph)
            {
                try
                {
                    var model = ModelOf(graph);
                    if (model is null)
                    {
                        return;
                    }
                    if (!s_resolved)
                    {
                        s_resolved = true;
                        var modelType = model.GetType();
                        s_setGraphObjectDirty = modelType.GetMethod(
                            "SetGraphObjectDirty", InstanceFlags);
                        s_graphObject = modelType.GetProperty(
                            "GraphObject", InstanceFlags);
                        s_dirty = s_graphObject?.PropertyType.GetProperty(
                            "Dirty", InstanceFlags);
                    }
                    if (s_setGraphObjectDirty is not null)
                    {
                        s_setGraphObjectDirty.Invoke(model, null);
                    }
                    else if (s_dirty is not null &&
                        s_graphObject!.GetValue(model) is { } graphObject)
                    {
                        s_dirty.SetValue(graphObject, true);
                    }
                }
                catch (Exception)
                {
                    // A failed mark leaves the persist unwritten — the
                    // file proof keeps the pending intent standing.
                }
            }

            // The occupancy read SyncSeamOccupancy performs: a private
            // member's accessibility domain is its own type's program
            // text, so the containing GraphJsonSync cannot reach
            // s_resolved directly — the read is exposed on the nested
            // class itself, internal so nothing outside GraphJsonSync
            // gains a new surface.
            internal static bool Resolved => s_resolved;

            // The resolved-once cache is one of the type's static seams
            // (T-W5.3-16): the shared reset clears it so a later run
            // re-resolves rather than inheriting a previous run's world.
            internal static void Reset()
            {
                s_implementation = null;
                s_setGraphObjectDirty = null;
                s_graphObject = null;
                s_dirty = null;
                s_resolved = false;
            }

            private static object? ModelOf(OpWeaveGraph graph)
            {
                s_implementation ??= FindImplementation(graph.GetType());
                return s_implementation?.GetValue(graph);
            }

            // The graph model lives in the toolkit base types'
            // m_Implementation field — walk the hierarchy for it, the
            // same walk ToolkitPass makes.
            private static FieldInfo? FindImplementation(Type type)
            {
                for (var t = type; t is not null; t = t.BaseType)
                {
                    var field = t.GetField("m_Implementation", InstanceFlags);
                    if (field is not null)
                    {
                        return field;
                    }
                }
                return null;
            }
        }

        // The proof the persisted .opweavegraph carries the graph's
        // current link and baseline — the same read the reopen tests'
        // (6b') assertion performs: the serialized asset is text, so the
        // file carries the baseline exactly when the current
        // syncedBytesHash appears in it, and the link when the current
        // jsonPath does — verbatim, in the serializer's
        // backslash-escaped form or in the forward-slash-normalized
        // form. An empty link or an unrecorded baseline cannot be proved
        // from the text and fails closed. Reading the file never
        // touches the live instance.
        private static bool PersistedFileCarriesSyncState(
            string assetPath, OpWeaveGraph graph)
        {
            if (graph.jsonPath.Length == 0 || graph.syncedBytesHash.Length == 0)
            {
                return false;
            }
            string text;
            try
            {
                text = File.ReadAllText(assetPath);
            }
            catch (Exception e) when (e is IOException ||
                e is UnauthorizedAccessException || e is NotSupportedException ||
                e is ArgumentException || e is System.Security.SecurityException)
            {
                return false;
            }
            return text.Contains(graph.syncedBytesHash) &&
                (text.Contains(graph.jsonPath) ||
                    text.Contains(graph.jsonPath.Replace("\\", "\\\\")) ||
                    text.Contains(graph.jsonPath.Replace("\\", "/")));
        }

        // The baseline the graph's sync state records: the stored
        // GUI-owned projection hash, or — for assets synced before the
        // revision fields existed — the projection of the serialized
        // synced document. Null when no baseline exists at all.
        private static string? BaselineGuiOwnedHash(OpWeaveGraph graph)
        {
            if (graph.syncedGuiOwnedHash.Length != 0)
            {
                return graph.syncedGuiOwnedHash;
            }
            var baseline = ReadBaseline(graph);
            return baseline is null ? null : GraphJsonFile.GuiOwnedHash(baseline);
        }

        // The merge base a first save uses when the adopted path holds no
        // document: the graph supplies the GUI-owned keys and the seed the
        // rest — schemaVersion 1.0, the WF id and title derived from the
        // graph asset name — the only non-GUI-owned content a brand-new
        // workflow can carry when there is no file to inherit it from.
        private static Result<WorkflowDocument> SeedDocument(string assetPath)
        {
            var stem = Path.GetFileNameWithoutExtension(assetPath);
            return WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = WorkflowIdFor(stem),
                Title = stem,
                Nodes = new List<NodeData>(),
                ControlEdges = new List<ControlEdge>(),
            });
        }

        // A WF id (SPEC §A4: [a-z0-9] runs joined by . _ -, at most 128
        // characters) derived from the graph asset name: lowercased, every
        // run of other characters collapsed to a single '-'. A name that
        // yields nothing falls back to "workflow".
        private static string WorkflowIdFor(string? stem)
        {
            var builder = new StringBuilder();
            var separator = false;
            if (stem is not null)
            {
                foreach (var raw in stem)
                {
                    var c = char.ToLowerInvariant(raw);
                    if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                    {
                        if (separator &&
                            builder.Length != 0 &&
                            builder.Length < IdGrammar.WorkflowIdMaxLength - 1)
                        {
                            builder.Append('-');
                        }
                        separator = false;
                        if (builder.Length < IdGrammar.WorkflowIdMaxLength)
                        {
                            builder.Append(c);
                        }
                    }
                    else if (builder.Length != 0)
                    {
                        separator = true;
                    }
                }
            }
            return builder.Length == 0 ? "workflow" : builder.ToString();
        }

        private static WorkflowDocument? ReadBaseline(OpWeaveGraph graph)
        {
            if (graph.syncedJson.Length == 0)
            {
                return null;
            }
            var read = WorkflowSerializer.Read(graph.syncedJson);
            return read.Ok ? read.Value : null;
        }

        // The standalone relation a reopened graph's stored sync state
        // bears to its file — CheckOnOpen's evaluation without the open
        // machinery (the open drain owns it once T-W5.3-07 lands).
        private static GraphJsonSyncCheck SyncStateOfOpen(OpWeaveGraph graph)
        {
            if (graph.jsonPath.Length == 0)
            {
                return new GraphJsonSyncCheck(GraphJsonSyncState.Unlinked, null, null, null);
            }
            return CheckFromDisk(GraphJsonFile.Read(graph.jsonPath), graph);
        }

        // The parsed document a disk read holds, or null when the file is
        // absent, unreadable or does not parse.
        private static WorkflowDocument? DiskDocument(GraphJsonFile.DiskRead disk) =>
            disk.Document is { Ok: true } read ? read.Value : null;

        // The merged write prepared for publication: the document the
        // merge produced (the new baseline MarkSynced records) and the
        // §A3 output bytes carrying the non-owned members byte-exactly.
        internal sealed class PreparedOutput
        {
            internal PreparedOutput(WorkflowDocument document, GraphJsonFile.Output output)
            {
                Document = document;
                Output = output;
            }

            internal WorkflowDocument Document { get; }

            internal GraphJsonFile.Output Output { get; }
        }

        // The outcome of building a PreparedOutput: Ok is the export's
        // own result (diagnostics travel separately — the caller decides
        // whether warned carried items make the build a conflict or an
        // explicit overwrite accepts the drop).
        private sealed class PreparedBuild
        {
            internal bool Ok;
            internal IReadOnlyList<Diagnostic> Diagnostics = Array.Empty<Diagnostic>();
            internal PreparedOutput? Value;
        }

        // The monolith's BuildOutput(graph, nonOwnedSource, carryText):
        // the graph's export merged over the non-owned source, then the
        // §A3 writer's output with the carried members' raw text preserved
        // byte-exactly (SPEC §0.6 SG-17, §H7 item 3).
        private static PreparedBuild BuildPrepared(
            OpWeaveGraph graph, WorkflowDocument nonOwnedSource, string? carryText)
        {
            var merged = GraphJsonConverter.Export(graph, nonOwnedSource);
            if (!merged.Ok)
            {
                return new PreparedBuild
                {
                    Ok = false,
                    Diagnostics = merged.Diagnostics,
                };
            }
            var output = GraphJsonFile.BuildOutput(merged.Value!, carryText);
            return new PreparedBuild
            {
                Ok = true,
                Diagnostics = merged.Diagnostics,
                Value = new PreparedOutput(merged.Value!, output),
            };
        }
    }
}
