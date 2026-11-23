#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the custom-codec normalization contract (T-W1.4-07;
    /// SPEC §B7.1, §X.6, §D0.2, §C0.1). An external
    /// <see cref="IClrValueCodec{T}"/> must normalize values byte-identically
    /// to <c>ToJson(FromJson(value))</c>; a non-CLR Codec must offer an
    /// equivalent normal form. Every Codec's output is canonical per §0.3,
    /// deterministic, input-preserving and idempotent, and
    /// <see cref="ValueValidation.Normalize"/> must classify contract
    /// violations — exceptions, null results, values failing revalidation,
    /// non-idempotent results — as a single sanitized OW-TYPE-003 that never
    /// embeds value or exception content (SPEC §B7.1 item 4).
    /// </summary>
    internal sealed class CustomCodecNormalizationTests
    {
        // ── Custom codecs under test ───────────────────────────────────────

        // A CLR-backed external struct codec (SPEC §B7.1 item 2): values are
        // {"x":…,"y":…,"z":…} objects whose components are Double positions —
        // integer tokens are accepted and converted to Double on Normalize.
        private readonly struct PosePoint
        {
            internal PosePoint(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            internal double X { get; }
            internal double Y { get; }
            internal double Z { get; }
        }

        private sealed class PosePointCodec : IClrValueCodec<PosePoint>
        {
            private static readonly string[] s_axes = { "x", "y", "z" };

            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                var diagnostics = new List<Diagnostic>();
                var token = value.ToToken();
                if (token.Type != JTokenType.Object)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003, "Value is not a pose point object."));
                    return diagnostics;
                }
                var obj = (JObject)token;
                foreach (var axis in s_axes)
                {
                    var member = obj[axis];
                    if (member is null ||
                        (member.Type != JTokenType.Integer && member.Type != JTokenType.Float))
                    {
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003,
                            $"Member '{axis}' is missing or not a number.",
                            jsonPath: "$." + axis));
                    }
                }
                return diagnostics;
            }

            public PosePoint FromJson(JsonValue value)
            {
                var obj = (JObject)value.ToToken();
                return new PosePoint(Axis(obj, "x"), Axis(obj, "y"), Axis(obj, "z"));
            }

            public JsonValue ToJson(PosePoint value) => JsonValue.From(new JObject
            {
                ["x"] = new JValue(value.X),
                ["y"] = new JValue(value.Y),
                ["z"] = new JValue(value.Z),
            });

            public JsonValue Normalize(JsonValue value) => ToJson(FromJson(value));

            private static double Axis(JObject obj, string name) => obj[name]!.ToObject<double>();
        }

        // A non-CLR Codec offering an equivalent normal form (SPEC §B7.1
        // item 2): it accepts a bare number or the boxed {"v":number}
        // spelling and persists both as the object form.
        private sealed class BoxedNumberCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                var token = value.ToToken();
                switch (token.Type)
                {
                    case JTokenType.Integer:
                    case JTokenType.Float:
                        return Array.Empty<Diagnostic>();
                    case JTokenType.Object:
                        var obj = (JObject)token;
                        var member = obj["v"];
                        if (obj.Count == 1 && member is not null &&
                            (member.Type == JTokenType.Integer || member.Type == JTokenType.Float))
                        {
                            return Array.Empty<Diagnostic>();
                        }
                        break;
                }
                return new[]
                {
                    ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "Value is not a number or a '{\"v\":number}' object."),
                };
            }

            public JsonValue Normalize(JsonValue value)
            {
                var token = value.ToToken();
                double number;
                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                {
                    number = token.ToObject<double>();
                }
                else if (token.Type == JTokenType.Object)
                {
                    var member = ((JObject)token)["v"];
                    if (member is null ||
                        (member.Type != JTokenType.Integer && member.Type != JTokenType.Float))
                    {
                        return value;
                    }
                    number = member.ToObject<double>();
                }
                else
                {
                    return value;
                }
                return JsonValue.From(new JObject { ["v"] = new JValue(number) });
            }
        }

        // An identity Codec admitting every value; used for the null rule on
        // custom types.
        private sealed class PassthroughCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) => value;
        }

        // Promotes every input — including an explicit null — to a fixed
        // non-null marker; proves an accepted null is preserved before the
        // Codec ever runs, never rewritten by Codec normalization.
        private sealed class NullPromotingCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) => Json("\"promoted\"");
        }

        // ── Contract-violating codecs (SPEC §B7.1 item 4) ──────────────────

        // Throws inside Normalize; the exception text contains a fake secret
        // to prove sanitization. Behaves on the registration probe inputs so
        // the fixture still registers under the §B7.1 screen (T-W2.12-03);
        // every codec below behaves on the probes for the same reason.
        private sealed class ThrowingCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) =>
                CodecProbeTokens.IsProbe(value)
                    ? value
                    : throw new InvalidOperationException(
                        "upstream API key 'sk-live-9f8e7d6c' expired");
        }

        // Returns a null JsonValue reference.
        private sealed class NullReturningCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) =>
                CodecProbeTokens.IsProbe(value) ? value : null!;
        }

        // Turns a non-null input into an explicit JSON null — the "wrong
        // null" of SPEC §B7.1 item 4 outside a Nullable port.
        private sealed class NullTokenCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) =>
                CodecProbeTokens.IsProbe(value) ? value : Json("null");
        }

        // Every call returns a different value — never idempotent.
        private sealed class NonIdempotentCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                value.ToToken().Type == JTokenType.Integer
                    ? Array.Empty<Diagnostic>()
                    : new[]
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003, "Value is not a JSON integer."),
                    };

            public JsonValue Normalize(JsonValue value)
            {
                if (CodecProbeTokens.IsProbe(value))
                {
                    return value;
                }
                var token = value.ToToken();
                return token.Type == JTokenType.Integer
                    ? JsonValue.From(new JValue((long)((JValue)token).Value! + 1L))
                    : value;
            }
        }

        // Validates JSON strings but normalizes to an integer, which fails
        // the same codec's own revalidation.
        private sealed class InvalidResultCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                value.ToToken().Type == JTokenType.String
                    ? Array.Empty<Diagnostic>()
                    : new[]
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003, "Value is not a JSON string."),
                    };

            public JsonValue Normalize(JsonValue value) =>
                CodecProbeTokens.IsProbe(value) ? value : Json("42");
        }

        // Honors port constraints on Validate, then normalizes to a value the
        // same constraints reject ("red" → "blue" under enum ["red"]).
        private sealed class ConstraintEscapingCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                var diagnostics = new List<Diagnostic>();
                var token = value.ToToken();
                if (token.Type != JTokenType.String)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003, "Value is not a JSON string."));
                    return diagnostics;
                }
                var text = (string)((JValue)token).Value!;
                if (constraints?.Enum is not null && !ContainsOrdinal(constraints.Enum, text))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "Value is not in the allowed set of values."));
                }
                return diagnostics;
            }

            public JsonValue Normalize(JsonValue value) => Json("\"blue\"");

            private static bool ContainsOrdinal(IReadOnlyList<string> allowed, string text)
            {
                for (var i = 0; i < allowed.Count; i++)
                {
                    if (string.Equals(allowed[i], text, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        // Normalizes to a fixed marker but throws when given that marker —
        // the exception surfaces only on the idempotence check's second call.
        private sealed class ThrowsOnOwnOutputCodec : IValueCodec
        {
            private const string Marker = "\"normalized\"";

            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value)
            {
                if (CodecProbeTokens.IsProbe(value))
                {
                    return value;
                }
                if (string.Equals(value.CanonicalText, Marker, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("cannot handle own output");
                }
                return Json(Marker);
            }
        }

        // Throws inside Validate — used to prove a failing Codec surfaces as
        // OW-TYPE-003 diagnostics, never as an exception crossing the
        // Result<T> boundary (SPEC §0.4, §B7.1 item 4).
        private sealed class ThrowingValidateCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                CodecProbeTokens.IsProbe(value)
                    ? Array.Empty<Diagnostic>()
                    : throw new InvalidOperationException(
                        "connection string 'Server=secret.example;Password=pw' rejected");

            public JsonValue Normalize(JsonValue value) => value;
        }

        // Returns a null diagnostic list from Validate.
        private sealed class NullDiagnosticsCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                CodecProbeTokens.IsProbe(value) ? Array.Empty<Diagnostic>() : null!;

            public JsonValue Normalize(JsonValue value) => value;
        }

        // Returns a diagnostic list containing a null element.
        private sealed class NullElementDiagnosticsCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                CodecProbeTokens.IsProbe(value)
                    ? Array.Empty<Diagnostic>()
                    : new Diagnostic[] { null! };

            public JsonValue Normalize(JsonValue value) => value;
        }

        // Builds the failing diagnostic's JsonPath from the rejected value
        // — the secret-leak channel of the revalidation failure (SPEC
        // §B7.1 item 4). Accepts only the string "ok"; Normalize maps
        // "ok" to a fake secret that its own Validate then rejects under
        // a value-derived JsonPath.
        private sealed class SecretPathCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                var token = value.ToToken();
                if (token.Type == JTokenType.String &&
                    string.Equals((string)((JValue)token).Value!, "ok", StringComparison.Ordinal))
                {
                    return Array.Empty<Diagnostic>();
                }
                return new[]
                {
                    ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "Value is not accepted.",
                        jsonPath: "$.rejected-" + value.CanonicalText),
                };
            }

            public JsonValue Normalize(JsonValue value) => Json("\"sk-secret-path-0042\"");
        }

        // Variant of SecretPathCodec whose value-derived JsonPath is a bare
        // numeric bracket segment — the same shape the framework's own
        // element indices take, so provenance cannot be recovered by parsing
        // the composed string. Accepts only the string "ok"; Normalize maps
        // "ok" to the integer 123456, which its own Validate then rejects
        // under "$[123456]".
        private sealed class SecretIndexCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                var token = value.ToToken();
                if (token.Type == JTokenType.String &&
                    string.Equals((string)((JValue)token).Value!, "ok", StringComparison.Ordinal))
                {
                    return Array.Empty<Diagnostic>();
                }
                return new[]
                {
                    ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "Value is not accepted.",
                        jsonPath: "$[" + value.CanonicalText + "]"),
                };
            }

            public JsonValue Normalize(JsonValue value) => Json("123456");
        }

        // ── Registry plumbing ─────────────────────────────────────────────

        private sealed class TestTypeProvider : ITypeProvider
        {
            private readonly TypeDefinition[] _types;

            internal TestTypeProvider(params TypeDefinition[] types) => _types = types;

            public IEnumerable<TypeDefinition> GetTypes() => _types;
        }

        private static TypeDefinition CustomType(string id, IValueCodec codec) =>
            new TypeDefinition(
                id, TypeKind.Value, 0, Array.Empty<string>(), null, "a custom test type", codec);

        private static ITypeRegistry Registry(params TypeDefinition[] types)
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "acme", new TestTypeProvider(types), OperationOrigin.Custom),
            });
            Assert.AreEqual(0, build.Failures.Count, "registry build failed");
            return build.Registry;
        }

        private static readonly ITypeRegistry s_registry = Registry(
            CustomType("acme.PosePoint", new PosePointCodec()),
            CustomType("acme.BoxedNumber", new BoxedNumberCodec()),
            CustomType("acme.Passthrough", new PassthroughCodec()),
            CustomType("acme.Throws", new ThrowingCodec()),
            CustomType("acme.NullResult", new NullReturningCodec()),
            CustomType("acme.NullToken", new NullTokenCodec()),
            CustomType("acme.NotIdempotent", new NonIdempotentCodec()),
            CustomType("acme.InvalidResult", new InvalidResultCodec()),
            CustomType("acme.Escape", new ConstraintEscapingCodec()),
            CustomType("acme.SelfThrow", new ThrowsOnOwnOutputCodec()),
            CustomType("acme.ThrowingValidate", new ThrowingValidateCodec()),
            CustomType("acme.NullDiagnostics", new NullDiagnosticsCodec()),
            CustomType("acme.NullElement", new NullElementDiagnosticsCodec()),
            CustomType("acme.Promote", new NullPromotingCodec()),
            CustomType("acme.SecretPath", new SecretPathCodec()),
            CustomType("acme.SecretIndex", new SecretIndexCodec()));

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "JsonValue.Parse failed for " + text);
            return parsed.Value!;
        }

        private static TypeRef Type(string id)
        {
            var parsed = TypeRef.Parse(id);
            Assert.IsTrue(parsed.Ok, "TypeRef.Parse failed: " + id);
            return parsed.Value!;
        }

        private static Result<JsonValue> Normalize(
            ITypeRegistry registry,
            string typeId,
            JsonValue value,
            PortConstraints? constraints = null,
            bool nullable = false) =>
            ValueValidation.Normalize(registry, Type(typeId), value, constraints, nullable);

        private static Diagnostic MustFailNormalize(
            ITypeRegistry registry,
            string typeId,
            JsonValue value,
            PortConstraints? constraints = null,
            bool nullable = false)
        {
            var result = Normalize(registry, typeId, value, constraints, nullable);
            Assert.IsFalse(result.Ok, "Normalize unexpectedly succeeded: " + typeId);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Retryable);
            return diagnostic;
        }

        private static Diagnostic MustFailValidate(
            ITypeRegistry registry, string typeId, JsonValue value)
        {
            var diagnostics = ValueValidation.Validate(
                registry, Type(typeId), value, null, false);
            Assert.AreEqual(1, diagnostics.Count, typeId);
            var diagnostic = diagnostics[0];
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Retryable);
            return diagnostic;
        }

        // ── The pure value transformation contract (SPEC §B7.1 items 1–3) ──

        [Test]
        public void ClrCodecNormalizeByteEqualsCanonicalToJsonOfFromJson()
        {
            // SPEC §B7.1 item 2: an IClrValueCodec<T>'s normalization result
            // is Canonical JSON v1 byte-identical to ToJson(FromJson(value)).
            var codec = new PosePointCodec();
            var value = Json("{\"x\":0,\"y\":1.0,\"z\":2}");

            var normalized = codec.Normalize(value);
            var expected = codec.ToJson(codec.FromJson(value));
            Assert.AreEqual(expected.CanonicalText, normalized.CanonicalText);
            Assert.AreEqual("{\"x\":0e0,\"y\":1e0,\"z\":2e0}", normalized.CanonicalText);
        }

        [Test]
        public void IntegerAndDecimalSpellingsShareOneNormalForm()
        {
            // SPEC §B7.1 test: Double positions collapse integer and decimal
            // spellings — written 0 and 0.0 persist as identical bytes.
            var fromIntegers = Normalize(
                s_registry, "acme.PosePoint", Json("{\"x\":0,\"y\":1,\"z\":0}"));
            var fromDecimals = Normalize(
                s_registry, "acme.PosePoint", Json("{\"x\":0.0,\"y\":1.0,\"z\":0.0}"));
            Assert.IsTrue(fromIntegers.Ok);
            Assert.IsTrue(fromDecimals.Ok);
            Assert.AreEqual(fromIntegers.Value!.CanonicalText, fromDecimals.Value!.CanonicalText);
            Assert.AreEqual("{\"x\":0e0,\"y\":1e0,\"z\":0e0}", fromIntegers.Value!.CanonicalText);
            Assert.AreEqual(fromIntegers.Value, fromDecimals.Value);
        }

        [Test]
        public void ReorderedStructKeysProduceIdenticalCanonicalBytes()
        {
            // SPEC §B7.1 test: input key order does not change the stored
            // value — the inputs that would differ in a planHash reduce to
            // identical canonical bytes (planHash itself is T-W1.6-07).
            var forward = Normalize(
                s_registry, "acme.PosePoint", Json("{\"x\":1,\"y\":2,\"z\":3}"));
            var reordered = Normalize(
                s_registry, "acme.PosePoint", Json("{\"z\":3,\"y\":2,\"x\":1}"));
            Assert.IsTrue(forward.Ok);
            Assert.IsTrue(reordered.Ok);
            Assert.AreEqual(forward.Value!.CanonicalText, reordered.Value!.CanonicalText);
            Assert.AreEqual(forward.Value!.Sha256, reordered.Value!.Sha256);
        }

        [Test]
        public void NormalizeIsDeterministicIdempotentAndInputPreserving()
        {
            var input = Json("{\"x\":0,\"y\":1,\"z\":0}");
            var first = Normalize(s_registry, "acme.PosePoint", input);
            var second = Normalize(s_registry, "acme.PosePoint", input);
            Assert.IsTrue(first.Ok);
            Assert.IsTrue(second.Ok);
            Assert.AreEqual(first.Value!.CanonicalText, second.Value!.CanonicalText);

            // The framework's own idempotence probe calls the codec a second
            // time; a direct second call agrees byte-for-byte.
            var codec = new PosePointCodec();
            Assert.AreEqual(
                first.Value!.CanonicalText, codec.Normalize(first.Value!).CanonicalText);

            // The input JsonValue is immutable and is not consumed.
            Assert.AreEqual("{\"x\":0,\"y\":1,\"z\":0}", input.CanonicalText);
        }

        [Test]
        public void ClrRoundTripDecodeReencodesToSameCanonicalText()
        {
            // SPEC §B7.1 test: a stored value decoded back to the CLR value
            // and re-encoded yields the same CanonicalText.
            var codec = new PosePointCodec();
            var normalized = Normalize(
                s_registry, "acme.PosePoint", Json("{\"x\":0,\"y\":1,\"z\":0}"));
            Assert.IsTrue(normalized.Ok);
            var reencoded = codec.ToJson(codec.FromJson(normalized.Value!));
            Assert.AreEqual(normalized.Value!.CanonicalText, reencoded.CanonicalText);
        }

        [Test]
        public void NormalizedValueSurvivesStoreAndReloadRoundTrip()
        {
            // SPEC §B7.1 test: the persisted form is invariant across a
            // Canonical JSON store/reload cycle — the Journal integration
            // itself is owned by T-W1.8-04.
            var normalized = Normalize(
                s_registry, "acme.PosePoint", Json("{\"x\":0,\"y\":1,\"z\":0}"));
            Assert.IsTrue(normalized.Ok);
            var reloaded = JsonValue.Parse(normalized.Value!.CanonicalText);
            Assert.IsTrue(reloaded.Ok);
            Assert.AreEqual(normalized.Value, reloaded.Value);
            Assert.AreEqual(normalized.Value!.Sha256, reloaded.Value!.Sha256);
        }

        [Test]
        public void NonClrCodecNormalizesEquivalentSpellingsToOneForm()
        {
            // Non-CLR codecs offer an equivalent normal form (SPEC §B7.1
            // item 2): the bare-number and boxed spellings of the same value
            // persist identically.
            var bare = Normalize(s_registry, "acme.BoxedNumber", Json("5"));
            var boxed = Normalize(s_registry, "acme.BoxedNumber", Json("{\"v\":5}"));
            Assert.IsTrue(bare.Ok);
            Assert.IsTrue(boxed.Ok);
            Assert.AreEqual("{\"v\":5e0}", bare.Value!.CanonicalText);
            Assert.AreEqual(bare.Value!.CanonicalText, boxed.Value!.CanonicalText);
        }

        [Test]
        public void CoreJsonKeepsWrittenTokensAlongsideCustomTypes()
        {
            // SPEC §B7 item 4 / §D0.2: core.Json is untyped, so 1 and 1.0
            // remain distinct normal forms even in a registry that also
            // carries custom types.
            var one = Normalize(s_registry, "core.Json", Json("1"));
            var onePointZero = Normalize(s_registry, "core.Json", Json("1.0"));
            Assert.IsTrue(one.Ok);
            Assert.IsTrue(onePointZero.Ok);
            Assert.AreEqual("1", one.Value!.CanonicalText);
            Assert.AreEqual("1e0", onePointZero.Value!.CanonicalText);
            Assert.AreNotEqual(one.Value!.CanonicalText, onePointZero.Value!.CanonicalText);
        }

        [Test]
        public void ListOfCustomTypeNormalizesEachElement()
        {
            var result = Normalize(
                s_registry,
                "core.List<acme.PosePoint>",
                Json("[{\"x\":0,\"y\":1,\"z\":0},{\"x\":0.5,\"y\":0,\"z\":1}]"));
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(
                "[{\"x\":0e0,\"y\":1e0,\"z\":0e0},{\"x\":5e-1,\"y\":0e0,\"z\":1e0}]",
                result.Value!.CanonicalText);
        }

        [Test]
        public void AcceptedExplicitNullNormalizesToNullNotMissing()
        {
            // On a Nullable port the explicit null accepted by the null rule
            // stays an explicit null — never confused with Missing, which is
            // the caller's own absence marker (SPEC §A5/§B0.1, §B7.1 item 2).
            var result = Normalize(
                s_registry, "acme.Passthrough", Json("null"), nullable: true);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("null", result.Value!.CanonicalText);
        }

        [Test]
        public void AcceptedNullOnNullableCustomPortNeverReachesTheCodec()
        {
            // The null rule accepts the explicit null at the framework
            // boundary (SPEC §A5/§B0.1): PosePointCodec's Normalize would
            // throw on it (its FromJson expects an object), so success
            // proves the Codec is never invoked on a sanctioned null.
            Assert.AreEqual(
                0,
                ValueValidation.Validate(
                    s_registry, Type("acme.PosePoint"), Json("null"), null, true).Count);
            var result = Normalize(
                s_registry, "acme.PosePoint", Json("null"), nullable: true);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("null", result.Value!.CanonicalText);
        }

        [Test]
        public void AcceptedNullIsNotRewrittenByCodecNormalization()
        {
            // A Codec that would promote null to a non-null normal form
            // never sees it: the accepted null is preserved verbatim.
            var result = Normalize(
                s_registry, "acme.Promote", Json("null"), nullable: true);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual("null", result.Value!.CanonicalText);
        }

        [Test]
        public void NullOnNonNullableCustomPortFailsClosed()
        {
            // A null the null rule does not admit fails with the same
            // OW-TYPE-003 as Validate instead of reaching the Codec.
            var diagnostic = MustFailNormalize(s_registry, "acme.Promote", Json("null"));
            StringAssert.Contains("null", diagnostic.Message);
        }

        // ── Contract-violation classification (SPEC §B7.1 item 4) ──────────

        [Test]
        public void ThrowingCodecFailsWithSanitizedOwType003()
        {
            // The input and the thrown message both embed a fake secret;
            // neither may reach the diagnostic (SPEC §B7.1 item 4).
            var diagnostic = MustFailNormalize(
                s_registry, "acme.Throws", Json("\"sk-live-9f8e7d6c\""));
            StringAssert.DoesNotContain("sk-live-9f8e7d6c", diagnostic.Message);
            StringAssert.DoesNotContain("upstream API key", diagnostic.Message);
            StringAssert.DoesNotContain("expired", diagnostic.Message);
            StringAssert.DoesNotContain("InvalidOperationException", diagnostic.Message);
            StringAssert.Contains("threw an exception", diagnostic.Message);
            Assert.IsNull(diagnostic.Hint);
            Assert.IsNull(diagnostic.JsonPath);
        }

        [Test]
        public void CodecExceptionOnUnexpectedInputIsStillOwType003()
        {
            var diagnostic = MustFailNormalize(s_registry, "acme.PosePoint", Json("5"));
            StringAssert.Contains("threw an exception", diagnostic.Message);
        }

        [Test]
        public void NullReturningCodecFailsWithOwType003()
        {
            var diagnostic = MustFailNormalize(s_registry, "acme.NullResult", Json("5"));
            StringAssert.Contains("returned null", diagnostic.Message);
        }

        [Test]
        public void WrongNullResultFailsWithOwType003()
        {
            // A codec that turns a non-null input into explicit JSON null on
            // a non-Nullable position produces an "unexpected null"
            // (SPEC §B7.1 item 4) and is rejected.
            var diagnostic = MustFailNormalize(s_registry, "acme.NullToken", Json("5"));
            StringAssert.Contains("fails validation", diagnostic.Message);
        }

        [Test]
        public void WrongNullResultOnNullablePortFailsWithOwType003()
        {
            // On a Nullable port the same codec-produced JSON null would
            // satisfy the revalidation's null rule and silently replace
            // the non-null input with null as the persisted value; it is
            // still an "unexpected null" (SPEC §B7.1 item 4) and fails
            // closed as one sanitized OW-TYPE-003.
            var diagnostic = MustFailNormalize(
                s_registry, "acme.NullToken", Json("5"), nullable: true);
            StringAssert.Contains("unexpected null", diagnostic.Message);
            Assert.IsNull(diagnostic.Hint);
            Assert.IsNull(diagnostic.JsonPath);
        }

        [Test]
        public void NonIdempotentCodecFailsWithOwType003()
        {
            var diagnostic = MustFailNormalize(s_registry, "acme.NotIdempotent", Json("5"));
            StringAssert.Contains("not idempotent", diagnostic.Message);
        }

        [Test]
        public void InvalidNormalizedValueFailsWithOwType003()
        {
            var diagnostic = MustFailNormalize(
                s_registry, "acme.InvalidResult", Json("\"abc\""));
            StringAssert.Contains("fails validation", diagnostic.Message);
        }

        [Test]
        public void ConstraintEscapingNormalizationFailsWithOwType003()
        {
            var constraints = new PortConstraints(new[] { "red" }, null, null, null, null);
            // The written input satisfies the port constraints …
            Assert.AreEqual(
                0,
                ValueValidation.Validate(
                    s_registry, Type("acme.Escape"), Json("\"red\""), constraints, false).Count);
            // … but the Codec normalizes it to a value the same constraints
            // reject, and the revalidation catches the escape.
            var diagnostic = MustFailNormalize(
                s_registry, "acme.Escape", Json("\"red\""), constraints);
            StringAssert.Contains("fails validation", diagnostic.Message);
        }

        [Test]
        public void ThrowingOnOwnNormalizedOutputFailsWithOwType003()
        {
            // The codec produces a value that passes revalidation but throws
            // when asked to normalize it again — still one OW-TYPE-003.
            var diagnostic = MustFailNormalize(s_registry, "acme.SelfThrow", Json("5"));
            StringAssert.Contains("threw an exception", diagnostic.Message);
        }

        [Test]
        public void FailingNormalizeLeavesInputUnchanged()
        {
            var input = Json("{\"x\":0,\"y\":1,\"z\":0}");
            MustFailNormalize(s_registry, "acme.Throws", input);
            Assert.AreEqual("{\"x\":0,\"y\":1,\"z\":0}", input.CanonicalText);
        }

        [Test]
        public void ThrowingValidateIsClassifiedAsOwType003()
        {
            // A Codec failing inside Validate produces OW-TYPE-003
            // diagnostics — fail closed, without the exception text.
            var diagnostic = MustFailValidate(s_registry, "acme.ThrowingValidate", Json("5"));
            StringAssert.DoesNotContain("secret.example", diagnostic.Message);
            StringAssert.DoesNotContain("connection string", diagnostic.Message);
            StringAssert.DoesNotContain("Password", diagnostic.Message);
        }

        [Test]
        public void NullDiagnosticsResultIsClassifiedAsOwType003()
        {
            var diagnostic = MustFailValidate(s_registry, "acme.NullDiagnostics", Json("5"));
            StringAssert.Contains("no validation result", diagnostic.Message);
        }

        [Test]
        public void NullDiagnosticElementIsClassifiedAsOwType003()
        {
            var diagnostic = MustFailValidate(s_registry, "acme.NullElement", Json("5"));
            StringAssert.Contains("invalid validation result", diagnostic.Message);
        }

        [Test]
        public void RevalidationFailureDropsCodecSuppliedJsonPath()
        {
            // The codec builds the failing diagnostic's JsonPath from the
            // rejected value — a fake secret. The sanitized OW-TYPE-003
            // keeps only framework-derived location information, so the
            // root-level diagnostic carries no JsonPath at all (SPEC
            // §B7.1 item 4).
            var input = Json("\"ok\"");
            Assert.AreEqual(
                0,
                ValueValidation.Validate(
                    s_registry, Type("acme.SecretPath"), input, null, false).Count);
            var diagnostic = MustFailNormalize(s_registry, "acme.SecretPath", input);
            StringAssert.Contains("fails validation", diagnostic.Message);
            Assert.IsNull(diagnostic.JsonPath);
            StringAssert.DoesNotContain("sk-secret-path-0042", diagnostic.Message);
        }

        [Test]
        public void RevalidationFailureDropsNumericCodecSuppliedJsonPath()
        {
            // The codec's value-derived JsonPath is a bare numeric bracket
            // segment — identical in shape to a framework element index, so
            // the sanitized OW-TYPE-003 cannot keep it by parsing: the
            // framework-stamped location is the value root, which carries
            // no JsonPath (SPEC §B7.1 item 4).
            var input = Json("\"ok\"");
            Assert.AreEqual(
                0,
                ValueValidation.Validate(
                    s_registry, Type("acme.SecretIndex"), input, null, false).Count);
            var diagnostic = MustFailNormalize(s_registry, "acme.SecretIndex", input);
            StringAssert.Contains("fails validation", diagnostic.Message);
            Assert.IsNull(diagnostic.JsonPath);
            StringAssert.DoesNotContain("123456", diagnostic.Message);
        }

        // ── Element-level classification inside core.List<T> ───────────────

        [Test]
        public void ThrowingElementCodecFailsElementValidationInPlace()
        {
            // A throwing element Codec inside a closed core.List<T> produces
            // element-scoped OW-TYPE-003 diagnostics instead of aborting the
            // whole list validation.
            var broken = Registry(CustomType("acme.Bomb", new ThrowingValidateCodec()));
            var diagnostics = ValueValidation.Validate(
                broken, Type("core.List<acme.Bomb>"), Json("[1,2]"), null, false);
            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostics[0].Code);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostics[1].Code);
            Assert.AreEqual("$[0]", diagnostics[0].JsonPath);
            Assert.AreEqual("$[1]", diagnostics[1].JsonPath);
        }

        [Test]
        public void InvalidElementInNormalizedListCarriesElementJsonPath()
        {
            var broken = Registry(CustomType("acme.BadElement", new InvalidResultCodec()));
            var result = ValueValidation.Normalize(
                broken, Type("core.List<acme.BadElement>"), Json("[\"a\"]"), null, false);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, result.Diagnostics[0].Code);
            Assert.AreEqual("$[0]", result.Diagnostics[0].JsonPath);
        }

        [Test]
        public void ElementRevalidationFailureKeepsOnlyTheFrameworkPath()
        {
            // Inside core.List<T> the framework-derived "$[0]" element
            // location is retained, but the Codec-supplied suffix built
            // from the rejected value — a fake secret — is dropped.
            var result = ValueValidation.Normalize(
                s_registry, Type("core.List<acme.SecretPath>"), Json("[\"ok\"]"), null, false);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostic.Code);
            Assert.AreEqual("$[0]", diagnostic.JsonPath);
            StringAssert.DoesNotContain("sk-secret-path-0042", diagnostic.Message);
            StringAssert.DoesNotContain("rejected-", diagnostic.JsonPath!);
        }

        [Test]
        public void ElementRevalidationFailureDropsNumericCodecSuppliedJsonPath()
        {
            // Inside core.List<T> the codec appends its own value-derived
            // numeric segment after the framework's "$[0]"; the sanitized
            // failure keeps only the framework-stamped prefix — the codec's
            // "$[123456]" is dropped although it parses like an element
            // index (SPEC §B7.1 item 4).
            var result = ValueValidation.Normalize(
                s_registry, Type("core.List<acme.SecretIndex>"), Json("[\"ok\"]"), null, false);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostic.Code);
            Assert.AreEqual("$[0]", diagnostic.JsonPath);
            StringAssert.DoesNotContain("123456", diagnostic.JsonPath!);
            StringAssert.DoesNotContain("123456", diagnostic.Message);
        }

        [Test]
        public void ThrowingElementCodecFailsTheWholeNormalize()
        {
            var broken = Registry(CustomType("acme.Bomb", new ThrowingCodec()));
            var result = ValueValidation.Normalize(
                broken, Type("core.List<acme.Bomb>"), Json("[1]"), null, false);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostic.Code);
            StringAssert.DoesNotContain("sk-live-9f8e7d6c", diagnostic.Message);
        }
    }
}
