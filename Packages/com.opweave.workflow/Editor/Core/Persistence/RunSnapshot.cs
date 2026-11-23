#nullable enable

using System.Collections.Generic;
using OpWeave.Core.Compiler;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The recorded state of a Run, reconstructed by
    /// <see cref="IRunStore.Load"/> purely from the append-only journal — the
    /// single source of truth (SPEC §F1, §F0) — after the integrity
    /// verification of SPEC §D0.1/§D7 item 6. Every member is what the journal
    /// records literally say: no transition rules are applied, completion is
    /// never inferred without an explicit record, and an integrity failure
    /// surfaces as <see cref="RunState.NeedsReview"/> rather than a guessed
    /// state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Plan"/> is the reverified <see cref="CompiledPlan"/> decoded
    /// from plan.json (digest, version, planHash and §0.6 SG-12 cross-checks
    /// all passed); it is null only when the document could not be decoded at
    /// all, in which case <see cref="State"/> is NeedsReview.
    /// </para>
    /// <para>
    /// <see cref="RecoveryDecisionRequired"/> is the "F5 decision required"
    /// flag: the recorded state leaves an incomplete side effect possible — a
    /// step whose last record is <c>StepStarted</c> or <c>StepWaiting</c>, a
    /// <c>ResourceAcquired</c> with no matching <c>ResourceReleased</c>, or a
    /// <c>CancelRequested</c> not yet processed — so recovery must go through
    /// the SPEC §F5 table (Reconcile/NeedsReview) instead of any automatic
    /// success. A broken last journal line that was dropped can only raise
    /// this flag, never hide it.
    /// </para>
    /// </remarks>
    public sealed class RunSnapshot
    {
        internal RunSnapshot(
            string runId,
            CompiledPlan? plan,
            RunState state,
            IReadOnlyList<StepSnapshot> steps,
            JsonValue? outputs,
            IReadOnlyList<RunResource> openResources,
            bool cancelPending,
            bool recoveryDecisionRequired,
            string? createdAt,
            string? updatedAt,
            string? session,
            long lastSeq,
            bool ended = false,
            int? lastStepIndex = null,
            int? reviewRetryStep = null,
            string? cancelReason = null,
            string? creationSession = null)
        {
            RunId = runId;
            Plan = plan;
            State = state;
            Steps = ReadOnlyCollections.CopyList(steps);
            Outputs = outputs;
            OpenResources = ReadOnlyCollections.CopyList(openResources);
            CancelPending = cancelPending;
            RecoveryDecisionRequired = recoveryDecisionRequired;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            Session = session;
            LastSeq = lastSeq;
            Ended = ended;
            LastStepIndex = lastStepIndex;
            ReviewRetryStep = reviewRetryStep;
            CancelReason = cancelReason;
            CreationSession = creationSession;
        }

        /// <summary>The Run identifier the snapshot was loaded under.</summary>
        public string RunId { get; }

        /// <summary>The reverified Plan, or null when plan.json was undecodable.</summary>
        public CompiledPlan? Plan { get; }

        /// <summary>The Run state the journal records end in (SPEC §E1).</summary>
        public RunState State { get; }

        /// <summary>
        /// The last recorded state of every step the journal mentions, ordered
        /// by step index (SPEC §F2). Steps with no record are absent and stay
        /// <see cref="StepState.Pending"/>.
        /// </summary>
        public IReadOnlyList<StepSnapshot> Steps { get; }

        /// <summary>
        /// The Run's outputs from the terminal <c>RunFinished</c> record, or
        /// null while no such record exists.
        /// </summary>
        public JsonValue? Outputs { get; }

        /// <summary>
        /// Run resources acquired and not yet released (SPEC §F2, §E6 item 4).
        /// </summary>
        public IReadOnlyList<RunResource> OpenResources { get; }

        /// <summary>
        /// A <c>CancelRequested</c> record the journal has not yet followed
        /// with the Run's terminal <c>RunFinished</c> (SPEC §E6 item 2).
        /// </summary>
        public bool CancelPending { get; }

        /// <summary>
        /// The journal leaves an incomplete side effect possible — the SPEC
        /// §F5 recovery decision (Reconcile or NeedsReview) is required and
        /// automatic success is forbidden (SPEC §F0, §F3 item 3).
        /// </summary>
        public bool RecoveryDecisionRequired { get; }

        /// <summary>The <c>ts</c> of the RunAccepted record, or null when none was folded.</summary>
        public string? CreatedAt { get; }

        /// <summary>The <c>ts</c> of the last folded journal record, or null when none.</summary>
        public string? UpdatedAt { get; }

        /// <summary>
        /// The Editor session id (<c>session</c>) of the last folded record —
        /// the value SPEC §F4 item 4 compares with the current session to
        /// detect an Editor restart. Null when no record was folded.
        /// </summary>
        public string? Session { get; }

        /// <summary>
        /// The <c>seq</c> of the last valid journal envelope — a Skipped
        /// unknown-optional record spends its position too (SPEC §F2); 0
        /// when none was read. The next append continues at
        /// <see cref="LastSeq"/> + 1.
        /// </summary>
        public long LastSeq { get; }

        /// <summary>
        /// True once the terminal <c>RunFinished</c> record was folded — the
        /// distinction between a journaled end decision (an
        /// <c>ApprovalRejected</c>/<c>ReviewResolved</c> abort can already
        /// fold to Cancelled) and a Run whose end is fully recorded: an
        /// unfinished end still owes step records, the SPEC §E6 item 4
        /// resource release and the terminal record itself.
        /// </summary>
        public bool Ended { get; }

        /// <summary>
        /// The step index of the last state-changing step record — the
        /// record the SPEC §F4 item 5 resume branch keys on — or null when
        /// the journal carries none.
        /// </summary>
        public int? LastStepIndex { get; }

        /// <summary>
        /// The step index a journaled <c>review_resolved{retry_step}</c>
        /// re-arms, or null (SPEC §E1, §F4).
        /// </summary>
        public int? ReviewRetryStep { get; }

        /// <summary>The <c>reason</c> of the pending CancelRequested, or null.</summary>
        public string? CancelReason { get; }

        /// <summary>
        /// The <c>session</c> of the first folded record — normally the
        /// <c>RunAccepted</c> committed with plan.json — the producing
        /// session of the Plan-side values (execution inputs, constants,
        /// defaults) the SPEC §F4 item 4 handle discard compares with the
        /// current Editor session on every rebuild.
        /// </summary>
        public string? CreationSession { get; }
    }
}
