#nullable enable

using System;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The clock abstraction the Core run machinery depends on instead of
    /// touching the wall clock (SPEC §E3 item 1): <c>RunLoop</c> and
    /// <see cref="RunCoordinator"/> receive it injected, so [Core] tests drive
    /// time-dependent behaviour with a fake clock.
    /// </summary>
    public interface IClock
    {
        /// <summary>The current UTC instant.</summary>
        DateTimeOffset UtcNow { get; }
    }
}
