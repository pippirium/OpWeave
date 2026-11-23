#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The human approval surface of SPEC §H4 item 3 (T-W3.4-02): the only
    /// Unity-side entry point a human approval UI (Run Viewer, T-W3.5-02) and
    /// the host's expiry sweep use to decide an AwaitingApproval Run. Every
    /// decision goes through the Core human-only API of
    /// <see cref="IRunCoordinator"/> (T-W1.8-09) — the single writer of the
    /// <c>approval_granted</c>/<c>approval_rejected</c> journal transitions;
    /// this service never writes the journal itself.
    /// </summary>
    /// <remarks>
    /// <para><b>planHash binding.</b> The human decides on the detail this
    /// service showed — the Plan's exact <see cref="RunApprovalDetail.PlanHash"/>,
    /// so <see cref="Approve"/> and <see cref="Reject"/> take the presented
    /// hash and refuse a stale one with OW-RUN-003 before deciding. A Run's
    /// Plan is immutable once accepted, so a granted approval can only ever
    /// cover the Plan it was shown; a re-executed workflow produces a new
    /// Plan with a new hash and asks again (SPEC §H4 test 2).</para>
    /// <para><b>Approver.</b> The journaled approver is the OS user name
    /// (SPEC §T.4 L-09, §H4 item 3) — <see cref="Environment.UserName"/> in
    /// production; the expiry sweep's rejection carries it too.</para>
    /// <para><b>Expiry.</b> <see cref="NotifyAwaiting"/> records the accept
    /// instant and the Run's admission-time
    /// <see cref="OpWeaveSettings.ApprovalTimeoutMinutes"/> (default 1440 —
    /// 24 h, SPEC §H4 item 3). The host's per-Tick
    /// <see cref="SweepExpired"/> ends an overdue Run through
    /// <see cref="IRunCoordinator.Reject"/> — Cancelled with OW-PERM-003, the
    /// same journal shape a human rejection produces; a decision attempted
    /// at or past the deadline applies the expiry rejection first and fails
    /// the attempt with OW-PERM-003.</para>
    /// <para><b>Non-interactive.</b> In batchmode no human can decide:
    /// <see cref="Approve"/> and <see cref="Reject"/> fail with OW-PERM-004 —
    /// and the <see cref="NonInteractiveApprovalGate"/> refuses such a Run at
    /// acceptance in the first place.</para>
    /// <para><b>Events.</b> <see cref="ApprovalPending"/> is the Run Viewer
    /// notification hook of SPEC §H5 item 3 — fired synchronously on the main
    /// thread when a Run enters AwaitingApproval, with the detail the screen
    /// shows; a subscriber's exception is logged and ignored (SPEC §E8).</para>
    /// </remarks>
    public sealed class OpWeaveApprovalService
    {
        /// <summary>
        /// The <c>approval_rejected</c> reason an expiry writes — fixed
        /// English (AGENTS conventions).
        /// </summary>
        internal const string ExpiredReason = "The approval timed out.";

        private readonly RunCoordinator _coordinator;
        private readonly Func<RegistrySnapshot> _currentRegistry;
        private readonly IClock _clock;
        private readonly Func<bool> _isBatchMode;
        private readonly Func<string> _osUserName;

        // The tracked AwaitingApproval Runs: acceptance order, expiry
        // material (accept instant + admission-time timeout), removed on a
        // decision or once the Run is no longer awaiting.
        private readonly List<PendingApproval> _pending = new List<PendingApproval>();

        // The Runs this service ended through the Core rejection path — a
        // human rejection or an expiry. Their recorded outcome is
        // OW-PERM-003 (SPEC §J: approval not obtained), so a decision
        // attempted on one afterwards answers with that code; a decision on
        // a Run in any other state is an untabled §E1 transition and throws
        // like the Core decision API.
        private readonly HashSet<string> _approvalEnded =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Creates the service over the Core decision API and the injected
        /// environment seams. <paramref name="currentRegistry"/> is re-read
        /// per detail build, like the runner host's own lookups;
        /// <paramref name="osUserName"/> supplies the journaled approver
        /// (SPEC §T.4 L-09: <see cref="Environment.UserName"/>).
        /// </summary>
        /// <exception cref="ArgumentNullException">Any seam is null.</exception>
        internal OpWeaveApprovalService(
            RunCoordinator coordinator,
            Func<RegistrySnapshot> currentRegistry,
            IClock clock,
            Func<bool> isBatchMode,
            Func<string> osUserName)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _currentRegistry =
                currentRegistry ?? throw new ArgumentNullException(nameof(currentRegistry));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _isBatchMode = isBatchMode ?? throw new ArgumentNullException(nameof(isBatchMode));
            _osUserName = osUserName ?? throw new ArgumentNullException(nameof(osUserName));
        }

        /// <summary>
        /// The SPEC §H5 item 3 notification hook: fired once — synchronously
        /// on the main thread — when a Run enters AwaitingApproval, carrying
        /// the detail the approval screen shows. Subscriber exceptions are
        /// logged and ignored (SPEC §E8); they never disturb the accepted
        /// Run. Subscriptions do not survive a domain reload; the Viewer
        /// re-registers on load.
        /// </summary>
        public event Action<RunApprovalDetail>? ApprovalPending;

        /// <summary>
        /// The awaiting Runs' details, in acceptance order — the pending
        /// list the approval surface renders. Entries whose Run was decided
        /// elsewhere (a rejection, the §E2 item 5 invalidation) are pruned.
        /// </summary>
        public IReadOnlyList<RunApprovalDetail> PendingApprovals
        {
            get
            {
                PruneDecided();
                var details = new List<RunApprovalDetail>(_pending.Count);
                foreach (var entry in _pending)
                {
                    var detail = DetailOf(entry.RunId);
                    if (detail is not null)
                    {
                        details.Add(detail);
                    }
                }
                return ReadOnlyCollections.CopyList(details);
            }
        }

        /// <summary>
        /// The approval detail of one AwaitingApproval Run — the per-node
        /// effects, paths, full code, package identifiers and used
        /// custom/external Operations with their sources (SPEC §H4 item 3)
        /// — or null when the Run is unknown or not awaiting a decision.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="runId"/> is null.</exception>
        public RunApprovalDetail? DetailOf(string runId)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            var plan = _coordinator.PlanOf(runId);
            if (plan is null || _coordinator.StateOf(runId) != RunState.AwaitingApproval)
            {
                return null;
            }
            return RunApprovalDetail.Build(
                runId, plan, _currentRegistry(), DeadlineOf(runId));
        }

        /// <summary>
        /// Grants the Run's asks through the Core human-only API — the
        /// single <c>approval_granted</c> journal path — with the OS user
        /// name as approver (SPEC §T.4 L-09), then the Run queues
        /// (AwaitingApproval → Queued, SPEC §E1).
        /// </summary>
        /// <param name="expectedPlanHash">
        /// The planHash the approval surface presented: the decision binds
        /// exactly that Plan — a stale or wrong hash is refused with
        /// OW-RUN-003 and nothing is journaled.
        /// </param>
        /// <returns>
        /// The decision's result: Queued on success; OW-RUN-010 for an
        /// unknown Run, OW-PERM-004 in a non-interactive Editor,
        /// OW-PERM-003 when this approval workflow already ended the Run
        /// by rejection or expiry (a late attempt applies the expiry
        /// rejection first), and the Core decision's own outcome otherwise
        /// (an OW-RUN-011 invalidation still cancels the Run at the
        /// decisive moment).
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/> or <paramref name="expectedPlanHash"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The Run is not AwaitingApproval and this approval workflow did
        /// not already end it — the decision asks for a transition absent
        /// from the SPEC §E1 table, an implementation bug detected by
        /// throwing like the Core decision API (§E1: unlisted transitions
        /// are exceptions).
        /// </exception>
        public Result<RunHandle> Approve(string runId, string expectedPlanHash)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            if (expectedPlanHash is null)
            {
                throw new ArgumentNullException(nameof(expectedPlanHash));
            }
            var refused = CheckApprovable(runId, out var plan);
            if (refused is not null)
            {
                return refused.Value;
            }
            if (!string.Equals(expectedPlanHash, plan!.PlanHash, StringComparison.Ordinal))
            {
                return Result<RunHandle>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_003,
                    "The expectedPlanHash does not match the Run's Plan."));
            }
            var entry = PendingEntryOf(runId);
            if (entry is not null && _clock.UtcNow >= entry.DeadlineUtc)
            {
                return Expire(entry);
            }
            var approved = _coordinator.Approve(runId, _osUserName());
            if (approved.Ok)
            {
                RemovePending(runId);
            }
            return approved;
        }

        /// <summary>
        /// Refuses the Run's asks through the Core human-only API — the
        /// single <c>approval_rejected</c> journal path — with the OS user
        /// name as approver and <paramref name="reason"/> journaled; the Run
        /// ends Cancelled with OW-PERM-003 (SPEC §H4 item 3).
        /// </summary>
        /// <param name="expectedPlanHash">
        /// The planHash the approval surface presented — bound exactly as in
        /// <see cref="Approve"/>; a stale or wrong hash is refused with
        /// OW-RUN-003 and nothing is journaled.
        /// </param>
        /// <param name="reason">The human's rejection reason, journaled.</param>
        /// <returns>
        /// The decision's result: Cancelled with the OW-PERM-003 diagnostic
        /// on success; the same gate outcomes as <see cref="Approve"/>
        /// otherwise.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/>, <paramref name="expectedPlanHash"/> or
        /// <paramref name="reason"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The Run is not AwaitingApproval and this approval workflow did
        /// not already end it — the same untabled-transition detection
        /// <see cref="Approve"/> documents.
        /// </exception>
        public Result<RunHandle> Reject(string runId, string expectedPlanHash, string reason)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            if (expectedPlanHash is null)
            {
                throw new ArgumentNullException(nameof(expectedPlanHash));
            }
            if (reason is null)
            {
                throw new ArgumentNullException(nameof(reason));
            }
            var refused = CheckApprovable(runId, out var plan);
            if (refused is not null)
            {
                return refused.Value;
            }
            if (!string.Equals(expectedPlanHash, plan!.PlanHash, StringComparison.Ordinal))
            {
                return Result<RunHandle>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_003,
                    "The expectedPlanHash does not match the Run's Plan."));
            }
            var rejected = _coordinator.Reject(runId, _osUserName(), reason);
            if (rejected.Ok)
            {
                _approvalEnded.Add(runId);
                RemovePending(runId);
            }
            return rejected;
        }

        /// <summary>
        /// The runner host's accept-time notification (SPEC §H4 item 3):
        /// records the accept instant and the Run's admission-time
        /// <c>approvalTimeoutMinutes</c> for the expiry sweep, then raises
        /// <see cref="ApprovalPending"/> with the decision detail.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/> or <paramref name="settings"/> is null.
        /// </exception>
        internal void NotifyAwaiting(string runId, OpWeaveSettings settings)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            RemovePending(runId);
            _pending.Add(new PendingApproval(
                runId, _clock.UtcNow, settings.ApprovalTimeoutMinutes));
            var detail = DetailOf(runId);
            if (detail is null)
            {
                // The Run stopped awaiting between acceptance and the
                // notification — nothing to show.
                return;
            }
            var handler = ApprovalPending;
            if (handler is null)
            {
                return;
            }
            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<RunApprovalDetail>)subscriber)(detail);
                }
                catch (Exception exception)
                {
                    // SPEC §E8: a subscriber's exception is logged and
                    // ignored — it never disturbs the accepted Run.
                    Debug.LogException(exception);
                }
            }
        }

        /// <summary>
        /// The host's per-Tick expiry sweep (SPEC §H4 item 3): every tracked
        /// Run whose deadline passed is ended through
        /// <see cref="IRunCoordinator.Reject"/> — the single decision-journal
        /// path — with the OS user name and the timeout reason, so the Run
        /// finishes Cancelled with OW-PERM-003. Entries whose Run was
        /// decided elsewhere are pruned first.
        /// </summary>
        /// <returns>
        /// Success with the applied expiries' diagnostics, or failure when a
        /// rejection could not be applied — the affected Run then took the
        /// SPEC §F1 store-failure path inside the coordinator.
        /// </returns>
        internal Result<Unit> SweepExpired()
        {
            var diagnostics = new List<Diagnostic>();
            var failed = false;
            PruneDecided();
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var entry = _pending[i];
                if (_clock.UtcNow < entry.DeadlineUtc)
                {
                    continue;
                }
                var rejected = _coordinator.Reject(entry.RunId, _osUserName(), ExpiredReason);
                if (rejected.Ok)
                {
                    _approvalEnded.Add(entry.RunId);
                }
                _pending.RemoveAt(i);
                diagnostics.AddRange(rejected.Diagnostics);
                failed |= !rejected.Ok;
            }
            return failed
                ? Result<Unit>.Failure(diagnostics)
                : Result<Unit>.Success(Unit.Value, diagnostics);
        }

        // The shared decision gate: the Run must exist (OW-RUN-010), the
        // Editor must be interactive (OW-PERM-004 — a non-interactive Editor
        // can never produce the human decision) and the Run must be
        // awaiting. A decision on a Run this approval workflow already ended
        // by rejection or expiry answers with the Run's recorded outcome —
        // OW-PERM-003 (SPEC §J: approval not obtained). A decision on a Run
        // in any other state asks for a transition the SPEC §E1 table does
        // not contain: the service detects the same implementation bug the
        // Core decision API does — by throwing, before anything is journaled.
        // On admission the Run's Plan is the out value.
        private Result<RunHandle>? CheckApprovable(string runId, out CompiledPlan? plan)
        {
            plan = null;
            var state = _coordinator.StateOf(runId);
            if (state is null)
            {
                return Result<RunHandle>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_010, "The Run is unknown."));
            }
            if (_isBatchMode())
            {
                return Result<RunHandle>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_PERM_004,
                    "A human approval cannot be granted in a non-interactive Editor."));
            }
            if (state == RunState.AwaitingApproval)
            {
                plan = _coordinator.PlanOf(runId);
                return null;
            }
            if (_approvalEnded.Contains(runId))
            {
                return Result<RunHandle>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_PERM_003,
                    "The approval already ended: the Run was rejected or expired."));
            }
            // SPEC §E1: the Run table allows no transition for an approval
            // decision in this state — the inapplicable decision is an
            // implementation bug, detected by throwing (§E1: unlisted
            // transitions are exceptions) before anything is journaled or
            // changed, exactly like the Core decision API.
            throw new InvalidOperationException(
                $"An approval decision on a Run in state {state} asks for a " +
                "transition the SPEC E1 table does not contain.");
        }

        // The expiry path an overdue Approve takes: the Run is ended through
        // the same rejection the sweep applies — the approver never stamped
        // a dead Plan — and the late attempt itself fails with OW-PERM-003.
        private Result<RunHandle> Expire(PendingApproval entry)
        {
            var rejected = _coordinator.Reject(entry.RunId, _osUserName(), ExpiredReason);
            if (rejected.Ok)
            {
                _approvalEnded.Add(entry.RunId);
            }
            RemovePending(entry.RunId);
            return rejected.Ok
                ? Result<RunHandle>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_PERM_003,
                    "The approval timed out before it was granted."))
                : rejected;
        }

        private PendingApproval? PendingEntryOf(string runId)
        {
            foreach (var entry in _pending)
            {
                if (string.Equals(entry.RunId, runId, StringComparison.Ordinal))
                {
                    return entry;
                }
            }
            return null;
        }

        private DateTimeOffset? DeadlineOf(string runId) => PendingEntryOf(runId)?.DeadlineUtc;

        private void RemovePending(string runId) =>
            _pending.RemoveAll(
                entry => string.Equals(entry.RunId, runId, StringComparison.Ordinal));

        private void PruneDecided()
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_coordinator.StateOf(_pending[i].RunId) != RunState.AwaitingApproval)
                {
                    _pending.RemoveAt(i);
                }
            }
        }

        // The tracked AwaitingApproval bookkeeping: the accept instant and
        // the admission-time timeout produce the deadline; a zero or
        // negative timeout expires immediately.
        private sealed class PendingApproval
        {
            internal PendingApproval(string runId, DateTimeOffset awaitingSince, long timeoutMinutes)
            {
                RunId = runId;
                DeadlineUtc = awaitingSince +
                    (timeoutMinutes <= 0
                        ? TimeSpan.Zero
                        : TimeSpan.FromMinutes(
                            Math.Min(timeoutMinutes, (long)TimeSpan.MaxValue.TotalMinutes)));
            }

            internal string RunId { get; }
            internal DateTimeOffset DeadlineUtc { get; }
        }
    }
}
