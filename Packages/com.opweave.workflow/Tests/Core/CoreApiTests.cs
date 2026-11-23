#nullable enable

using System;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §B0.2 Core public API version and compatibility
    /// range (T-W1.5-01): the <see cref="CoreApi"/> constants, strict
    /// <c>MAJOR.MINOR.PATCH</c> parsing, numeric Major → Minor → Patch ordering
    /// and the half-open <see cref="CoreApiRange.Contains"/> check
    /// (<c>minInclusive &lt;= v &lt; maxExclusive</c>). Parse failures carry the
    /// internal empty-code diagnostic; no published OW code is emitted here
    /// (OW-OP-012 belongs to W2.12).
    /// </summary>
    internal sealed class CoreApiTests
    {
        private static CoreApiVersion MustParseVersion(string text)
        {
            var result = CoreApiVersion.Parse(text);
            Assert.IsTrue(result.Ok, "Parse failed: " + string.Join(
                "; ", System.Linq.Enumerable.Select(
                    result.Diagnostics, d => d.Code + " " + d.Message + " " + d.Hint)));
            return result.Value;
        }

        private static CoreApiRange MustParseRange(string minInclusive, string maxExclusive)
        {
            var result = CoreApiRange.Parse(minInclusive, maxExclusive);
            Assert.IsTrue(result.Ok, "Parse failed: " + string.Join(
                "; ", System.Linq.Enumerable.Select(
                    result.Diagnostics, d => d.Code + " " + d.Message + " " + d.Hint)));
            return result.Value!;
        }

        private static void MustFailVersion(string text)
        {
            var result = CoreApiVersion.Parse(text);
            Assert.IsFalse(result.Ok, "Parse unexpectedly succeeded: '" + text + "'");
            Assert.AreEqual(default(CoreApiVersion), result.Value, text);
            Assert.AreEqual(1, result.Diagnostics.Count, text);
            var diagnostic = result.Diagnostics[0];
            // No published code is emitted at this layer (SPEC §B0.2 / ticket):
            // W2.12 maps the failure to OW-OP-012.
            Assert.AreEqual(string.Empty, diagnostic.Code, text);
            Assert.AreEqual(Severity.Error, diagnostic.Severity, text);
            Assert.IsFalse(diagnostic.Retryable, text);
        }

        private static void MustFailRange(string minInclusive, string maxExclusive)
        {
            var result = CoreApiRange.Parse(minInclusive, maxExclusive);
            Assert.IsFalse(result.Ok,
                $"Parse unexpectedly succeeded: '{minInclusive}'..'{maxExclusive}'");
            Assert.IsNull(result.Value);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual(string.Empty, diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Retryable);
        }

        // ── SPEC §B0.2 — CoreApi constants ─────────────────────────────────────

        [Test]
        public void CoreApiVersionIs_1_0_0() =>
            Assert.AreEqual("1.0.0", CoreApi.Version);

        [Test]
        public void CoreApiCurrentParsesVersion()
        {
            Assert.AreEqual(new CoreApiVersion(1, 0, 0), CoreApi.Current);
            Assert.AreEqual(MustParseVersion(CoreApi.Version), CoreApi.Current);
        }

        // ── SPEC §B0.2 test — [1.0.0, 2.0.0) membership ───────────────────────

        [Test]
        public void RangeContainsLowerBound()
        {
            var range = MustParseRange("1.0.0", "2.0.0");
            Assert.IsTrue(range.Contains(MustParseVersion("1.0.0")));
            Assert.IsTrue(range.Contains(CoreApi.Current));
        }

        [TestCase("1.0.1")]
        [TestCase("1.9.9")]
        [TestCase("1.99.99")]
        public void RangeContainsInterior(string text) =>
            Assert.IsTrue(MustParseRange("1.0.0", "2.0.0").Contains(MustParseVersion(text)));

        [Test]
        public void RangeExcludesUpperBound() =>
            Assert.IsFalse(
                MustParseRange("1.0.0", "2.0.0").Contains(MustParseVersion("2.0.0")));

        [TestCase("0.0.0")]
        [TestCase("0.9.9")]
        [TestCase("2.0.1")]
        [TestCase("3.0.0")]
        public void RangeExcludesOutside(string text) =>
            Assert.IsFalse(
                MustParseRange("1.0.0", "2.0.0").Contains(MustParseVersion(text)));

        [Test]
        public void ConstructedRangeBehavesLikeParsed()
        {
            var range = new CoreApiRange(
                new CoreApiVersion(1, 0, 0), new CoreApiVersion(2, 0, 0));
            Assert.IsTrue(range.Contains(new CoreApiVersion(1, 0, 0)));
            Assert.IsFalse(range.Contains(new CoreApiVersion(2, 0, 0)));
            Assert.AreEqual(range, MustParseRange("1.0.0", "2.0.0"));
        }

        // ── SPEC §B0.2 — strict MAJOR.MINOR.PATCH grammar ──────────────────────

        [TestCase("0.0.0", 0, 0, 0)]
        [TestCase("1.0.0", 1, 0, 0)]
        [TestCase("10.20.30", 10, 20, 30)]
        [TestCase("2147483647.0.0", int.MaxValue, 0, 0)]
        public void ValidVersionsParse(string text, int major, int minor, int patch)
        {
            var version = MustParseVersion(text);
            Assert.AreEqual(major, version.Major);
            Assert.AreEqual(minor, version.Minor);
            Assert.AreEqual(patch, version.Patch);
        }

        // SPEC §B0.2 test: "1.0", "01.0.0" and "1.0.0-beta" are parse errors.
        [TestCase("1.0")]
        [TestCase("01.0.0")]
        [TestCase("1.0.0-beta")]
        [TestCase("")]
        [TestCase("1")]
        [TestCase("1.")]
        [TestCase("1.0.")]
        [TestCase("1.0.0.0")]      // extra component
        [TestCase(".1.0.0")]
        [TestCase("1..0")]
        [TestCase("1.0.0+abc")]    // build metadata
        [TestCase("1.0.0-alpha.1")]
        [TestCase("00.0.0")]       // leading zeros
        [TestCase("1.01.0")]
        [TestCase("1.0.00")]
        [TestCase("-1.0.0")]       // sign
        [TestCase("+1.0.0")]
        [TestCase("1.-0.0")]
        [TestCase(" 1.0.0")]       // whitespace
        [TestCase("1.0.0 ")]
        [TestCase("1 .0.0")]
        [TestCase("v1.0.0")]
        [TestCase("a.b.c")]
        [TestCase("2147483648.0.0")] // outside the Int32 range
        [TestCase("1.0.0.")]        // trailing dot after full version
        public void MalformedVersionsFailToParse(string text) => MustFailVersion(text);

        [Test]
        public void NullVersionTextThrows() =>
            Assert.Throws<ArgumentNullException>(() => CoreApiVersion.Parse(null!));

        // ── SPEC §B0.2 — range parse ───────────────────────────────────────────

        // SPEC §B0.2 test: min >= max is a parse error.
        [TestCase("1.0.0", "1.0.0")]
        [TestCase("2.0.0", "1.0.0")]
        [TestCase("1.0.0", "0.9.9")]
        public void MinNotBelowMaxFailsToParse(string minInclusive, string maxExclusive) =>
            MustFailRange(minInclusive, maxExclusive);

        [TestCase("1.0", "2.0.0")]     // malformed min
        [TestCase("1.0.0", "2.0")]     // malformed max
        [TestCase("beta", "2.0.0")]
        [TestCase("1.0.0", "")]
        public void MalformedBoundFailsToParse(string minInclusive, string maxExclusive) =>
            MustFailRange(minInclusive, maxExclusive);

        [Test]
        public void NullRangeBoundThrows()
        {
            Assert.Throws<ArgumentNullException>(() => CoreApiRange.Parse(null!, "2.0.0"));
            Assert.Throws<ArgumentNullException>(() => CoreApiRange.Parse("1.0.0", null!));
        }

        [Test]
        public void RangeBoundFailureNamesTheBound()
        {
            var minFailure = CoreApiRange.Parse("x", "2.0.0");
            Assert.IsFalse(minFailure.Ok);
            StringAssert.Contains("minInclusive", minFailure.Diagnostics[0].Message);
            var maxFailure = CoreApiRange.Parse("1.0.0", "x");
            Assert.IsFalse(maxFailure.Ok);
            StringAssert.Contains("maxExclusive", maxFailure.Diagnostics[0].Message);
        }

        // ── SPEC §B0.2 rule 2 — numeric ordering Major → Minor → Patch ────────

        [Test]
        public void OrderingIsNumericNotLexicographic()
        {
            // Lexicographic order would put "1.10.0" before "1.9.0".
            Assert.Greater(MustParseVersion("1.10.0").CompareTo(MustParseVersion("1.9.0")), 0);
            Assert.Greater(new CoreApiVersion(2, 0, 0).CompareTo(new CoreApiVersion(1, 99, 99)), 0);
            Assert.Greater(new CoreApiVersion(1, 0, 1).CompareTo(new CoreApiVersion(1, 0, 0)), 0);
            Assert.AreEqual(0,
                new CoreApiVersion(1, 0, 0).CompareTo(new CoreApiVersion(1, 0, 0)));
            Assert.Less(new CoreApiVersion(0, 9, 9).CompareTo(new CoreApiVersion(1, 0, 0)), 0);
        }

        [Test]
        public void VersionEqualityIsByValue()
        {
            var a = MustParseVersion("1.2.3");
            var b = new CoreApiVersion(1, 2, 3);
            Assert.AreEqual(a, b);
            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.AreNotEqual(a, new CoreApiVersion(1, 2, 4));
        }

        [Test]
        public void VersionDeconstructsIntoComponents()
        {
            var (major, minor, patch) = MustParseVersion("1.2.3");
            Assert.AreEqual(1, major);
            Assert.AreEqual(2, minor);
            Assert.AreEqual(3, patch);
        }

        [Test]
        public void VersionImplementsIComparable() =>
            Assert.IsInstanceOf<IComparable<CoreApiVersion>>(CoreApi.Current);
    }
}
