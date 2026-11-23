#nullable enable

namespace OpWeave.Viewer
{
    /// <summary>
    /// Which source named the NeedsReview reason of SPEC §H5 item 1 — the
    /// cause that actually put the Run in its current NeedsReview state.
    /// </summary>
    internal enum RunViewerReviewSource
    {
        /// <summary>The journal's latest <c>step_needs_review</c> record.</summary>
        StepJournal,

        /// <summary>
        /// The Run Store's load-time integrity diagnostic — the OW-STORE-002
        /// failure that itself moved the Run to NeedsReview (SPEC §F1, §F3).
        /// </summary>
        StoreIntegrity,
    }
}
