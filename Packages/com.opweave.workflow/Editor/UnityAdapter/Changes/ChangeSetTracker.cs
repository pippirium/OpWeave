#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §F7 attribution of one runner host: it owns each executing Run's
    /// <see cref="RunChangeSet"/>, opens the per-step
    /// <see cref="UnityChangeRecorder"/> behind <c>ctx.Changes</c>, binds what the
    /// <see cref="AssetChangePostprocessor"/> reports to the step whose attempt the
    /// Run last opened, and finalizes the list into
    /// <c>changes.json</c> when the Run ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the last attempt.</b> <c>OnPostprocessAllAssets</c> carries no Run
    /// or step identity, and Unity delivers it whenever the import pipeline runs:
    /// for the synchronous imports an Operation asks for
    /// (<c>AssetDatabase.CreateAsset</c>, <c>ImportAssetOptions.ForceSynchronousImport</c>)
    /// that is inside <c>Execute</c>/<c>Poll</c>; for a deferred import it is
    /// afterwards, while the Run is still executing. Reports are therefore
    /// attributed to the latest attempt of the Run that is Running — the step that
    /// made the change — and dropped whenever no Run is executing (the Editor's
    /// own imports, a Run waiting for approval or review), so nothing outside a
    /// Run is ever blamed on one.
    /// </para>
    /// <para>
    /// <b>One executing Run.</b> SPEC §E2 lets a single Run execute per Editor,
    /// so one attribution slot is enough; every live tracker of the domain is
    /// asked (§E8 events are instance-scoped, the AssetDatabase callback is not)
    /// and only the one whose Run is Running accepts.
    /// </para>
    /// <para>
    /// <b>The list a reconstruction reopens.</b> The lists live in memory, so the
    /// host a Run gets after a domain reload owns none of what its earlier steps
    /// changed. Opening a list this Run does not already have therefore reads the
    /// elements its finished steps journaled back out of the Run's journal
    /// (<see cref="JournaledChanges"/>) before this domain records anything, and the
    /// file written at Run end is the union. Evidence that cannot be read is
    /// reported, never written off as a short list — see <see cref="FinalizeRun"/>.
    /// </para>
    /// </remarks>
    internal sealed class ChangeSetTracker
    {
        // Every tracker of the domain: the AssetDatabase callback is static and
        // carries no host, so it reaches the trackers through this list. Core is
        // main-thread only (AGENTS invariants) and so is the import pipeline, so
        // the list needs no lock.
        private static readonly List<ChangeSetTracker> Live =
            new List<ChangeSetTracker>();

        private const string AssetKind = "asset";

        private readonly string _runStoreRoot;
        private readonly Dictionary<string, RunChangeSet> _sets =
            new Dictionary<string, RunChangeSet>(StringComparer.Ordinal);

        private UnityChangeRecorder? _attempt;
        private string? _executingRunId;

        // Internal test seam for the finalize write: invoked with the target path
        // and the list before the real write; a non-null return is thrown instead of
        // writing, so tests inject the store failure deterministically. Production
        // callers leave it null.
        internal Func<string, IReadOnlyList<ChangeRecord>, Exception?>? WriteProbe { get; set; }

        /// <summary>
        /// Tracks the change lists of the Runs <c>runStoreRoot</c> holds — the
        /// SPEC §F1 root the finalized <c>changes.json</c> files live under.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="runStoreRoot"/> is null or empty.
        /// </exception>
        internal ChangeSetTracker(string runStoreRoot)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.",
                    nameof(runStoreRoot));
            }
            _runStoreRoot = runStoreRoot;
            Live.Add(this);
        }

        /// <summary>
        /// Stops this tracker from receiving asset reports and drops the lists it
        /// still held (the host's Run bookkeeping dies with it).
        /// </summary>
        public void Dispose()
        {
            Live.Remove(this);
            _sets.Clear();
            _attempt = null;
            _executingRunId = null;
        }

        /// <summary>
        /// Opens the recorder of one step attempt — the context factory calls it
        /// with the run loop's per-step recorder, and the returned recorder is
        /// what the step's Operation sees as <c>ctx.Changes</c>. Without a known
        /// active Run the loop's own recorder is returned unchanged: the change
        /// list of an unknown Run could only invent its <c>step</c>/<c>nodeId</c>
        /// owner.
        /// </summary>
        /// <param name="runId">
        /// The Run the attempt belongs to, or null when none is active.
        /// </param>
        /// <param name="stepRecorder">The run loop's recorder for this step.</param>
        /// <param name="stepIndex">The Plan step index.</param>
        /// <param name="nodeId">The Plan node id.</param>
        /// <param name="attempt">
        /// The §F2 envelope attempt of this step attempt — the
        /// <c>attempt</c> the elements it produces carry.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stepRecorder"/> or <paramref name="nodeId"/> is null.
        /// </exception>
        internal IChangeRecorder OpenAttempt(
            string? runId, IChangeRecorder stepRecorder, int stepIndex, string nodeId,
            int attempt)
        {
            if (stepRecorder is null)
            {
                throw new ArgumentNullException(nameof(stepRecorder));
            }
            if (string.IsNullOrEmpty(runId))
            {
                return stepRecorder;
            }
            var set = SetFor(runId);
            var recorder = new UnityChangeRecorder(
                stepRecorder, set, stepIndex, nodeId, attempt);
            _attempt = recorder;
            _executingRunId = runId;
            return recorder;
        }

        /// <summary>
        /// Follows the Run's execution for the attribution window alone:
        /// <c>running</c> opens it and any other state ends it — a Run that is
        /// not executing steps owns no import. The change list itself is
        /// finalized once, inside the Run's durable completion path, by the
        /// coordinator's single run_finished writer calling
        /// <see cref="FinalizeRun"/> — never here, so no end path can write or
        /// report the same finalize a second time.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
        internal void OnRunStateChanged(RunStateChanged change)
        {
            if (change is null)
            {
                throw new ArgumentNullException(nameof(change));
            }
            switch (change.State)
            {
                case RunState.Running:
                    _executingRunId = change.RunId;
                    if (_attempt is not null &&
                        !string.Equals(
                            _attempt.Changes.RunId, change.RunId, StringComparison.Ordinal))
                    {
                        _attempt = null;
                    }
                    return;

                default:
                    // terminal states included: a Run that is not executing
                    // owns no import — the finalize already ran inside the
                    // durable completion path.
                    _executingRunId = null;
                    if (_attempt is not null &&
                        string.Equals(
                            _attempt.Changes.RunId, change.RunId, StringComparison.Ordinal))
                    {
                        _attempt = null;
                    }
                    return;
            }
        }

        /// <summary>
        /// Routes one <c>OnPostprocessAllAssets</c> report to every live tracker;
        /// each one that owns a Running Run attributes it to its latest step
        /// attempt and the others ignore it. Never throws: an exception escaping
        /// here would reach Unity's import pipeline.
        /// </summary>
        internal static void ReportAssetChanges(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            for (var i = 0; i < Live.Count; i++)
            {
                try
                {
                    Live[i].Report(importedAssets, deletedAssets, movedAssets,
                        movedFromAssetPaths);
                }
                catch (Exception)
                {
                    // A failed attribution must not break the import: the change
                    // is still recorded by the Operation that made it, and SPEC
                    // §E8 keeps a subscriber fault from stopping the Run.
                }
            }
        }

        private RunChangeSet SetFor(string runId)
        {
            if (!_sets.TryGetValue(runId, out var set))
            {
                // The baseline is taken at the Run's first step attempt: every
                // asset the AssetDatabase already knows existed before it ran.
                set = new RunChangeSet(runId, new HashSet<string>(
                    AssetDatabase.GetAllAssetPaths(), StringComparer.Ordinal));
                RestoreJournaledChanges(runId, set);
                _sets.Add(runId, set);
            }
            return set;
        }

        // A list opened here may be the second list of one Run: a domain reload or a
        // new runner host loses the in-memory one while the Run keeps its journal,
        // and §F7 still wants that Run's whole list at its end. The elements every
        // finished step already journaled are therefore read back before this
        // domain's steps record anything of their own, so the finalized file is the
        // union — the replay rule of <see cref="RunChangeSet.Restore"/> keeps the
        // durable list exactly as the writer left it, and the live merge rule then
        // keeps a step that re-ran after the resume from listing its change twice.
        private void RestoreJournaledChanges(string runId, RunChangeSet set)
        {
            try
            {
                var restored = JournaledChanges.Read(
                    JournaledChanges.PathOf(_runStoreRoot, runId), out var unreadable);
                for (var i = 0; i < restored.Count; i++)
                {
                    set.Restore(restored[i]);
                }
                set.IncompleteEvidence = unreadable;
            }
            catch (Exception e)
            {
                // Reading the journal must never fail the step that reopened the
                // Run; what it must not do is pretend the list is whole.
                set.IncompleteEvidence = e.Message;
            }
        }

        private void Report(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            var executing = _executingRunId;
            var recorder = _attempt;
            if (executing is null || recorder is null)
            {
                return;
            }
            var changes = recorder.Changes;
            if (!string.Equals(changes.RunId, executing, StringComparison.Ordinal) ||
                changes.Finalized)
            {
                return;
            }

            // Unity's callback contract is non-null arrays; the empty-array
            // fallbacks keep one malformed report from losing the rest.
            var moves = movedAssets ?? Array.Empty<string>();
            var movesFrom = movedFromAssetPaths ?? Array.Empty<string>();
            var deletions = deletedAssets ?? Array.Empty<string>();
            var imports = importedAssets ?? Array.Empty<string>();

            // One element per path per report: Unity lists a move destination in
            // movedAssets and reimports it in the same batch, and the move is the
            // stronger statement of what happened (§F7 has no second target to
            // record a rename against).
            var reported = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < moves.Length; i++)
            {
                var path = moves[i];
                if (string.IsNullOrEmpty(path) || !reported.Add(path))
                {
                    continue;
                }
                var from = i < movesFrom.Length ? movesFrom[i] : null;
                changes.NoteMoved(from ?? string.Empty, path);
                recorder.Record(ChangeAction.Moved, TargetOf(path), undoable: false);
            }

            for (var i = 0; i < deletions.Length; i++)
            {
                var path = deletions[i];
                if (string.IsNullOrEmpty(path) || !reported.Add(path))
                {
                    continue;
                }
                changes.NoteDeleted(path);
                recorder.Record(ChangeAction.Deleted, TargetOf(path), undoable: false);
            }

            for (var i = 0; i < imports.Length; i++)
            {
                var path = imports[i];
                if (string.IsNullOrEmpty(path) || !reported.Add(path))
                {
                    continue;
                }
                // An asset the Run produced reads as created, an import of one
                // that already existed — including one an earlier step produced —
                // as modified.
                var action = changes.ReportsAsCreated(path)
                    ? ChangeAction.Created
                    : ChangeAction.Modified;
                recorder.Record(action, TargetOf(path), undoable: false);
            }
        }

        // An asset change is never undoable (SPEC §G3 item 3), so the automatic
        // elements always report undoable: false. The guid is the durable §F5
        // key when the AssetDatabase still knows the path — for a deleted asset
        // the report arrives after the entry is gone, and §F7 leaves guid optional
        // precisely so such an element stays listable.
        private static ChangeTarget TargetOf(string assetPath) =>
            new ChangeTarget(AssetKind, assetPath, GuidOf(assetPath));

        private static string? GuidOf(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }

        /// <summary>
        /// Writes one Run's change list into its <c>changes.json</c> slot — the
        /// SPEC §F7 "Run 終了時に確定". The coordinator's single run_finished
        /// writer calls it inside the Run's durable completion path, BEFORE the
        /// terminal record, so a refused write can end the Run Failed (SPEC §F1
        /// OW-STORE-001); it is the only finalize path.
        /// </summary>
        /// <returns>
        /// Null when the list reached the Run Store; the SPEC §F1 OW-STORE-001
        /// diagnostic when it did not. Two kinds of failure answer here, and neither
        /// leaves the list marked finalized: a write the Run Store refused, and a
        /// list whose journaled elements could not be read back after a
        /// reconstruction — writing that would hand review a list silently shorter
        /// than the Run's changes. The set stays owned so the caller can finalize it
        /// again once the Store works.
        /// </returns>
        internal Diagnostic? FinalizeRun(string runId)
        {
            // A list this domain never opened is restored from the journal
            // exactly like a reopened attempt's (SetFor): the §F4 resume
            // completing an interrupted end owns none of what the Run's steps
            // changed, and a crash between the changes.json write and the
            // terminal record re-finalizes from the same durable elements —
            // idempotent, never duplicated. A Run that recorded nothing
            // restores nothing and writes the empty list its slot already is.
            var set = SetFor(runId);
            if (set.IncompleteEvidence is { } evidence)
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_STORE_001,
                    "The Run change list could not be finalized because the changes it " +
                    "already journaled could not be read back.",
                    hint: evidence);
            }
            var path = ChangeSetFile.PathOf(_runStoreRoot, runId);
            try
            {
                var simulated = WriteProbe?.Invoke(path, set.Records);
                if (simulated is not null)
                {
                    throw simulated;
                }
                ChangeSetFile.Write(path, set.Records);
            }
            catch (Exception e)
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_STORE_001,
                    "The Run change list could not be written to the Run Store.",
                    hint: DescribeFailure(e));
            }
            // Only a list that reached the Run Store is a finalized one: marking it
            // before the write would let a failed write drop the Run's changes with
            // nothing left to report or retry.
            set.MarkFinalized();
            _sets.Remove(runId);
            return null;
        }

        // The failure detail the refusal's hint carries: the outer wrapper's
        // message plus every inner exception's own, so the Console line names
        // the real Win32 cause (a sharing violation, an access denial) instead
        // of only the wrapper's fixed text. The hint never reaches the journal
        // — the record carries the fixed §F1 message — so the file path and OS
        // detail stay where §F6 masking does not have to catch up with them.
        private static string DescribeFailure(Exception e)
        {
            var text = e.Message;
            for (var inner = e.InnerException; inner is not null; inner = inner.InnerException)
            {
                text += " " + inner.Message;
            }
            return text;
        }
    }
}
