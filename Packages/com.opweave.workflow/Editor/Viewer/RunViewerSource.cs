#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;

namespace OpWeave.Viewer
{
    /// <summary>
    /// The Runs-tab read model of SPEC §H5 item 1: lists the Run Store's
    /// Runs, rebuilds one Run's detail from the journal truth (SPEC §F1)
    /// and issues the human's cancel request through the coordinator — the
    /// single journal writer (SPEC §E6 item 2). The SPEC §E8 event surface
    /// the window subscribes to is exposed as <see cref="Events"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The step list's "所要時間" (elapsed time) is a display-side measure:
    /// the latest attempt's <c>step_started</c> timestamp to the step's
    /// latest journal record — or to now while the step is still Running or
    /// Waiting. The retry bookkeeping stays in the attempt count; this is a
    /// documented local choice, not a contract.
    /// </para>
    /// <para>
    /// The change list always comes from
    /// <see cref="JournaledChanges"/> — the durable half of SPEC §F7 — for a
    /// running and a finished Run alike: <c>changes.json</c> is only the
    /// projection finalized at Run end, the journaled elements are the same
    /// elements it holds.
    /// </para>
    /// </remarks>
    internal sealed class RunViewerSource
    {
        /// <summary>
        /// The §E6 cancel reason the window's Cancel button journals —
        /// a fixed English word, like the Agent surface's
        /// <c>cancel_workflow</c>.
        /// </summary>
        internal const string CancelReason = "run_viewer";

        private readonly OpWeaveRunnerHost _host;
        private readonly string _runStoreRoot;
        private readonly IClock _clock;

        /// <summary>
        /// Binds the source to the runner host whose Run Store lives under
        /// <paramref name="runStoreRoot"/>. <paramref name="clock"/> supplies
        /// "now" for the live duration of a still-running step — the same
        /// clock the host journals with, so the measure and the records share
        /// one time base.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="host"/> or <paramref name="clock"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="runStoreRoot"/> is null or empty.
        /// </exception>
        internal RunViewerSource(OpWeaveRunnerHost host, string runStoreRoot, IClock clock)
        {
            if (host is null)
            {
                throw new ArgumentNullException(nameof(host));
            }
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.",
                    nameof(runStoreRoot));
            }
            _host = host;
            _runStoreRoot = runStoreRoot;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// The production binding of <paramref name="host"/> — the domain's
        /// installed runner host — or null when none is installed (the
        /// window then shows the empty state).
        /// </summary>
        internal static RunViewerSource? ForHost(OpWeaveRunnerHost? host) =>
            host is null
                ? null
                : new RunViewerSource(host, host.RunStoreRoot, new SystemUtcClock());

        /// <summary>The SPEC §E8 dispatcher the window's refresh subscribes to.</summary>
        internal IRunEvents Events => _host.Events;

        /// <summary>
        /// The SPEC §H5 item 3 notification hook of the approval service —
        /// fired once per Run that enters AwaitingApproval, carrying the
        /// detail the approval panel shows. The window surfaces it as a
        /// notification; <see cref="RunViewerApprovalNotifier"/> opens the
        /// window for it when none is open.
        /// </summary>
        internal event Action<RunApprovalDetail>? ApprovalPending
        {
            add => _host.Approvals.ApprovalPending += value;
            remove => _host.Approvals.ApprovalPending -= value;
        }

        /// <summary>The clock's current instant — the live-duration time base.</summary>
        internal DateTimeOffset Now => _clock.UtcNow;

        /// <summary>
        /// The Run Store's index (SPEC §F1): the journal-derived entries of
        /// every Run, newest or oldest exactly as the store reports. A store
        /// failure reads as an empty list — the window's status line keeps
        /// the detail view honest rather than inventing Runs.
        /// </summary>
        internal IReadOnlyList<RunIndexEntry> ListRuns()
        {
            var listed = _host.Store.List(new RunQuery());
            return listed.Ok ? listed.Value! : Array.Empty<RunIndexEntry>();
        }

        /// <summary>
        /// The SPEC §E6 item 2 cancel request of the window's Cancel button:
        /// the coordinator journals <c>cancel_requested</c> and the Run ends
        /// through the §E6 paths — never a direct state write from the UI.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="runId"/> is null.</exception>
        internal Result<RunHandle> Cancel(string runId) =>
            _host.Coordinator.Cancel(runId, CancelReason);

