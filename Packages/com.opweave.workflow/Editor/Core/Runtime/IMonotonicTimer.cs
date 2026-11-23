#nullable enable

using System;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The monotonic elapsed-time source for duration measurement
    /// (SPEC §0.4: durations are measured with
    /// <see cref="System.Diagnostics.Stopwatch"/>, never the wall clock —
    /// UTC time can jump backwards or forwards). A fresh instance starts
    /// at zero and <see cref="Elapsed"/> only grows. <see cref="IClock"/>
    /// stays the source for journal timestamps and the <c>nextPollAt</c> /
    /// <c>deadline</c> instants the SPEC defines on wall time.
    /// </summary>
    internal interface IMonotonicTimer
    {
        /// <summary>The time elapsed since this timer was created.</summary>
        TimeSpan Elapsed { get; }
    }
}
