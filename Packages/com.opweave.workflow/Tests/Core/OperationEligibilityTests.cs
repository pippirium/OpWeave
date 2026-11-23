#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §C2 item 1 class eligibility check
    /// (T-W1.5-03): <see cref="OperationEligibility.Check"/> accepts eligible
    /// classes and <see cref="OperationEligibility.Instantiate"/> constructs
    /// them; every ineligible shape — missing
    /// <see cref="OpWeaveOperationAttribute"/>, abstract, no public
    /// parameterless constructor, no <see cref="IOperation"/>, or a throwing
    /// constructor — is reported as OW-OP-008, never as an exception.
    /// </summary>
    internal sealed class OperationEligibilityTests
    {
        // ── Eligibility fixtures: each varies one SPEC §C2 item 1 property ──────

        // Declared identities: an eligible fixture keeps the base's own
        // Descriptor identity (a deliberate same-(id,version) pair — the
        // OW-OP-004 case); the ineligible shapes carry their own declared
        // id so a Registry scan still reaches their stage-6 OW-OP-008.
        [OpWeaveOperation("test.dummy.trivial", 1)]
        private sealed class EligibleOperation
            : OperationContractFixtures.TrivialOperation
        {
        }

        private sealed class UnattributedOperation
            : OperationContractFixtures.TrivialOperation
        {
        }

        [OpWeaveOperation("test.eligibility.abstract", 1)]
        private abstract class AbstractOperation
            : OperationContractFixtures.TrivialOperation
        {
        }

        [OpWeaveOperation("test.eligibility.ctorparam", 1)]
        private sealed class ConstructorParameterOperation
            : OperationContractFixtures.TrivialOperation
        {
            public ConstructorParameterOperation(int ignored)
            {
            }
        }

        [OpWeaveOperation("test.eligibility.nonpublicctor", 1)]
        private sealed class NonPublicConstructorOperation
            : OperationContractFixtures.TrivialOperation
        {
            internal NonPublicConstructorOperation()
            {
            }
        }

        [OpWeaveOperation("test.eligibility.throwing", 1)]
        private sealed class ThrowingOperation
            : OperationContractFixtures.TrivialOperation
        {
            public ThrowingOperation()
            {
                throw new InvalidOperationException("Constructor failed on purpose.");
            }
        }

        private const string SensitiveMarker = "OW-TEST-SECRET-c4f2a91b";

        [OpWeaveOperation("test.eligibility.sensitivethrow", 1)]
        private sealed class SensitiveThrowingOperation
            : OperationContractFixtures.TrivialOperation
        {
            public SensitiveThrowingOperation()
            {
                throw new InvalidOperationException(
                    "connection failed with credential " + SensitiveMarker);
            }
        }

        [OpWeaveOperation("test.eligibility.notop", 1)]
        private sealed class NotAnOperation
        {
        }

        private abstract class FullyIneligible
        {
            private FullyIneligible(int ignored)
            {
            }
        }

        private static void AssertOwOp008(Diagnostic diagnostic)
        {
            Assert.AreEqual(ErrorCodes.OW_OP_008, diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
        }

        // ── Declared identity (SPEC §C2 item 2, T-W2.2-08) ──────────────────────

        [Test]
        public void DeclaredIdentityOfReadsTheAttributeWithoutConstructing()
        {
            var declared = OperationEligibility.DeclaredIdentityOf(typeof(EligibleOperation));
            Assert.IsNotNull(declared);
            Assert.AreEqual("test.dummy.trivial", declared!.Id);
            Assert.AreEqual(1, declared.Version);
            StringAssert.EndsWith("OperationEligibilityTests.cs", declared.SourceFile);

            Assert.IsNull(
                OperationEligibility.DeclaredIdentityOf(typeof(UnattributedOperation)));
            Assert.Throws<ArgumentNullException>(
                () => OperationEligibility.DeclaredIdentityOf(null!));
        }

        // ── Eligible classes ────────────────────────────────────────────────────

        [Test]
        public void EligibleClassesAreAcceptedAndInstantiated()
        {
            var types = new[]
            {
                typeof(OperationContractFixtures.ImmediateOperation),
                typeof(OperationContractFixtures.WaitableOperation),
                typeof(OperationContractFixtures.ReconcilableOperation),
                typeof(EligibleOperation),
            };

            foreach (var type in types)
            {
                Assert.AreEqual(
                    0, OperationEligibility.Check(type).Count, type.FullName);
                var instantiated = OperationEligibility.Instantiate(type);
                Assert.IsTrue(instantiated.Ok, type.FullName);
                Assert.IsInstanceOf<IOperation>(instantiated.Value);
                Assert.AreSame(type, instantiated.Value!.GetType());
            }
        }

        // ── Ineligible classes: OW-OP-008 each ──────────────────────────────────

        [Test]
        public void MissingAttributeYieldsOwOp008()
        {
            var diagnostics = OperationEligibility.Check(typeof(UnattributedOperation));
            Assert.AreEqual(1, diagnostics.Count);
            AssertOwOp008(diagnostics[0]);
            StringAssert.Contains("[OpWeaveOperation]", diagnostics[0].Message);
        }

        [Test]
        public void AbstractClassYieldsOwOp008()
        {
            // An abstract class also lacks a *public* parameterless
            // constructor (the implicit one is protected), so both violated
            // conditions are reported — each as OW-OP-008.
            var diagnostics = OperationEligibility.Check(typeof(AbstractOperation));
            Assert.AreEqual(2, diagnostics.Count);
            AssertOwOp008(diagnostics[0]);
            AssertOwOp008(diagnostics[1]);
            StringAssert.Contains("abstract", diagnostics[0].Message);
            StringAssert.Contains("parameterless", diagnostics[1].Message);
        }

        [Test]
        public void MissingParameterlessConstructorYieldsOwOp008()
        {
            var diagnostics = OperationEligibility.Check(
                typeof(ConstructorParameterOperation));
            Assert.AreEqual(1, diagnostics.Count);
            AssertOwOp008(diagnostics[0]);
            StringAssert.Contains("parameterless", diagnostics[0].Message);

            var nonPublic = OperationEligibility.Check(
                typeof(NonPublicConstructorOperation));
            Assert.AreEqual(1, nonPublic.Count);
            AssertOwOp008(nonPublic[0]);
        }

        [Test]
        public void NonOperationClassYieldsOwOp008()
        {
            var diagnostics = OperationEligibility.Check(typeof(NotAnOperation));
            Assert.AreEqual(1, diagnostics.Count);
            AssertOwOp008(diagnostics[0]);
            StringAssert.Contains("IOperation", diagnostics[0].Message);
        }

        [Test]
        public void ConstructorExceptionYieldsOwOp008NotAnException()
        {
            // The class passes the metadata check, then its constructor throws:
            // the failure crosses the boundary as OW-OP-008 (SPEC §C2 item 1,
            // §C3 step 6), not as an exception.
            Assert.AreEqual(
                0, OperationEligibility.Check(typeof(ThrowingOperation)).Count);

            var result = OperationEligibility.Instantiate(typeof(ThrowingOperation));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Diagnostics.Count);
            AssertOwOp008(result.Diagnostics[0]);
            StringAssert.Contains(
                "could not be instantiated", result.Diagnostics[0].Message);
            StringAssert.Contains(
                nameof(InvalidOperationException), result.Diagnostics[0].Message);
            StringAssert.DoesNotContain(
                "Constructor failed on purpose.", result.Diagnostics[0].Message);
        }

        [Test]
        public void ConstructorExceptionNeverLeaksSensitiveMessageContent()
        {
            // SPEC §C3: a registration failure returns a safe reason. The
            // thrown exception's message may carry credentials or source
            // text, so no diagnostic field may contain it.
            var result = OperationEligibility.Instantiate(
                typeof(SensitiveThrowingOperation));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            AssertOwOp008(diagnostic);
            StringAssert.DoesNotContain(SensitiveMarker, diagnostic.Code);
            StringAssert.DoesNotContain(SensitiveMarker, diagnostic.Message);
            StringAssert.DoesNotContain(
                SensitiveMarker, diagnostic.NodeId ?? string.Empty);
            StringAssert.DoesNotContain(
                SensitiveMarker, diagnostic.Port ?? string.Empty);
            StringAssert.DoesNotContain(
                SensitiveMarker, diagnostic.JsonPath ?? string.Empty);
            StringAssert.DoesNotContain(
                SensitiveMarker, diagnostic.Hint ?? string.Empty);
        }

        [Test]
        public void EveryViolatedConditionIsReportedInFixedOrder()
        {
            var diagnostics = OperationEligibility.Check(typeof(FullyIneligible));
            Assert.AreEqual(4, diagnostics.Count);
            foreach (var diagnostic in diagnostics)
            {
                AssertOwOp008(diagnostic);
            }
            StringAssert.Contains("[OpWeaveOperation]", diagnostics[0].Message);
            StringAssert.Contains("abstract", diagnostics[1].Message);
            StringAssert.Contains("parameterless", diagnostics[2].Message);
            StringAssert.Contains("IOperation", diagnostics[3].Message);
        }

        [Test]
        public void InstantiateNeverConstructsAnIneligibleClass()
        {
            // An ineligible class is rejected before its constructor runs, so a
            // throwing constructor cannot fire (SPEC §C3 step 2: metadata first).
            var result = OperationEligibility.Instantiate(
                typeof(ConstructorParameterOperation));
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(1, result.Diagnostics.Count);
            AssertOwOp008(result.Diagnostics[0]);

            result = OperationEligibility.Instantiate(typeof(UnattributedOperation));
            Assert.IsFalse(result.Ok);
            AssertOwOp008(result.Diagnostics[0]);
        }

        [Test]
        public void NullTypeIsAnArgumentErrorNotADiagnostic()
        {
            Assert.Throws<ArgumentNullException>(
                () => OperationEligibility.Check(null!));
            Assert.Throws<ArgumentNullException>(
                () => OperationEligibility.Instantiate(null!));
        }
    }
}
