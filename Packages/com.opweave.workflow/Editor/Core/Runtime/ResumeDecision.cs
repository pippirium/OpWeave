#nullable enable

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The closed outcome set of the single SPEC §F5 re-execution decision
    /// (<see cref="ResumePolicy.Decide"/>): re-execute the step with an
    /// incremented attempt, adopt the outputs a Reconcile verdict restored,
    /// or hold the Run for human review.
    /// </summary>
    public enum ResumeDecision
    {
        /// <summary>
        /// Re-execute the step (attempt+1). The §F5 verdict for Idempotent
        /// steps and for Reconcilable steps whose Reconcile returns
        /// <see cref="Operations.ReconcileResult.NotApplied"/>.
        /// </summary>
        Reexecute,

        /// <summary>
        /// Do not re-execute: Reconcile proved the step already ran and
        /// restored every declared output (SPEC §F5
        /// <see cref="Operations.ReconcileResult.Completed"/>), so the Run
        /// treats the step as succeeded.
        /// </summary>
        AdoptOutputs,

        /// <summary>
        /// Neither re-execution nor completion is provable. The Run goes to
        /// NeedsReview; automatic success and automatic re-execution are
        /// both forbidden from this verdict (SPEC §F5, §F0).
        /// </summary>
        NeedsReview,
    }
}
