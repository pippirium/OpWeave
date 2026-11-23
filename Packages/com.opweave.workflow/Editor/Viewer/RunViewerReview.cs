#nullable enable

namespace OpWeave.Viewer
{
    /// <summary>
    /// The NeedsReview reason of SPEC §H5 item 1 the review panel shows
    /// ("理由"): the cause that put the Run in its current NeedsReview
    /// state — the step and node the <c>step_needs_review</c> record
    /// belongs to and the diagnostic code and message it journaled, or the
    /// Run Store's load-time integrity diagnostic when that is what moved
    /// the Run to NeedsReview. Every member is what the record or the
    /// diagnostic literally says — the reason is read, never reconstructed.
    /// </summary>
    internal sealed class RunViewerReview
    {
        internal RunViewerReview(
            RunViewerReviewSource source,
            int? stepIndex, string? nodeId, string? code, string? message)
        {
            Source = source;
            StepIndex = stepIndex;
            NodeId = nodeId;
            Code = code;
            Message = message;
        }

        /// <summary>Which source named the reason.</summary>
        internal RunViewerReviewSource Source { get; }

        /// <summary>The Plan step index the record belongs to, or null.</summary>
        internal int? StepIndex { get; }

        /// <summary>The node's workflow id, or null when the Plan cannot be read.</summary>
        internal string? NodeId { get; }

        /// <summary>The journaled diagnostic code, or null.</summary>
        internal string? Code { get; }

        /// <summary>The journaled diagnostic message, or null.</summary>
        internal string? Message { get; }
    }
}
