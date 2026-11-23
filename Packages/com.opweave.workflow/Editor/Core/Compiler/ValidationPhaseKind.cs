#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// Identifies one validation phase of the SPEC §D1 item 1 order
    /// (<see cref="D2"/> → <see cref="D6"/>, plus <see cref="D8"/> for Dry Run only) so
    /// the pipeline can apply the per-phase rules: an error from the
    /// <see cref="D2"/> phase stops the pipeline, while errors from later phases only
    /// exclude the node they name (SPEC §D1 item 2).
    /// </summary>
    internal enum ValidationPhaseKind
    {
        /// <summary>Structural/document-level checks (SPEC §D2).</summary>
        D2,

        /// <summary>Operation and parameter checks (SPEC §D3).</summary>
        D3,

        /// <summary>Type and supplier checks (SPEC §D4).</summary>
        D4,

        /// <summary>Control-flow graph checks (SPEC §D5).</summary>
        D5,

        /// <summary>Permission and static precondition checks (SPEC §D6).</summary>
        D6,

        /// <summary>Dry Run preflight (SPEC §D8); runs only for <see cref="ValidationMode.DryRun"/>.</summary>
        D8,
    }
}
