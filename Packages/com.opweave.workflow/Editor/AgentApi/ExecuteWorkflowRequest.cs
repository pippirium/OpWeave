#nullable enable

using Newtonsoft.Json.Linq;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>execute_workflow</c> command (SPEC §H1):
    /// the workflow as an inline JSON object (<see cref="Workflow"/>) or a
    /// project-relative <see cref="Path"/> — exactly one, enforced by
    /// <see cref="WorkflowSource"/> — the required WF
    /// <see cref="Inputs"/> (Codec-converted before hashing, SPEC §D0.1),
    /// the optional <see cref="ExpectedPlanHash"/> the §E2 acceptance
    /// re-checks against the compiled Plan (mismatch → OW-RUN-003) and the
    /// <see cref="Queue"/> flag admitting the Run into the §E2 waiting queue
    /// while another mutating Run is active.
    /// </summary>
    public sealed record ExecuteWorkflowRequest(
        JToken? Workflow,
        string? Path,
        JObject? Inputs,
        string? ExpectedPlanHash = null,
        bool Queue = false);
}
