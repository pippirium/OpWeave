#nullable enable

using System;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    internal sealed class DiagnosticTests
    {
        [Test]
        public void RecordCarriesEverySpecField()
        {
            var diagnostic = new Diagnostic(
                "OW-VAL-005",
                Severity.Error,
                "A required input is not satisfied.",
                NodeId: "n1",
                Port: "in",
                JsonPath: "$.nodes[3].parameters.name",
                Hint: "Bind a value to the input.",
                Retryable: true);

            Assert.AreEqual("OW-VAL-005", diagnostic.Code);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.AreEqual("A required input is not satisfied.", diagnostic.Message);
            Assert.AreEqual("n1", diagnostic.NodeId);
            Assert.AreEqual("in", diagnostic.Port);
            Assert.AreEqual("$.nodes[3].parameters.name", diagnostic.JsonPath);
            Assert.AreEqual("Bind a value to the input.", diagnostic.Hint);
            Assert.IsTrue(diagnostic.Retryable);
        }

        [Test]
        public void OptionalFieldsDefaultToNullAndFalse()
        {
            var diagnostic = new Diagnostic("OW-WF-001", Severity.Error, "Not valid JSON.");

            Assert.IsNull(diagnostic.NodeId);
            Assert.IsNull(diagnostic.Port);
            Assert.IsNull(diagnostic.JsonPath);
            Assert.IsNull(diagnostic.Hint);
            Assert.IsFalse(diagnostic.Retryable);
        }

        [Test]
        public void SeverityMembersAreErrorWarningInfoInSpecOrder()
        {
            Assert.AreEqual(
                new[] { Severity.Error, Severity.Warning, Severity.Info },
                (Severity[])Enum.GetValues(typeof(Severity)));
        }

        [Test]
        public void RecordEqualityIsByValue()
        {
            var first = new Diagnostic("OW-RUN-006", Severity.Error, "Operation threw.");
            var same = new Diagnostic("OW-RUN-006", Severity.Error, "Operation threw.");
            var different = first with { Retryable = true };

            Assert.AreEqual(first, same);
            Assert.AreNotEqual(first, different);
        }
    }
}
