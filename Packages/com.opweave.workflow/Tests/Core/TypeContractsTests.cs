#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §B2/§B7/§C0.1/§C1 type-extension contracts
    /// (T-W1.4-02): the single <see cref="IValueCodec"/> interface with
    /// <c>Normalize(JsonValue)</c> (SPEC §B7.1), the
    /// <see cref="IClrValueCodec{T}"/> extension, <see cref="PortConstraints"/>,
    /// <see cref="TypeDefinition"/> record shape and the
    /// <see cref="OpWeaveTypeProviderAttribute"/> detection contract.
    /// </summary>
    internal sealed class TypeContractsTests
    {
        // ── A custom IClrValueCodec<T> implementation with no Unity references ──
        // Normalizes per SPEC §B7.1 item 2: Normalize(value) is Canonical-JSON
        // byte-identical to ToJson(FromJson(value)).
        private sealed class CustomDoubleCodec : IClrValueCodec<double>
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                var type = value.ToToken().Type;
                if (type == JTokenType.Integer || type == JTokenType.Float)
                {
                    return Array.Empty<Diagnostic>();
                }
                return new[]
                {
                    ErrorCodes.Create(ErrorCodes.OW_TYPE_003, "Value is not a number."),
                };
            }

            public JsonValue Normalize(JsonValue value) => ToJson(FromJson(value));

            public double FromJson(JsonValue value) => value.ToToken().ToObject<double>();

            public JsonValue ToJson(double value) => JsonValue.From(new JValue(value));
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "JsonValue.Parse failed for " + text);
            return parsed.Value!;
        }

        // ── IValueCodec: the single codec contract (SPEC §B7.1, §C0.1) ──────────

        [Test]
        public void IValueCodecDeclaresExactlyValidateAndNormalize()
        {
            // SPEC §B7 publishes exactly two members on the public Codec contract;
            // a second, incompatible codec interface must never appear.
            var methods = typeof(IValueCodec).GetMethods();
            Assert.AreEqual(2, methods.Length);

            var normalize = typeof(IValueCodec).GetMethod(nameof(IValueCodec.Normalize));
            Assert.IsNotNull(normalize);
            Assert.AreEqual(typeof(JsonValue), normalize!.ReturnType);
            var parameters = normalize.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(JsonValue), parameters[0].ParameterType);

            var validate = typeof(IValueCodec).GetMethod(nameof(IValueCodec.Validate));
            Assert.IsNotNull(validate);
            Assert.AreEqual(typeof(IReadOnlyList<Diagnostic>), validate!.ReturnType);
            var validateParameters = validate.GetParameters();
            Assert.AreEqual(2, validateParameters.Length);
            Assert.AreEqual(typeof(JsonValue), validateParameters[0].ParameterType);
            Assert.AreEqual(typeof(PortConstraints), validateParameters[1].ParameterType);
        }

        [Test]
        public void IClrValueCodecExtendsIValueCodec()
        {
            Assert.IsTrue(typeof(IValueCodec).IsAssignableFrom(typeof(IClrValueCodec<double>)));
            var codec = (IValueCodec)new CustomDoubleCodec();
            var normalized = codec.Normalize(Json("0"));
            Assert.AreEqual("0e0", normalized.CanonicalText);
        }

        [Test]
        public void CustomCodecNormalizesSpellingsToOneCanonicalForm()
        {
            // SPEC §B7.1: a Double value written as integer 0 or decimal 0.0
            // normalizes to the same persisted form "0e0", and Normalize is
            // idempotent on its own output.
            var codec = new CustomDoubleCodec();
            var fromInteger = codec.Normalize(Json("0"));
            var fromDecimal = codec.Normalize(Json("0.0"));
            Assert.AreEqual("0e0", fromInteger.CanonicalText);
            Assert.AreEqual(fromInteger.CanonicalText, fromDecimal.CanonicalText);
            Assert.AreEqual(fromInteger, fromDecimal);

            var twice = codec.Normalize(fromInteger);
            Assert.AreEqual(fromInteger.CanonicalText, twice.CanonicalText);
        }

        [Test]
        public void CustomCodecRoundTripsAndValidates()
        {
            var codec = new CustomDoubleCodec();
            Assert.AreEqual("5e0", codec.ToJson(codec.FromJson(Json("5"))).CanonicalText);

            Assert.AreEqual(0, codec.Validate(Json("1.5"), null).Count);
            var diagnostics = codec.Validate(Json("\"x\""), null);
            Assert.AreEqual(1, diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_TYPE_003, diagnostics[0].Code);
        }

        // ── PortConstraints (SPEC §C1) ──────────────────────────────────────────

        [Test]
        public void PortConstraintsDefaultsAndConstantOnly()
        {
            var none = new PortConstraints(null, null, null, null, null);
            Assert.IsNull(none.Enum);
            Assert.IsNull(none.Pattern);
            Assert.IsNull(none.Min);
            Assert.IsNull(none.Max);
            Assert.IsNull(none.MaxLength);
            Assert.IsFalse(none.ConstantOnly);

            var constantOnly = new PortConstraints(null, null, null, null, null, ConstantOnly: true);
            Assert.IsTrue(constantOnly.ConstantOnly);

            var constrained = new PortConstraints(new[] { "a", "b" }, "^x", 0.0, 1.0, 8);
            Assert.AreEqual(2, constrained.Enum!.Count);
            Assert.AreEqual("^x", constrained.Pattern);
            Assert.AreEqual(0.0, constrained.Min);
            Assert.AreEqual(1.0, constrained.Max);
            Assert.AreEqual(8, constrained.MaxLength);
        }

        [Test]
        public void PortConstraintsEnumIsDefensivelyCopied()
        {
            var values = new List<string> { "a" };
            var constraints = new PortConstraints(values, null, null, null, null);
            values.Add("b");
            Assert.AreEqual(1, constraints.Enum!.Count);
            Assert.Throws<ArgumentException>(
                () => new PortConstraints(new string[] { null! }, null, null, null, null));
        }

        // ── TypeDefinition record shape (SPEC §B2) ──────────────────────────────

        private static readonly IValueCodec s_codec = new CustomDoubleCodec();

        [Test]
        public void TypeDefinitionHoldsASpecShapedCandidate()
        {
            var definition = new TypeDefinition(
                "acme.Widget",
                TypeKind.Value,
                0,
                new[] { "core.String" },
                null,
                "a test type",
                s_codec);
            Assert.AreEqual("acme.Widget", definition.Id);
            Assert.AreEqual(TypeKind.Value, definition.Kind);
            Assert.AreEqual(0, definition.GenericArity);
            Assert.AreEqual(1, definition.BaseTypes.Count);
            Assert.IsNull(definition.GenericConstraint);
            Assert.AreSame(s_codec, definition.Codec);
        }

        [Test]
        public void TypeDefinitionRejectsStructurallyInvalidShapes()
        {
            Assert.Throws<ArgumentException>(
                () => new TypeDefinition(null!, TypeKind.Primitive, 0,
                    Array.Empty<string>(), null, "d", s_codec));
            Assert.Throws<ArgumentException>(
                () => new TypeDefinition("", TypeKind.Primitive, 0,
                    Array.Empty<string>(), null, "d", s_codec));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TypeDefinition("a.B", TypeKind.Primitive, -1,
                    Array.Empty<string>(), null, "d", s_codec));
            Assert.Throws<ArgumentNullException>(
                () => new TypeDefinition("a.B", TypeKind.Primitive, 0,
                    null!, null, "d", s_codec));
            Assert.Throws<ArgumentException>(
                () => new TypeDefinition("a.B", TypeKind.Primitive, 0,
                    new string[] { null! }, null, "d", s_codec));
            Assert.Throws<ArgumentNullException>(
                () => new TypeDefinition("a.B", TypeKind.Primitive, 0,
                    Array.Empty<string>(), null, null!, s_codec));
        }

        [Test]
        public void TypeDefinitionToleratesANullCodecForBuildScreening()
        {
            // A codec-less candidate must be representable so the registry build
            // can reject it with a MissingCodec record (SPEC §C3 → OW-OP-016).
            var definition = new TypeDefinition(
                "a.B", TypeKind.Primitive, 0, Array.Empty<string>(), null, "d", null!);
            Assert.IsNull(definition.Codec);
        }

        [Test]
        public void TypeDefinitionSchemaMustBeAJsonObject()
        {
            // SPEC §B8 fragments are objects; a provider passing a scalar is a
            // programming error rejected at construction, not silently dropped.
            Assert.IsNull(new TypeDefinition(
                "a.B", TypeKind.Value, 0, Array.Empty<string>(), null, "d", s_codec).Schema);
            var fragment = JsonValue.Parse("{\"type\":\"string\"}").Value!;
            Assert.AreSame(fragment, new TypeDefinition(
                "a.B", TypeKind.Value, 0, Array.Empty<string>(), null, "d", s_codec)
            {
                Schema = fragment,
            }.Schema);
            Assert.Throws<ArgumentException>(
                () => new TypeDefinition(
                    "a.B", TypeKind.Value, 0, Array.Empty<string>(), null, "d", s_codec)
                {
                    Schema = JsonValue.Parse("\"x\"").Value!,
                });
            Assert.Throws<ArgumentException>(
                () => new TypeDefinition(
                    "a.B", TypeKind.Value, 0, Array.Empty<string>(), null, "d", s_codec)
                {
                    Schema = JsonValue.Parse("[1]").Value!,
                });
        }

        [Test]
        public void TypeKindHasTheSpecMembers()
        {
            Assert.AreEqual(
                new[] { "Primitive", "Value", "Ref", "List", "Class", "Json" },
                Enum.GetNames(typeof(TypeKind)));
        }

        // ── OpWeaveTypeProviderAttribute (SPEC §C0.1, §X.6) ─────────────────────

        [OpWeaveTypeProvider]
        private sealed class AttributedProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => Array.Empty<TypeDefinition>();
        }

        [Test]
        public void OpWeaveTypeProviderAttributeIsAClassOnlyDetectionContract()
        {
            var attribute = (OpWeaveTypeProviderAttribute)Attribute.GetCustomAttribute(
                typeof(AttributedProvider), typeof(OpWeaveTypeProviderAttribute))!;
            Assert.IsNotNull(attribute);
            StringAssert.EndsWith("TypeContractsTests.cs", attribute.SourceFile);

            var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
                typeof(OpWeaveTypeProviderAttribute), typeof(AttributeUsageAttribute))!;
            Assert.AreEqual(AttributeTargets.Class, usage.ValidOn);
            Assert.IsFalse(usage.Inherited);
        }

        // ── Providers cannot reach mutable registry internals (SPEC §C0.1) ──────

        [Test]
        public void RegistryImplementationIsNotPublic()
        {
            var result = TypeRegistryBuilder.Build(Array.Empty<TypeProviderCandidate>());
            Assert.IsFalse(result.Registry.GetType().IsPublic);
            Assert.AreEqual(0, result.Registry.All.Count);
            Assert.AreEqual(0, result.Failures.Count);
        }
    }
}
