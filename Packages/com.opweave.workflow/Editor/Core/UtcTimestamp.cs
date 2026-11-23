#nullable enable

using System;
using System.Globalization;

namespace OpWeave.Core
{
    /// <summary>
    /// UTC ISO 8601 timestamps with millisecond precision, always ending in "Z" (SPEC §0.4).
    /// Durations are measured with <see cref="System.Diagnostics.Stopwatch"/>, not from
    /// formatted timestamps.
    /// </summary>
    public static class UtcTimestamp
    {
        private const string Iso8601Milliseconds = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>Formats <paramref name="timestamp"/> in UTC as "yyyy-MM-ddTHH:mm:ss.fffZ".</summary>
        public static string Format(DateTimeOffset timestamp) =>
            timestamp.UtcDateTime.ToString(Iso8601Milliseconds, CultureInfo.InvariantCulture);

        /// <summary>The current UTC time in the <see cref="Format"/> format.</summary>
        public static string Now() => Format(DateTimeOffset.UtcNow);
    }
}
