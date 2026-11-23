#nullable enable

namespace OpWeave.Core.Model
{
    /// <summary>
    /// A workflow output <c>{id, from:{node,port}, description?}</c>: a value published
    /// as a Run result on <c>get_run_status</c> (SPEC §A6).
    /// </summary>
    public sealed record WorkflowOutput(string Id, PortRef From, string? Description = null);
}
