#nullable enable

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The <c>cancel_workflow</c> arguments of SPEC §H1: the Run's
    /// <paramref name="RunId"/> and an optional <paramref name="Reason"/>
    /// journaled with the SPEC §E6 cancel request. A missing or malformed
    /// runId is request-shape misuse answered with OW-API-001 (SPEC §J); a
    /// well-formed but unknown one is the Run Store's OW-RUN-010, and a Run
    /// in a state an Agent cannot cancel is OW-RUN-014 (SPEC §0.6 SG-14).
    /// </summary>
    public sealed record CancelWorkflowRequest(string? RunId, string? Reason = null);
}
