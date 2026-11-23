#nullable enable

using Newtonsoft.Json.Linq;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>validate_workflow</c> command (SPEC §H1):
    /// the workflow as an inline JSON object (<see cref="Workflow"/>) or a
    /// project-relative <see cref="Path"/> — exactly one, enforced by
    /// <see cref="WorkflowSource"/> — plus the optional WF input values,
    /// the §D8 <see cref="DryRun"/> flag and the <see cref="IncludePlan"/>
    /// flag that adds the serialized Plan to an otherwise compact summary
    /// (SPEC §H3 item 2).
    /// </summary>
    public sealed record ValidateWorkflowRequest(
        JToken? Workflow,
        string? Path,
        JObject? Inputs = null,
        bool DryRun = false,
        bool IncludePlan = false);
}
