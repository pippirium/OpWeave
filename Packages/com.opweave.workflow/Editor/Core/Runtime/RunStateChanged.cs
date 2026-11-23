#nullable enable

using System;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The payload of <see cref="IRunEvents.RunStateChanged"/> (SPEC §E8):
    /// the Run whose journal just recorded a state change and the SPEC §E1
    /// state the journal now folds to. Delivered synchronously on the main
    /// thread after the journal write.
    /// </summary>
    public sealed record RunStateChanged(string RunId, RunState State)
    {
        /// <summary>The Run the change belongs to.</summary>
        public string RunId { get; init; } =
            string.IsNullOrEmpty(RunId)
                ? throw new ArgumentException(
                    "A Run state event must carry a Run id.", nameof(RunId))
                : RunId;
    }
}
