#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the <see cref="ValueValidation"/> framework entry
    /// point (T-W1.4-04): the SPEC §A5/§B0.1 explicit-null rule driven by the
    /// port's nullable flag, <c>core.List&lt;T&gt;</c> element recursion with
    /// element <see cref="Diagnostic.JsonPath"/>s, type resolution failures
    /// and closed-type normalization (SPEC §B7.1).
    /// </summary>
    internal sealed class ValueValidationTests
    {
        private static readonly ITypeRegistry s_registry = TypeRegistryBuilder.Build(new[]
        {
            new TypeProviderCandidate(
                CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
        }).Registry;

        private static TypeRef Type(string id)
        {
            var parsed = TypeRef.Parse(id);
            Assert.IsTrue(parsed.Ok, "TypeRef.Parse failed: " + id);
            return parsed.Value!;
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "JsonValue.Parse failed for " + text);
            return parsed.Value!;
        }

        private static IReadOnlyList<Diagnostic> Validate(
            string typeId, string json, PortConstraints? constraints = null, bool nullable = false) =>
            ValueValidation.Validate(s_registry, Type(typeId), Json(json), constraints, nullable);

        private static Diagnostic MustFail(
            string typeId, string json, string code,
            PortConstraints? constraints = null, bool nullable = false)
        {
            var diagnostics = Validate(typeId, json, constraints, nullable);
            Assert.AreEqual(1, diagnostics.Count, typeId + " / " + json);
            Assert.AreEqual(code, diagnostics[0].Code);
            Assert.AreEqual(Severity.Error, diagnostics[0].Severity);
            Assert.IsFalse(diagnostics[0].Retryable);
            return diagnostics[0];
        }

        private static string Normalize(string typeId, string json, bool nullable = false)
        {
            var normalized =
                ValueValidation.Normalize(s_registry, Type(typeId), Json(json), null, nullable);
            Assert.IsTrue(normalized.Ok, "Normalize failed: " + typeId + " / " + json);
            return normalized.Value!.CanonicalText;
        }

        // ── SPEC §A5 test 2 / §B0.1: the explicit-null rule ───────────────────

        [Test]
        public void ExplicitNullIsRejectedOnNonNullablePorts()
        {
            // Nullable=false (the default) rejects explicit null with OW-TYPE-003.
            var diagnostic = MustFail("core.String", "null", ErrorCodes.OW_TYPE_003);
            StringAssert.Contains("null", diagnostic.Message);
            MustFail("core.Int", "null", ErrorCodes.OW_TYPE_003);
            MustFail("core.Float", "null", ErrorCodes.OW_TYPE_003);
            MustFail("core.Bool", "null", ErrorCodes.OW_TYPE_003);
            MustFail("core.List<core.Int>", "null", ErrorCodes.OW_TYPE_003);
        }

        [Test]
        public void ExplicitNullIsAcceptedOnNullablePortsAndCoreJson()
        {
            // SPEC §A5 test 2: Nullable=true and core.Json admit explicit null.
            Assert.AreEqual(0, Validate("core.String", "null", nullable: true).Count);
            Assert.AreEqual(0, Validate("core.Int", "null", nullable: true).Count);
            Assert.AreEqual(0, Validate("core.Float", "null", nullable: true).Count);
            Assert.AreEqual(0, Validate("core.Bool", "null", nullable: true).Count);
            Assert.AreEqual(0, Validate("core.List<core.Int>", "null", nullable: true).Count);
            Assert.AreEqual(0, Validate("core.Json", "null").Count);
            Assert.AreEqual(0, Validate("core.Json", "null", nullable: true).Count);
        }

        [Test]
        public void AcceptedNullOnNullablePortSkipsConstraints()
        {
            // Documented local choice: an explicit null accepted through the
            // nullable flag is the sanctioned absence of a value; constraint
            // keywords restrict actual data and do not apply to it.
            var withEnum = new PortConstraints(new[] { "a" }, null, null, null, null);
            Assert.AreEqual(0, Validate("core.String", "null", withEnum, nullable: true).Count);
        }

        [Test]
        public void NullableFlagDoesNotLoosenTypeChecks()
        {
            MustFail("core.Int", "\"x\"", ErrorCodes.OW_TYPE_003, nullable: true);
            MustFail("core.Int", "1.0", ErrorCodes.OW_TYPE_003, nullable: true);
        }

        // ── Type-level validation through the registry ────────────────────────

        [Test]
        public void ValidateAppliesTypeChecksAndConstraints()
        {
            // Acceptance: core.Float accepts 1 and stores 1e0.
            Assert.AreEqual(0, Validate("core.Float", "1").Count);
            Assert.AreEqual("1e0", Normalize("core.Float", "1"));

            // Acceptance: core.Int rejects 1.0 with OW-TYPE-003.
            MustFail("core.Int", "1.0", ErrorCodes.OW_TYPE_003);

            var withEnum = new PortConstraints(new[] { "red" }, null, null, null, null);
            Assert.AreEqual(0, Validate("core.String", "\"red\"", withEnum).Count);
            MustFail("core.String", "\"blue\"", ErrorCodes.OW_TYPE_003, withEnum);
        }

        [Test]
        public void UnknownTypeFailsWithOwType001()
        {
            MustFail("acme.Missing", "5", ErrorCodes.OW_TYPE_001);
        }

        // ── core.List<T> element recursion (SPEC §B3: elements are T) ────────

        [Test]
        public void ListElementsAreValidatedAgainstTheElementType()
        {
            Assert.AreEqual(0, Validate("core.List<core.Int>", "[]").Count);
            Assert.AreEqual(0, Validate("core.List<core.Int>", "[1,2,3]").Count);
            Assert.AreEqual(0, Validate("core.List<core.String>", "[\"a\",\"b\"]").Count);
            Assert.AreEqual(0, Validate("core.List<core.Float>", "[1,0.5]").Count);
            Assert.AreEqual(0, Validate("core.List<core.Bool>", "[true,false]").Count);

            MustFail("core.List<core.Int>", "5", ErrorCodes.OW_TYPE_003); // not an array
            MustFail("core.List<core.Int>", "\"x\"", ErrorCodes.OW_TYPE_003);
        }

        [Test]
        public void ListElementFailuresCarryElementJsonPaths()
        {
            var diagnostics = Validate("core.List<core.Int>", "[1,\"x\",2.5]");
            Assert.AreEqual(2, diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostics[0].Code);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostics[1].Code);
            Assert.AreEqual("$[1]", diagnostics[0].JsonPath);
            Assert.AreEqual("$[2]", diagnostics[1].JsonPath);
        }

        [Test]
        public void ListElementNullFollowsTheElementTypeNullRule()
        {
            // Elements have no port-level Nullable flag: only a core.Json
            // element type admits an explicit null.
            var diagnostic = MustFail("core.List<core.Int>", "[1,null]", ErrorCodes.OW_TYPE_003);
            Assert.AreEqual("$[1]", diagnostic.JsonPath);
            Assert.AreEqual(0, Validate("core.List<core.Json>", "[1,null,\"a\",{\"k\":2}]").Count);
        }

        [Test]
        public void NestedListsRecurseToTheInnermostElementType()
        {
            Assert.AreEqual(0, Validate("core.List<core.List<core.Int>>", "[[1],[2,3]]").Count);
            Assert.AreEqual(0, Validate("core.List<core.List<core.Int>>", "[[],[]]").Count);

            var diagnostic = MustFail(
                "core.List<core.List<core.Int>>", "[[\"a\"]]", ErrorCodes.OW_TYPE_003);
            Assert.AreEqual("$[0][0]", diagnostic.JsonPath);
            var wrongShape = MustFail(
                "core.List<core.List<core.Int>>", "[[[]]]", ErrorCodes.OW_TYPE_003);
            Assert.AreEqual("$[0][0]", wrongShape.JsonPath);
        }

        [Test]
        public void UnregisteredListElementTypeFailsWithOwType001()
        {
            MustFail("core.List<acme.Missing>", "[]", ErrorCodes.OW_TYPE_001);
            MustFail("core.List<acme.Missing>", "[1]", ErrorCodes.OW_TYPE_001);
        }

        // ── Closed-type normalization (SPEC §B7.1) ───────────────────────────

        [Test]
        public void NormalizeConvertsIntegerTokensInDoublePositionsOnly()
        {
            Assert.AreEqual("0e0", Normalize("core.Float", "0"));
            Assert.AreEqual("0e0", Normalize("core.Float", "0.0"));
            Assert.AreEqual("5", Normalize("core.Int", "5"));
            Assert.AreEqual("\"x\"", Normalize("core.String", "\"x\""));
            Assert.AreEqual("true", Normalize("core.Bool", "true"));
        }

        [Test]
        public void JsonNormalizePreservesWrittenTokens()
        {
            Assert.AreEqual("1", Normalize("core.Json", "1"));
            Assert.AreEqual("1e0", Normalize("core.Json", "1.0"));
            Assert.AreNotEqual(Normalize("core.Json", "1"), Normalize("core.Json", "1.0"));
            Assert.AreEqual("[1,1e0]", Normalize("core.Json", "[1,1.0]"));
            Assert.AreEqual("null", Normalize("core.Json", "null"));
        }

        [Test]
        public void ListNormalizeConvertsEachElementWithItsCodec()
        {
            Assert.AreEqual("[0e0,1e0]", Normalize("core.List<core.Float>", "[0,1]"));
            Assert.AreEqual("[15e-1,0e0]", Normalize("core.List<core.Float>", "[1.5,0.0]"));
            Assert.AreEqual("[1,2]", Normalize("core.List<core.Int>", "[1,2]"));
            Assert.AreEqual("[1,1e0]", Normalize("core.List<core.Json>", "[1,1.0]"));
            Assert.AreEqual("[[0e0],[15e-1]]",
                Normalize("core.List<core.List<core.Float>>", "[[0],[1.5]]"));
            Assert.AreEqual("[]", Normalize("core.List<core.Float>", "[]"));

            // The element type is normalized with the element codec: Json
            // elements keep their tokens even inside a typed list.
            Assert.AreEqual("[1,1e0]", Normalize("core.List<core.Json>", "[1,1e0]"));
        }

        [Test]
        public void ListNormalizeIsIdempotent()
        {
            var once = ValueValidation.Normalize(
                s_registry, Type("core.List<core.Float>"), Json("[0,1.5]"), null, false);
            Assert.IsTrue(once.Ok);
            var twice = ValueValidation.Normalize(
                s_registry, Type("core.List<core.Float>"), once.Value!, null, false);
            Assert.IsTrue(twice.Ok);
            Assert.AreEqual(once.Value!.CanonicalText, twice.Value!.CanonicalText);
            Assert.AreEqual(once.Value, twice.Value);
        }

        [Test]
        public void NormalizePreservesExplicitNull()
        {
            // An explicit null accepted through the nullable flag is the
            // sanctioned absence of a value: Normalize leaves it untouched
            // (SPEC §B7.1 item 2, "null and Missing follow A0/B0.1").
            Assert.AreEqual("null", Normalize("core.String", "null", nullable: true));
            Assert.AreEqual("null", Normalize("core.List<core.Int>", "null", nullable: true));
        }

        [Test]
        public void NormalizeFailsOnUnresolvableTypes()
        {
            var missing = ValueValidation.Normalize(
                s_registry, Type("acme.Missing"), Json("5"), null, false);
            Assert.IsFalse(missing.Ok);
            Assert.AreEqual(ErrorCodes.OW_TYPE_001, missing.Diagnostics[0].Code);

            var missingElement = ValueValidation.Normalize(
                s_registry, Type("core.List<acme.Missing>"), Json("[]"), null, false);
            Assert.IsFalse(missingElement.Ok);
            Assert.AreEqual(ErrorCodes.OW_TYPE_001, missingElement.Diagnostics[0].Code);
        }

        [Test]
        public void ThreeArgumentNormalizeOverloadRemainsCallable()
        {
            // The pre-§B7.1 signature stays callable for existing consumers:
            // it applies the guarded contract without port constraints, and
            // an explicit null passes through unchanged as it did when the
            // Codec saw the token directly.
            var normalized = ValueValidation.Normalize(
                s_registry, Type("core.Float"), Json("1"));
            Assert.IsTrue(normalized.Ok);
            Assert.AreEqual("1e0", normalized.Value!.CanonicalText);

            var explicitNull = ValueValidation.Normalize(
                s_registry, Type("core.String"), Json("null"));
            Assert.IsTrue(explicitNull.Ok);
            Assert.AreEqual("null", explicitNull.Value!.CanonicalText);

            Assert.Throws<ArgumentNullException>(
                () => ValueValidation.Normalize(null!, Type("core.Int"), Json("1")));
        }

        [Test]
        public void ValidateRejectsNullArguments()
        {
            Assert.Throws<ArgumentNullException>(
                () => ValueValidation.Validate(null!, Type("core.Int"), Json("1"), null, false));
            Assert.Throws<ArgumentNullException>(
                () => ValueValidation.Validate(s_registry, null!, Json("1"), null, false));
            Assert.Throws<ArgumentNullException>(
                () => ValueValidation.Validate(s_registry, Type("core.Int"), null!, null, false));
            Assert.Throws<ArgumentNullException>(
                () => ValueValidation.Normalize(null!, Type("core.Int"), Json("1"), null, false));
            Assert.Throws<ArgumentNullException>(
                () => ValueValidation.Normalize(s_registry, null!, Json("1"), null, false));
            Assert.Throws<ArgumentNullException>(
                () => ValueValidation.Normalize(s_registry, Type("core.Int"), null!, null, false));
        }
    }
}
