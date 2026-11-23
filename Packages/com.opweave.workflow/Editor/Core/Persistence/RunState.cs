#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The Run state of SPEC §E1, exactly as defined there. Members serialize as
    /// §0.4 snake_case (<c>"queued"</c>, <c>"awaiting_approval"</c>,
    /// <c>"needs_review"</c>, ...) inside journal record payloads. Allowed
    /// transitions are defined by
    /// <see cref="OpWeave.Core.Runtime.StateRules"/> (SPEC §E1).
    /// </summary>
    public enum RunState
    {
        Queued,
        AwaitingApproval,
        Running,
        Suspended,
        NeedsReview,
        Succeeded,
        Failed,
        Cancelled,
    }
}
