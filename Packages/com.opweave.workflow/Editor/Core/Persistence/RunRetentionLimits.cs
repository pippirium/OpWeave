#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The two bounds of SPEC §F8 applied to finished Runs: at most
    /// <see cref="MaxFinishedRuns"/> finished Runs are retained, and their
    /// combined Run-directory size stays under
    /// <see cref="MaxFinishedBytes"/>.
    /// </summary>
    /// <remarks>
    /// A non-positive limit is read as "keep nothing": the settings loader
    /// accepts any JSON integer for <c>retainRuns</c> /
    /// <c>retainMegabytes</c> (SPEC §H4.1), so a negative value is clamped
    /// at the pass — never silently replaced with the default.
    /// </remarks>
    public sealed record RunRetentionLimits(long MaxFinishedRuns, long MaxFinishedBytes)
    {
        /// <summary>
        /// Converts the SPEC §H4.1 settings values (<c>retainRuns</c>,
        /// <c>retainMegabytes</c>) into the byte limits the pass applies.
        /// One MB is one MiB (1,048,576 bytes — the storage unit the
        /// Run Store's file sizes are measured in); an overflowing
        /// megabyte count saturates to an unbounded byte limit, and a
        /// negative value clamps to zero.
        /// </summary>
        public static RunRetentionLimits FromSettings(long retainRuns, long retainMegabytes)
        {
            const long bytesPerMegabyte = 1024L * 1024L;
            var maxRuns = retainRuns < 0 ? 0 : retainRuns;
            long maxBytes;
            if (retainMegabytes < 0)
            {
                maxBytes = 0;
            }
            else if (retainMegabytes > long.MaxValue / bytesPerMegabyte)
            {
                maxBytes = long.MaxValue;
            }
            else
            {
                maxBytes = retainMegabytes * bytesPerMegabyte;
            }
            return new RunRetentionLimits(maxRuns, maxBytes);
        }
    }
}
