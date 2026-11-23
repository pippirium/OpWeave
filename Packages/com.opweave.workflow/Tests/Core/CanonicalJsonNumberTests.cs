#nullable enable

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for SPEC §0.3 items 5–7 and §T.4 L-12: the numeric rows of the §0.3.1
    /// normative table (CJ-12..CJ-30), the error rows (CJ-14, CJ-16, CJ-31, CJ-32), the
    /// boundary set, a fixed-seed round-trip property test and a cross-runtime canonical
    /// digest over the same sample. The same sources run under dotnet and Unity Mono
    /// (local:T-W1.2-02:numeric-vectors, :roundtrip-property, :cross-runtime-digest).
    /// </summary>
    internal sealed class CanonicalJsonNumberTests
    {
        // SPEC §0.3.1 numeric vectors CJ-12..CJ-30 (error rows are separate cases below).
        [TestCase("9223372036854775807", "9223372036854775807", TestName = "CJ-12 Int64 max")]
        [TestCase("-9223372036854775808", "-9223372036854775808", TestName = "CJ-13 Int64 min")]
        [TestCase("-0", "0", TestName = "CJ-15 integer negative zero")]
        [TestCase("1", "1", TestName = "CJ-17 integer token stays Int64")]
        [TestCase("1.0", "1e0", TestName = "CJ-18 fraction token is Double")]
        [TestCase("0.0", "0e0", TestName = "CJ-19 Double zero")]
        [TestCase("-0.0", "0e0", TestName = "CJ-20 Double negative zero")]
        [TestCase("10.0", "1e1", TestName = "CJ-21 trailing zeros into exponent")]
        [TestCase("1E+2", "1e2", TestName = "CJ-22 exponent-only token")]
        [TestCase("1.5", "15e-1", TestName = "CJ-23 negative exponent")]
        [TestCase("-0.25", "-25e-2", TestName = "CJ-24 negative value")]
        [TestCase("0.1", "1e-1", TestName = "CJ-25 shortest round-trip coefficient")]
        [TestCase("123.456", "123456e-3", TestName = "CJ-26 multi-digit coefficient")]
        [TestCase("2.5e-5", "25e-6", TestName = "CJ-27 exponent input")]
        [TestCase("9007199254740993.0", "9007199254740992e0", TestName = "CJ-28 2^53+1 rounding")]
        [TestCase("1.7976931348623157e308", "17976931348623157e292", TestName = "CJ-29 Double max")]
        [TestCase("5e-324", "5e-324", TestName = "CJ-30 smallest subnormal")]
        public void NumericVectorsProduceTheSpecCanonicalForm(string token, string expected)
        {
            var result = CanonicalJsonNumber.Canonicalize(token);
            Assert.IsTrue(result.Ok, $"Token {token} was rejected: {Describe(result)}");
            Assert.AreEqual(expected, result.Value, $"Token {token}");
        }

        // SPEC §0.3.1 error rows and the strict grammar / range boundaries around them.
        [TestCase("9223372036854775808", TestName = "CJ-14 Int64 range exceeded")]
        [TestCase("01", TestName = "CJ-16 leading zero")]
        [TestCase("1e309", TestName = "CJ-31 overflow")]
        [TestCase("NaN", TestName = "CJ-32 NaN is not a JSON number")]
        [TestCase("Infinity", TestName = "CJ-32 Infinity is not a JSON number")]
        [TestCase("-9223372036854775809", TestName = "below Int64 min")]
        [TestCase("18446744073709551616", TestName = "far beyond Int64 range")]
        [TestCase("-1e309", TestName = "negative overflow")]
        [TestCase("9e999", TestName = "extreme exponent overflow")]
        [TestCase("-01", TestName = "negative leading zero")]
        [TestCase("00", TestName = "zero leading zero")]
        [TestCase("+1", TestName = "leading plus rejected")]
        [TestCase("1.", TestName = "fraction requires digits")]
        [TestCase(".5", TestName = "integer part required")]
        [TestCase("1e", TestName = "exponent requires digits")]
        [TestCase("1e+", TestName = "exponent sign requires digits")]
        [TestCase("-", TestName = "sign only")]
        [TestCase("", TestName = "empty token")]
        [TestCase("0x10", TestName = "hex is not JSON")]
        [TestCase("1 0", TestName = "embedded space")]
        [TestCase("1_0", TestName = "separator not JSON")]
        [TestCase("1.2.3", TestName = "second fraction point")]
        [TestCase("1e2.5", TestName = "fraction after exponent")]
        public void InvalidTokensAreRejectedWithInternalDiagnostics(string token)
        {
            var result = CanonicalJsonNumber.Canonicalize(token);
            Assert.IsFalse(result.Ok, $"Token {token} unexpectedly canonicalized to {result.Value}");
            Assert.IsNotEmpty(result.Diagnostics, $"Token {token} failed without a diagnostic");
            foreach (var diagnostic in result.Diagnostics)
            {
                // SG-02: low-level failures carry an empty internal code (never a published
                // OW-* code) and a fixed English reason; the caller maps to OW-* once.
                Assert.AreEqual(string.Empty, diagnostic.Code);
                Assert.AreEqual(Severity.Error, diagnostic.Severity);
                Assert.IsFalse(string.IsNullOrEmpty(diagnostic.Message));
                Assert.IsFalse(ErrorCodes.IsPublished(diagnostic.Code));
            }
        }

        // Boundary values around the normative vectors: exact powers, subnormal edges,
        // decimal-order transitions and coefficient/exponent normalization.
        [TestCase("2.2250738585072014e-308", "22250738585072014e-324", TestName = "smallest normal")]
        [TestCase("4.9406564584124654e-324", "5e-324", TestName = "decimal of min subnormal")]
        [TestCase("2.2250738585072009e-308", "2225073858507201e-323", TestName = "largest subnormal")]
        [TestCase("1e-324", "0e0", TestName = "underflow below half of min subnormal")]
        [TestCase("3e-324", "5e-324", TestName = "above half of min subnormal")]
        [TestCase("9007199254740991.0", "9007199254740991e0", TestName = "2^53-1 exact")]
        [TestCase("9007199254740992.0", "9007199254740992e0", TestName = "2^53 exact")]
        [TestCase("9007199254740994.0", "9007199254740994e0", TestName = "2^53+2 exact")]
        [TestCase("1234567890.0", "123456789e1", TestName = "trailing zero into exponent")]
        [TestCase("5.9604644775390625e-8", "5960464477539063e-23", TestName = "2^-24 shortest digits")]
        [TestCase("1e22", "1e22", TestName = "power of ten")]
        [TestCase("1e23", "1e23", TestName = "power of ten past exact range")]
        [TestCase("1e100", "1e100", TestName = "large power of ten")]
        [TestCase("1e-7", "1e-7", TestName = "small power of ten")]
        [TestCase("2.0e-1", "2e-1", TestName = "trailing fraction zero")]
        [TestCase("0e0", "0e0", TestName = "canonical zero input")]
        [TestCase("1e0", "1e0", TestName = "canonical form input")]
        [TestCase("-5e-324", "-5e-324", TestName = "negative min subnormal")]
        [TestCase("0.5", "5e-1", TestName = "one half")]
        [TestCase("3.0", "3e0", TestName = "integral double")]
        [TestCase("0.3333333333333333", "3333333333333333e-16", TestName = "one third")]
        public void BoundaryValuesProduceTheSpecCanonicalForm(string token, string expected)
        {
            var result = CanonicalJsonNumber.Canonicalize(token);
            Assert.IsTrue(result.Ok, $"Token {token} was rejected: {Describe(result)}");
            Assert.AreEqual(expected, result.Value, $"Token {token}");
        }

        [Test]
        public void IntegerAndDoubleFormsStayDistinct()
        {
            // SPEC §0.3 item 7: Int64 1 and Double 1.0 are different canonical forms.
            Assert.AreEqual("1", CanonicalJsonNumber.Canonicalize("1").Value);
            Assert.AreEqual("1e0", CanonicalJsonNumber.Canonicalize("1.0").Value);
            Assert.AreEqual("1e0", CanonicalJsonNumber.Canonicalize("1e0").Value);
            Assert.AreEqual("1", CanonicalJsonNumber.FormatInt64(1));
            Assert.AreEqual("1e0", CanonicalJsonNumber.FormatDouble(1.0).Value);
        }

        [Test]
        public void FormatDoubleRejectsNonFiniteValues()
        {
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var result = CanonicalJsonNumber.FormatDouble(value);
                Assert.IsFalse(result.Ok);
                Assert.AreEqual(string.Empty, result.Diagnostics[0].Code);
            }
        }

        [Test]
        public void RoundTripPropertyHoldsOverBoundaryAndRandomDoubles()
        {
            var count = 0;
            foreach (var value in FixedSeedOrderedSample())
            {
                var formatted = CanonicalJsonNumber.FormatDouble(value);
                Assert.IsTrue(formatted.Ok, $"Format failed for bits {Binary64.ToBits(value):X16}");
                var parsed = CanonicalJsonNumber.ParseDouble(formatted.Value!);
                Assert.IsTrue(parsed.Ok, $"Parse failed for canonical {formatted.Value}");
                var reparsed = parsed.Value;
                if (value == 0.0)
                {
                    Assert.AreEqual(0.0, reparsed, $"canonical {formatted.Value}");
                    Assert.AreEqual("0e0", formatted.Value);
                }
                else
                {
                    Assert.AreEqual(
                        Binary64.ToBits(value), Binary64.ToBits(reparsed),
                        $"bits {Binary64.ToBits(value):X16} -> {formatted.Value} -> bits {Binary64.ToBits(reparsed):X16}");
                }
                // Canonical form is a fixed point of format(parse(format(x))).
                Assert.AreEqual(formatted.Value, CanonicalJsonNumber.FormatDouble(reparsed).Value);
                count++;
            }

            Assert.AreEqual(FixedSeedSampleCount, count);
        }

        [Test]
        public void FixedSeedSampleProducesTheCrossRuntimeCanonicalDigest()
        {
            // SPEC §T.4 L-12: the property test proves each runtime round-trips its own
            // output; this digest proves the same fixed-seed ordered sample yields
            // byte-identical canonical strings on Unity Mono and .NET. Both runtimes hash
            // the UTF-8 bytes of every canonical output joined with "\n" and must equal
            // the constant below.
            var outputs = new List<string>(FixedSeedSampleCount);
            foreach (var value in FixedSeedOrderedSample())
            {
                var formatted = CanonicalJsonNumber.FormatDouble(value);
                Assert.IsTrue(formatted.Ok, $"Format failed for bits {Binary64.ToBits(value):X16}");
                outputs.Add(formatted.Value!);
            }
            Assert.AreEqual(FixedSeedSampleCount, outputs.Count);

            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", outputs)));
            }
            Assert.AreEqual(FixedSeedSampleSha256, ToLowerHex(hash));
        }

        // 2047 finite exponent fields x 6 boundary mantissas + 639 powers of ten
        // (k = -330..308) + 20000 finite fixed-seed bit patterns.
        private const int FixedSeedSampleCount = 32921;
        private const string FixedSeedSampleSha256 =
            "02c76189c21d182e3f932ec1335fa56a6aa882320dc03c2a79b47f85806b6b70";

        // The fixed-seed ordered sample shared by the round-trip property test and the
        // cross-runtime digest test: every finite exponent field with the boundary
        // mantissas, the powers-of-ten sweep, then 20000 finite xorshift64* bit
        // patterns, always in this order so both runtimes hash the same byte sequence.
        private static IEnumerable<double> FixedSeedOrderedSample()
        {
            // Boundary set: every exponent field with boundary mantissas.
            ulong[] mantissas = { 0x0000000000000000UL, 0x0000000000000001UL, 0x0008000000000000UL,
                                  0x000FFFFFFFFFFFFFUL, 0x000AAAAAAAAAAAAAUL, 0x0005555555555555UL };
            for (uint exponentField = 0; exponentField <= 0x7FE; exponentField++)
            {
                foreach (var mantissa in mantissas)
                {
                    yield return Binary64.FromBits(((ulong)exponentField << 52) | mantissa);
                }
            }

            // Powers of ten through their decimal tokens, incl. subnormal range.
            for (var k = -330; k <= 308; k++)
            {
                var tokenResult = CanonicalJsonNumber.ParseDouble(
                    "1e" + k.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.IsTrue(tokenResult.Ok, $"1e{k} rejected");
                yield return tokenResult.Value;
            }

            // xorshift64* with a fixed seed: identical sequences on Mono and .NET.
            ulong state = 0x9E3779B97F4A7C15UL;
            var remaining = 20000;
            while (remaining > 0)
            {
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                var value = Binary64.FromBits(state * 0x2545F4914F6CDD1DUL);
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    continue;
                }
                yield return value;
                remaining--;
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[i * 2 + 1] = digits[bytes[i] & 0xF];
            }
            return new string(chars);
        }

