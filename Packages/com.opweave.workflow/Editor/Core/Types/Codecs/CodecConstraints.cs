#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// Shared <see cref="PortConstraints"/> evaluation for the built-in codecs
    /// (SPEC §B7, §C1): enum membership, regex pattern, numeric bounds and
    /// maximum string length, each emitted as OW-TYPE-003.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constraint keywords follow the SPEC §B8 JSON-Schema-like semantics the
    /// consolidated contract implies: a keyword applies only to the JSON kinds
    /// it can meaningfully constrain. <see cref="PortConstraints.Enum"/> is a
    /// closed set of string values, so every non-string value violates it;
    /// <see cref="PortConstraints.Pattern"/> and
    /// <see cref="PortConstraints.MaxLength"/> apply to strings only;
    /// <see cref="PortConstraints.Min"/>/<see cref="PortConstraints.Max"/>
    /// apply to numbers only. Keywords on the remaining JSON kinds are ignored,
    /// never silently treated as violations.
    /// </para>
    /// <para>
    /// Local, reversible choices where the SPEC is silent: the pattern is an
    /// unanchored <see cref="Regex.IsMatch(string, string)"/> search (JSON
    /// Schema draft 2020-12 <c>pattern</c> semantics); <c>MaxLength</c> counts
    /// Unicode code points — the draft 2020-12 <c>maxLength</c> unit — so a
    /// supplementary character (a UTF-16 surrogate pair) counts as one and a
    /// lone surrogate counts as one each; non-finite bounds
    /// (NaN, which a <see cref="JsonValue"/> can never carry and only a
    /// hand-built descriptor can produce) are ignored because Descriptor
    /// validation — not the codec — is responsible for rejecting malformed
    /// constraints; and an invalid regular expression fails closed as a
    /// value violation. No constraint keyword mutates input or has side
    /// effects (SPEC §B0.1).
    /// </para>
    /// <para>
    /// Comparisons between an Int64 token and a <see cref="double"/> bound are
    /// exact: naive <c>(double)value</c> conversion rounds integers beyond
    /// 2^53 and would misjudge boundary cases, so
    /// <see cref="CompareIntegerToBound"/> compares without precision loss.
    /// </para>
    /// </remarks>
    internal static class CodecConstraints
    {
        /// <summary>2^63 — the first binary64 value no Int64 can reach.</summary>
        private const double TwoTo63 = 9223372036854775808.0;

        /// <summary>
        /// Appends one OW-TYPE-003 <see cref="Diagnostic"/> per violated
        /// constraint of <paramref name="constraints"/> to
        /// <paramref name="sink"/>; no-op when <paramref name="constraints"/>
        /// is null or every applicable keyword is satisfied.
        /// </summary>
        public static void Check(JToken token, PortConstraints? constraints, List<Diagnostic> sink)
        {
            if (constraints is null)
            {
                return;
            }

            if (constraints.Enum is not null && !IsAllowedEnumValue(token, constraints.Enum))
            {
                sink.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Value is not in the allowed set of values."));
            }

            switch (token.Type)
            {
                case JTokenType.String:
                {
                    var text = (string)((JValue)token).Value!;
                    if (constraints.Pattern is not null && !MatchesPattern(text, constraints.Pattern))
                    {
                        sink.Add(ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003,
                            "Value does not match the required pattern."));
                    }
                    if (constraints.MaxLength is int maxLength && ExceedsMaxLength(text, maxLength))
                    {
                        sink.Add(ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003,
                            "Value exceeds the maximum length."));
                    }
                    break;
                }
                case JTokenType.Integer:
                {
                    var integer = (long)((JValue)token).Value!;
                    if (IsBound(constraints.Min, out var min) &&
                        CompareIntegerToBound(integer, min) < 0)
                    {
                        sink.Add(ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003,
                            "Value is below the minimum bound."));
                    }
                    if (IsBound(constraints.Max, out var max) &&
                        CompareIntegerToBound(integer, max) > 0)
                    {
                        sink.Add(ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003,
                            "Value is above the maximum bound."));
                    }
                    break;
                }
                case JTokenType.Float:
                {
                    var number = (double)((JValue)token).Value!;
                    if (IsBound(constraints.Min, out var min) && number < min)
                    {
                        sink.Add(ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003,
                            "Value is below the minimum bound."));
                    }
                    if (IsBound(constraints.Max, out var max) && number > max)
                    {
                        sink.Add(ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003,
                            "Value is above the maximum bound."));
                    }
                    break;
                }
            }
        }

        // Enum entries are strings (SPEC §C1 PortConstraints); a non-string
        // value can never be a member, Ordinal equality decides membership.
        private static bool IsAllowedEnumValue(JToken token, IReadOnlyList<string> allowed)
        {
            if (token.Type != JTokenType.String)
            {
                return false;
            }
            var text = (string)((JValue)token).Value!;
            for (var i = 0; i < allowed.Count; i++)
            {
                if (string.Equals(allowed[i], text, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // Unanchored match; an invalid pattern fails closed as a violation.
        private static bool MatchesPattern(string text, string pattern)
        {
            try
            {
                return Regex.IsMatch(text, pattern);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // Draft 2020-12 string length is the Unicode code-point count (RFC
        // 8259): a surrogate pair is one code point, a lone surrogate one each.
        private static bool ExceedsMaxLength(string text, int maxLength)
        {
            var length = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) &&
                    i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }
                if (++length > maxLength)
                {
                    return true;
                }
            }
            return false;
        }

        // A bound applies only when present and not NaN; a NaN bound can arise
        // only from a hand-built Descriptor and is left for Descriptor
        // validation to reject rather than silently deciding values here.
        private static bool IsBound(double? bound, out double value)
        {
            value = bound ?? 0.0;
            return bound.HasValue && !double.IsNaN(value);
        }

        /// <summary>
        /// Exact sign of <paramref name="value"/> − <paramref name="bound"/>
        /// for an Int64 against a finite binary64: −1, 0 or +1 without the
        /// precision loss of a double conversion on integers above 2^53.
        /// </summary>
        private static int CompareIntegerToBound(long value, double bound)
        {
            if (bound >= TwoTo63)
            {
                return -1; // bound exceeds every Int64
            }
            if (bound < -TwoTo63)
            {
                return 1; // bound is below every Int64
            }
            var whole = (long)Math.Truncate(bound); // |bound| < 2^63, so the cast is exact
            if (value != whole)
            {
                return value < whole ? -1 : 1;
            }
            var fraction = bound - whole; // exact: whole and bound are within a factor of two
            return fraction > 0.0 ? -1 : (fraction < 0.0 ? 1 : 0);
        }
    }
}
