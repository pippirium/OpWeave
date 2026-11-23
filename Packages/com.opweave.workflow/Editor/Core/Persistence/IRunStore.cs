#nullable enable

using System.Collections.Generic;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The Run Store contract of SPEC §F1: the append-only journal is the single
    /// source of truth and every other file is derived or replaceable state.
    /// </summary>
    /// <remarks>
    /// <see cref="Load"/> reconstructs the recorded state only from the
    /// journal after the SPEC §D0.1 integrity verification — a missing,
    /// corrupt or tampered store never produces an automatic success; it
    /// produces NeedsReview with OW-STORE-002, or a dropped last line with an
    /// OW-STORE-002 warning (SPEC §F3 item 3).
    /// </remarks>
    public interface IRunStore
    {
        /// <summary>
        /// Persists a newly accepted Run (SPEC §F1, §F2): writes
        /// <c>plan.json</c> as the Plan's Canonical JSON bytes, the audit copy
        /// <c>workflow.json</c>, line 1 of <c>journal.ndjson</c> and the
        /// RunAccepted record whose <c>planFileDigest</c> is the SHA-256 of the
        /// exact plan.json bytes (SPEC §D7 item 6, §D0.1), plus the
        /// regenerable <c>state.json</c> cache, the <c>changes.json</c> slot
        /// and the <c>index.json</c> entry. A runId recorded in the SG-16
        /// deletion tombstones is never reused — the create is refused as
        /// OW-STORE-003.
        /// </summary>
        Result<Unit> CreateRun(string runId, CompiledPlan plan, WorkflowDocument doc);

        /// <summary>
        /// Appends one journal record under the SPEC §F3 durability rules:
        /// the journal is opened <c>FileMode.Append</c>/<c>FileShare.Read</c>
        /// and a Flushed record — and every Buffered line still pending —
        /// reaches disk (<c>Flush(flushToDisk: true)</c>) before the call
        /// returns. Buffered writes are emitted at the next Flushed append or
        /// the explicit flush hook. A journal tail SPEC §F3 item 3 drops is
        /// truncated before the write so the record lands at a record
        /// boundary; a state.json/index.json maintenance failure after a
        /// committed journal write is a non-fatal warning, not a failed
        /// append, because <see cref="Load"/> rebuilds both caches.
        /// </summary>
        Result<Unit> Append(string runId, JournalRecord record, Durability durability);

        /// <summary>
        /// Rebuilds a Run's recorded state from its journal and re-verifies
        /// the Plan (SPEC §F1, §D0.1): the recorded <c>planFileDigest</c>
        /// against the plan.json bytes, the format/canonicalJson versions
        /// against the JournalHeader, and the recomputed planHash. A broken
        /// last journal line is dropped with an OW-STORE-002 warning; any
        /// other integrity failure is OW-STORE-002 and NeedsReview — never an
        /// automatic success. A missing or corrupt <c>state.json</c> is
        /// regenerated from the journal. The SG-16 deletion tombstones are
        /// consulted first, so a deleted Run answers OW-STORE-003 and a
        /// never-created one OW-RUN-010, while a tombstone file that
        /// cannot prove its contents fails closed as OW-STORE-002.
        /// </summary>
        Result<RunSnapshot> Load(string runId);

        /// <summary>
        /// The Run index entries matching <paramref name="query"/> (SPEC §F1).
        /// The index is a cache over the journals — the single truth — so it
        /// is never trusted for results: both membership and state are
        /// derived from the per-Run journals on every call (a missing or
        /// corrupt journal yields the NeedsReview <see cref="Load"/> itself
        /// produces, never a dropped Run), and <c>index.json</c> is rewritten
        /// as a cache when it differs from the derived truth. The scan fails
        /// closed: a genuinely absent Runs directory is an empty result
        /// while an enumeration or journal-metadata failure — an
        /// inaccessible Runs path included — is OW-STORE-001, never a
        /// partial list or a rewritten index. The SG-16 deletion
        /// tombstones decide membership the same way <see cref="Load"/>
        /// does: a tombstoned Run never lists and a tombstone file that
        /// cannot prove its contents fails the scan closed.
        /// </summary>
        Result<IReadOnlyList<RunIndexEntry>> List(RunQuery query);

        /// <summary>
        /// Deletes a finished Run's directory and its index entry as the
        /// SG-16 tombstone transaction (SPEC §F8, §0.6 SG-16): under the
        /// Run Store inter-process exclusion the
        /// <c>deleted-runs.ndjson</c> tombstone is appended and fsync'd
        /// before the Run directory and index entry are removed, so an
        /// interrupted deletion keeps the Run provably deleted — never
        /// OW-RUN-010 — until a later sweep or re-delete finishes the
        /// removal. An unfinished or NeedsReview Run is refused as
        /// OW-STORE-001 untouched, an unknown <paramref name="runId"/>
        /// is OW-RUN-010, an already-tombstoned runId completes the
        /// removal idempotently, and a tombstone file that cannot prove
        /// its contents is OW-STORE-002 with nothing touched.
        /// </summary>
        Result<Unit> Delete(string runId);
    }
}
