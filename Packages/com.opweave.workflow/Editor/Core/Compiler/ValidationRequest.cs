#nullable enable

using OpWeave.Core.Model;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The input of <see cref="IWorkflowCompiler.Validate"/> (SPEC §D1): the immutable
    /// workflow document to check, the optional runtime input values for the WF inputs
    /// (checked by D4 when present), the requested <see cref="ValidationMode"/> and the
    /// <see cref="ActorKind"/> the permission rules apply to.
    /// </summary>
    public sealed record ValidationRequest(
        WorkflowDocument Document,
        JsonValue? Inputs,
        ValidationMode Mode,
        ActorKind Actor);
}
