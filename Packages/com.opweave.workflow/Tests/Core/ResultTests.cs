#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    internal sealed class ResultTests
    {
        private static readonly Diagnostic s_warning = new Diagnostic(
            "OW-CFG-003", Severity.Warning, "An unknown settings key was ignored.");

        private static readonly Diagnostic s_error = new Diagnostic(
            "OW-VAL-005", Severity.Error, "A required input is not satisfied.");

        [Test]
        public void SuccessExposesValueAndEmptyDiagnostics()
        {
            var result = Result<int>.Success(42);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(42, result.Value);
            Assert.IsNotNull(result.Diagnostics);
            Assert.AreEqual(0, result.Diagnostics.Count);
        }

        [Test]
        public void SuccessCanCarryWarningDiagnostics()
        {
            var result = Result<int>.Success(7, s_warning);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(7, result.Value);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual(s_warning, result.Diagnostics[0]);
        }

        [Test]
        public void SuccessWithEnumerableDiagnosticsDefensivelyCopies()
        {
            var diagnostics = new List<Diagnostic> { s_warning };
            var result = Result<int>.Success(1, diagnostics);
            diagnostics.Add(s_error);
            Assert.AreEqual(1, result.Diagnostics.Count);
        }

        [Test]
        public void DiagnosticsCannotBeMutatedThroughCasts()
        {
            var result = Result<int>.Failure(s_error, s_warning);
            Assert.IsNull(result.Diagnostics as Diagnostic[]);

            var list = (IList<Diagnostic>)result.Diagnostics;
            Assert.IsTrue(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = s_error);
            Assert.Throws<NotSupportedException>(() => list.Add(s_error));
            Assert.Throws<NotSupportedException>(() => list.Insert(0, s_error));
            Assert.Throws<NotSupportedException>(() => list.Remove(s_error));
            Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => list.Clear());
            Assert.AreEqual(2, result.Diagnostics.Count);
        }

        [Test]
        public void EmptyDiagnosticsCannotBeMutatedThroughCasts()
        {
            var success = Result<int>.Success(1);
            Assert.IsNull(success.Diagnostics as Diagnostic[]);
            Assert.Throws<NotSupportedException>(
                () => ((IList<Diagnostic>)success.Diagnostics).Add(s_warning));

            var defaulted = default(Result<int>);
            Assert.IsNull(defaulted.Diagnostics as Diagnostic[]);
            Assert.Throws<NotSupportedException>(
                () => ((IList<Diagnostic>)defaulted.Diagnostics).Add(s_warning));
        }

        [Test]
        public void FailureExposesDiagnosticsAndNoValue()
        {
            var result = Result<string>.Failure(s_error);
            Assert.IsFalse(result.Ok);
            Assert.IsNull(result.Value);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual(s_error, result.Diagnostics[0]);
        }

        [Test]
        public void FailureKeepsMultipleDiagnosticsInOrder()
        {
            var result = Result<int>.Failure(s_error, s_warning);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(0, result.Value);
            Assert.AreEqual(2, result.Diagnostics.Count);
            Assert.AreEqual(s_error, result.Diagnostics[0]);
            Assert.AreEqual(s_warning, result.Diagnostics[1]);
        }

        [Test]
        public void FailureRequiresAtLeastOneDiagnostic()
        {
            Assert.Throws<ArgumentException>(() => Result<int>.Failure());
            Assert.Throws<ArgumentException>(
                () => Result<int>.Failure((IEnumerable<Diagnostic>)new List<Diagnostic>()));
        }

        [Test]
        public void NullDiagnosticsAreRejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => Result<int>.Success(1, (IEnumerable<Diagnostic>)null!));
            Assert.Throws<ArgumentNullException>(
                () => Result<int>.Failure((IEnumerable<Diagnostic>)null!));
        }

        [Test]
        public void DefaultResultIsAFailedResultWithEmptyDiagnostics()
        {
            var result = default(Result<int>);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(0, result.Value);
            Assert.IsNotNull(result.Diagnostics);
            Assert.AreEqual(0, result.Diagnostics.Count);
        }

        [Test]
        public void UnitResultCarriesNoPayload()
        {
            var result = Result<Unit>.Success(Unit.Value);
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(Unit.Value, result.Value);
            Assert.AreEqual(default(Unit), result.Value);
        }
    }
}
