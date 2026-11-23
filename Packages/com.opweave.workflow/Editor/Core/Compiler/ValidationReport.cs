#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The structured result of <see cref="IWorkflowCompiler.Validate"/> (SPEC §D1):
    /// <see cref="Errors"/> and <see cref="Warnings"/> are ordered by node document
    /// order then diagnostic code, capped at 100 diagnostics with
    /// <see cref="Truncated"/> set when more were emitted. <see cref="Plan"/> carries
    /// the built plan only when <see cref="Ok"/> is true;
    /// <see cref="PlannedChanges"/> is populated only for a
    /// <see cref="ValidationMode.DryRun"/> request (SPEC §D8).
    /// </summary>
    public sealed record ValidationReport(
        bool Ok,
        IReadOnlyList<Diagnostic> Errors,
        IReadOnlyList<Diagnostic> Warnings,
        bool Truncated,
        CompiledPlan? Plan,
        IReadOnlyList<PlannedChange>? PlannedChanges)
    {
        /// <summary>
        /// The Dry Run guarantee marker of SPEC §D8 item 3: the effects a
        /// preflight predicts cannot be fully guaranteed, so a Dry Run
        /// report always carries <c>"none"</c>. <see langword="null"/> for a
        /// <see cref="ValidationMode.Static"/> report, which carries no
        /// <see cref="PlannedChanges"/> either — the marker exists exactly
        /// where the predicted changes do.
        /// </summary>
        public string? Guarantee => PlannedChanges is not null ? "none" : null;
    }
}
