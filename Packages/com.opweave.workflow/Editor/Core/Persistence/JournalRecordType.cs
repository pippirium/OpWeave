#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The journal record types of the SPEC §F2 table — every kind that can
    /// appear in the envelope line
    /// <c>{"seq","ts","session","type","required","step"?,"attempt"?,"data","crc"}</c>.
    /// The line-1 <see cref="JournalHeader"/> is not a member: it has its own
    /// fixed shape and is never carried in the record envelope.
    /// </summary>
    /// <remarks>
    /// The wire name of each member is its §0.4 snake_case form
    /// (<c>"run_accepted"</c>, <c>"step_needs_review"</c>, <c>"log"</c>, ...),
    /// frozen in <see cref="JournalRecordTypes.All"/>.
    /// </remarks>
    public enum JournalRecordType
    {
        RunAccepted,
        ApprovalGranted,
        ApprovalRejected,
        RunStarted,
        RunSuspended,
        RunResumed,
        StepStarted,
        StepWaiting,
        StepProgress,
        StepSucceeded,
        StepFailed,
        StepCancelled,
        StepSkipped,
        StepNeedsReview,
        CancelRequested,
        ReviewResolved,
        ResourceAcquired,
        ResourceReleased,
        Log,
        RunFinished,
    }
}
