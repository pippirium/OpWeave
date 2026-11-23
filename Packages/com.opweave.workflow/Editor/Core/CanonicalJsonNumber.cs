#nullable enable

using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpWeave.Core
{
    /// <summary>
    /// Canonical JSON v1 number rules (SPEC §0.3 items 5–7, §T.4 L-12): strict JSON number
    /// grammar, integer tokens that must fit Int64, fraction/exponent tokens that must parse
    /// to a finite binary64, and self-implemented Ryu shortest round-trip output rendered as
    /// normalized scientific notation (<c>1e0</c>, <c>-25e-2</c>, <c>0e0</c>). No culture- or
    /// runtime-specific numeric parsing or formatting is used, so Unity Mono and .NET
    /// produce identical bytes.
    /// </summary>
    /// <remarks>
    /// Rejections are failed <see cref="Result{T}"/>s whose diagnostics carry an empty
    /// internal <see cref="Diagnostic.Code"/> plus a fixed English reason; the caller layer
    /// maps them to a published OW-* code exactly once and attaches offset/JsonPath before
    /// publishing (SPEC §0.6 SG-02). The rejected token text is never copied into a
    /// diagnostic.
    /// </remarks>
    internal static class CanonicalJsonNumber
    {
        // Saturates absurd decimal exponents during scanning; any magnitude past this bound
        // is decided by the decimal-order check before conversion.
        private const long ExponentSaturation = 1000000000000L;

        // Decimal order window inside which exact conversion decides the outcome. order =
        // significantDigits + decimalExponent, i.e. value in [10^(order-1), 10^order).
        // order > 309 cannot stay finite (10^309 > double.MaxValue); order < -400 is always
        // below the 2^-1075 underflow-to-zero boundary (~2.47e-324).
        private const long MaxFiniteOrder = 309;
        private const long MinNonZeroOrder = -400;

        /// <summary>
        /// Canonical text of an Int64 from an integer token or an integral JToken
        /// (SPEC §0.3 item 5). Signed decimal, <c>-0</c> normalized to <c>0</c> by the caller
        /// parsing to <c>0</c>.
        /// </summary>
        public static string FormatInt64(long value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Canonical text of a finite binary64: normalized scientific notation
        /// <c>[sign]digits e exponent</c> with trailing coefficient zeros carried into the
        /// exponent and <c>-0.0</c> normalized to <c>0e0</c> (SPEC §0.3 item 6).
        /// </summary>
        public static Result<string> FormatDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return Result<string>.Failure(Reason("JSON number is not a finite binary64 value."));
            }
            if (value == 0.0)
            {
                return Result<string>.Success("0e0");
            }

            var negative = value < 0.0;
            RyuFormatter.ShortestDigits(Math.Abs(value), out var mantissa, out var exponent);
            while (mantissa % 10 == 0)
            {
                mantissa /= 10;
                exponent++;
            }

            var text = new StringBuilder(24);
            if (negative)
            {
                text.Append('-');
            }
            text.Append(mantissa.ToString(CultureInfo.InvariantCulture));
            text.Append('e');
            text.Append(exponent.ToString(CultureInfo.InvariantCulture));
            return Result<string>.Success(text.ToString());
        }

        /// <summary>
        /// Validates a JSON number token against the strict RFC 8259 number grammar and
        /// returns its Canonical JSON text: a signed decimal integer for integer tokens
        /// (must fit Int64, <c>-0</c> → <c>0</c>) or normalized scientific notation for
        /// fraction/exponent tokens (must stay finite; NaN, Infinity and overflow are
        /// rejected). <paramref name="token"/> must be the number token alone.
        /// </summary>
        public static Result<string> Canonicalize(string token)
        {
            var error = Scan(token, out var negative, out var intStart, out var intLength,
                out var fracStart, out var fracLength, out var expValue, out var isDouble);
            if (error != null)
            {
                return Result<string>.Failure(error);
            }
            if (!isDouble)
            {
                return TryParseInt64(token, intStart, intLength, negative, out var intValue)
                    ? Result<string>.Success(FormatInt64(intValue))
                    : Result<string>.Failure(Reason("JSON integer token is out of the Int64 range."));
            }
            var parsed = ParseDoubleSegments(token, negative, intStart, intLength, fracStart, fracLength, expValue);
            return parsed.Ok
                ? FormatDouble(parsed.Value)
                : Result<string>.Failure(parsed.Diagnostics);
        }

        /// <summary>
        /// Validates a JSON number token against the strict RFC 8259 number grammar and
        /// returns its value as binary64. Integer tokens convert through their Int64 value;
        /// fraction/exponent tokens use the exact decimal conversion. Overflow of the finite
        /// binary64 range fails.
        /// </summary>
        public static Result<double> ParseDouble(string token)
        {
            var error = Scan(token, out var negative, out var intStart, out var intLength,
                out var fracStart, out var fracLength, out var expValue, out var isDouble);
            if (error != null)
            {
                return Result<double>.Failure(error);
            }
            if (!isDouble)
            {
                return TryParseInt64(token, intStart, intLength, negative, out var intValue)
                    ? Result<double>.Success(intValue)
                    : Result<double>.Failure(Reason("JSON integer token is out of the Int64 range."));
            }
            return ParseDoubleSegments(token, negative, intStart, intLength, fracStart, fracLength, expValue);
        }

        /// <summary>
        /// Validates a JSON number token against the strict RFC 8259 number grammar and
        /// returns its Int64 value. Integer tokens only: a fraction or exponent marker, an
        /// out-of-range magnitude or any grammar violation fails.
        /// </summary>
        public static Result<long> ParseInt64(string token)
        {
            var error = Scan(token, out var negative, out var intStart, out var intLength,
                out var fracStart, out var fracLength, out var expValue, out var isDouble);
            if (error != null)
            {
                return Result<long>.Failure(error);
            }
            if (isDouble)
            {
                return Result<long>.Failure(Reason("JSON number token is not an integer."));
            }
            return TryParseInt64(token, intStart, intLength, negative, out var value)
                ? Result<long>.Success(value)
                : Result<long>.Failure(Reason("JSON integer token is out of the Int64 range."));
        }

        // Strict RFC 8259 number grammar: [-] (0 | [1-9]digits) [. digits] [(e|E)[+|-]digits].
        // Returns an internal diagnostic on the first violation, null on a valid token.
        private static Diagnostic? Scan(
            string token,
            out bool negative,
            out int intStart,
            out int intLength,
            out int fracStart,
            out int fracLength,
            out long expValue,
            out bool isDouble)
        {
            negative = false;
            intStart = 0;
            intLength = 0;
            fracStart = 0;
            fracLength = 0;
            expValue = 0;
            isDouble = false;

            if (string.IsNullOrEmpty(token))
            {
                return Reason("JSON number token is empty.");
            }
            var i = 0;
            var n = token.Length;
            if (token[i] == '-')
            {
                negative = true;
                i++;
            }
            if (i == n)
            {
                return Reason(InvalidGrammar);
            }

            if (token[i] == '0')
            {
                i++;
                if (i < n && IsDigit(token[i]))
                {
                    return Reason("JSON number token has a leading zero.");
                }
            }
            else if (token[i] >= '1' && token[i] <= '9')
            {
                do
                {
                    i++;
                }
                while (i < n && IsDigit(token[i]));
            }
            else
            {
                return Reason(InvalidGrammar);
            }
            intStart = negative ? 1 : 0;
            intLength = i - intStart;

            if (i < n && token[i] == '.')
            {
                i++;
                fracStart = i;
                while (i < n && IsDigit(token[i]))
                {
                    i++;
                }
                fracLength = i - fracStart;
                if (fracLength == 0)
                {
                    return Reason(InvalidGrammar);
                }
                isDouble = true;
            }

            if (i < n && (token[i] == 'e' || token[i] == 'E'))
            {
                i++;
                var expNegative = false;
                if (i < n && (token[i] == '+' || token[i] == '-'))
                {
                    expNegative = token[i] == '-';
                    i++;
                }
                var expDigits = 0;
                long acc = 0;
                while (i < n && IsDigit(token[i]))
                {
                    if (acc < ExponentSaturation)
                    {
                        acc = acc * 10 + (token[i] - '0');
                        if (acc > ExponentSaturation)
                        {
                            acc = ExponentSaturation;
                        }
                    }
                    expDigits++;
                    i++;
                }
                if (expDigits == 0)
                {
                    return Reason(InvalidGrammar);
                }
                expValue = expNegative ? -acc : acc;
                isDouble = true;
            }

            return i == n ? null : Reason(InvalidGrammar);
        }

        // Accumulates the integer-token digits; |value| <= 2^63 on success, and the sign
        // decides whether 2^63 is allowed (Int64.MinValue only).
        private static bool TryParseInt64(string token, int start, int length, bool negative, out long value)
        {
            const ulong maxMagnitude = 9223372036854775808UL; // |Int64.MinValue|
            var acc = 0UL;
            for (var i = start; i < start + length; i++)
            {
                if (acc > maxMagnitude / 10)
                {
                    value = 0;
                    return false;
                }
                acc = acc * 10 + (ulong)(token[i] - '0');
                if (acc > maxMagnitude)
                {
                    value = 0;
                    return false;
                }
            }
            if (acc == 0)
            {
                value = 0; // covers "-0"
                return true;
            }
            if (negative)
            {
                value = acc == maxMagnitude ? long.MinValue : -(long)acc;
                return true;
            }
            if (acc > long.MaxValue)
            {
                value = 0;
                return false;
            }
            value = (long)acc;
            return true;
        }

        // Exact decimal-to-binary64 for a validated fraction/exponent token. The decimal
        // value is M * 10^E where M is the concatenated significant digits and
        // E = exponent - fracLength.
        private static Result<double> ParseDoubleSegments(
            string token, bool negative, int intStart, int intLength,
            int fracStart, int fracLength, long expValue)
        {
            var e = expValue - fracLength;

            var significant = 0;
            var seenNonZero = false;
            CountSignificantDigits(token, intStart, intLength, ref seenNonZero, ref significant);
            CountSignificantDigits(token, fracStart, fracLength, ref seenNonZero, ref significant);
            if (!seenNonZero)
            {
                return Result<double>.Success(negative ? -0.0 : 0.0);
            }

            var order = (long)significant + e;
            if (order > MaxFiniteOrder)
            {
                return Result<double>.Failure(
                    Reason("JSON number token overflows the finite binary64 range."));
            }
            if (order < MinNonZeroOrder)
            {
                return Result<double>.Success(negative ? -0.0 : 0.0);
            }

            var mantissa = BigInteger.Zero;
            AccumulateDigits(token, intStart, intLength, ref mantissa);
            AccumulateDigits(token, fracStart, fracLength, ref mantissa);
            return Binary64.TryDecimalToDouble(negative, mantissa, e, out var value)
                ? Result<double>.Success(value)
                : Result<double>.Failure(
                    Reason("JSON number token overflows the finite binary64 range."));
        }

        private static void CountSignificantDigits(
            string token, int start, int length, ref bool seenNonZero, ref int significant)
        {
            for (var i = start; i < start + length; i++)
            {
                if (!seenNonZero && token[i] == '0')
                {
                    continue;
                }
                seenNonZero = true;
                significant++;
            }
        }

        private static void AccumulateDigits(string token, int start, int length, ref BigInteger mantissa)
        {
            for (var i = start; i < start + length; i++)
            {
                mantissa = mantissa * 10 + (token[i] - '0');
            }
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private const string InvalidGrammar = "JSON number token does not match the JSON number grammar.";

        // Internal diagnostic for SG-02: empty Code, fixed English reason; callers map to a
        // published OW-* code exactly once and attach offset/JsonPath before publishing.
        private static Diagnostic Reason(string message) => new Diagnostic(string.Empty, Severity.Error, message);
    }
}
