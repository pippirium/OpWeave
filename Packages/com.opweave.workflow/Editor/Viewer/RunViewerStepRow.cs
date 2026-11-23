#nullable enable

using System;
using OpWeave.Core.Persistence;

namespace OpWeave.Viewer
{
    /// <summary>
    /// One row of the SPEC §H5 Runs-tab step list: the Plan step's identity,
    /// the SPEC §E0 state its journal records fold to, the attempt it is on
    /// and the elapsed time of the latest attempt — accruing while the step
    /// is still Running or Waiting (<see cref="Live"/>).
    /// </summary>
    internal sealed class RunViewerStepRow
    {
        internal RunViewerStepRow(
            int index,
            string? nodeId,
            string? operationId,
            int? version,
            StepState state,
            int? attempt,
            TimeSpan? duration,
            bool live,
            DateTimeOffset? liveSinceUtc)
        {
            Index = index;
            NodeId = nodeId;
            OperationId = operationId;
            Version = version;
            State = state;
            Attempt = attempt;
            Duration = duration;
            Live = live;
            LiveSinceUtc = liveSinceUtc;
        }

        /// <summary>The Plan step index.</summary>
        internal int Index { get; }

        /// <summary>The workflow node id, or null when the Plan cannot be read.</summary>
        internal string? NodeId { get; }

        /// <summary>The step's Operation id, or null when the Plan cannot be read.</summary>
        internal string? OperationId { get; }

        /// <summary>The step's Operation version, or null when the Plan cannot be read.</summary>
        internal int? Version { get; }

        /// <summary>The last recorded step state (SPEC §E0).</summary>
        internal StepState State { get; }

        /// <summary>The attempt the step is on (SPEC §F2), or null when it never started.</summary>
        internal int? Attempt { get; }

        /// <summary>
        /// The elapsed time of the latest attempt as of the last read —
        /// <see cref="StepStarted"/> to the step's latest journal record, or
        /// to now while <see cref="Live"/>. Null for a step that never ran.
        /// </summary>
        internal TimeSpan? Duration { get; }

        /// <summary>True while the duration still accrues (Running or Waiting).</summary>
        internal bool Live { get; }

        /// <summary>
        /// The UTC instant the latest attempt started — the base a live row's
        /// duration is measured from between journal reads.
        /// </summary>
        internal DateTimeOffset? LiveSinceUtc { get; }
    }
}
