#nullable enable

using System.Collections.Generic;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;

namespace OpWeave.Viewer
{
    /// <summary>
    /// The read model behind the SPEC §H5 Runs-tab detail pane of one Run:
    /// the journal-folded state (SPEC §F1 — the single source of truth)
    /// together with the step, log and change lists the window renders.
    /// </summary>
    internal sealed class RunViewerDetail
    {
        internal RunViewerDetail(
            string runId,
            RunState state,
            string? workflowId,
            string? createdAt,
            string? updatedAt,
            bool cancelPending,
            string? cancelReason,
            IReadOnlyList<RunViewerStepRow> steps,
            IReadOnlyList<RunViewerLogRow> logs,
            IReadOnlyList<ChangeRecord> changes,
            RunApprovalDetail? approval,
            RunViewerReview? review,
            string? journalWarning)
        {
            RunId = runId;
            State = state;
            WorkflowId = workflowId;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            CancelPending = cancelPending;
            CancelReason = cancelReason;
            Steps = steps;
            Logs = logs;
            Changes = changes;
            Approval = approval;
            Review = review;
            JournalWarning = journalWarning;
        }

        /// <summary>The Run identifier.</summary>
        internal string RunId { get; }

        /// <summary>The Run state the journal records end in (SPEC §E1).</summary>
        internal RunState State { get; }

        /// <summary>The workflow id of the Run's Plan, or null when it cannot be read.</summary>
        internal string? WorkflowId { get; }

        /// <summary>The RunAccepted timestamp, or null when none was folded.</summary>
        internal string? CreatedAt { get; }

        /// <summary>The timestamp of the last folded journal record, or null.</summary>
        internal string? UpdatedAt { get; }

        /// <summary>A cancel request the journal has not yet followed (SPEC §E6 item 2).</summary>
        internal bool CancelPending { get; }

        /// <summary>The journaled reason of the pending cancel request, or null.</summary>
        internal string? CancelReason { get; }

        /// <summary>The step rows in Plan order.</summary>
        internal IReadOnlyList<RunViewerStepRow> Steps { get; }

        /// <summary>The journaled log rows in journal order.</summary>
        internal IReadOnlyList<RunViewerLogRow> Logs { get; }

        /// <summary>The Run's journaled change elements (SPEC §F7).</summary>
        internal IReadOnlyList<ChangeRecord> Changes { get; }

        /// <summary>
        /// The §H4 item 3 approval detail the approval panel renders while
        /// the Run is AwaitingApproval — the per-node effects and details
        /// and the planHash the decision binds — or null when the Run is
        /// not awaiting a decision.
        /// </summary>
        internal RunApprovalDetail? Approval { get; }

        /// <summary>
        /// The NeedsReview reason of the cause that put the Run in its
        /// current NeedsReview state — the Run Store's load-time integrity
        /// diagnostic or the latest <c>step_needs_review</c> record — or
        /// null when neither source names one. The review panel is offered
        /// on the Run's NeedsReview state alone — the reason is display,
        /// never a decision input.
        /// </summary>
        internal RunViewerReview? Review { get; }

        /// <summary>
        /// Why the journal could not be read whole — the step, log or change
        /// lists are then provably partial — or null when it read clean.
        /// </summary>
        internal string? JournalWarning { get; }

        /// <summary>
        /// True while a Cancel request can still take effect: the Run is in
        /// one of the §E6 item 2 cancellable states (Queued, Running,
        /// Suspended) and no request is pending. Approval and review
        /// decisions belong to their own panels (T-W3.5-02), so
        /// AwaitingApproval and NeedsReview are not listed here.
        /// </summary>
        internal bool CanCancel =>
            !CancelPending &&
            (State == RunState.Queued ||
             State == RunState.Running ||
             State == RunState.Suspended);
    }
}
