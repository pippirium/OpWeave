#nullable enable

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The arguments of the <c>list_workflows</c> command (SPEC §H1, §A8):
    /// the optional <paramref name="Query"/> matched against each
    /// workflow's <c>id</c>/<c>title</c>/<c>description</c>/<c>tags</c> and
    /// the optional project-relative <paramref name="Folder"/> the scan
    /// is rooted at — <c>"Assets"</c> by default; a folder escaping the
    /// project root is OW-API-001.
    /// </summary>
    public sealed record ListWorkflowsRequest(
        string? Query = null,
        string? Folder = null,
        string? Cursor = null);
}
