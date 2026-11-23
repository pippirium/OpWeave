#nullable enable

using System;
using System.Diagnostics;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The production <see cref="IMonotonicTimer"/> over
    /// <see cref="System.Diagnostics.Stopwatch"/> (SPEC §0.4) — the timer
    /// starts on construction.
    /// </summary>
    internal sealed class StopwatchMonotonicTimer : IMonotonicTimer
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public TimeSpan Elapsed => _stopwatch.Elapsed;
    }
}
