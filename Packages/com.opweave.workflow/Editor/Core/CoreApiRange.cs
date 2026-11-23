#nullable enable

using System;

namespace OpWeave.Core
{
    /// <summary>
    /// The Core public API compatibility range an external extension declares in
    /// <c>opweave.extension.json</c> (SPEC §B0.2): the half-open interval
    /// <c>[MinInclusive, MaxExclusive)</c> over <see cref="CoreApiVersion"/>.
    /// </summary>
    /// <remarks>
    /// W2.12 parses the manifest's <c>coreApi.minInclusive</c> /
    /// <c>coreApi.maxExclusive</c> with <see cref="Parse"/>; a missing or
    /// malformed bound, or <c>minInclusive &gt;= maxExclusive</c>, fails and the
    /// extension is refused with OW-OP-012. A range containing
    /// <see cref="CoreApi.Current"/> is required for registration (SPEC §B0.2
    /// rules 1–3). Failures carry one internal diagnostic (empty code, fixed
    /// English — the SG-02 convention); no published code is emitted here.
    /// </remarks>
    public sealed record CoreApiRange(CoreApiVersion MinInclusive, CoreApiVersion MaxExclusive)
    {
        /// <summary>
        /// True when <c>MinInclusive &lt;= version &lt; MaxExclusive</c>
        /// (SPEC §B0.2).
        /// </summary>
        public bool Contains(CoreApiVersion version) =>
            version.CompareTo(MinInclusive) >= 0 && version.CompareTo(MaxExclusive) < 0;

        /// <summary>
        /// Parses both range bounds with <see cref="CoreApiVersion.Parse"/> and
        /// requires <paramref name="minInclusive"/> &lt;
        /// <paramref name="maxExclusive"/> (SPEC §B0.2 rule 1).
        /// </summary>
        /// <exception cref="ArgumentNullException">A bound is null.</exception>
        public static Result<CoreApiRange> Parse(string minInclusive, string maxExclusive)
        {
            if (minInclusive is null)
            {
                throw new ArgumentNullException(nameof(minInclusive));
            }
            if (maxExclusive is null)
            {
                throw new ArgumentNullException(nameof(maxExclusive));
            }

            var min = CoreApiVersion.Parse(minInclusive);
            if (!min.Ok)
            {
                return MalformedBound(nameof(minInclusive), minInclusive, min.Diagnostics[0].Hint);
            }
            var max = CoreApiVersion.Parse(maxExclusive);
            if (!max.Ok)
            {
                return MalformedBound(nameof(maxExclusive), maxExclusive, max.Diagnostics[0].Hint);
            }
            var minVersion = min.Value;
            var maxVersion = max.Value;
            if (minVersion.CompareTo(maxVersion) >= 0)
            {
                return Result<CoreApiRange>.Failure(new Diagnostic(
                    string.Empty,
                    Severity.Error,
                    $"Malformed CoreApi range '{minInclusive}'..'{maxExclusive}'.",
                    Hint: "minInclusive must be less than maxExclusive (SPEC §B0.2)."));
            }
            return Result<CoreApiRange>.Success(new CoreApiRange(minVersion, maxVersion));
        }

        // Internal diagnostic convention (empty code, fixed English): the caller
        // maps a malformed range to a published code — W2.12 emits OW-OP-012.
        private static Result<CoreApiRange> MalformedBound(
            string bound, string text, string? innerHint) =>
            Result<CoreApiRange>.Failure(new Diagnostic(
                string.Empty,
                Severity.Error,
                $"Malformed CoreApi range: {bound} '{text}' is not a valid version.",
                Hint: innerHint));
    }
}
