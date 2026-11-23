#nullable enable

using System;
using System.Numerics;

namespace OpWeave.Core
{
    /// <summary>
    /// Binary64 bit helpers and the exact decimal-to-binary64 conversion used by the
    /// Canonical JSON number rules (SPEC §0.3 item 6, §T.4 L-12). All arithmetic is exact
    /// <see cref="BigInteger"/> math with round-to-nearest, ties to even, so Unity Mono and
    /// .NET produce identical results without relying on runtime-specific parsing.
    /// </summary>
    internal static class Binary64
    {
        private const ulong SignMask = 1UL << 63;
        private const int MaxFiniteExponent = 1023; // largest e with 2^e <= double.MaxValue
        private const int MinNormalExponent = -1022;
        private const int MantissaBits = 52;
        private const int SubnormalScale = 1074; // subnormals are m * 2^-1074

        private static readonly BigInteger s_two53 = BigInteger.One << 53;
        private static readonly BigInteger s_two52 = BigInteger.One << 52;
        private static readonly BigInteger s_ten = new BigInteger(10);

        // Powers of ten up to the magnitude window a caller may pass after bounding the
        // value's decimal order; larger exponents are computed on demand.
        private static readonly BigInteger[] s_pow10 = BuildPow10(512);

        private static BigInteger[] BuildPow10(int count)
        {
            var table = new BigInteger[count];
            table[0] = BigInteger.One;
            for (var i = 1; i < count; i++)
            {
                table[i] = table[i - 1] * s_ten;
            }
            return table;
        }

        private static BigInteger Pow10(long e)
        {
            // The caller's decimal-order bound keeps |e| near the token's digit count, so the
            // cast only overflows for absurd >2^31-digit tokens; clamp defensively there.
            var i = (int)Math.Max(int.MinValue + 1, Math.Min(int.MaxValue - 1, e));
            return i >= 0 && i < s_pow10.Length ? s_pow10[i] : BigInteger.Pow(s_ten, i);
        }

        /// <summary>The raw IEEE 754 binary64 bits of <paramref name="value"/>.</summary>
        public static ulong ToBits(double value) => unchecked((ulong)BitConverter.DoubleToInt64Bits(value));

        /// <summary>The binary64 value whose raw IEEE 754 bits are <paramref name="bits"/>.</summary>
        public static double FromBits(ulong bits) =>
            BitConverter.ToDouble(BitConverter.GetBytes(unchecked((long)bits)), 0);

        /// <summary>
        /// Converts the finite decimal value <c>mantissa * 10^exponent10</c> to the nearest
        /// binary64 (round half to even). Returns false when the rounded result overflows
        /// the finite binary64 range; underflow below the smallest subnormal yields
        /// signed zero.
        /// </summary>
        public static bool TryDecimalToDouble(
            bool negative, BigInteger mantissa, long exponent10, out double value)
        {
            if (mantissa.IsZero)
            {
                value = negative ? -0.0 : 0.0;
                return true;
            }

            // value = a / b with a, b positive integers.
            BigInteger a;
            BigInteger b;
            if (exponent10 >= 0)
            {
                a = mantissa * Pow10(exponent10);
                b = BigInteger.One;
            }
            else
            {
                a = mantissa;
                b = Pow10(-exponent10);
            }

            var e = FloorLog2(a, b);
            if (e >= MinNormalExponent)
            {
                if (e > MaxFiniteExponent)
                {
                    value = 0.0;
                    return false;
                }

                // q = round((a/b) * 2^(52-e)); 2^52 <= q <= 2^53.
                var s = MantissaBits - e;
                var q = s >= 0
                    ? DivRoundHalfEven(a << s, b)
                    : DivRoundHalfEven(a, b << (-s));
                if (q == s_two53)
                {
                    q = s_two52;
                    e++;
                    if (e > MaxFiniteExponent)
                    {
                        value = 0.0;
                        return false;
                    }
                }

                var normalBits = ((ulong)(e + 1023) << MantissaBits) | (ulong)(q - s_two52);
                value = FromBits(negative ? normalBits | SignMask : normalBits);
                return true;
            }

            // Subnormal range: value = m * 2^-1074 for a 52-bit m.
            var m = DivRoundHalfEven(a << SubnormalScale, b);
            if (m.IsZero)
            {
                value = negative ? -0.0 : 0.0;
                return true;
            }

            // m == 2^52 legitimately encodes the smallest normal number (exponent field 1).
            var bits = (ulong)m;
            value = FromBits(negative ? bits | SignMask : bits);
            return true;
        }

        /// <summary>floor(log2(a / b)) for positive integers a, b.</summary>
        private static int FloorLog2(BigInteger a, BigInteger b)
        {
            var e = BitLength(a) - BitLength(b);
            var ge = e >= 0 ? a >= (b << e) : (a << (-e)) >= b;
            return ge ? e : e - 1;
        }

        /// <summary>Number of bits in the binary representation of positive <paramref name="v"/>.</summary>
        private static int BitLength(BigInteger v)
        {
            var bytes = v.ToByteArray(); // little-endian two's complement; v > 0 here.
            var i = bytes.Length - 1;
            while (i > 0 && bytes[i] == 0)
            {
                i--;
            }
            var top = bytes[i];
            var bits = i * 8;
            while (top != 0)
            {
                bits++;
                top >>= 1;
            }
            return bits;
        }

        /// <summary>round(num / den) to nearest integer, ties to even, for positive operands.</summary>
        private static BigInteger DivRoundHalfEven(BigInteger num, BigInteger den)
        {
            var q = BigInteger.DivRem(num, den, out var rem);
            var cmp = (rem << 1).CompareTo(den);
            if (cmp > 0 || (cmp == 0 && !q.IsEven))
            {
                q += BigInteger.One;
            }
            return q;
        }
    }
}