#if NET6_0_OR_GREATER
        [Test]
        public void CanonicalDigitsMatchTheDotnetRyuReference()
        {
            // dotnet-only cross-check (not part of the product contract): .NET's own
            // shortest round-trip text is Ryu, so canonicalizing it must reproduce our
            // formatter's digits. This test does not run under Unity Mono, where the
            // runtime formatter is not a valid reference (SPEC §T.4 L-12).
            ulong state = 0xDEADBEEFCAFEF00DUL;
            ulong Next()
            {
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                return state * 0x2545F4914F6CDD1DUL;
            }

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var count = 0;
            for (var i = 0; i < 50000; i++)
            {
                var value = Binary64.FromBits(Next());
                if (double.IsNaN(value) || double.IsInfinity(value) || value == 0.0)
                {
                    continue;
                }
                var runtimeShortest = value.ToString(culture);
                if (runtimeShortest.IndexOf('.') < 0 && runtimeShortest.IndexOf('e') < 0 &&
                    runtimeShortest.IndexOf('E') < 0)
                {
                    // The runtime prints integral doubles like an integer token; keep the
                    // Double class (SPEC §0.3 item 7 typed equality).
                    runtimeShortest += ".0";
                }
                var viaRuntime = CanonicalJsonNumber.Canonicalize(runtimeShortest);
                var ours = CanonicalJsonNumber.FormatDouble(value);
                Assert.IsTrue(viaRuntime.Ok, $"runtime text {runtimeShortest} rejected");
                Assert.AreEqual(
                    ours.Value, viaRuntime.Value,
                    $"bits {Binary64.ToBits(value):X16}: runtime {runtimeShortest} -> {viaRuntime.Value}, ours {ours.Value}");
                count++;
            }
            Assert.Greater(count, 40000);
        }
#endif

        private static string Describe<T>(Result<T> result)
        {
            var messages = new string[result.Diagnostics.Count];
            for (var i = 0; i < messages.Length; i++)
            {
                messages[i] = result.Diagnostics[i].Message;
            }
            return string.Join("; ", messages);
        }
    }
}
