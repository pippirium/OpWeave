#nullable enable

using System;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The registration-time probe of the SPEC §B7.1 Codec contract
    /// (T-W2.12-03): "登録時に明確なNormalize契約違反が判る候補はOW-OP-016
    /// で登録しない" — a candidate whose normalization contract is provably
    /// broken on evidence the registration build itself can produce is
    /// rejected before it reaches the type snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe feeds each codec a small fixed set of probe values — a
    /// number, a string, a boolean, an empty array and an empty object —
    /// through the same guarded boundaries <see cref="ValueValidation"/>
    /// applies: <see cref="IValueCodec.Validate"/> decides whether a probe
    /// is the codec's input, and only a probe the codec itself accepts
    /// reaches <see cref="IValueCodec.Normalize"/>. A codec that accepts
    /// nothing yields no verdict: registration proves violations, it does
    /// not guess them (the §B7.1 item 4 wording is "明確な…違反が判る").
    /// </para>
    /// <para>
    /// For an accepted probe the §B7.1 contract must hold: no exception,
    /// a non-null result, no produced explicit <c>null</c>, a result the
    /// codec itself still validates, and an idempotent second
    /// <see cref="IValueCodec.Normalize"/> pass (CanonicalText equality).
    /// A <see cref="IValueCodec.Validate"/> that throws or returns a
    /// malformed result is a contract breach of the same family — the
    /// codec cannot even classify values — and is reported identically.
    /// Exceptions and results are reduced to fixed-English detail strings;
    /// exception text never propagates (it may carry secrets, SPEC §B7.1
    /// item 4).
    /// </para>
    /// <para>
    /// Local, reversible choices: the probe set itself and the decision to
    /// treat a throwing <see cref="IValueCodec.Validate"/> as the same
    /// contract breach a throwing <see cref="IValueCodec.Normalize"/> is.
    /// </para>
    /// </remarks>
    internal static class CodecContractProbe
    {
        private static readonly JsonValue[] s_probes =
        {
            JsonValue.Parse("0").Value!,
            JsonValue.Parse("\"x\"").Value!,
            JsonValue.Parse("false").Value!,
            JsonValue.Parse("[]").Value!,
            JsonValue.Parse("{}").Value!,
        };

        /// <summary>
        /// Probes <paramref name="codec"/>'s §B7.1 contract. Returns null
        /// when nothing provably broken is observed; otherwise a
        /// fixed-English detail suitable for a diagnostic message — never
        /// exception text or value content.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="codec"/> is null.</exception>
        internal static string? Violation(IValueCodec codec)
        {
            if (codec is null)
            {
                throw new ArgumentNullException(nameof(codec));
            }

            for (var i = 0; i < s_probes.Length; i++)
            {
                var probe = s_probes[i];

                // Validate decides whether the probe is the codec's input
                // at all; a throwing or malformed Validate is itself a
                // contract breach — the codec cannot classify values.
                var diagnostics = TryValidate(codec, probe, out var validateDetail);
                if (validateDetail is not null)
                {
                    return validateDetail;
                }
                if (diagnostics is null || diagnostics.Count != 0)
                {
                    // Null is unreachable (TryValidate maps it to a
                    // detail); diagnostics mean the codec legitimately
                    // rejects this probe — no contract evidence either way.
                    continue;
                }

                var detail = ProbeNormalize(codec, probe);
                if (detail is not null)
                {
                    return detail;
                }
            }
            return null;
        }

        // Runs codec.Validate inside the probe's error boundary: exceptions
        // and malformed results (null list, null entries) are contract
        // breaches, never propagated exceptions.
        private static System.Collections.Generic.IReadOnlyList<Diagnostic>? TryValidate(
            IValueCodec codec, JsonValue probe, out string? detail)
        {
            detail = null;
            System.Collections.Generic.IReadOnlyList<Diagnostic>? diagnostics;
            try
            {
                diagnostics = codec.Validate(probe, constraints: null);
            }
            catch (Exception)
            {
                detail = "its Validate threw an exception on a probe value";
                return null;
            }
            if (diagnostics is null)
            {
                detail = "its Validate returned no result on a probe value";
                return null;
            }
            for (var i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i] is null)
                {
                    detail = "its Validate returned a null diagnostic on a probe value";
                    return null;
                }
            }
            return diagnostics;
        }

        // The §B7.1 checks of ValueValidation.Normalize replayed against
        // one accepted probe value: no throw, non-null, no produced
        // explicit null, self-validating result, idempotent.
        private static string? ProbeNormalize(IValueCodec codec, JsonValue probe)
        {
            JsonValue? normalized;
            try
            {
                normalized = codec.Normalize(probe);
            }
            catch (Exception)
            {
                return "its Normalize threw an exception on an accepted probe value";
            }
            if (normalized is null)
            {
                return "its Normalize returned null on an accepted probe value";
            }
            if (normalized.ToToken().Type == JTokenType.Null)
            {
                return "its Normalize produced an unexpected explicit null";
            }

            var revalidation = TryValidate(codec, normalized, out var validateDetail);
            if (validateDetail is not null)
            {
                return validateDetail;
            }
            if (revalidation is null || revalidation.Count != 0)
            {
                return "its Normalize produced a value it then rejects";
            }

            JsonValue? twice;
            try
            {
                twice = codec.Normalize(normalized);
            }
            catch (Exception)
            {
                return "its Normalize threw an exception on its own output";
            }
            if (twice is null)
            {
                return "its Normalize returned null on its own output";
            }
            if (!string.Equals(
                normalized.CanonicalText, twice.CanonicalText, StringComparison.Ordinal))
            {
                return "its Normalize is not idempotent";
            }
            return null;
        }
    }
}