        /// <summary>
        /// The approval panel's Approve action (SPEC §H4 item 3, §H5 item
        /// 1): the decision goes through the human approval service — the
        /// only entry point allowed to journal <c>approval_granted</c> —
        /// bound to the planHash the panel presented.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal Result<RunHandle> Approve(string runId, string planHash) =>
            _host.Approvals.Approve(runId, planHash);

        /// <summary>
        /// The approval panel's Reject action (SPEC §H4 item 3, §H5 item
        /// 1): <c>approval_rejected</c> through the same human approval
        /// service, ending the Run Cancelled with OW-PERM-003.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal Result<RunHandle> Reject(string runId, string planHash, string reason) =>
            _host.Approvals.Reject(runId, planHash, reason);

        /// <summary>
        /// The review panel's human decision (SPEC §E2, §H5 item 1):
        /// <see cref="ReviewDecision.RetryStep"/> re-runs the reviewed step
        /// (<c>review_resolved</c>, NeedsReview → Running),
        /// <see cref="ReviewDecision.Abort"/> ends the Run Cancelled — the
        /// Core human-only API is the single <c>review_resolved</c> writer.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="runId"/> is null.</exception>
        internal Result<RunHandle> ResolveReview(string runId, ReviewDecision decision) =>
            _host.Coordinator.ResolveReview(runId, decision);

