#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §C3 rule-3 descriptor checks of
    /// <see cref="OperationDescriptorValidator"/> (T-W1.5-02, grammar check
    /// T-W1.5-05): valid SPEC §C2 and §M.4 examples pass, and each listed
    /// violation — an id outside the SPEC §A4 grammar, duplicate port
    /// names, unresolvable types, unknown side effects (SPEC §C4 test 1),
    /// summary over 120 characters, ControlOutputs other than
    /// <c>["next"]</c> — yields OW-OP-003, while the reserved blueprint kind
    /// yields OW-OP-010 (SPEC §N.5.5).
    /// </summary>
    internal sealed class OperationDescriptorValidationTests
    {
        private static readonly ITypeRegistry s_types = DescriptorTestFixtures.CreateRegistry();

        private static IReadOnlyList<Diagnostic> Validate(OperationDescriptor descriptor) =>
            OperationDescriptorValidator.Validate(descriptor, s_types);

        private static void AssertViolation(
            IReadOnlyList<Diagnostic> diagnostics, string code)
        {
            Assert.AreEqual(1, diagnostics.Count, "expected exactly one diagnostic");
            var diagnostic = diagnostics[0];
            Assert.AreEqual(code, diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Retryable);
        }

        private static OperationDescriptor ValidWith(
            Func<OperationDescriptor, OperationDescriptor> change) =>
            change(DescriptorTestFixtures.BuildC2Example());

        private static OperationDescriptor Minimal() =>
            Op.Define("acme.valid", 1)
                .Summary("A minimal valid descriptor.")
                .Input("value", "core.String", required: true)
                .Output("result", "core.String")
                .Build();

        // ── Normal behavior: the SPEC examples validate cleanly ───────────────

        [Test]
        public void C2ExampleValidates()
        {
            Assert.AreEqual(0, Validate(DescriptorTestFixtures.BuildC2Example()).Count);
        }

        [Test]
        public void M4ExampleValidates()
        {
            Assert.AreEqual(0, Validate(DescriptorTestFixtures.BuildM4Example()).Count);
        }

        [Test]
        public void MinimalDescriptorValidates()
        {
            Assert.AreEqual(0, Validate(Minimal()).Count);
        }

        // ── Operation id grammar → OW-OP-003 (SPEC §A4, §C3 rule 3) ──────────
        // local:T-W1.5-05:operation-id-grammar — each rejected shape of the
        // §A4 Operation id grammar ^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$.

        [TestCase("acme.bad-id.op")]      // '-' is not in the grammar
        [TestCase("1acme.op")]            // the first segment must not start with a digit
        [TestCase("acme.Bad.op")]         // uppercase letters are not allowed
        [TestCase("acme")]                // a single segment never matches
        [TestCase("acme..op")]            // an empty segment never matches
        [TestCase("acme.op.")]            // a trailing dot leaves an empty segment
        public void InvalidOperationIdShapeIsRejected(string id)
        {
            var descriptor = Minimal() with { Id = id };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        [TestCase("acme.valid")]
        [TestCase("a.b")]
        [TestCase("test.contract.ensure_marker")]
        [TestCase("x0.y_z9.w_0")]
        public void ValidOperationIdShapeIsAccepted(string id)
        {
            var descriptor = Minimal() with { Id = id };
            Assert.AreEqual(0, Validate(descriptor).Count, "id must validate: " + id);
        }

        [Test]
        public void InvalidIdIsRejectedAtRegistrationOthersStillRegister()
        {
            // SPEC §C3 step 6: the grammar-invalid Descriptor id fails its
            // own candidate with OW-OP-003; unrelated Operations register.
            var snapshot = RegistrySnapshotBuilder.Build(
                new[]
                {
                    new OperationCandidate(
                        Minimal() with { Id = "acme.bad-id" },
                        OperationOrigin.Custom, "Acme.Editor", null,
                        new string('a', 64), true),
                    new OperationCandidate(
                        Minimal() with { Id = "acme.still.ok" },
                        OperationOrigin.Custom, "Acme.Editor", null,
                        new string('b', 64), true),
                },
                s_types);

            Assert.AreEqual(1, snapshot.Operations.Count);
            Assert.AreEqual("acme.still.ok", snapshot.Operations[0].Descriptor.Id);
            Assert.AreEqual(1, snapshot.Failures.Count);
            Assert.AreEqual(RegistryChangeKinds.Failed, snapshot.Failures[0].Kind);
            Assert.AreEqual(
                ErrorCodes.OW_OP_003, snapshot.Failures[0].Diagnostics[0].Code);
        }

        // ── Operation id 128-character bound (SPEC §A4, 2026-11-16) ──────────
        // local:T-W1.3-09:operation-id-length-bound — registration shares the
        // IdGrammar bound: exactly 128 characters registers, 129 fails
        // OW-OP-003 with the existing code and JsonPath-free Diagnostic.

        [Test]
        public void OperationIdAt128CharactersIsAccepted()
        {
            var descriptor = Minimal() with { Id = "a." + new string('b', 126) };
            Assert.AreEqual(0, Validate(descriptor).Count);
        }

        [Test]
        public void OperationIdAt129CharactersIsRejected()
        {
            var descriptor = Minimal() with { Id = "a." + new string('b', 127) };
            var diagnostics = Validate(descriptor);
            AssertViolation(diagnostics, ErrorCodes.OW_OP_003);
            StringAssert.Contains("128", diagnostics[0].Hint);
            // The over-long id is published through the existing PublishedId
            // handling — masked, never interpolated verbatim.
            StringAssert.Contains("<malformed id len=129", diagnostics[0].Message);
        }

        // A 129-character id that also violates the regex is one OW-OP-003,
        // not two reports for the two reasons.
        [Test]
        public void OverLongMalformedOperationIdReportsOnce()
        {
            var descriptor = Minimal() with { Id = "A." + new string('b', 127) };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        // SPEC §A4 test (ID): the bound at registration — 128 registers,
        // 129 fails OW-OP-003 while unrelated Operations still register.
        [Test]
        public void OperationIdLengthBoundAtRegistration()
        {
            var id128 = "a." + new string('b', 126);
            var snapshot = RegistrySnapshotBuilder.Build(
                new[]
                {
                    new OperationCandidate(
                        Minimal() with { Id = id128 },
                        OperationOrigin.Custom, "Acme.Editor", null,
                        new string('a', 64), true),
                    new OperationCandidate(
                        Minimal() with { Id = "a." + new string('b', 127) },
                        OperationOrigin.Custom, "Acme.Editor", null,
                        new string('b', 64), true),
                    new OperationCandidate(
                        Minimal() with { Id = "acme.still.ok" },
                        OperationOrigin.Custom, "Acme.Editor", null,
                        new string('c', 64), true),
                },
                s_types);

            Assert.AreEqual(2, snapshot.Operations.Count);
            Assert.AreEqual(id128, snapshot.Operations[0].Descriptor.Id);
            Assert.AreEqual("acme.still.ok", snapshot.Operations[1].Descriptor.Id);
            Assert.AreEqual(1, snapshot.Failures.Count);
            Assert.AreEqual(RegistryChangeKinds.Failed, snapshot.Failures[0].Kind);
            Assert.AreEqual(
                ErrorCodes.OW_OP_003, snapshot.Failures[0].Diagnostics[0].Code);
            StringAssert.StartsWith("<malformed id len=129", snapshot.Failures[0].OperationId);
        }

        // ── Duplicate port names → OW-OP-003 ──────────────────────────────────

        [Test]
        public void DuplicateInputPortName()
        {
            var ports = new[]
            {
                new PortDescriptor("a", "core.String", true, null, string.Empty, null),
                new PortDescriptor("a", "core.Int", false, null, string.Empty, null),
            };
            var descriptor = Minimal() with { Inputs = ports };
            var diagnostics = Validate(descriptor);
            AssertViolation(diagnostics, ErrorCodes.OW_OP_003);
            Assert.AreEqual("a", diagnostics[0].Port);
        }

        [Test]
        public void DuplicateOutputPortName()
        {
            var ports = new[]
            {
                new PortDescriptor("r", "core.String", false, null, string.Empty, null),
                new PortDescriptor("r", "core.String", false, null, string.Empty, null),
            };
            var descriptor = Minimal() with { Outputs = ports };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        // ── Unresolvable types → OW-OP-003 malformed / OW-OP-014 unregistered ──
        // (SPEC §C3 step 6: "未知の入出力型はOW-OP-014", T-W2.12-03)

        [Test]
        public void MalformedPortType()
        {
            var ports = new[]
            {
                new PortDescriptor("a", "badtype", true, null, string.Empty, null),
            };
            var descriptor = Minimal() with { Inputs = ports };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        [Test]
        public void UnregisteredPortType()
        {
            var ports = new[]
            {
                new PortDescriptor("a", "acme.Missing", true, null, string.Empty, null),
            };
            var descriptor = Minimal() with { Inputs = ports };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_014);
        }

        [Test]
        public void UnregisteredOutputType()
        {
            var ports = new[]
            {
                new PortDescriptor("r", "acme.Missing", false, null, string.Empty, null),
            };
            var descriptor = Minimal() with { Outputs = ports };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_014);
        }

        // ── Unknown side effects → OW-OP-003 (SPEC §C4 test 1) ────────────────

        [Test]
        public void UnknownSideEffect()
        {
            var descriptor = Minimal() with
            {
                SideEffects = new[] { "scene.modify", "warp.drive" },
            };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        [Test]
        public void EveryKnownEffectIsAccepted()
        {
            var descriptor = Minimal() with
            {
                SideEffects = EffectNames.All,
            };
            Assert.AreEqual(0, Validate(descriptor).Count);
        }

        // ── Summary length → OW-OP-003 ────────────────────────────────────────

        [Test]
        public void SummaryAtLimitPasses()
        {
            var descriptor = Minimal() with { Summary = new string('x', 120) };
            Assert.AreEqual(0, Validate(descriptor).Count);
        }

        [Test]
        public void SummaryOverLimit()
        {
            var descriptor = Minimal() with { Summary = new string('x', 121) };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        // ── ControlOutputs other than ["next"] → OW-OP-003 ────────────────────

        [Test]
        public void EmptyControlOutputs()
        {
            var descriptor = Minimal() with { ControlOutputs = Array.Empty<string>() };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        [Test]
        public void ExtraControlOutput()
        {
            var descriptor = Minimal() with { ControlOutputs = new[] { "next", "branch" } };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        [Test]
        public void RenamedControlOutput()
        {
            var descriptor = Minimal() with { ControlOutputs = new[] { "other" } };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        // ── Reserved OperationKind (SPEC §N.5.5, §X.10.4) ────────────────────

        [Test]
        public void BlueprintKindYieldsOp010()
        {
            var descriptor = Minimal() with { Kind = OperationKind.Blueprint };
            var diagnostics = Validate(descriptor);
            AssertViolation(diagnostics, ErrorCodes.OW_OP_010);
        }

        [Test]
        public void UnrecognizedKindYieldsOp003()
        {
            var descriptor = Minimal() with { Kind = (OperationKind)99 };
            AssertViolation(Validate(descriptor), ErrorCodes.OW_OP_003);
        }

        // ── Multiple violations are all reported ──────────────────────────────

        [Test]
        public void MultipleViolationsAllReported()
        {
            var descriptor = Minimal() with
            {
                Summary = new string('x', 200),
                SideEffects = new[] { "bogus.effect" },
                ControlOutputs = new[] { "a", "b" },
            };
            var diagnostics = Validate(descriptor);
            Assert.AreEqual(3, diagnostics.Count);
            foreach (var diagnostic in diagnostics)
            {
                Assert.AreEqual(ErrorCodes.OW_OP_003, diagnostic.Code);
            }
        }

        [Test]
        public void NullArgumentsThrow()
        {
            Assert.Throws<ArgumentNullException>(
                () => OperationDescriptorValidator.Validate(null!, s_types));
            Assert.Throws<ArgumentNullException>(
                () => OperationDescriptorValidator.Validate(Minimal(), null!));
        }
    }
}
