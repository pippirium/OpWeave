#nullable enable

using System;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The payload of <see cref="IRunEvents.StepStateChanged"/> (SPEC §E8):
    /// the Run and step whose journal just recorded a state change, the
    /// step's Plan node id, the SPEC §E0 state the journal now folds to and
    /// the attempt the record belongs to — null for a step that never
    /// started (a Run end's <c>step_skipped</c>). A <c>step_failed</c>
    /// record carrying a decided retry reports
    /// <see cref="StepState.Running"/> with the pending attempt, exactly
    /// like the SPEC §F2 fold. Delivered synchronously on the main thread
    /// after the journal write.
    /// </summary>
    public sealed record StepStateChanged(
        string RunId, int Step, string NodeId, StepState State, int? Attempt)
    {
        /// <summary>The Run the change belongs to.</summary>
        public string RunId { get; init; } =
            string.IsNullOrEmpty(RunId)
                ? throw new ArgumentException(
                    "A step state event must carry a Run id.", nameof(RunId))
                : RunId;

        /// <summary>The step's Plan node id.</summary>
        public string NodeId { get; init; } =
            string.IsNullOrEmpty(NodeId)
                ? throw new ArgumentException(
                    "A step state event must carry a node id.", nameof(NodeId))
                : NodeId;
    }
}
