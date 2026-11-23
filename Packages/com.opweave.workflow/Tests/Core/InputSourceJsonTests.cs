#nullable enable

using System;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SG-12 tagged canonical encoding of
    /// <see cref="InputSource"/> (SPEC §A0.1, §D7, §0.6 SG-12, §0.4): round-trip
    /// of every kind, frozen golden bytes and hashes, Missing-vs-explicit-null
    /// distinctness, and rejection of unknown kinds, extra members and missing
    /// or mistyped required members. The same sources run under dotnet and
    /// Unity Mono (local:T-W1.3-05:missing-null-encoding).
    /// </summary>
    internal sealed class InputSourceJsonTests
    {
        private static JsonValue MustParse(string json)
        {
            var parsed = JsonValue.Parse(json);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {json}");
            return parsed.Value!;
        }

        // SPEC §0.6 SG-12: the five tags are the approved persisted format and
        // their canonical bytes are frozen here — {"kind":"missing"} and the
        // literal-null constant differ byte-for-byte (SPEC §A0.1).
        [Test]
        public void WriteProducesTheGoldenCanonicalBytes()
        {
            Assert.AreEqual("{\"kind\":\"missing\"}",
                InputSourceJson.Write(new Missing()).CanonicalText);
            Assert.AreEqual("{\"kind\":\"constant\",\"value\":null}",
                InputSourceJson.Write(new Constant(MustParse("null"))).CanonicalText);
            Assert.AreEqual("{\"kind\":\"default\",\"value\":true}",
                InputSourceJson.Write(new Default(MustParse("true"))).CanonicalText);
            Assert.AreEqual("{\"inputId\":\"player-name\",\"kind\":\"from_input\"}",
                InputSourceJson.Write(new FromInput("player-name")).CanonicalText);
            Assert.AreEqual("{\"kind\":\"from_step\",\"port\":\"result\",\"stepIndex\":2}",
                InputSourceJson.Write(new FromStep(2, "result")).CanonicalText);
        }

        // Lowercase-hex SHA-256 of each tag's canonical UTF-8 bytes, frozen
        // against the SG-12 format so a byte change breaks the fixture.
        [Test]
        public void WriteProducesTheGoldenDigests()
        {
            Assert.AreEqual(
                "e85ecccce6c9f4c4c803b816aeaf45e3c7a2fa1b0fe84f89f59f3c41253407e6",
                InputSourceJson.Write(new Missing()).Sha256);
            Assert.AreEqual(
                "15373d67b8024e3a360db0fb05bf1bc6a6e36a14a97238c4c913ba90a48ba2bc",
                InputSourceJson.Write(new Constant(MustParse("null"))).Sha256);
            Assert.AreEqual(
                "888f9c0ee419963aa97d772ab92f26d91aff3c8de68790d63a14e487df57989a",
                InputSourceJson.Write(new Default(MustParse("true"))).Sha256);
            Assert.AreEqual(
                "a3089775f14a34770cda2e2018fc79e13bd43c8906e61c97a67e5d71e3bfbe22",
                InputSourceJson.Write(new FromInput("player-name")).Sha256);
            Assert.AreEqual(
                "ebb1073806269bebcce71bab1ef7b49d5f909b6e4ca20b6f739afd9c0c7d0b5c",
                InputSourceJson.Write(new FromStep(2, "result")).Sha256);
            Assert.AreEqual(
                "d0630ef3a91f4f9782f5ca3e7b67294b1a97fa572d2801b60f58766351dbbb2b",
                InputSourceJson.Write(new Constant(MustParse("{\"a\":1,\"b\":[1,2]}"))).Sha256);
        }

        // SPEC §A0.1: Missing is its own tag, never JSON null — different bytes,
        // different hashes and different decoded kinds from a literal-null
        // constant.
        [Test]
        public void MissingAndLiteralNullConstantAreDistinct()
        {
            var missing = InputSourceJson.Write(new Missing());
            var nullConstant = InputSourceJson.Write(new Constant(MustParse("null")));

            Assert.AreNotEqual("null", missing.CanonicalText);
            StringAssert.Contains("\"kind\":\"missing\"", missing.CanonicalText);
            StringAssert.Contains("\"value\":null", nullConstant.CanonicalText);
            Assert.AreNotEqual(missing.CanonicalText, nullConstant.CanonicalText);
            Assert.AreNotEqual(missing.Sha256, nullConstant.Sha256);

            var missingDecoded = InputSourceJson.Read(missing);
            var nullDecoded = InputSourceJson.Read(nullConstant);
            Assert.IsTrue(missingDecoded.Ok && nullDecoded.Ok);
            Assert.IsInstanceOf<Missing>(missingDecoded.Value);
            var constant = nullDecoded.Value as Constant;
            Assert.IsNotNull(constant);
            Assert.AreEqual("null", constant!.Value.CanonicalText,
                "an explicit-null constant keeps a real present value");
            Assert.AreNotEqual(missingDecoded.Value, nullDecoded.Value);
            Assert.AreNotEqual(new Missing(), nullDecoded.Value);
        }

        // Acceptance criterion: each kind round-trips through canonical JSON.
        [Test]
        public void EveryKindRoundTrips()
        {
            InputSource[] sources =
            {
                new Missing(),
                new Constant(MustParse("null")),
                new Constant(MustParse("{\"a\":1,\"b\":[1,2],\"c\":\"x\"}")),
                new Default(MustParse("false")),
                new Default(MustParse("[]")),
                new FromStep(0, "next"),
                new FromStep(int.MaxValue, "result"),
                new FromInput("player-name"),
                new FromInput(string.Empty),
            };
            foreach (var source in sources)
            {
                var tag = InputSourceJson.Write(source);
                var decoded = InputSourceJson.Read(tag);
                Assert.IsTrue(decoded.Ok, $"decode failed for {tag.CanonicalText}");
                Assert.AreEqual(source, decoded.Value, $"round-trip changed {tag.CanonicalText}");

                // Re-reading the persisted text (any member order) yields the
                // same source: parse the canonical bytes back independently.
                var reparsed = InputSourceJson.Read(MustParse(tag.CanonicalText));
                Assert.IsTrue(reparsed.Ok);
                Assert.AreEqual(source, reparsed.Value);
            }
        }

        [Test]
        public void ReadAcceptsNonCanonicalMemberOrder()
        {
            // Canonicalization normalizes member order; decode sees the JObject.
            var parsed = InputSourceJson.Read(
                MustParse("{\"port\":\"result\",\"kind\":\"from_step\",\"stepIndex\":2}"));
            Assert.IsTrue(parsed.Ok);
            Assert.AreEqual(new FromStep(2, "result"), parsed.Value);
        }

        // Acceptance criterion: unknown kind or malformed tag fails decoding.
        // Every failure is a failed Result with internal (empty-code) diagnostics
        // for the owning layer to map once — never an exception.
        [TestCase("{}", TestName = "no kind member")]
        [TestCase("{\"kind\":null}", TestName = "kind is JSON null")]
        [TestCase("{\"kind\":1}", TestName = "kind is a number")]
        [TestCase("{\"kind\":{\"x\":1}}", TestName = "kind is an object")]
        [TestCase("{\"kind\":\"bogus\"}", TestName = "unknown kind")]
        [TestCase("{\"kind\":\"MISSING\"}", TestName = "kind is case sensitive")]
        [TestCase("{\"kind\":\"fromStep\",\"stepIndex\":0,\"port\":\"x\"}", TestName = "camelCase kind rejected")]
        [TestCase("{\"kind\":\"missing\",\"value\":1}", TestName = "missing with extra member")]
        [TestCase("{\"kind\":\"missing\",\"extra\":null}", TestName = "missing with null extra member")]
        [TestCase("{\"kind\":\"constant\"}", TestName = "constant without value")]
        [TestCase("{\"kind\":\"constant\",\"value\":null,\"x\":1}", TestName = "constant with extra member")]
        [TestCase("{\"kind\":\"default\"}", TestName = "default without value")]
        [TestCase("{\"kind\":\"default\",\"value\":0,\"kind2\":\"x\"}", TestName = "default with extra member")]
        [TestCase("{\"kind\":\"from_step\"}", TestName = "from_step without members")]
        [TestCase("{\"kind\":\"from_step\",\"stepIndex\":0}", TestName = "from_step without port")]
        [TestCase("{\"kind\":\"from_step\",\"port\":\"x\"}", TestName = "from_step without stepIndex")]
        [TestCase("{\"kind\":\"from_step\",\"stepIndex\":-1,\"port\":\"x\"}", TestName = "from_step negative stepIndex")]
        [TestCase("{\"kind\":\"from_step\",\"stepIndex\":1.5,\"port\":\"x\"}", TestName = "from_step fractional stepIndex")]
        [TestCase("{\"kind\":\"from_step\",\"stepIndex\":2e0,\"port\":\"x\"}", TestName = "from_step double stepIndex")]
        [TestCase("{\"kind\":\"from_step\",\"stepIndex\":2147483648,\"port\":\"x\"}", TestName = "from_step stepIndex above Int32")]
        [TestCase("{\"kind\":\"from_step\",\"stepIndex\":\"0\",\"port\":\"x\"}", TestName = "from_step string stepIndex")]
        [TestCase("{\"kind\":\"from_step\",\"stepIndex\":0,\"port\":null}", TestName = "from_step null port")]
        [TestCase("{\"kind\":\"from_step\",\"stepIndex\":0,\"port\":7}", TestName = "from_step non-string port")]
        [TestCase("{\"kind\":\"from_step\",\"stepIndex\":0,\"port\":\"x\",\"extra\":true}", TestName = "from_step with extra member")]
        [TestCase("{\"kind\":\"from_input\"}", TestName = "from_input without inputId")]
        [TestCase("{\"kind\":\"from_input\",\"inputId\":null}", TestName = "from_input null inputId")]
        [TestCase("{\"kind\":\"from_input\",\"inputId\":7}", TestName = "from_input non-string inputId")]
        [TestCase("{\"kind\":\"from_input\",\"inputId\":\"a\",\"extra\":2}", TestName = "from_input with extra member")]
        public void UnknownKindOrMalformedTagFailsDecoding(string tagText)
        {
            Result<InputSource> decoded = default;
            Assert.DoesNotThrow(() => decoded = InputSourceJson.Read(MustParse(tagText)));

            Assert.IsFalse(decoded.Ok, $"tag was accepted: {tagText}");
            Assert.IsNull(decoded.Value);
            Assert.AreEqual(1, decoded.Diagnostics.Count);
            var diagnostic = decoded.Diagnostics[0];
            Assert.AreEqual(string.Empty, diagnostic.Code,
                "internal diagnostic carries no OW-* code; the owning layer maps once");
            Assert.IsFalse(ErrorCodes.IsPublished(diagnostic.Code));
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.IsFalse(string.IsNullOrEmpty(diagnostic.Message));
        }

        // The tag root itself must be a JSON object; scalars and arrays fail.
        [TestCase("null", TestName = "null root")]
        [TestCase("5", TestName = "integer root")]
        [TestCase("\"missing\"", TestName = "string root")]
        [TestCase("[{\"kind\":\"missing\"}]", TestName = "array root")]
        [TestCase("true", TestName = "boolean root")]
        public void NonObjectTagFailsDecoding(string tagText)
        {
            var decoded = InputSourceJson.Read(MustParse(tagText));
            Assert.IsFalse(decoded.Ok);
            Assert.AreEqual(1, decoded.Diagnostics.Count);
            Assert.AreEqual(string.Empty, decoded.Diagnostics[0].Code);
        }

        // Missing is never stored or serialized as null: the decoded Missing is
        // a real InputSource instance, and its value-level view is an absent
        // InputValue — not a present JsonValue of null.
        [Test]
        public void MissingNeverAppearsAsNullAnywhere()
        {
            var decoded = InputSourceJson.Read(InputSourceJson.Write(new Missing()));
            Assert.IsTrue(decoded.Ok);
            Assert.IsInstanceOf<Missing>(decoded.Value);

            // The value-level view: Missing -> absent; literal-null constant ->
            // present JsonValue of null (SPEC §A0.1).
            var absent = InputValue.Missing;
            Assert.IsFalse(absent.IsPresent);
            Assert.IsNull(absent.Value);

            var nullConstant = (Constant)InputSourceJson.Read(
                MustParse("{\"kind\":\"constant\",\"value\":null}")).Value!;
            var present = InputValue.Present(nullConstant.Value);
            Assert.IsTrue(present.IsPresent);
            Assert.AreEqual("null", present.Value!.CanonicalText);
            Assert.AreNotEqual(absent, present);
        }

        // Boundary guards: values outside the SG-12 persisted contract are
        // programming errors on the write side, not silently emitted bytes the
        // reader would reject.
        [Test]
        public void WriteRejectsSourcesOutsideThePersistedContract()
        {
            Assert.Throws<ArgumentNullException>(() => InputSourceJson.Write(null!));
            Assert.Throws<ArgumentException>(() => InputSourceJson.Write(new FromStep(-1, "p")));
            Assert.Throws<ArgumentException>(() => InputSourceJson.Write(new FromStep(0, null!)));
            Assert.Throws<ArgumentException>(() => InputSourceJson.Write(new FromInput(null!)));
            Assert.Throws<ArgumentException>(() => InputSourceJson.Write(new Constant(null!)));
            Assert.Throws<ArgumentException>(() => InputSourceJson.Write(new Default(null!)));
            Assert.Throws<ArgumentException>(() => InputSourceJson.Write(new AlienSource()));
            Assert.Throws<ArgumentNullException>(() => InputSourceJson.Read(null!));
        }

        // An InputSource subclass outside the five SG-12 kinds can be declared;
        // Write must reject it rather than emit an unapproved tag.
        private sealed record AlienSource : InputSource;
    }
}
