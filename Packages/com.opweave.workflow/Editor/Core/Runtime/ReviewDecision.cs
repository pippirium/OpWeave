#nullable enable

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The human decision a <see cref="RunState.NeedsReview"/> Run resolves
    /// with (SPEC §E2, §E1): <see cref="RetryStep"/> re-runs the step and
    /// <see cref="Abort"/> cancels the Run.
    /// </summary>
    public enum ReviewDecision
    {
        RetryStep,
        Abort,
    }
}
