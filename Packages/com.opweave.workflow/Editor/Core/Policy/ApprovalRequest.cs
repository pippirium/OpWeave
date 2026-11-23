#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// One <c>ask</c> outcome of a permission evaluation (SPEC §D6 item 1): the
    /// node and the side effect that need a human approval. This is the policy
    /// stage's contribution to the Plan's <c>approvals</c>; the approval detail
    /// (path, code, package id, extension sources) is filled by the compiler and
    /// adapter stages (SPEC §H4 item 3, §D7).
    /// </summary>
    public sealed record ApprovalRequest(string NodeId, string Effect);
}
