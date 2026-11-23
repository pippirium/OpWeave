#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The filter of <see cref="IRunStore.List"/> (SPEC §F1). A null or empty
    /// <see cref="States"/> selects every Run; otherwise only Runs whose
    /// recorded state is one of the listed states are returned — the recovery
    /// engine passes the unfinished states (Queued, AwaitingApproval, Running,
    /// Suspended, NeedsReview) to find the Runs it must inspect (SPEC §F4
    /// item 1, §E2).
    /// </summary>
    public sealed record RunQuery(IReadOnlyList<RunState>? States = null);
}
