#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The SPEC §F8 retention pass: keeps the newest
    /// <see cref="RunRetentionLimits.MaxFinishedRuns"/> finished Runs whose
    /// combined Run-directory size stays under
    /// <see cref="RunRetentionLimits.MaxFinishedBytes"/>, and selects the
    /// oldest finished Runs past either bound as deletion candidates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Finished" is exactly the terminal set —
    /// <see cref="RunState.Succeeded"/>, <see cref="RunState.Failed"/> and
    /// <see cref="RunState.Cancelled"/>. SPEC §F8 forbids deleting an
    /// unfinished or NeedsReview Run, so the candidate selection never sees
    /// them: the journal-derived <see cref="IRunStore.List"/> truth already
    /// classifies a damaged Run NeedsReview, which keeps it out of the
    /// terminal query (a corrupt Run can never be deleted by retention).
    /// </para>
    /// <para>
    /// "Newest" is the most recently finished: index entries sort by
    /// <c>updatedAt</c> descending — the fixed-width UTC ISO 8601 format of
    /// <see cref="UtcTimestamp"/> sorts chronologically under an Ordinal
    /// comparison — with the <c>runId</c> ULID as the deterministic
    /// tie-break (a newer Run id wins). The retained set is the longest
    /// newest-first prefix that satisfies both bounds; a finished Run that
    /// alone exceeds the byte limit is itself a candidate, never a silent
    /// exception.
    /// </para>
    /// <para>
    /// This type owns selection and scheduling only. Physical deletion is
    /// delegated to the injected <paramref name="deleteRun"/> provider —
    /// the SG-16 transactional tombstone writer of T-W4.4-02,
    /// <see cref="FileRunStore.Delete"/>. While no provider is
    /// bound (or one refuses), nothing is physically deleted and no Run id
    /// is reported as successful cleanup: <see cref="RunRetentionOutcome"/>
    /// keeps the selected <see cref="RunRetentionOutcome.Candidates"/>
    /// apart from the confirmed <see cref="RunRetentionOutcome.Deleted"/>.
    /// A refused candidate does not stop the remaining deletions. A Run
    /// whose size cannot be measured fails toward keeping: it counts for
    /// the pass at zero bytes — under-deleting is the safe direction for
    /// a bound that only limits growth — it is never a deletion
    /// candidate, so the provider never sees it, and the provider's
    /// diagnostic is surfaced unchanged. Core runs on the main thread
    /// only, so no synchronization is taken.
    /// </para>
    /// </remarks>
    public sealed class RunRetention
    {
        // The terminal Run states of SPEC §E1 that SPEC §F8 calls "終了した
        // Run". NeedsReview is deliberately absent — it is never a
        // retention candidate.
        private static readonly RunState[] s_finishedStates =
        {
            RunState.Succeeded, RunState.Failed, RunState.Cancelled,
        };

        private readonly IRunStore _store;
        private readonly Func<string, Result<long>> _measureRunSize;
        private readonly Func<string, Result<Unit>>? _deleteRun;

        /// <summary>
        /// A retention pass over <paramref name="store"/>.
        /// <paramref name="measureRunSize"/> supplies each finished Run's
        /// persisted size in bytes (<see cref="FileRunStore"/>'s
        /// <c>MeasureRunBytes</c> in production); a failed measurement is
        /// reported, counted as zero and kept out of the candidate set
        /// (see the class remarks).
        /// <paramref name="deleteRun"/> is the physical-deletion provider —
        /// <see cref="FileRunStore.Delete"/>, the SG-16 tombstone writer;
        /// omitted means selection only, no deletion is attempted.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="store"/> or <paramref name="measureRunSize"/> is null.
        /// </exception>
        public RunRetention(
            IRunStore store,
            Func<string, Result<long>> measureRunSize,
            Func<string, Result<Unit>>? deleteRun = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _measureRunSize =
                measureRunSize ?? throw new ArgumentNullException(nameof(measureRunSize));
            _deleteRun = deleteRun;
        }

        /// <summary>
        /// Runs one SPEC §F8 pass — the unit the Editor-start and Run-end
        /// schedules invoke: selects the oldest finished Runs past either
        /// limit and delegates their deletion, oldest first, to the bound
        /// provider. The outcome names the selected candidates and, of
        /// those, only the ids the provider confirmed deleted.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="limits"/> is null.</exception>
        public Result<RunRetentionOutcome> Apply(RunRetentionLimits limits)
        {
            if (limits is null)
            {
                throw new ArgumentNullException(nameof(limits));
            }
            var listed = _store.List(new RunQuery(s_finishedStates));
            if (!listed.Ok || listed.Value is null)
            {
                return Result<RunRetentionOutcome>.Failure(listed.Diagnostics);
            }
            var finished = new List<RunIndexEntry>(listed.Value);
            // Newest finished first: updatedAt (the last state change, i.e.
            // the finish) Ordinal-descending, runId ULID descending as the
            // deterministic tie-break.
            finished.Sort(CompareNewestFirst);

            var maxRuns = Math.Max(0, limits.MaxFinishedRuns);
            var maxBytes = Math.Max(0, limits.MaxFinishedBytes);
            var diagnostics = new List<Diagnostic>();
            var retained = new List<string>();
            var candidates = new List<RunIndexEntry>();
            // Every finished Run is measured once, newest first, before
            // either set is built: the byte bound needs the sizes, and
            // fail toward keeping needs every measurement failure
            // observed — a Run whose size cannot be measured is reported,
            // counts for the pass at zero bytes and is never a deletion
            // candidate, wherever it falls in the order.
            var sizes = new long[finished.Count];
            var measurable = new bool[finished.Count];
            for (var i = 0; i < finished.Count; i++)
            {
                measurable[i] =
                    TryMeasureSize(finished[i].RunId, diagnostics, out var size);
                sizes[i] = size;
            }
            // The retained set is the newest-first prefix that satisfies
            // both bounds — "delete the oldest finished Runs until within
            // the limits" — so the first Run that fits neither bound ends
            // the prefix: it and every measurable older Run are
            // candidates, a finished Run that alone exceeds the byte
            // bound included.
            // sizes[index] <= maxBytes - cumulative compares without
            // overflowing.
            var index = 0;
            long cumulative = 0;
            while (index < finished.Count && retained.Count < maxRuns &&
                   sizes[index] <= maxBytes - cumulative)
            {
                retained.Add(finished[index].RunId);
                cumulative += sizes[index];
                index++;
            }
            for (; index < finished.Count; index++)
            {
                // Fail toward keeping holds past the prefix too: a Run
                // whose size could not be measured is reported and kept,
                // never handed to the deletion provider.
                if (measurable[index])
                {
                    candidates.Add(finished[index]);
                }
            }

            var deleted = new List<string>();
            var selected = new List<string>(candidates.Count);
            // Candidates arrive newest-of-the-overflow first; deletion runs
            // oldest first, so a partially failing provider still frees the
            // oldest Runs first.
            for (var i = candidates.Count - 1; i >= 0; i--)
            {
                var runId = candidates[i].RunId;
                selected.Add(runId);
                if (_deleteRun is null)
                {
                    continue;
                }
                var removed = _deleteRun(runId);
                if (removed.Ok)
                {
                    deleted.Add(runId);
                }
                else
                {
                    diagnostics.AddRange(removed.Diagnostics);
                }
            }

            return Result<RunRetentionOutcome>.Success(
                new RunRetentionOutcome(
                    ReadOnlyCollections.CopyList(selected),
                    ReadOnlyCollections.CopyList(deleted)),
                diagnostics);
        }

        // The Run's persisted size and whether the provider could measure
        // it. A failed measurement is relayed unchanged and the Run counts
        // for the pass at zero bytes — it pushes nothing out — while the
        // false measurability keeps it out of the candidate set entirely:
        // an unmeasurable Run is never silently dropped from the index it
        // was listed from and is never handed to the deletion provider
        // (the safe direction of the class remarks).
        private bool TryMeasureSize(
            string runId, List<Diagnostic> diagnostics, out long size)
        {
            var measured = _measureRunSize(runId);
            if (measured.Ok && measured.Value is { } value)
            {
                size = value;
                return true;
            }
            diagnostics.AddRange(measured.Diagnostics);
            size = 0;
            return false;
        }

        private static int CompareNewestFirst(RunIndexEntry a, RunIndexEntry b)
        {
            var byUpdated = string.Compare(b.UpdatedAt, a.UpdatedAt, StringComparison.Ordinal);
            return byUpdated != 0
                ? byUpdated
                : string.Compare(b.RunId, a.RunId, StringComparison.Ordinal);
        }
    }
}
