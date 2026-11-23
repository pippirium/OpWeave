#nullable enable

using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the immutable <see cref="JsonValue"/> (SPEC §0.1 F-07, §0.4,
    /// §0.6 SG-02): the §0.4 JsonValue tests — <see cref="JsonValue.ToToken"/> output
    /// mutation never reaches the value, and key-order-only variants share one
    /// CanonicalText — plus equality/hash-code consistency, explicit-null validity,
    /// input-side defensive copying and failed-<see cref="Result{T}"/> parse errors.
    /// The same sources run under dotnet and Unity Mono
    /// (local:T-W1.2-04:jsonvalue-immutability).
    /// </summary>
    internal sealed class JsonValueTests
    {
        private static JsonValue MustParse(string json)
        {
            var parsed = JsonValue.Parse(json);
            Assert.IsTrue(parsed.Ok, $"Input was rejected: {Describe(parsed)}");
            return parsed.Value!;
        }

        // SPEC §0.4 test 1: rewriting ToToken()'s return value does not change the
        // JsonValue; each call hands out a fresh clone.
        [Test]
        public void MutatingToTokenOutputLeavesTheJsonValueUnchanged()
        {
            const string canonical = "{\"a\":{\"b\":[1,2,3]},\"c\":\"x\",\"n\":null}";
            var value = MustParse(canonical);

            var first = value.ToToken();
            var second = value.ToToken();
            Assert.AreNotSame(first, second, "ToToken() must return a new clone each call");

            var obj = (JObject)first;
            obj["a"]!["b"]![0] = 999;
            obj["c"] = "mutated";
            obj["added"] = new JArray(1, 2);
            obj.Remove("n");
            ((JObject)second["a"]!).Remove("b");

            Assert.AreEqual(canonical, value.CanonicalText);
            Assert.IsTrue(
                JToken.DeepEquals(MustParse(canonical).ToToken(), value.ToToken()),
                "the held token is unaffected by mutations of earlier clones");
        }

        // SPEC §0.4 test 2: documents differing only in key order have the same
        // CanonicalText; equality and hash codes follow it.
        [Test]
        public void KeyOrderVariantsHaveEqualCanonicalTextEqualsAndHashCodes()
        {
            var a = MustParse("{\"a\":1,\"b\":{\"x\":true,\"y\":null},\"c\":[3,{\"p\":0,\"q\":1}]}");
            var b = MustParse("{\"c\":[3,{\"q\":1,\"p\":0}],\"b\":{\"y\":null,\"x\":true},\"a\":1}");

            Assert.AreEqual("{\"a\":1,\"b\":{\"x\":true,\"y\":null},\"c\":[3,{\"p\":0,\"q\":1}]}",
                a.CanonicalText);
            Assert.AreEqual(a.CanonicalText, b.CanonicalText);
            Assert.IsTrue(a.Equals(b));
            Assert.IsTrue(b.Equals(a));
            Assert.IsTrue(a.Equals((object)b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.AreEqual(a.Sha256, b.Sha256);
        }

        [Test]
        public void InequalityFollowsCanonicalText()
        {
            var integer = MustParse("1");
            var @double = MustParse("1.0");
            // SPEC §0.3: typed numeric equality — Int64 1 and Double 1.0 differ.
            Assert.AreNotEqual(integer.CanonicalText, @double.CanonicalText);
            Assert.IsFalse(integer.Equals(@double));
            object? nullObject = null;
            Assert.IsFalse(MustParse("1").Equals(nullObject));
            Assert.IsFalse(MustParse("1").Equals(null));
            object nonJson = "{\"a\":1}";
            Assert.IsFalse(integer.Equals(nonJson));
            Assert.IsFalse(MustParse("{\"a\":1}").Equals(MustParse("{\"a\":2}")));
        }

        // SPEC §0.1 F-07: explicit null is a valid JsonValue, held and serialized as
        // null; it is not a stand-in for absence.
        [Test]
        public void ExplicitNullIsAValidJsonValue()
        {
            var value = MustParse("null");
            Assert.AreEqual("null", value.CanonicalText);
            Assert.AreEqual(JTokenType.Null, value.ToToken().Type);

            var holder = MustParse("{\"v\":null}");
            Assert.AreEqual(JTokenType.Null, holder.ToToken()["v"]!.Type);

            // A JsonValue that wraps JSON null is still a real, non-null instance.
            Assert.IsNotNull(value);
            Assert.IsFalse(value.Equals(MustParse("0")));
        }

        // SPEC §0.4: From(JToken) clones before canonicalizing, so mutating the
        // caller's token afterwards cannot change the JsonValue.
        [Test]
        public void FromClonesTheInputTokenBeforeCanonicalizing()
        {
            var inner = new JObject(new JProperty("x", true), new JProperty("y", JValue.CreateNull()));
            var source = new JObject(
                new JProperty("b", inner),
                new JProperty("a", new JArray(1L, 2L)));

            var value = JsonValue.From(source);
            const string canonical = "{\"a\":[1,2],\"b\":{\"x\":true,\"y\":null}}";
            Assert.AreEqual(canonical, value.CanonicalText);

            source["a"] = 0L;
            inner["x"] = false;
            source.Add(new JProperty("z", "mutated"));

            Assert.AreEqual(canonical, value.CanonicalText);
            Assert.IsTrue(JToken.DeepEquals(MustParse(canonical).ToToken(), value.ToToken()));
        }

        [Test]
        public void FromNormalizesNumberTypesAndKeyOrder()
        {
            var source = new JObject(
                new JProperty("z", 1.5m),              // decimal -> Double
                new JProperty("i", new BigInteger(7)), // in-range BigInteger -> Int64
                new JProperty("a", 1.0));
            var value = JsonValue.From(source);
            Assert.AreEqual("{\"a\":1e0,\"i\":7,\"z\":15e-1}", value.CanonicalText);

            var token = (JObject)value.ToToken();
            Assert.IsInstanceOf<long>(((JValue)token["i"]!).Value);
            Assert.IsInstanceOf<double>(((JValue)token["z"]!).Value);
        }

        // SPEC §0.4 acceptance: parse failures return a failed Result, no exception.
        // SG-02: the diagnostic stays internal — empty code, payload-free reason with
        // offset, "*"-wildcard object member steps — for the owning layer to map once.
        [TestCase("{\"a\":", TestName = "unterminated object")]
        [TestCase("[1,]", TestName = "trailing comma")]
        [TestCase("{\"a\":1,\"a\":2}", TestName = "duplicate key")]
        [TestCase("01", TestName = "leading zero")]
        [TestCase("{\"s\":\"s3cr3t\",\"ok\":[0,01]}", TestName = "nested error with secret content")]
        [TestCase("", TestName = "empty input")]
        [TestCase("NaN", TestName = "NaN literal")]
        public void ParseFailuresReturnAFailedResultWithoutExceptions(string input)
        {
            Result<JsonValue> parsed = default;
            Assert.DoesNotThrow(() => parsed = JsonValue.Parse(input));

            Assert.IsFalse(parsed.Ok);
            Assert.IsNull(parsed.Value);
            Assert.AreEqual(1, parsed.Diagnostics.Count);
            var diagnostic = parsed.Diagnostics[0];
            Assert.AreEqual(string.Empty, diagnostic.Code, "internal diagnostic carries no OW-* code");
            Assert.IsFalse(ErrorCodes.IsPublished(diagnostic.Code));
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            StringAssert.Contains("offset", diagnostic.Message);
            StringAssert.DoesNotContain("s3cr3t", diagnostic.Message);
            StringAssert.DoesNotContain("s3cr3t", diagnostic.JsonPath ?? string.Empty);
        }

        [Test]
        public void Sha256IsTheLowercaseHexDigestOfCanonicalUtf8()
        {
            var value = MustParse("{\"b\":1,\"a\":\"x\"}");
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(new UTF8Encoding(false).GetBytes(value.CanonicalText));
            }
            var expected = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            Assert.AreEqual(expected, value.Sha256);
            Assert.AreEqual(64, value.Sha256.Length);
            // Consistent with the single CanonicalJson hash over the exported token.
            Assert.AreEqual(CanonicalJson.Sha256(value.ToToken()), value.Sha256);
        }

        [Test]
        public void Sha256OfExplicitNullMatchesTheGoldenDigest()
        {
            // SHA-256 of the 4 canonical UTF-8 bytes of "null".
            Assert.AreEqual(
                "74234e98afe7498fb5daf1f36ac2d78acc339464f950703b8c019892f982b90b",
                MustParse("null").Sha256);
        }

        // From(JToken) shares the CanonicalJson.Serialize contract: tokens outside the
        // JSON value model are programming errors, not parse failures.
        [Test]
        public void FromRejectsTokensOutsideTheJsonValueModel()
        {
            Assert.Throws<ArgumentException>(() => JsonValue.From(new JValue(double.NaN)));
            Assert.Throws<ArgumentException>(() => JsonValue.From(new JValue(double.PositiveInfinity)));
            Assert.Throws<ArgumentException>(() =>
                JsonValue.From(new JValue(BigInteger.Parse("9223372036854775808"))));
            Assert.Throws<ArgumentException>(() => JsonValue.From(new JValue(DateTime.Now)));
            Assert.Throws<ArgumentException>(() => JsonValue.From(new JValue("\uD800")));
            Assert.Throws<ArgumentNullException>(() => JsonValue.From(null!));
        }

        private static string Describe(Result<JsonValue> result)
        {
            if (result.Diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            var builder = new StringBuilder();
            foreach (var diagnostic in result.Diagnostics)
            {
                builder.Append(diagnostic.Code).Append(' ').Append(diagnostic.Message)
                    .Append(" path=").Append(diagnostic.JsonPath).AppendLine();
            }
            return builder.ToString();
        }
    }
}
