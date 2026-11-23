#nullable enable

using System;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for Canonical JSON v1 (SPEC §0.3, §0.6 SG-02): the complete §0.3.1
    /// normative vector table CJ-01..CJ-35 byte-for-byte, parse-time rejection of the
    /// error vectors, strict text-level fixtures (comments and duplicate keys at depth),
    /// the SG-02 internal-diagnostic contract and its single mapping to the owning
    /// layer's OW-* code, and JToken-side validation. The same sources run under dotnet
    /// and Unity Mono (local:T-W1.2-03:cj-vectors-both-runtimes; G-01/AT-F04 evidence is
    /// produced at Phase Done).
    /// </summary>
    internal sealed class CanonicalJsonTests
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        // SPEC §0.3.1 normative vectors: input text -> expected canonical UTF-8 text.
        [TestCase("{ \"b\" : 1, \"a\" : 2 }", "{\"a\":2,\"b\":1}", TestName = "CJ-01 whitespace removal and key sort")]
        [TestCase("{\"\uFF71\":1,\"\uD83D\uDE00\":2,\"\u00E9\":3,\"a\":4}",
            "{\"a\":4,\"\u00E9\":3,\"\uD83D\uDE00\":2,\"\uFF71\":1}",
            TestName = "CJ-02 UTF-16 code unit key order (surrogate pair below U+FF71)")]
        [TestCase("\"\u00E9\"", "\"\u00E9\"", TestName = "CJ-03 non-ASCII kept literal")]
        [TestCase("\"e\\u0301\"", "\"e\u0301\"", TestName = "CJ-04 decomposed form kept (no NFC)")]
        [TestCase("\"\uD83D\uDE00\"", "\"\uD83D\uDE00\"", TestName = "CJ-05 surrogate pair emitted as one UTF-8 char")]
        [TestCase("\"a\\\"b\\\\c/d\"", "\"a\\\"b\\\\c/d\"", TestName = "CJ-07 only quote and reverse solidus escaped")]
        [TestCase("{\"a\":null,\"b\":[null]}", "{\"a\":null,\"b\":[null]}", TestName = "CJ-09 explicit null preserved")]
        [TestCase("[3,1,2]", "[3,1,2]", TestName = "CJ-10 array order preserved")]
        [TestCase("{\"z\":[{\"b\":2,\"a\":1}],\"a\":{\"d\":[],\"c\":{}}}",
            "{\"a\":{\"c\":{},\"d\":[]},\"z\":[{\"a\":1,\"b\":2}]}",
            TestName = "CJ-11 nested sort with empty containers")]
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
        [TestCase("\"\\u0008\\u0009\\u000A\\u000C\\u000D\"", "\"\\b\\t\\n\\f\\r\"",
            TestName = "CJ-33 short-form control escapes")]
        public void NormativeVectorProducesTheExpectedCanonicalBytes(string input, string expected)
        {
            var parsed = CanonicalJson.Parse(input);
            Assert.IsTrue(parsed.Ok, $"Input was rejected: {Describe(parsed)}");
            var canonical = CanonicalJson.Serialize(parsed.Value!);
            Assert.AreEqual(expected, canonical);
            CollectionAssert.AreEqual(Utf8.GetBytes(expected), Utf8.GetBytes(canonical));
        }

        // CJ-34 expects literal bytes: '"', , , 0x7F, '"'.
        [Test]
        public void Cj34ControlsUseLowercaseHexAndU007FStaysLiteral()
        {
            var parsed = CanonicalJson.Parse("\"\\u0000\\u001F\\u007F\"");
            Assert.IsTrue(parsed.Ok, Describe(parsed));
            var expected = new byte[]
            {
                0x22, 0x5C, 0x75, 0x30, 0x30, 0x30, 0x30,
                0x5C, 0x75, 0x30, 0x30, 0x31, 0x66, 0x7F, 0x22
            };
            CollectionAssert.AreEqual(expected, Utf8.GetBytes(CanonicalJson.Serialize(parsed.Value!)));
        }

        // Byte-level assertions for the non-ASCII rows (SPEC §0.3.1 parenthesized bytes).
        [Test]
        public void NonAsciiVectorsProduceTheExactUtf8ByteSequences()
        {
            var eacute = CanonicalJson.Parse("\"\u00E9\"");
            Assert.IsTrue(eacute.Ok);
            CollectionAssert.AreEqual(
                new byte[] { 0x22, 0xC3, 0xA9, 0x22 },
                Utf8.GetBytes(CanonicalJson.Serialize(eacute.Value!)), "CJ-03");

            var decomposed = CanonicalJson.Parse("\"e\\u0301\"");
            Assert.IsTrue(decomposed.Ok);
            CollectionAssert.AreEqual(
                new byte[] { 0x22, 0x65, 0xCC, 0x81, 0x22 },
                Utf8.GetBytes(CanonicalJson.Serialize(decomposed.Value!)), "CJ-04");

            var emoji = CanonicalJson.Parse("\"\uD83D\uDE00\"");
            Assert.IsTrue(emoji.Ok);
            CollectionAssert.AreEqual(
                new byte[] { 0x22, 0xF0, 0x9F, 0x98, 0x80, 0x22 },
                Utf8.GetBytes(CanonicalJson.Serialize(emoji.Value!)), "CJ-05");
        }

        // SPEC §0.3.1 error vectors are rejected at the parse stage (no canonical form).
        [TestCase("\"\\ud800\"", TestName = "CJ-06 lone surrogate escape")]
        [TestCase("{\"a\":1,\"a\":2}", TestName = "CJ-08 duplicate key")]
        [TestCase("9223372036854775808", TestName = "CJ-14 Int64 range exceeded")]
        [TestCase("01", TestName = "CJ-16 leading zero")]
        [TestCase("1e309", TestName = "CJ-31 overflow")]
        [TestCase("NaN", TestName = "CJ-32 NaN is not a JSON number")]
        [TestCase("Infinity", TestName = "CJ-32 Infinity is not a JSON number")]
        public void ErrorVectorsAreRejectedAtParseTime(string input)
        {
            var parsed = CanonicalJson.Parse(input);
            Assert.IsFalse(parsed.Ok, $"Input unexpectedly canonicalized: {input}");
            AssertInternalDiagnostics(parsed);
        }

        // Strict text-level rejection fixtures: comments, duplicate keys and grammar
        // violations anywhere in the document, nested at depth.
        [TestCase("{\"a\":1 /* comment */}", TestName = "block comment inside object")]
        [TestCase("/* comment */ {\"a\":1}", TestName = "block comment before root")]
        [TestCase("{\"a\":// line\n1}", TestName = "line comment inside object")]
        [TestCase("{\"a\":{\"b\":[1,/* c */2]}}", TestName = "comment nested at depth")]
        [TestCase("{\"a\":{\"b\":{\"c\":1,\"c\":2}}}", TestName = "duplicate key nested at depth")]
        [TestCase("[{\"x\":0,\"x\":1}]", TestName = "duplicate key inside array item")]
        [TestCase("{\"a\":1,\"\\u0061\":2}", TestName = "duplicate key via escape decoding")]
        [TestCase("{\"a\":1,}", TestName = "trailing comma in object")]
        [TestCase("[1,]", TestName = "trailing comma in array")]
        [TestCase("+1", TestName = "leading plus sign")]
        [TestCase(".5", TestName = "missing integer part")]
        [TestCase("1.", TestName = "missing fraction digits")]
        [TestCase("1e", TestName = "missing exponent digits")]
        [TestCase("-", TestName = "sign only")]
        [TestCase("{\"a\":", TestName = "unterminated object value")]
        [TestCase("{\"a\"", TestName = "unterminated object")]
        [TestCase("\"abc", TestName = "unterminated string")]
        [TestCase("{\"a\":1} {\"b\":2}", TestName = "trailing content after root")]
        [TestCase("1 2", TestName = "second token after root")]
        [TestCase("", TestName = "empty input")]
        [TestCase("   ", TestName = "whitespace only")]
        [TestCase("tru", TestName = "truncated literal")]
        [TestCase("nulll", TestName = "literal with trailing character")]
        [TestCase("'a'", TestName = "single quotes are not JSON")]
        [TestCase("\uFEFF{\"a\":1}", TestName = "BOM is not JSON whitespace")]
        [TestCase("\"a\\xd\"", TestName = "invalid escape sequence")]
        [TestCase("\"\\udc00\"", TestName = "lone low surrogate escape")]
        [TestCase("\"\\ud800\\u0041\"", TestName = "high surrogate not followed by low surrogate")]
        [TestCase("\"\\ud800x\"", TestName = "high surrogate not followed by escape")]
        [TestCase("{\"a\":1}\n[]", TestName = "two root values")]
        [TestCase("{\"a\"::1}", TestName = "double colon")]
        [TestCase("{a:1}", TestName = "unquoted key")]
        [TestCase("[\"a\" \"b\"]", TestName = "missing comma")]
        [TestCase("{\"a\":1 \"b\":2}", TestName = "missing comma in object")]
        public void MalformedTextIsRejectedWithInternalDiagnostics(string input)
        {
            var parsed = CanonicalJson.Parse(input);
            Assert.IsFalse(parsed.Ok, $"Input unexpectedly parsed: {input}");
            AssertInternalDiagnostics(parsed);
        }

        // Raw lone surrogates cannot travel through NUnit TestCase attribute metadata
        // (they are not valid UTF-8), so the fixtures are built in code.
        [Test]
        public void RawLoneSurrogatesInsideStringsAreRejected()
        {
            foreach (var c in new[] { (char)0xD800, (char)0xDBFF, (char)0xDC00, (char)0xDFFF })
            {
                var parsed = CanonicalJson.Parse("\"" + c + "\"");
                Assert.IsFalse(parsed.Ok, $"raw U+{(int)c:X4} unexpectedly parsed");
                AssertInternalDiagnostics(parsed);
            }
        }

        [Test]
        public void RawControlCharactersInsideStringsAreRejected()
        {
            var parsed = CanonicalJson.Parse("\"a\tb\"");
            Assert.IsFalse(parsed.Ok);
            Assert.IsTrue(parsed.Diagnostics[0].Message.Contains("control character"));
            AssertInternalDiagnostics(parsed);
        }

        [Test]
        public void ParseFailuresCarryOffsetAndJsonPath()
        {
            var nested = CanonicalJson.Parse("{\"a\":[0,01]}");
            Assert.IsFalse(nested.Ok);
            var diagnostic = nested.Diagnostics[0];
            // SG-02: object member steps are "*" wildcards so decoded key text never
            // reaches a diagnostic; array indices stay literal.
            Assert.AreEqual("$.*[1]", diagnostic.JsonPath);
            StringAssert.Contains("leading zero", diagnostic.Message);
            StringAssert.Contains("offset", diagnostic.Message);

            var duplicate = CanonicalJson.Parse("{\"x\":{\"dup\":1,\"dup\":2}}");
            Assert.IsFalse(duplicate.Ok);
            Assert.AreEqual("$.*.*", duplicate.Diagnostics[0].JsonPath);
            StringAssert.Contains("Duplicate object key", duplicate.Diagnostics[0].Message);

            var root = CanonicalJson.Parse("01");
            Assert.IsFalse(root.Ok);
            Assert.AreEqual("$", root.Diagnostics[0].JsonPath);
        }

        // SPEC §0.6 SG-02: the same malformed JSON maps exactly once to the owning layer's
        // published code — OW-WF-001 (workflow reading), OW-STORE-002 (Run Store),
        // OW-CFG-001 (settings) — with no payload leak and no new parse code. The three
        // owning layers are later tickets; each performs this same single remapping
        // through ErrorCodes.Create before publishing. Keys are source JSON just like
        // values, so a secret anywhere in the text — including inside a key — may appear
        // in no Diagnostic field; JsonPath carries it only as the "*" member wildcard.
        [TestCase("{\"token\":\"<<SECRET>>\",\"ok\":[0,01]}", "$.*[1]",
            TestName = "secret inside a string value")]
        [TestCase("{\"<<SECRET>>\":01}", "$.*",
            TestName = "secret as an object key with an invalid value after it")]
        [TestCase("{\"<<SECRET>>\":1,\"<<SECRET>>\":2}", "$.*",
            TestName = "secret as a duplicate object key")]
        [TestCase("{\"outer\":{\"<<SECRET>>\":[0,01]}}", "$.*.*[1]",
            TestName = "secret inside a nested key path")]
        public void ParseFailuresMapOnceToEachOwningLayerCode(string template, string expectedPath)
        {
            const string secret = "s3cr3t-payload-12345";
            var parsed = CanonicalJson.Parse(template.Replace("<<SECRET>>", secret));
            Assert.IsFalse(parsed.Ok);
            Assert.AreEqual(1, parsed.Diagnostics.Count, "one reason per parse failure");
            var inner = parsed.Diagnostics[0];
            Assert.AreEqual(string.Empty, inner.Code, "internal diagnostic carries no OW-* code");
            Assert.IsFalse(ErrorCodes.IsPublished(inner.Code));
            Assert.AreEqual(expectedPath, inner.JsonPath);
            AssertNoFieldLeaks(inner, secret, "internal diagnostic");

            foreach (var code in new[]
            {
                ErrorCodes.OW_WF_001, ErrorCodes.OW_STORE_002, ErrorCodes.OW_CFG_001
            })
            {
                var mapped = ErrorCodes.Create(code, inner.Message, jsonPath: inner.JsonPath);
                Assert.AreEqual(code, mapped.Code);
                Assert.AreEqual(Severity.Error, mapped.Severity);
                Assert.AreEqual(inner.Message, mapped.Message);
                Assert.AreEqual(inner.JsonPath, mapped.JsonPath);
                AssertNoFieldLeaks(mapped, secret, code + " diagnostic");
            }
        }

        [Test]
        public void CanonicalJsonVersionConstantIsOne()
        {
            // SPEC §0.3: the integer canonicalJsonVersion stored by Plan and JournalHeader
            // writers, distinct from formatVersion and schemaVersion.
            Assert.AreEqual(1, CanonicalJson.Version);
        }

        [Test]
        public void Sha256MatchesTheNormativeCj35Digest()
        {
            // CJ-35: SHA-256 of the 3 canonical UTF-8 bytes of "a", lowercase hex.
            var parsed = CanonicalJson.Parse("\"a\"");
            Assert.IsTrue(parsed.Ok);
            Assert.AreEqual(
                "ac8d8342bbb2362d13f0a559a3621bb407011368895164b628a54f7fc33fc43c",
                CanonicalJson.Sha256(parsed.Value!));
        }

        [Test]
        public void SerializeOrdersJTokenKeysByUtf16Ordinal()
        {
            // The same key set as CJ-02, supplied out of order through the JToken API.
            var obj = new JObject(
                new JProperty("\uFF71", 1L),
                new JProperty("\uD83D\uDE00", 2L),
                new JProperty("\u00E9", 3L),
                new JProperty("a", 4L));
            Assert.AreEqual(
                "{\"a\":4,\"\u00E9\":3,\"\uD83D\uDE00\":2,\"\uFF71\":1}",
                CanonicalJson.Serialize(obj));
        }

        [Test]
        public void SerializePreservesTypedNumbersAndNull()
        {
            var obj = new JObject(
                new JProperty("i", 1L),
                new JProperty("d", 1.0),
                new JProperty("n", JValue.CreateNull()),
                new JProperty("b", true),
                new JProperty("big", new BigInteger(9223372036854775807L)));
            Assert.AreEqual(
                "{\"b\":true,\"big\":9223372036854775807,\"d\":1e0,\"i\":1,\"n\":null}",
                CanonicalJson.Serialize(obj));
        }

        [Test]
        public void SerializeIsIdempotentOnCanonicalText()
        {
            const string canonical =
                "{\"a\":{\"c\":{},\"d\":[]},\"n\":-25e-2,\"s\":\"\\u0000\\u001f\u007F\",\"z\":[1,\"x\"]}";
            var parsed = CanonicalJson.Parse(canonical);
            Assert.IsTrue(parsed.Ok, Describe(parsed));
            Assert.AreEqual(canonical, CanonicalJson.Serialize(parsed.Value!));
        }

        // JToken input is validated for Int64 range, finite doubles and valid Unicode;
        // tokens outside the JSON value model violate the Serialize contract.
        [Test]
        public void SerializeRejectsTokensOutsideTheJsonValueModel()
        {
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(new JValue(double.NaN)));
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(new JValue(double.PositiveInfinity)));
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(new JValue(double.NegativeInfinity)));
            Assert.Throws<ArgumentException>(() =>
                CanonicalJson.Serialize(new JValue(BigInteger.Parse("9223372036854775808"))));
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(new JValue(ulong.MaxValue)));
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(new JValue("\uD800")));
            Assert.Throws<ArgumentException>(() =>
                CanonicalJson.Serialize(new JObject(new JProperty("\uD800", 1L))));
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(new JValue(DateTime.Now)));
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(new JValue(new byte[] { 1 })));
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(JValue.CreateUndefined()));
            Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(new JValue(new Uri("http://x"))));
            Assert.Throws<ArgumentNullException>(() => CanonicalJson.Serialize(null!));
        }

        [Test]
        public void Sha256RejectsInvalidTokensToo()
        {
            Assert.Throws<ArgumentException>(() => CanonicalJson.Sha256(new JValue(double.NaN)));
        }

        private static void AssertInternalDiagnostics(Result<JToken> result)
        {
            Assert.AreEqual(1, result.Diagnostics.Count);
            foreach (var diagnostic in result.Diagnostics)
            {
                // SG-02: empty internal code (never a published OW-* code), fixed English
                // reason; the caller maps to OW-WF-001 / OW-STORE-002 / OW-CFG-001 once.
                Assert.AreEqual(string.Empty, diagnostic.Code);
                Assert.AreEqual(Severity.Error, diagnostic.Severity);
                Assert.IsFalse(string.IsNullOrEmpty(diagnostic.Message));
                Assert.IsFalse(ErrorCodes.IsPublished(diagnostic.Code));
                StringAssert.Contains("offset", diagnostic.Message);
                // SG-02: a parser-failure JsonPath is "$" followed only by "*" member
                // wildcards and literal array indices — decoded key text never appears.
                Assert.IsTrue(
                    s_parserFailurePath.IsMatch(diagnostic.JsonPath ?? string.Empty),
                    "JsonPath must use only '*' member steps and literal indices: " +
                        diagnostic.JsonPath);
            }
        }

        private static readonly Regex s_parserFailurePath =
            new Regex(@"^\$(\.\*|\[\d+\])*$");

        private static void AssertNoFieldLeaks(Diagnostic diagnostic, string secret, string what)
        {
            StringAssert.DoesNotContain(secret, diagnostic.Code, what + ".Code");
            StringAssert.DoesNotContain(secret, diagnostic.Message, what + ".Message");
            StringAssert.DoesNotContain(secret, diagnostic.NodeId ?? string.Empty, what + ".NodeId");
            StringAssert.DoesNotContain(secret, diagnostic.Port ?? string.Empty, what + ".Port");
            StringAssert.DoesNotContain(secret, diagnostic.JsonPath ?? string.Empty, what + ".JsonPath");
            StringAssert.DoesNotContain(secret, diagnostic.Hint ?? string.Empty, what + ".Hint");
        }

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
