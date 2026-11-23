#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// One <c>ask</c> outcome the Run needs a human approval for (SPEC §D7,
    /// §H4 item 3): the node, the side effect and the detail shown on the
    /// approval screen (the Operation identity and version the step runs).
    /// </summary>
    public sealed record ApprovalRequirement(string NodeId, string Effect, string Detail);
}
