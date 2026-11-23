#nullable enable

using Newtonsoft.Json.Linq;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>migrate_workflow</c> command (SPEC §H1, §A7):
    /// the workflow as an inline JSON object (<see cref="Workflow"/>) or a
    /// project-relative <see cref="Path"/> — exactly one, enforced by
    /// <see cref="WorkflowSource"/>. The command returns the migrated JSON
    /// and the applied change list; it never writes the file back (SPEC §A7
    /// item 3 — saving stays with the caller).
    /// </summary>
    public sealed record MigrateWorkflowRequest(
        JToken? Workflow,
        string? Path);
}
