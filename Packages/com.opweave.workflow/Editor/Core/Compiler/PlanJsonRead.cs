#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The outcome of reading a plan.json document (SPEC §D0.1, §D7).
    /// <see cref="Plan"/> is non-null exactly when <see cref="Status"/> is
    /// <see cref="PlanJsonStatus.Decoded"/>; <see cref="Diagnostic"/> is
    /// non-null on <see cref="PlanJsonStatus.NeedsReview"/> and
    /// <see cref="PlanJsonStatus.Corrupt"/>.
    /// </summary>
    /// <remarks>
    /// The diagnostic follows the SG-02 internal convention (SPEC §0.6): an
    /// empty <see cref="Diagnostic.Code"/>, a payload-free fixed-English
    /// message and a JsonPath relative to the document root. The Run Store
    /// maps it to OW-STORE-002 exactly once.
    /// </remarks>
    public sealed record PlanJsonRead(
        PlanJsonStatus Status,
        CompiledPlan? Plan,
        Diagnostic? Diagnostic);
}
