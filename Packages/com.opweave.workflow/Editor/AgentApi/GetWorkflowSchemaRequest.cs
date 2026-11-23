#nullable enable

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>get_workflow_schema</c> command (SPEC §H1,
    /// §A2): none besides the opaque page <paramref name="Cursor"/> that
    /// continues a segmented schema response — an invalid one is
    /// OW-API-002.
    /// </summary>
    public sealed record GetWorkflowSchemaRequest(string? Cursor = null);
}
