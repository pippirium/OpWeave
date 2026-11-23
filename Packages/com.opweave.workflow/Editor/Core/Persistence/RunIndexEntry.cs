#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// One row of the Run Store <c>index.json</c> (SPEC §F1):
    /// <c>{runId, workflowId, state, createdAt, updatedAt}</c>. The index is a
    /// cache over the per-Run journals — the recovery engine scans it for
    /// unfinished Runs (SPEC §F4 item 1) — and is rebuilt from the journals
    /// when missing or unreadable.
    /// </summary>
    public sealed record RunIndexEntry(
        string RunId,
        string WorkflowId,
        RunState State,
        string CreatedAt,
        string UpdatedAt);
}
