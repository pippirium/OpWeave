#nullable enable

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// How an Operation completes (SPEC §C1): <see cref="Immediate"/> finishes
    /// inside <c>Execute</c>; <see cref="Waitable"/> returns
    /// <c>StepResult.Waiting</c> and keeps polling through <c>Poll</c>, so it can
    /// span domain reloads.
    /// </summary>
    public enum ExecutionMode
    {
        Immediate,
        Waitable,
    }
}
