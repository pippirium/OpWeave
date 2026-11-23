#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// The fixed probe set <see cref="CodecContractProbe"/> feeds a codec at
    /// registration (T-W2.12-03, SPEC §B7.1 item 4). Fixtures that model a
    /// broken codec must behave correctly on these exact inputs — so they
    /// can still register — while keeping their contract violation for the
    /// values the runtime-failure tests actually feed.
    /// </summary>
    internal static class CodecProbeTokens
    {
        private static readonly HashSet<string> s_canonical =
            new HashSet<string>(System.StringComparer.Ordinal)
            {
                "0", "\"x\"", "false", "[]", "{}",
            };

        /// <summary>Whether <paramref name="value"/> is one of the registration probes.</summary>
        internal static bool IsProbe(JsonValue value) =>
            s_canonical.Contains(value.CanonicalText);
    }
}
