#nullable enable

using System;
using System.Numerics;

namespace OpWeave.Core
{
    /// <summary>
    /// Self-implemented Ryu shortest round-trip digit generation for finite binary64
    /// (SPEC §0.3 item 6, §T.4 L-12). Port of Ulf Adams' Ryu <c>d2d</c> algorithm in which
    /// the fixed-width 128-bit table multiplications are replaced by exact
    /// <see cref="BigInteger"/> arithmetic on the same scaled interval bounds, so Unity Mono
    /// and .NET produce identical digits without runtime-specific formatting.
    /// </summary>
    internal static class RyuFormatter
    {
        private const int MantissaBits = 52;
        private const int ExponentBits = 11;
        private const int Bias = 1023;

        // Index bound covers every i and q the algorithm can reach for binary64 inputs
        // (max i = 325 on the e2 < 0 path, max q = 290 on the e2 >= 0 path).
        private static readonly BigInteger[] s_pow5 = BuildPow5(342);

        private static BigInteger[] BuildPow5(int count)
        {
            var table = new BigInteger[count];
            table[0] = BigInteger.One;
            for (var i = 1; i < count; i++)
            {
                table[i] = table[i - 1] * 5;
            }
            return table;
        }

        /// <summary>
        /// Computes the shortest decimal digits that round-trip to
        /// <paramref name="positiveFinite"/> (finite and &gt; 0): the value equals
        /// <paramref name="mantissa"/> * 10^<paramref name="exponent"/>.
        /// </summary>
        public static void ShortestDigits(double positiveFinite, out ulong mantissa, out int exponent)
        {
            var bits = Binary64.ToBits(positiveFinite);
            var ieeeMantissa = bits & ((1UL << MantissaBits) - 1);
            var ieeeExponent = (uint)(bits >> MantissaBits) & ((1U << ExponentBits) - 1);

            int e2;
            ulong m2;
            if (ieeeExponent == 0)
            {
                // Subtract 2 so that the bounds computation has 2 additional bits.
                e2 = 1 - Bias - MantissaBits - 2;
                m2 = ieeeMantissa;
            }
            else
            {
                e2 = (int)ieeeExponent - Bias - MantissaBits - 2;
                m2 = (1UL << MantissaBits) | ieeeMantissa;
            }
            var acceptBounds = (m2 & 1) == 0;

            // Step 2: the interval of valid decimal representations (mv, vm, vp scaled by 4).
            var mv = 4 * m2;
            var mmShift = (ieeeMantissa != 0 || ieeeExponent <= 1) ? 1UL : 0UL;

            // Step 3: convert to a decimal power base with exact arithmetic:
            //   e2 >= 0: vr/vp/vm = floor(X * 2^(e2-q) / 5^q)
            //   e2 <  0: vr/vp/vm = floor(X * 5^(-e2-q) / 2^q)
            ulong vr;
            ulong vp;
            ulong vm;
            int e10;
            var vmIsTrailingZeros = false;
            var vrIsTrailingZeros = false;
            if (e2 >= 0)
            {
                var q = Log10Pow2(e2) - (e2 > 3 ? 1 : 0);
                e10 = q;
                var den = s_pow5[q];
                var sh = e2 - q;
                vr = ScaleDown(mv, sh, den);
                vp = ScaleDown(mv + 2, sh, den);
                vm = ScaleDown(mv - 1 - mmShift, sh, den);
                if (q <= 21)
                {
                    // Only one of mp, mv, and mm can be a multiple of 5, if any.
                    if (mv % 5 == 0)
                    {
                        vrIsTrailingZeros = MultipleOfPowerOf5(mv, q);
                    }
                    else if (acceptBounds)
                    {
                        vmIsTrailingZeros = MultipleOfPowerOf5(mv - 1 - mmShift, q);
                    }
                    else if (MultipleOfPowerOf5(mv + 2, q))
                    {
                        // The exclusive upper bound lands on an exact decimal: step inside.
                        vp -= 1;
                    }
                }
            }
            else
            {
                var q = Log10Pow5(-e2) - (-e2 > 1 ? 1 : 0);
                e10 = q + e2;
                var i = -e2 - q;
                var p5 = s_pow5[i];
                vr = ScaleUp(mv, p5, q);
                vp = ScaleUp(mv + 2, p5, q);
                vm = ScaleUp(mv - 1 - mmShift, p5, q);
                if (q <= 1)
                {
                    // {vr,vp,vm} is trailing zeros if {mv,mp,mm} has at least q trailing 0
                    // bits. mv = 4 * m2, so it always has at least two trailing 0 bits.
                    vrIsTrailingZeros = true;
                    if (acceptBounds)
                    {
                        // mm = mv - 1 - mmShift, so it has 1 trailing 0 bit iff mmShift == 1.
                        vmIsTrailingZeros = mmShift == 1;
                    }
                    else
                    {
                        // mp = mv + 2, so it always has at least one trailing 0 bit.
                        vp -= 1;
                    }
                }
                else if (q < 63)
                {
                    vrIsTrailingZeros = (mv & ((1UL << q) - 1)) == 0;
                }
            }

            // Step 4: find the shortest decimal representation in the interval.
            var removed = 0;
            var lastRemovedDigit = 0;
            ulong output;
            if (vmIsTrailingZeros || vrIsTrailingZeros)
            {
                // General case: digits may be removed while the true values end in zeros.
                for (;;)
                {
                    var vpDiv10 = vp / 10;
                    var vmDiv10 = vm / 10;
                    if (vpDiv10 <= vmDiv10)
                    {
                        break;
                    }
                    var vmMod10 = vm - 10 * vmDiv10;
                    var vrDiv10 = vr / 10;
                    var vrMod10 = vr - 10 * vrDiv10;
                    vmIsTrailingZeros &= vmMod10 == 0;
                    vrIsTrailingZeros &= lastRemovedDigit == 0;
                    lastRemovedDigit = (int)vrMod10;
                    vr = vrDiv10;
                    vp = vpDiv10;
                    vm = vmDiv10;
                    removed++;
                }
                if (vmIsTrailingZeros)
                {
                    for (;;)
                    {
                        var vmDiv10 = vm / 10;
                        var vmMod10 = vm - 10 * vmDiv10;
                        if (vmMod10 != 0)
                        {
                            break;
                        }
                        var vpDiv10 = vp / 10;
                        var vrDiv10 = vr / 10;
                        var vrMod10 = vr - 10 * vrDiv10;
                        vrIsTrailingZeros &= lastRemovedDigit == 0;
                        lastRemovedDigit = (int)vrMod10;
                        vr = vrDiv10;
                        vp = vpDiv10;
                        vm = vmDiv10;
                        removed++;
                    }
                }
                if (vrIsTrailingZeros && lastRemovedDigit == 5 && vr % 2 == 0)
                {
                    // Round even if the exact number is .....50..0.
                    lastRemovedDigit = 4;
                }
                // Take vr + 1 if vr is outside bounds or we need to round up.
                output = vr + (((vr == vm && (!acceptBounds || !vmIsTrailingZeros)) || lastRemovedDigit >= 5) ? 1UL : 0UL);
            }
            else
            {
                // Common case (~99.3%): no trailing-zero bookkeeping needed.
                var roundUp = false;
                var vpDiv100 = vp / 100;
                var vmDiv100 = vm / 100;
                if (vpDiv100 > vmDiv100)
                {
                    var vrDiv100 = vr / 100;
                    var vrMod100 = vr - 100 * vrDiv100;
                    roundUp = vrMod100 >= 50;
                    vr = vrDiv100;
                    vp = vpDiv100;
                    vm = vmDiv100;
                    removed += 2;
                }
                for (;;)
                {
                    var vpDiv10 = vp / 10;
                    var vmDiv10 = vm / 10;
                    if (vpDiv10 <= vmDiv10)
                    {
                        break;
                    }
                    var vrDiv10 = vr / 10;
                    var vrMod10 = vr - 10 * vrDiv10;
                    roundUp = vrMod10 >= 5;
                    vr = vrDiv10;
                    vp = vpDiv10;
                    vm = vmDiv10;
                    removed++;
                }
                // Take vr + 1 if vr is outside bounds or we need to round up.
                output = vr + ((vr == vm || roundUp) ? 1UL : 0UL);
            }

            mantissa = output;
            exponent = e10 + removed;
        }

        // floor(m * 2^sh / den) for the e2 >= 0 branch; sh >= 0 there.
        private static ulong ScaleDown(ulong m, int sh, BigInteger den) =>
            (ulong)(((BigInteger)m << sh) / den);

        // floor(m * p5 / 2^shr) for the e2 < 0 branch; shr >= 0 there.
        private static ulong ScaleUp(ulong m, BigInteger p5, int shr) =>
            (ulong)(((BigInteger)m * p5) >> shr);

        // floor(log10(2^e)) for 0 <= e <= 1650.
        private static int Log10Pow2(int e) => (int)(((uint)e * 78913) >> 18);

        // floor(log10(5^e)) for 0 <= e <= 2620.
        private static int Log10Pow5(int e) => (int)(((uint)e * 732923) >> 20);

        // Whether 5^q divides value; callers only rely on it for q <= 21.
        private static bool MultipleOfPowerOf5(ulong value, int q)
        {
            var count = 0;
            while (value % 5 == 0)
            {
                count++;
                value /= 5;
            }
            return count >= q;
        }
    }
}
