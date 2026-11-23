#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The reader classification of a plan.json document (SPEC §D0.1, §D7,
    /// §F2 item 4). This is the codec-level signal only: the Run Store maps
    /// every non-<see cref="Decoded"/> outcome to OW-STORE-002 and NeedsReview
    /// exactly once — there is no plan.json line a loader may drop or skip.
    /// </summary>
    public enum PlanJsonStatus
    {
        /// <summary>The document decoded into a <see cref="CompiledPlan"/>.</summary>
        Decoded,

        /// <summary>
        /// A structurally readable document the reader must not interpret:
        /// the <c>formatVersion</c> or <c>canonicalJsonVersion</c> member is
        /// missing, mistyped or holds a value this version does not know
        /// (SPEC §0.3, §F2 item 4), or the revalidation the 2026-11-06 SG-12
        /// amendment orders fails — the <c>planHashInput</c> member is
        /// missing, <c>planHash</c> does not match the persisted
        /// <c>planHashInput</c> SHA-256, or a persisted public field
        /// contradicts the persisted hash input. Never auto-migrated or
        /// auto-succeeded.
        /// </summary>
        NeedsReview,

        /// <summary>
        /// The document violates the plan.json format: unparseable or
        /// non-object JSON, a member outside the v1 set, a missing or
        /// mistyped member, a malformed InputSource tag, or a
        /// <c>planHashInput</c> member that is not the SG-12 twelve-field
        /// object.
        /// </summary>
        Corrupt,
    }
}
