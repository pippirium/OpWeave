#nullable enable

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The <c>list_runs</c> arguments of SPEC §H1: an optional Run-state
    /// filter <paramref name="State"/> (the §E1 state names in §0.4
    /// snake_case), an optional workflow id <paramref name="WorkflowId"/>
    /// and the required item cap <paramref name="Limit"/> — the SPEC §H1
    /// table lists it without the optional marker, so a missing or
    /// non-positive limit is request-shape misuse answered with
    /// OW-API-001 (SPEC §J); <paramref name="Cursor"/> resumes a
    /// truncated page (SPEC §H3 item 3, an invalid one is OW-API-002).
    /// </summary>
    public sealed record ListRunsRequest(
        string? State = null,
        string? WorkflowId = null,
        int? Limit = null,
        string? Cursor = null);
}
