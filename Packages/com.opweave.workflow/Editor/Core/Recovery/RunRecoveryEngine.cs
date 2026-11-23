#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Recovery
{
    /// <summary>
    /// The recovery start-up engine of SPEC §F4 items 1, 2 and 6 with the
    /// SPEC §E2 item 3 exclusion and takeover rules: after the Registry rebuild
    /// (the <c>RunnerStartupOrder.RunRecovery</c> slot of T-W2.1-04's ordered hook
    /// list) it confirms or takes the <c>coordinator.lock</c> exclusion, reads
    /// <c>lock.json</c> and <c>index.json</c> inside that critical section, and
    /// re-attaches every unfinished Run to the <see cref="RunCoordinator"/> —
    /// driving the §F4 resume of T-W1.8-08 for the one Run the exclusion passes
    /// to. Everything it cannot verify ends NeedsReview with no journal record
    /// written and no step re-executed (SPEC §F0).
    /// </summary>
    /// <remarks>
    /// <para><b>The exclusion decides everything.</b> A pass that cannot obtain it
    /// does nothing at all: no Run is re-attached, resumed or started
    /// (SPEC §F4 item 1 — <c>取得できなければ再開しない</c>) and the Run this
    /// Editor's previous domain owned is reported NeedsReview without being
    /// resumed (SPEC §E2 item 3 (a)). Nothing is read from the Run Store either:
    /// <c>lock.json</c> may only be read inside the exclusion (§E2 item 3), and
    /// scanning the journals while a live owner mutates them would decide recovery
    /// from a torn picture. When the pass holds the exclusion it holds it for the
    /// whole pass, which is what lets the takeover of another session's
    /// <c>editorSessionId</c> happen only after the journal verification of
    /// §F4/§F5 (§E2 test 2, takeover part).</para>
    /// <para><b>Who may take the exclusion.</b> At most one Run may be mutating
    /// (SPEC §E2 item 2), so at most one unfinished Run takes it, and the
    /// <c>lock.json</c> owner record arbitrates when more than one journal claims
    /// one. Each shape of that record that does not reconcile with the journals and
    /// <c>index.json</c> is a mismatch and ends NeedsReview with the record left in
    /// place: unreadable; naming a Run with no verifiable journal (the
    /// "lock.json だけ残っている" case of §F4); naming a Run whose journal proves it
    /// finished while another Run's journal is still unfinished; naming a Run the
    /// journal shows holds no exclusion; or leaving two unfinished mutating Runs,
    /// whether or not a record names one of them — a record that is itself one of
    /// two claimants arbitrates nothing, so the contradiction fails closed over all
    /// of them. A mismatched record is never overwritten —
    /// it is the residue that keeps <see cref="RunCoordinator.TryStartNext"/>
    /// answering OW-RUN-002 for new mutating Runs until a human decision ends the
    /// recorded Run, which is the fail-closed intent of §E2 item 3 ("二重実行はし
    /// ない"). Only a record the journals positively disprove (its Run is
    /// terminal) is treated as stale and emptied.</para>
    /// <para><b>The exclusion is handed over, never duplicated.</b> The pass offers
    /// its own handle to
    /// <see cref="RunCoordinator.AdoptRecoveredRun(RunSnapshot, CoordinatorRunLock?)"/>
    /// and the coordinator owns it from then on — §F4 item 6's hand-over, where the
    /// taken-over Run keeps the exclusion while <c>lock.json</c> is re-recorded with
    /// this Editor's session inside the held critical section. A handle the
    /// coordinator did not attach is still owned by this pass, which drops it
    /// through <see cref="CoordinatorRunLock.Dispose"/> (abandoned state: the
    /// recorded owner survives for the recovery pass that follows) except for the
    /// one case a journal positively proved stale, where
    /// <see cref="CoordinatorRunLock.Release"/> empties it. A pass never keeps an
    /// exclusion it did not hand over — that would block every other Editor for
    /// nothing.</para>
    /// <para><b><c>editorSessionId</c> is never proof</b> (AGENTS invariant,
    /// SPEC §E2 item 3, §F0): the pass never compares session ids to decide
    /// ownership. Adoption of the handle the previous domain persisted, or a fresh
    /// OS grant of the exclusion, is what establishes that no other process can be
    /// mutating, and only the journal verification underneath it may resume a Run.
    /// SPEC §E2 item 3 (b) covers a lock method that cannot prove the process a
    /// recorded <c>editorSessionId</c> belongs to has ended: such a source is
    /// answered OW-RUN-002 with the recorded Run reported NeedsReview and the
    /// residue left in place, so neither that Run nor a new one runs.</para>
    /// <para><b>NeedsReview without a record.</b> SPEC §F2 has no run-level
    /// NeedsReview record — <c>step_needs_review</c> is the record that moves a Run
    /// there, and it belongs to the §F5 decision the resume driver makes for a
    /// step. A verdict this pass reaches BEFORE any Run's journal was driven (no
    /// exclusion, an unreadable <c>lock.json</c>, an unverifiable residue, a record
    /// that contradicts the journal) is therefore reported, not written: nothing
    /// durable is claimed, nothing is resumed, and the ticket's "NeedsReview or no
    /// action" is met by the report plus the untouched residue — the same shape
    /// T-W1.8-08's <see cref="RunCoordinator.AdoptRecoveredRun(RunSnapshot, CoordinatorRunLock?)"/>
    /// uses to report an undecodable Plan. A driver-reached NeedsReview is journaled
    /// by that driver exactly as T-W1.8-08 implements it.</para>
    /// <para><b>Single writer.</b> Like every Core type this runs on the Editor main
    /// thread only and takes no locks (SPEC §0.4).</para>
    /// </remarks>
    internal sealed class RunRecoveryEngine
    {
        // SPEC §F4 item 1's "未終了の Run": every non-terminal §E1 state. A
        // journaled NeedsReview Run belongs here — it is not finished, SPEC §F8
        // forbids deleting it, and re-attaching it is what makes the human
        // ResolveReview decision reachable again after a reload.
        private static readonly IReadOnlyList<RunState> UnfinishedStates = new[]
        {
            RunState.Queued,
            RunState.AwaitingApproval,
            RunState.Running,
            RunState.Suspended,
            RunState.NeedsReview,
        };

        private readonly RunCoordinator _coordinator;
        private readonly RunLoop _loop;
        private readonly IRunStore _store;
        private readonly Func<Result<CoordinatorRunLock>> _lockSource;
        private readonly Func<bool> _previousOwnerTerminationProven;

        /// <summary>
        /// Creates the engine over the Runner objects of one Editor session and two
        /// injected seams. <paramref name="lockSource"/> obtains the exclusion —
        /// the Unity wiring confirms the handle the previous domain persisted
        /// first and falls back to a fresh acquisition (SPEC §E2 item 3's reload
        /// and restart paths); a source that reports failure means a live owner
        /// holds it. <paramref name="previousOwnerTerminationProven"/> answers
        /// whether that lock method can prove the process a recorded
        /// <c>editorSessionId</c> belongs to has ended; the handle-held exclusion of
        /// SPEC §E2 item 3 does (an OS grant is only possible once the holder is
        /// gone, proven per OS by the L-14 contract tests), a method that cannot
        /// must answer false and the takeover of a recorded owner then fails closed
        /// (§E2 item 3 (b)).
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal RunRecoveryEngine(
            RunCoordinator coordinator,
            RunLoop loop,
            IRunStore store,
            Func<Result<CoordinatorRunLock>> lockSource,
            Func<bool> previousOwnerTerminationProven)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _loop = loop ?? throw new ArgumentNullException(nameof(loop));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _lockSource = lockSource ?? throw new ArgumentNullException(nameof(lockSource));
            _previousOwnerTerminationProven =
                previousOwnerTerminationProven ??
                throw new ArgumentNullException(nameof(previousOwnerTerminationProven));
        }

        /// <summary>
        /// The first-delayCall recovery pass of SPEC §F4 item 1, registered after
        /// the Registry rebuild so the Operation lookups the resume performs see a
        /// verified snapshot. <paramref name="previousOwnedRunId"/> is the Run the
        /// previous domain of this Editor process held the exclusion for, when the
        /// wiring preserved which one that was — the only way SPEC §E2 item 3 (a)'s
        /// "自分の Run" can be identified once the managed state died with the
        /// reload.
        /// </summary>
        internal Result<RunRecoveryReport> RecoverAtStartup(string? previousOwnedRunId)
        {
            // A Run this domain already drives owns the exclusion, and the handle
            // the wiring persisted for the reload IS that handle: a second managed
            // owner for one OS exclusion could close it through the wrong one, so a
            // re-entered pass does nothing.
            if (_coordinator.HoldsAnyRunLock())
            {
                return Result<RunRecoveryReport>.Success(
                    new RunRecoveryReport(
                        true, Array.Empty<RunRecoveryResult>(), Array.Empty<Diagnostic>()));
            }

            var acquired = _lockSource();
            if (!acquired.Ok || acquired.Value is not { } held)
            {
                return Result<RunRecoveryReport>.Success(
                    WithoutExclusion(acquired.Diagnostics, previousOwnedRunId));
            }

            // Inside the critical section from here on (SPEC §E2 item 3):
            // lock.json here, and the unfinished set through IRunStore.List, which
            // derives membership and state from the journals — index.json alone is
            // a cache and a stale cache would hide exactly the Runs recovery must
            // find (SPEC §F1/§F3).
            var recorded = held.ReadState();
            var unfinished = _store.List(new RunQuery(UnfinishedStates));
            if (!unfinished.Ok || unfinished.Value is not { } entries)
            {
                // The unfinished set cannot be established: the exclusion is
                // dropped with the recorded state exactly as found — the pass may
                // neither resume on an unproven picture nor erase a record it never
                // reconciled.
                held.Dispose();
                return Result<RunRecoveryReport>.Failure(unfinished.Diagnostics);
            }

            var inactive = new List<string>();
            var mutating = new List<string>();
            foreach (var entry in entries)
            {
                if (entry.State == RunState.Queued || entry.State == RunState.AwaitingApproval)
                {
                    inactive.Add(entry.RunId);
                }
                else
                {
                    mutating.Add(entry.RunId);
                }
            }

            var diagnostics = new List<Diagnostic>();
            var results = new List<RunRecoveryResult>();

            // The recorded owner arbitrates which unfinished Run may take the
            // exclusion; every shape of it that does not reconcile with the
            // journals is a mismatch that ends the pass NeedsReview, record intact.
            var takeover = (string?)null;
            var takeoverRecorded = false;
            var residueProvenStale = false;
            var unreconciled = false;
            if (!recorded.Ok)
            {
                // lock.json is not a readable lock state — the recorded owner is
                // unknown, so nothing may be taken over and nothing may be recorded
                // over it (SPEC §E2 item 3: a corrupt lock.json fails closed).
                diagnostics.AddRange(recorded.Diagnostics);
                diagnostics.Add(Unreconciled(
                    "The recorded Run lock state is not readable; no Run is taken over."));
                unreconciled = true;
            }
            else if (recorded.Value is { } residue)
            {
                var claimed = LoadRecordedRun(residue.RunId);
                if (!claimed.Ok || claimed.Value is null)
                {
                    // lock.json-only residue: the Run it names has no journal to
                    // verify, so it is neither resumed nor recorded over — SPEC §F4
                    // item 1's "lock.json だけが残っている場合も同じ".
                    diagnostics.AddRange(claimed.Diagnostics);
                    diagnostics.Add(Unreconciled(
                        "The Run recorded in the lock state has no verifiable journal; it is not taken over."));
                    results.Add(new RunRecoveryResult(
                        residue.RunId, RunRecoveryOutcome.NeedsReview, null));
                    unreconciled = true;
                }
                else if (IsTerminal(claimed.Value.State))
                {
                    // The journal proves the recorded Run finished: the record is
                    // stale residue, not a live owner. A still-unfinished mutating
                    // Run alongside it contradicts the record — the crash-during-
                    // update case of SPEC §E2 item 3, which reconciles to
                    // NeedsReview and is never double-executed.
                    if (mutating.Count > 0)
                    {
                        diagnostics.Add(Unreconciled(
                            "The recorded Run lock state contradicts the Run journals; no Run is taken over."));
                        unreconciled = true;
                    }
                    else
                    {
                        residueProvenStale = true;
                    }
                }
                else if (mutating.Contains(residue.RunId))
                {
                    takeover = residue.RunId;
                    takeoverRecorded = true;
                    mutating.Remove(residue.RunId);
                }
                else
                {
                    // The record claims a Run the journal shows holds no exclusion
                    // (Queued/AwaitingApproval) as its owner: contradictory
                    // persisted state, answered the same safe way — no takeover, the
                    // record stays, and new mutating Runs keep failing closed until
                    // a human decision ends the recorded Run.
                    diagnostics.Add(Unreconciled(
                        "The recorded Run lock state contradicts the Run journal; no Run is taken over."));
                    results.Add(new RunRecoveryResult(
                        residue.RunId, RunRecoveryOutcome.NeedsReview, claimed.Value.State));
                    inactive.Remove(residue.RunId);
                    unreconciled = true;
                }
            }

            // With no record to arbitrate, a single unfinished mutating Run takes
            // the exclusion this pass holds; more than one contradicts SPEC §E2
            // item 2, so none of them is resumed.
            if (takeover is null && !unreconciled && mutating.Count == 1)
            {
                takeover = mutating[0];
                mutating.RemoveAt(0);
            }

            // A record naming one unfinished mutating Run does not arbitrate a
            // second unfinished journal that also claims a mutating state: here the
            // record is not the arbiter, it is one of the claimants, which is the
            // same §E2 item 2 contradiction seen when no record exists at all. The
            // candidate therefore goes back into the unresolved set so the whole of
            // the contradictory state fails closed — the exclusion passes to none of
            // them, none is resumed or taken over, each ends NeedsReview, and the
            // record stays for the human decision (§E2 item 3; §F0 — an uncertain
            // recovery never ends in success).
            var contradictoryRecord = false;
            if (takeover is { } contradicted && mutating.Count > 0)
            {
                mutating.Insert(0, contradicted);
                takeover = null;
                contradictoryRecord = true;
            }

            if (takeover is null && mutating.Count > 0)
            {
                var unresolved = contradictoryRecord
                    ? "The recorded Run lock state names an unfinished mutating Run while another unfinished Run also claims a mutating state; no Run is taken over."
                    : "The unfinished Run cannot be reconciled with the recorded lock state; it is not resumed.";
                foreach (var runId in mutating)
                {
                    diagnostics.Add(Unreconciled(unresolved));
                    results.Add(new RunRecoveryResult(
                        runId, RunRecoveryOutcome.NeedsReview, StateOfJournalRun(runId)));
                }
                held.Dispose();
                return Result<RunRecoveryReport>.Success(
                    new RunRecoveryReport(true, results, diagnostics));
            }

            // SPEC §E2 item 3 (b): even holding the exclusion, a recorded owner
            // whose owning process cannot be proven to have ended is neither
            // resumed nor replaced by a new Run — the record stays, which is what
            // makes the start gate answer OW-RUN-002.
            if (takeover is { } blocked && takeoverRecorded &&
                !_previousOwnerTerminationProven())
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_002,
                    "The process that recorded the Run lock cannot be confirmed to have ended; the Run is not resumed."));
                results.Add(new RunRecoveryResult(
                    blocked, RunRecoveryOutcome.NeedsReview, StateOfJournalRun(blocked)));
                held.Dispose();
                return Result<RunRecoveryReport>.Success(
                    new RunRecoveryReport(true, results, diagnostics));
            }

            // Queued and AwaitingApproval Runs hold no exclusion (SPEC §E2): they
            // are re-attached so the start gate and the human approval decision
            // find them again after the reload, and nothing is driven for them.
            foreach (var runId in inactive)
            {
                var reattached = _loop.Resume(runId);
                diagnostics.AddRange(reattached.Diagnostics);
                if (!reattached.Ok || reattached.Value is not { } handle)
                {
                    results.Add(new RunRecoveryResult(
                        runId, RunRecoveryOutcome.NeedsReview, _coordinator.StateOf(runId)));
                    continue;
                }
                results.Add(new RunRecoveryResult(
                    handle.RunId, OutcomeFor(handle.State), handle.State));
            }

            // The one Run the exclusion passes to: the §F4 driver verifies the Plan
            // and journal (item 2), decides the last step record through §F5
            // (item 5) and journals run_resumed while the taken-over exclusion is
            // held and lock.json is re-recorded for this session (item 6).
            var attached = false;
            if (takeover is { } resumedId)
            {
                var resumed = _loop.Resume(resumedId, held);
                diagnostics.AddRange(resumed.Diagnostics);
                if (resumed.Ok && resumed.Value is { } handle)
                {
                    results.Add(new RunRecoveryResult(
                        handle.RunId, OutcomeFor(handle.State), handle.State));
                }
                else
                {
                    // The driver refused the resume — a store refusal or the
                    // adoption's own fail-closed answer. The Run stays unfinished
                    // for the human path and the exclusion is dropped below with the
                    // recorded state left as found.
                    results.Add(new RunRecoveryResult(
                        resumedId, RunRecoveryOutcome.NeedsReview, _coordinator.StateOf(resumedId)));
                }
                attached = _coordinator.HoldsRunLock(resumedId, held);
            }
            if (!attached)
            {
                // Only a record the journals positively disproved may be emptied,
                // and only when no Run took the exclusion over; everything else
                // keeps the residue that blocks new mutating Runs. A failed
                // emptying falls back to the abandoned-state drop.
                if (residueProvenStale && takeover is null)
                {
                    if (!held.Release().Ok)
                    {
                        held.Dispose();
                    }
                }
                else
                {
                    held.Dispose();
                }
            }

            return Result<RunRecoveryReport>.Success(
                new RunRecoveryReport(true, results, diagnostics));
        }

        /// <summary>
        /// The Edit-Mode re-entry reconciliation of SPEC §1.4 and §E1: returning to
        /// Edit Mode without a domain reload leaves the managed Runner state alive
        /// with the Run journaled Suspended and its exclusion still held, so this
        /// runs the same §F4 resume over the tracked Suspended Run — the driver
        /// re-applies the SPEC §E3 item 4 Editor-state gate and, for a Run
        /// interrupted mid-step, the §F5 table. A Run the gate still holds stays
        /// Suspended; nothing here takes a second exclusion.
        /// </summary>
        internal Result<RunRecoveryReport> ReconcileSuspendedRun()
        {
            var active = _coordinator.ActiveHandle();
            if (active is not { } handle || handle.State != RunState.Suspended)
            {
                return Result<RunRecoveryReport>.Success(
                    new RunRecoveryReport(
                        _coordinator.HoldsAnyRunLock(),
                        Array.Empty<RunRecoveryResult>(),
                        Array.Empty<Diagnostic>()));
            }
            var resumed = _loop.Resume(handle.RunId);
            if (!resumed.Ok || resumed.Value is not { } resumedHandle)
            {
                return Result<RunRecoveryReport>.Failure(resumed.Diagnostics);
            }
            return Result<RunRecoveryReport>.Success(new RunRecoveryReport(
                _coordinator.HoldsAnyRunLock(),
                new[]
                {
                    new RunRecoveryResult(
                        resumedHandle.RunId, OutcomeFor(resumedHandle.State), resumedHandle.State),
                },
                resumed.Diagnostics));
        }

        // The SPEC §E2 item 3 (a) answer: the exclusion is held elsewhere, so this
        // pass resumes nothing. The Run the previous domain of this process owned —
        // when the wiring preserved which one that was — is reported NeedsReview and
        // is neither resumed nor journaled: without the exclusion this process may
        // not mutate a Run another owner could still be driving.
        private static RunRecoveryReport WithoutExclusion(
            IReadOnlyList<Diagnostic> lockDiagnostics, string? previousOwnedRunId)
        {
            var diagnostics = new List<Diagnostic>(lockDiagnostics);
            if (diagnostics.Count == 0)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_002,
                    "The Run lock is held by a live owner; a live owner's lock is never taken."));
            }
            var results = new List<RunRecoveryResult>();
            if (!string.IsNullOrEmpty(previousOwnedRunId))
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_002,
                    "The Run lock could not be re-acquired after the reload; this Editor's Run is not resumed."));
                results.Add(new RunRecoveryResult(
                    previousOwnedRunId!, RunRecoveryOutcome.NeedsReview, RunState.NeedsReview));
            }
            return new RunRecoveryReport(false, results, diagnostics);
        }

        // SPEC §E2 item 3: an unreconciled recorded lock state is never taken over
        // and never overwritten. OW-STORE-002 — the persisted Run Store state does
        // not reconcile with the journals that are the single truth.
        private static Diagnostic Unreconciled(string message) =>
            ErrorCodes.Create(ErrorCodes.OW_STORE_002, message);

        // The recorded runId of lock.json is untrusted persisted state: a value
        // outside the run identifier grammar makes IRunStore.Load throw
        // ArgumentException, which is corrupt recorded state (OW-STORE-002) and
        // fails closed exactly like an unloadable journal.
        private Result<RunSnapshot> LoadRecordedRun(string runId)
        {
            try
            {
                return _store.Load(runId);
            }
            catch (ArgumentException)
            {
                return Result<RunSnapshot>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_002,
                    "The recorded Run identifier is not a run identifier."));
            }
        }

        // The journal's own state for a Run this pass found unfinished — tracked
        // state once re-attached, the reconstructed state otherwise, null when the
        // journal cannot be read at all.
        private RunState? StateOfJournalRun(string runId)
        {
            if (_coordinator.StateOf(runId) is { } tracked)
            {
                return tracked;
            }
            var loaded = LoadRecordedRun(runId);
            return loaded.Ok && loaded.Value is { } snapshot ? snapshot.State : null;
        }

        private static RunRecoveryOutcome OutcomeFor(RunState state) =>
            state switch
            {
                RunState.Suspended => RunRecoveryOutcome.Suspended,
                RunState.NeedsReview => RunRecoveryOutcome.NeedsReview,
                RunState.Queued => RunRecoveryOutcome.Reattached,
                RunState.AwaitingApproval => RunRecoveryOutcome.Reattached,
                _ => RunRecoveryOutcome.Resumed,
            };

        private static bool IsTerminal(RunState state) =>
            state == RunState.Succeeded || state == RunState.Failed || state == RunState.Cancelled;
    }
}
