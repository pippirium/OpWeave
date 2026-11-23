#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the built-in <c>core.*</c> codecs and the standard
    /// <see cref="CoreTypeProvider"/> (T-W1.4-04): SPEC §B7 type checks and
    /// decision-Q3 numeric-token rules, §C1 <see cref="PortConstraints"/>
    /// enforcement and the §B7.1 single-<see cref="IValueCodec.Normalize"/>
    /// contract, all emitted as OW-TYPE-003.
    /// </summary>
    internal sealed class CoreCodecTests
    {
        private static readonly TypeRegistryBuildResult s_build = TypeRegistryBuilder.Build(new[]
        {
            new TypeProviderCandidate(
                CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
        });

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "JsonValue.Parse failed for " + text);
            return parsed.Value!;
        }

        private static TypeDefinition Resolve(string typeId)
        {
            var resolved = s_build.Registry.Resolve(TypeRef.Parse(typeId).Value!);
            Assert.IsTrue(resolved.Ok, "Resolve failed: " + typeId);
            return resolved.Value!;
        }

        private static IValueCodec Codec(string typeId) => Resolve(typeId).Codec;

        private static Diagnostic MustFailValidate(
            IValueCodec codec, JsonValue value, PortConstraints? constraints = null)
        {
            var diagnostics = codec.Validate(value, constraints);
            Assert.AreEqual(1, diagnostics.Count);
            var diagnostic = diagnostics[0];
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Retryable);
            return diagnostic;
        }

        // ── Standard provider registration (SPEC §B2, §B3) ────────────────────

        [Test]
        public void BuiltinProviderRegistersAllCoreTypes()
        {
            Assert.AreEqual(0, s_build.Failures.Count);
            Assert.AreEqual(CoreTypeProvider.ProviderId, "@builtin");

            var ids = new List<string>();
            foreach (var definition in s_build.Registry.All)
            {
                ids.Add(definition.Id);
            }
            Assert.AreEqual(
                new[] { "core.Bool", "core.Float", "core.Int", "core.Json", "core.List<>", "core.String" },
                ids.ToArray());
            Assert.AreEqual(6, ids.Count);

            Assert.AreEqual(TypeKind.Json, Resolve("core.Json").Kind);
            var closed = s_build.Registry.Resolve(TypeRef.Parse("core.List<core.Int>").Value!);
            Assert.IsTrue(closed.Ok);
            Assert.AreEqual("core.List<>", closed.Value!.Id);
            Assert.AreEqual(TypeKind.List, closed.Value!.Kind);
            Assert.AreEqual(1, closed.Value!.GenericArity);
            foreach (var id in new[] { "core.String", "core.Int", "core.Float", "core.Bool" })
            {
                Assert.AreEqual(TypeKind.Primitive, Resolve(id).Kind, id);
            }
        }

        // ── core.String ───────────────────────────────────────────────────────

        [Test]
        public void StringCodecAcceptsStringsAndRejectsOtherTokens()
        {
            var codec = Codec("core.String");
            Assert.AreEqual(0, codec.Validate(Json("\"abc\""), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("\"\""), null).Count);
            MustFailValidate(codec, Json("5"));
            MustFailValidate(codec, Json("1e0"));
            MustFailValidate(codec, Json("true"));
            MustFailValidate(codec, Json("null"));
            MustFailValidate(codec, Json("[]"));
            MustFailValidate(codec, Json("{}"));
        }

        [Test]
        public void StringCodecEnforcesEnumPatternAndMaxLength()
        {
            var codec = Codec("core.String");

            var withEnum = new PortConstraints(new[] { "red", "green" }, null, null, null, null);
            Assert.AreEqual(0, codec.Validate(Json("\"red\""), withEnum).Count);
            MustFailValidate(codec, Json("\"blue\""), withEnum);
            // A non-string is never an enum member: it fails both the type
            // check and the enum constraint, one diagnostic each.
            var nonString = codec.Validate(Json("5"), withEnum);
            Assert.AreEqual(2, nonString.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, nonString[0].Code);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, nonString[1].Code);

            var withPattern = new PortConstraints(null, "^[a-z]+$", null, null, null);
            Assert.AreEqual(0, codec.Validate(Json("\"abc\""), withPattern).Count);
            MustFailValidate(codec, Json("\"ABC\""), withPattern);
            MustFailValidate(codec, Json("\"abc1\""), withPattern);
            // The pattern is an unanchored search (JSON Schema draft 2020-12).
            var unanchored = new PortConstraints(null, "b+", null, null, null);
            Assert.AreEqual(0, codec.Validate(Json("\"aabbcc\""), unanchored).Count);

            var withLength = new PortConstraints(null, null, null, null, 3);
            Assert.AreEqual(0, codec.Validate(Json("\"abc\""), withLength).Count);
            MustFailValidate(codec, Json("\"abcd\""), withLength);
        }

        [Test]
        public void MaxLengthCountsUnicodeCodePoints()
        {
            // JSON Schema draft 2020-12 maxLength semantics: a supplementary
            // character is one code point although it is two UTF-16 code
            // units, so the Codec judges the same boundary the generated
            // schema emits (TypeSchemaTests.MaxLengthCountsUnicodeCodePoints).
            var codec = Codec("core.String");
            var maxOne = new PortConstraints(null, null, null, null, 1);
            Assert.AreEqual(0, codec.Validate(Json("\"\\ud83d\\ude00\""), maxOne).Count);
            MustFailValidate(codec, Json("\"\\ud83d\\ude00\\ud83d\\ude01\""), maxOne);

            // BMP boundaries are unchanged.
            Assert.AreEqual(0, codec.Validate(Json("\"a\""), maxOne).Count);
            MustFailValidate(codec, Json("\"ab\""), maxOne);

            // A lone surrogate — reachable only through a hand-built token,
            // since the value model rejects unpaired surrogates — counts as
            // one code point each.
            var sink = new List<Diagnostic>();
            CodecConstraints.Check(new JValue("\ud800"), maxOne, sink);
            Assert.AreEqual(0, sink.Count);
            CodecConstraints.Check(new JValue("\ud83d\ude00\ud800"), maxOne, sink);
            Assert.AreEqual(1, sink.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, sink[0].Code);
        }

        [Test]
        public void InvalidPatternFailsClosedAsOwType003()
        {
            var codec = Codec("core.String");
            var broken = new PortConstraints(null, "([", null, null, null);
            MustFailValidate(codec, Json("\"x\""), broken);
        }

        [Test]
        public void StringCodecNormalizeIsIdentity()
        {
            var codec = Codec("core.String");
            var value = Json("\"hello\"");
            var normalized = codec.Normalize(value);
            Assert.AreSame(value, normalized);
            Assert.AreEqual("\"hello\"", normalized.CanonicalText);
        }

        // ── core.Int (Q3 item 3: no Double→Int64 coercion) ────────────────────

        [Test]
        public void IntCodecAcceptsOnlyIntegerTokens()
        {
            var codec = Codec("core.Int");
            Assert.AreEqual(0, codec.Validate(Json("5"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("-9223372036854775808"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("9223372036854775807"), null).Count);

            // SPEC §B7 test 3: a fraction token in an Int64 position → OW-TYPE-003.
            var diagnostic = MustFailValidate(codec, Json("1.0"));
            StringAssert.Contains("integer", diagnostic.Message);
            MustFailValidate(codec, Json("1e0"));
            MustFailValidate(codec, Json("2.5"));
            MustFailValidate(codec, Json("\"5\""));
            MustFailValidate(codec, Json("true"));
            MustFailValidate(codec, Json("null"));
        }

        [Test]
        public void IntegerTokenBeyondInt64IsRejectedAtParse()
        {
            // SPEC §B7 item 5 / §0.3 CJ-14: out-of-Int64 integer tokens fail at
            // Canonical JSON parse — before any codec or Double position sees them.
            var parsed = JsonValue.Parse("9223372036854775808");
            Assert.IsFalse(parsed.Ok);
            Assert.AreEqual(1, parsed.Diagnostics.Count);
            Assert.IsFalse(JsonValue.Parse("-9223372036854775809").Ok);
            Assert.IsFalse(JsonValue.Parse("[9223372036854775808]").Ok);
        }

        [Test]
        public void IntCodecEnforcesMinAndMaxExactly()
        {
            var codec = Codec("core.Int");
            var range = new PortConstraints(null, null, 0.0, 10.0, null);
            Assert.AreEqual(0, codec.Validate(Json("0"), range).Count);
            Assert.AreEqual(0, codec.Validate(Json("10"), range).Count);
            Assert.AreEqual(0, codec.Validate(Json("5"), range).Count);
            MustFailValidate(codec, Json("-1"), range);
            MustFailValidate(codec, Json("11"), range);

            // Fractional bounds compare exactly, without Int64→Double rounding.
            var fractional = new PortConstraints(null, null, 2.5, null, null);
            MustFailValidate(codec, Json("2"), fractional);
            Assert.AreEqual(0, codec.Validate(Json("3"), fractional).Count);

            // Bounds beyond the Int64 range decide by magnitude alone.
            var hugeMin = new PortConstraints(null, null, 1e19, null, null);
            MustFailValidate(codec, Json("9223372036854775807"), hugeMin);
            var hugeMax = new PortConstraints(null, null, null, 1e19, null);
            Assert.AreEqual(0, codec.Validate(Json("-9223372036854775808"), hugeMax).Count);
            var hugeNegativeMin = new PortConstraints(null, null, -1e19, null, null);
            Assert.AreEqual(0, codec.Validate(Json("9223372036854775807"), hugeNegativeMin).Count);
        }

        [Test]
        public void IntCodecNormalizeIsIdentity()
        {
            var codec = Codec("core.Int");
            var value = Json("-42");
            Assert.AreSame(value, codec.Normalize(value));
            Assert.AreEqual("-42", codec.Normalize(value).CanonicalText);
        }

        // ── core.Float (Q3 items 1–2: integers accepted, stored as Double) ────

        [Test]
        public void FloatCodecAcceptsIntegerAndFractionTokens()
        {
            var codec = Codec("core.Float");
            Assert.AreEqual(0, codec.Validate(Json("1"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("1.0"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("-2.5e3"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("0"), null).Count);
            MustFailValidate(codec, Json("\"1\""));
            MustFailValidate(codec, Json("true"));
            MustFailValidate(codec, Json("null"));
            MustFailValidate(codec, Json("[]"));
        }

        [Test]
        public void FloatCodecNormalizesIntegerTokensToCanonicalDouble()
        {
            var codec = Codec("core.Float");

            // Acceptance: core.Float accepts 1 and stores 1e0.
            Assert.AreEqual("1e0", codec.Normalize(Json("1")).CanonicalText);
            Assert.AreEqual("0e0", codec.Normalize(Json("0")).CanonicalText);
            Assert.AreEqual("0e0", codec.Normalize(Json("0.0")).CanonicalText);
            Assert.AreEqual("0e0", codec.Normalize(Json("-0.0")).CanonicalText);
            Assert.AreEqual("5e0", codec.Normalize(Json("5")).CanonicalText);
            Assert.AreEqual("15e-1", codec.Normalize(Json("1.5")).CanonicalText);
            Assert.AreEqual("1e2", codec.Normalize(Json("1e2")).CanonicalText);
            // Exact -2^63 converts to the same Double and renders Ryu shortest.
            Assert.AreEqual("-9223372036854776e3",
                codec.Normalize(Json("-9223372036854775808")).CanonicalText);

            // Written 0 and 0.0 produce the identical persisted value.
            var fromInteger = codec.Normalize(Json("0"));
            var fromDecimal = codec.Normalize(Json("0.0"));
            Assert.AreEqual(fromInteger, fromDecimal);
            Assert.AreEqual(fromInteger.CanonicalText, fromDecimal.CanonicalText);
        }

        [Test]
        public void FloatCodecNormalizeIsIdempotentAndInputPreserving()
        {
            var codec = Codec("core.Float");
            var value = Json("7");
            var once = codec.Normalize(value);
            var twice = codec.Normalize(once);
            Assert.AreEqual("7", value.CanonicalText); // input is not modified
            Assert.AreEqual("7e0", once.CanonicalText);
            Assert.AreEqual(once.CanonicalText, twice.CanonicalText);
            Assert.AreEqual(once, twice);
        }

        [Test]
        public void FloatCodecEnforcesMinAndMax()
        {
            var codec = Codec("core.Float");
            var range = new PortConstraints(null, null, -1.5, 2.5, null);
            Assert.AreEqual(0, codec.Validate(Json("-1.5"), range).Count);
            Assert.AreEqual(0, codec.Validate(Json("2.5"), range).Count);
            Assert.AreEqual(0, codec.Validate(Json("0"), range).Count);
            MustFailValidate(codec, Json("-1.6"), range);
            MustFailValidate(codec, Json("2.6"), range);
            // Integer tokens bound-check by their written value.
            MustFailValidate(codec, Json("3"), range);
        }

        // ── core.Bool ─────────────────────────────────────────────────────────

        [Test]
        public void BoolCodecAcceptsOnlyBooleans()
        {
            var codec = Codec("core.Bool");
            Assert.AreEqual(0, codec.Validate(Json("true"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("false"), null).Count);
            MustFailValidate(codec, Json("1"));
            MustFailValidate(codec, Json("\"true\""));
            MustFailValidate(codec, Json("null"));

            var value = Json("false");
            Assert.AreSame(value, codec.Normalize(value));
        }

        // ── core.Json (untyped; tokens and explicit null preserved) ──────────

        [Test]
        public void JsonCodecAcceptsEveryJsonValue()
        {
            var codec = Codec("core.Json");
            foreach (var text in new[]
            {
                "null", "true", "5", "1.0", "\"x\"", "[]", "[1,\"a\",null]", "{}",
                "{\"k\":null}",
            })
            {
                Assert.AreEqual(0, codec.Validate(Json(text), null).Count, text);
            }
        }

        [Test]
        public void JsonCodecKeepsWrittenTokenSemantics()
        {
            var codec = Codec("core.Json");

            // SPEC §B7 item 4 / §B7.1 test: `1` and `1e0` stay distinct normal
            // forms under core.Json — no Double conversion ever applies.
            Assert.AreEqual("1", codec.Normalize(Json("1")).CanonicalText);
            Assert.AreEqual("1e0", codec.Normalize(Json("1.0")).CanonicalText);
            Assert.AreEqual("1e0", codec.Normalize(Json("1e0")).CanonicalText);
            Assert.AreNotEqual(
                codec.Normalize(Json("1")).CanonicalText,
                codec.Normalize(Json("1.0")).CanonicalText);

            var compound = Json("[1,1.0,{\"a\":0}]");
            Assert.AreSame(compound, codec.Normalize(compound));
            Assert.AreEqual("[1,1e0,{\"a\":0}]", compound.CanonicalText);

            var nullValue = Json("null");
            Assert.AreSame(nullValue, codec.Normalize(nullValue));
            Assert.AreEqual("null", codec.Normalize(nullValue).CanonicalText);
        }

        [Test]
        public void JsonCodecStillAppliesConstraints()
        {
            var codec = Codec("core.Json");
            var withEnum = new PortConstraints(new[] { "yes" }, null, null, null, null);
            Assert.AreEqual(0, codec.Validate(Json("\"yes\""), withEnum).Count);
            MustFailValidate(codec, Json("null"), withEnum);
            MustFailValidate(codec, Json("5"), withEnum);
        }

        // ── core.List<T>: the closed-use Codec validates elements itself ─────

        [Test]
        public void ListCodecAcceptsOnlyArrays()
        {
            var codec = Codec("core.List<core.Int>");
            Assert.AreEqual(0, codec.Validate(Json("[]"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("[1,2,3]"), null).Count);
            MustFailValidate(codec, Json("5"));
            MustFailValidate(codec, Json("{}"));
            MustFailValidate(codec, Json("null"));

            Assert.AreEqual("[1,2]", codec.Normalize(Json("[1,2]")).CanonicalText);
        }

        [Test]
        public void ClosedListCodecIsBoundToTheElementType()
        {
            // The open core.List<> definition keeps a shape-only codec; a
            // closed core.List<T> must resolve to an element-bound codec
            // (SPEC §B3: elements are T), never to that open codec.
            TypeDefinition? open = null;
            foreach (var definition in s_build.Registry.All)
            {
                if (definition.Id == "core.List<>")
                {
                    open = definition;
                }
            }
            Assert.IsNotNull(open);
            Assert.AreNotSame(open!.Codec, Codec("core.List<core.Int>"));
        }

        [Test]
        public void ClosedListCodecValidatesElementsWithTheElementCodec()
        {
            var codec = Codec("core.List<core.Float>");
            Assert.AreEqual(0, codec.Validate(Json("[]"), null).Count);
            Assert.AreEqual(0, codec.Validate(Json("[0,1.5]"), null).Count);

            var diagnostic = MustFailValidate(codec, Json("[\"x\"]"));
            Assert.AreEqual("$[0]", diagnostic.JsonPath);

            var diagnostics = codec.Validate(Json("[1,\"x\",true]"), null);
            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostics[0].Code);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostics[1].Code);
            Assert.AreEqual("$[1]", diagnostics[0].JsonPath);
            Assert.AreEqual("$[2]", diagnostics[1].JsonPath);
        }

        [Test]
        public void ClosedListCodecValidatesNestedListsRecursively()
        {
            var codec = Codec("core.List<core.List<core.Int>>");
            Assert.AreEqual(0, codec.Validate(Json("[[1],[2,3]]"), null).Count);

            var diagnostic = MustFailValidate(codec, Json("[[\"a\"]]"));
            Assert.AreEqual("$[0][0]", diagnostic.JsonPath);
        }

        [Test]
        public void ClosedListCodecAppliesTheElementNullRule()
        {
            // Elements have no port-level Nullable flag: only a core.Json
            // element type admits an explicit null (SPEC §A5/§B0.1).
            Assert.AreEqual(
                0, Codec("core.List<core.Json>").Validate(Json("[1,null,\"a\"]"), null).Count);
            var diagnostic = MustFailValidate(Codec("core.List<core.String>"), Json("[\"a\",null]"));
            Assert.AreEqual("$[1]", diagnostic.JsonPath);
        }

        [Test]
        public void ClosedListCodecNormalizesEveryElementWithItsCodec()
        {
            // SPEC §B7.1: Normalize([0,1]) on core.List<core.Float> persists
            // [0e0,1e0] — the codec bound to the closed type normalizes each
            // element with the element codec.
            var codec = Codec("core.List<core.Float>");
            Assert.AreEqual("[0e0,1e0]", codec.Normalize(Json("[0,1]")).CanonicalText);
            Assert.AreEqual("[15e-1,0e0]", codec.Normalize(Json("[1.5,0.0]")).CanonicalText);
            Assert.AreEqual("[]", codec.Normalize(Json("[]")).CanonicalText);

            var nested = Codec("core.List<core.List<core.Float>>");
            Assert.AreEqual("[[0e0],[15e-1]]", nested.Normalize(Json("[[0],[1.5]]")).CanonicalText);

            // Json elements keep their written tokens even inside a typed list.
            Assert.AreEqual(
                "[1,1e0]",
                Codec("core.List<core.Json>").Normalize(Json("[1,1.0]")).CanonicalText);

            // Idempotent, and non-array input passes through unchanged.
            var once = codec.Normalize(Json("[0,1]"));
            Assert.AreEqual(once.CanonicalText, codec.Normalize(once).CanonicalText);
            var scalar = Json("5");
            Assert.AreSame(scalar, codec.Normalize(scalar));
        }

        // ── Shared constraint machinery ───────────────────────────────────────

        [Test]
        public void ConstraintKeywordsApplyOnlyToTheirJsonKind()
        {
            // SPEC §B8-style semantics: pattern/maxLength apply to strings,
            // min/max to numbers; a keyword on another JSON kind is ignored.
            var stringKeywords = new PortConstraints(null, "^x", null, null, 1);
            Assert.AreEqual(0, Codec("core.Int").Validate(Json("5"), stringKeywords).Count);
            Assert.AreEqual(0, Codec("core.Bool").Validate(Json("true"), stringKeywords).Count);

            var numericKeywords = new PortConstraints(null, null, 100.0, 200.0, null);
            Assert.AreEqual(0, Codec("core.String").Validate(Json("\"abc\""), numericKeywords).Count);
            Assert.AreEqual(0, Codec("core.Bool").Validate(Json("false"), numericKeywords).Count);
        }

        [Test]
        public void BuiltInCodecsExposeTheSingleIValueCodecContract()
        {
            // The built-in codecs implement the one approved §B7.1 interface;
            // no second normalization method or interface is introduced.
            foreach (var id in new[] { "core.String", "core.Int", "core.Float", "core.Bool", "core.Json", "core.List<core.Int>" })
            {
                var codec = Codec(id);
                Assert.IsInstanceOf<IValueCodec>(codec, id);
                var normalized = codec.Normalize(Json("0"));
                Assert.IsNotNull(normalized, id);
            }
        }
    }
}
