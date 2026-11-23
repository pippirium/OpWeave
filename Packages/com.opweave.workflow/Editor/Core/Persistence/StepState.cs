#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The Step state of SPEC §E1, exactly as defined there. Members serialize as
    /// §0.4 snake_case (<c>"pending"</c>, <c>"waiting"</c>,
    /// <c>"needs_review"</c>, ...) inside journal record payloads. Allowed
    /// transitions are defined by
    /// <see cref="OpWeave.Core.Runtime.StateRules"/> (SPEC §E0).
    /// </summary>
    public enum StepState
    {
        Pending,
        Running,
        Waiting,
        Succeeded,
        Failed,
        Cancelled,
        Skipped,
        NeedsReview,
    }
}
