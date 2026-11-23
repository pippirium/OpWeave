#nullable enable

using System;
using OpWeave.Core.Runtime;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The production <see cref="IClock"/> of SPEC §E3 item 1: the real UTC
    /// wall clock feeding journal timestamps and the <c>nextPollAt</c> /
    /// <c>deadline</c> instants. The tick budget is measured on a separate
    /// monotonic timer (SPEC §0.4), so wall-clock jumps can never stretch it.
    /// </summary>
    internal sealed class SystemUtcClock : IClock
    {
        /// <inheritdoc cref="IClock.UtcNow"/>
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
