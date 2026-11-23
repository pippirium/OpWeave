#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// One phase of the validation pipeline (SPEC §D1 item 1). Phases inspect the
    /// <see cref="ValidationContext"/> document and report findings through
    /// <see cref="ValidationContext.Report"/>; the pipeline owns the ordering,
    /// D2-stop, node-exclusion and truncation rules, so a phase never decides whether
    /// the run continues. Internal: the public surface stays the SPEC §D1 one, and
    /// later tickets supply the D3–D6 and D8 implementations.
    /// </summary>
    internal interface IValidationPhase
    {
        /// <summary>Which pipeline phase this implementation plays (SPEC §D1 item 1).</summary>
        ValidationPhaseKind Kind { get; }

        /// <summary>Checks the document and reports every finding to <paramref name="context"/>.</summary>
        void Run(ValidationContext context);
    }
}