        /// <summary>
        /// The detail pane's model for one Run: the journal-folded snapshot
        /// (SPEC §F1) joined with the journal scan's step timing and log
        /// rows and the §F7 journaled change list. The load's diagnostics
        /// are kept so a Run Store integrity failure that itself put the
        /// Run in NeedsReview can be named as the review reason (SPEC §H5
        /// item 1). Null when the store cannot produce a snapshot for
        /// <paramref name="runId"/> (an unknown Run or a store failure).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="runId"/> is null.</exception>
        internal RunViewerDetail? DetailOf(string runId)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            var loaded = _host.Coordinator.LoadRun(runId);
            if (!loaded.Ok || loaded.Value is not { } snapshot)
            {
                return null;
            }
            var journalPath = JournaledChanges.PathOf(_runStoreRoot, runId);
            var scan = RunJournalScan.Read(journalPath);
            var changes = JournaledChanges.Read(journalPath, out var changeFailure);
            // SPEC §H4 item 3: while the Run awaits the human, the approval
            // panel renders the service's detail — the per-node effects and
            // the planHash the decision binds. DetailOf itself returns null
            // once the Run leaves AwaitingApproval.
            var approval = _host.Approvals.DetailOf(runId);
            return BuildDetail(
                snapshot, scan, changes, approval, scan.Failure ?? changeFailure,
                loaded.Diagnostics);
        }

        private RunViewerDetail BuildDetail(
            RunSnapshot snapshot,
            RunJournalScan scan,
            List<ChangeRecord> changes,
            RunApprovalDetail? approval,
            string? journalWarning,
            IReadOnlyList<Diagnostic> loadDiagnostics)
        {
            var recorded = new Dictionary<int, StepSnapshot>();
            foreach (var step in snapshot.Steps)
            {
                recorded[step.Index] = step;
            }
            var steps = new List<RunViewerStepRow>();
            if (snapshot.Plan is { } plan)
            {
                // The Plan lists every step — ones with no journal record
                // stay Pending — in D5 execution order, with the node id and
                // Operation identity the row shows.
                foreach (var step in plan.Steps)
                {
                    recorded.TryGetValue(step.Index, out var state);
                    steps.Add(StepRowOf(
                        step.Index, step.NodeId, step.OperationId, step.Version,
                        state, scan));
                }
            }
            else
            {
                // plan.json could not be decoded (the snapshot is then
                // NeedsReview): only the journaled steps can be listed —
                // the row keeps its index and drops the identity members it
                // cannot know.
                foreach (var step in snapshot.Steps)
                {
                    steps.Add(StepRowOf(step.Index, null, null, null, step, scan));
                }
            }
            return new RunViewerDetail(
                snapshot.RunId,
                snapshot.State,
                snapshot.Plan?.WorkflowId,
                snapshot.CreatedAt,
                snapshot.UpdatedAt,
                snapshot.CancelPending,
                snapshot.CancelReason,
                steps,
                scan.Logs,
                changes,
                approval,
                ReviewOf(snapshot, scan, steps, loadDiagnostics),
                journalWarning);
        }

        // The NeedsReview panel's reason is the cause that put the Run in
        // its current NeedsReview state (SPEC §H5 item 1). The Run Store's
        // own cause wins: the only error-severity diagnostic a successful
        // Load reports is the OW-STORE-002 integrity failure that moved
        // the Run to NeedsReview (SPEC §F1, §F3) — it, not an earlier
        // step_needs_review record, names the reason. A load diagnostic
        // that did not move the state — the dropped-tail OW-STORE-002 of
        // SPEC §F3 item 3 is reported as a Warning — can never be the
        // reason. Otherwise the journal's latest step_needs_review record
        // names the step — with the Plan's node id when plan.json decoded,
        // the step index alone when it could not — and when neither source
        // gives a reason the panel reports the reason as unknown.
        internal static RunViewerReview? ReviewOf(
            RunSnapshot snapshot,
            RunJournalScan scan,
            IReadOnlyList<RunViewerStepRow> steps,
            IReadOnlyList<Diagnostic> loadDiagnostics)
        {
            if (snapshot.State == RunState.NeedsReview)
            {
                foreach (var diagnostic in loadDiagnostics)
                {
                    if (diagnostic.Severity == Severity.Error)
                    {
                        return new RunViewerReview(
                            RunViewerReviewSource.StoreIntegrity,
                            null, null, diagnostic.Code, diagnostic.Message);
                    }
                }
            }
            if (scan.LatestReview is not { } review)
            {
                return null;
            }
            string? nodeId = null;
            if (review.Step is { } index)
            {
                foreach (var step in steps)
                {
                    if (step.Index == index)
                    {
                        nodeId = step.NodeId;
                        break;
                    }
                }
            }
            return new RunViewerReview(
                RunViewerReviewSource.StepJournal,
                review.Step, nodeId, review.Error, review.Message);
        }

        private RunViewerStepRow StepRowOf(
            int index,
            string? nodeId,
            string? operationId,
            int? version,
            StepSnapshot? recorded,
            RunJournalScan scan)
        {
            var state = recorded?.State ?? StepState.Pending;
            var attempt = recorded is { Attempt: > 0 } ? recorded.Attempt : (int?)null;
            scan.Steps.TryGetValue(index, out var journal);
            var live = state is StepState.Running or StepState.Waiting;
            DateTimeOffset? liveSince = null;
            TimeSpan? duration = null;
            if (journal is not null &&
                state is not (StepState.Pending or StepState.Skipped))
            {
                var started = StartTsOf(journal, attempt);
                var end = live ? (DateTimeOffset?)_clock.UtcNow : Parse(journal.LastTs);
                if (started is { } start && end is { } stop && stop >= start)
                {
                    duration = stop - start;
                }
                if (live)
                {
                    liveSince = started;
                }
            }
            return new RunViewerStepRow(
                index, nodeId, operationId, version, state, attempt, duration, live,
                liveSince);
        }

        // The elapsed-time base of the step's latest attempt: the
        // step_started of the attempt the snapshot reports, else of the
        // highest attempt the journal opened.
        private static DateTimeOffset? StartTsOf(
            RunJournalScan.StepJournal step, int? attempt)
        {
            if (attempt is { } a &&
                step.StartedByAttempt.TryGetValue(a, out var ts))
            {
                return Parse(ts);
            }
            if (step.LatestStartedAttempt is { } latest &&
                step.StartedByAttempt.TryGetValue(latest, out var latestTs))
            {
                return Parse(latestTs);
            }
            return null;
        }

        // §F2 timestamps are the §0.4 "yyyy-MM-ddTHH:mm:ss.fffZ" UTC form; an
        // unparseable one is no base at all rather than a wrong duration.
        private static DateTimeOffset? Parse(string? ts) =>
            ts is not null &&
            DateTimeOffset.TryParse(
                ts, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
    }
}
