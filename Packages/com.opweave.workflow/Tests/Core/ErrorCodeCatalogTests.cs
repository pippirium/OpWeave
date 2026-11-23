#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    internal sealed class ErrorCodeCatalogTests
    {
        // Independently transcribed SPEC §J table (table order) followed by §J.1.
        // Conditional cells take the documented default: OW-WF-006 Warning,
        // OW-STORE-002 Error, OW-RUN-004 retryable=false, OW-VAL-013 retryable=false.
        private static readonly (string Code, Severity Severity, bool Retryable)[] s_specTable =
        {
            ("OW-WF-001", Severity.Error, false),
            ("OW-WF-002", Severity.Error, false),
            ("OW-WF-003", Severity.Error, false),
            ("OW-WF-004", Severity.Error, false),
            ("OW-WF-005", Severity.Error, false),
            ("OW-WF-006", Severity.Warning, false),
            ("OW-WF-007", Severity.Error, false),
            ("OW-WF-008", Severity.Error, false),
            ("OW-TYPE-001", Severity.Error, false),
            ("OW-TYPE-002", Severity.Error, false),
            ("OW-TYPE-003", Severity.Error, false),
            ("OW-TYPE-005", Severity.Error, false),
            ("OW-OP-001", Severity.Error, false),
            ("OW-OP-002", Severity.Error, false),
            ("OW-OP-003", Severity.Error, false),
            ("OW-OP-004", Severity.Error, false),
            ("OW-OP-005", Severity.Error, false),
            ("OW-OP-006", Severity.Error, false),
            ("OW-OP-007", Severity.Error, false),
            ("OW-OP-008", Severity.Error, false),
            ("OW-VAL-001", Severity.Error, false),
            ("OW-VAL-002", Severity.Error, false),
            ("OW-VAL-003", Severity.Error, false),
            ("OW-VAL-004", Severity.Error, false),
            ("OW-VAL-005", Severity.Error, false),
            ("OW-VAL-006", Severity.Error, false),
            ("OW-VAL-007", Severity.Error, false),
            ("OW-VAL-008", Severity.Error, false),
            ("OW-VAL-010", Severity.Error, false),
            ("OW-VAL-011", Severity.Error, false),
            ("OW-VAL-012", Severity.Error, false),
            ("OW-VAL-013", Severity.Warning, false),
            ("OW-VAL-014", Severity.Warning, false),
            ("OW-VAL-015", Severity.Warning, false),
            ("OW-PERM-001", Severity.Error, false),
            ("OW-PERM-002", Severity.Error, false),
            ("OW-PERM-003", Severity.Error, false),
            ("OW-PERM-004", Severity.Error, false),
            ("OW-RUN-001", Severity.Error, true),
            ("OW-RUN-002", Severity.Error, true),
            ("OW-RUN-003", Severity.Error, false),
            ("OW-RUN-004", Severity.Error, false),
            ("OW-RUN-005", Severity.Info, false),
            ("OW-RUN-006", Severity.Error, false),
            ("OW-RUN-007", Severity.Error, false),
            ("OW-RUN-008", Severity.Error, false),
            ("OW-RUN-009", Severity.Error, false),
            ("OW-RUN-010", Severity.Error, false),
            ("OW-RUN-011", Severity.Error, false),
            ("OW-RUN-012", Severity.Error, false),
            ("OW-STORE-001", Severity.Error, false),
            ("OW-STORE-002", Severity.Error, false),
            ("OW-STORE-003", Severity.Error, false),
            ("OW-UNITY-001", Severity.Error, false),
            ("OW-UNITY-002", Severity.Error, false),
            ("OW-UNITY-003", Severity.Error, false),
            ("OW-UNITY-004", Severity.Error, false),
            ("OW-UNITY-005", Severity.Error, false),
            ("OW-UNITY-006", Severity.Error, false),
            ("OW-UNITY-007", Severity.Error, false),
            ("OW-UNITY-008", Severity.Error, true),
            ("OW-UNITY-009", Severity.Error, true),
            ("OW-UNITY-010", Severity.Error, false),
            ("OW-UNITY-011", Severity.Error, false),
            ("OW-UNITY-012", Severity.Error, false),
            ("OW-API-001", Severity.Error, false),
            ("OW-API-002", Severity.Error, false),
            ("OW-API-003", Severity.Error, true),
            ("OW-API-004", Severity.Error, false),
            ("OW-OP-010", Severity.Error, false),
            ("OW-OP-011", Severity.Error, false),
            ("OW-OP-012", Severity.Error, false),
            ("OW-OP-013", Severity.Error, false),
            ("OW-OP-014", Severity.Error, false),
            ("OW-OP-015", Severity.Error, false),
            ("OW-OP-016", Severity.Error, false),
            ("OW-OP-017", Severity.Error, false),
            ("OW-OP-018", Severity.Error, false),
            ("OW-OP-019", Severity.Error, false),
            ("OW-OP-020", Severity.Error, false),
            ("OW-RUN-013", Severity.Error, false),
            ("OW-RUN-014", Severity.Error, false),
            ("OW-CFG-001", Severity.Error, false),
            ("OW-CFG-002", Severity.Error, false),
            ("OW-CFG-003", Severity.Warning, false),
            ("OW-CFG-004", Severity.Warning, false),
            ("OW-CFG-005", Severity.Warning, false),
        };

        [Test]
        public void CatalogEqualsTheSpecTablesExactly()
        {
            var all = ErrorCodes.All;
            Assert.AreEqual(s_specTable.Length, all.Count, "Published code count differs from SPEC §J/§J.1");

            for (var i = 0; i < s_specTable.Length; i++)
            {
                var expected = s_specTable[i];
                var actual = all[i];
                Assert.AreEqual(expected.Code, actual.Code, $"Code at index {i}");
                Assert.AreEqual(expected.Severity, actual.DefaultSeverity, $"Severity of {expected.Code}");
                Assert.AreEqual(expected.Retryable, actual.DefaultRetryable, $"Retryability of {expected.Code}");
            }
        }

        [Test]
        public void EveryPublishedCodeFollowsTheOwAreaNnnShape()
        {
            foreach (var info in ErrorCodes.All)
            {
                Assert.IsTrue(info.Code.StartsWith("OW-", StringComparison.Ordinal), info.Code);
                var parts = info.Code.Split('-');
                Assert.AreEqual(3, parts.Length, info.Code);
                Assert.AreEqual("OW", parts[0]);
                Assert.Greater(parts[1].Length, 0, info.Code);
                Assert.AreEqual(3, parts[2].Length, info.Code);
                foreach (var c in parts[1])
                {
                    Assert.IsTrue(c >= 'A' && c <= 'Z', info.Code);
                }
                foreach (var c in parts[2])
                {
                    Assert.IsTrue(c >= '0' && c <= '9', info.Code);
                }
            }
        }

        [Test]
        public void EveryPublishedCodeIsFindable()
        {
            foreach (var expected in s_specTable)
            {
                Assert.IsTrue(ErrorCodes.IsPublished(expected.Code), expected.Code);
                var info = ErrorCodes.Find(expected.Code);
                Assert.IsNotNull(info, expected.Code);
                Assert.AreEqual(expected.Code, info!.Code);
                Assert.AreEqual(expected.Code, ErrorCodes.Get(expected.Code).Code);
            }
        }

        [Test]
        public void CatalogConstantsMatchTheirCodeStrings()
        {
            // Spot-check that callers never need hand-typed strings for published codes.
            Assert.AreEqual("OW-WF-001", ErrorCodes.OW_WF_001);
            Assert.AreEqual("OW-VAL-005", ErrorCodes.OW_VAL_005);
            Assert.AreEqual("OW-RUN-006", ErrorCodes.OW_RUN_006);
            Assert.AreEqual("OW-OP-010", ErrorCodes.OW_OP_010);
            Assert.AreEqual("OW-OP-011", ErrorCodes.OW_OP_011);
            Assert.AreEqual("OW-RUN-014", ErrorCodes.OW_RUN_014);
            Assert.AreEqual("OW-CFG-005", ErrorCodes.OW_CFG_005);
        }

        [Test]
        public void CatalogListCannotBeMutatedThroughCasts()
        {
            var all = ErrorCodes.All;
            Assert.IsNull(all as ErrorCodeInfo[]);

            var list = (IList<ErrorCodeInfo>)all;
            Assert.IsTrue(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = all[1]);
            Assert.Throws<NotSupportedException>(() => list.Add(all[0]));
            Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }

        [Test]
        public void RetiredNumbersAreReservedAndCannotBeEmitted()
        {
            var retired = new[]
            {
                ErrorCodes.OW_OP_009,
                ErrorCodes.OW_PERM_005,
                ErrorCodes.OW_VAL_009,
            };
            Assert.AreEqual(new[] { "OW-OP-009", "OW-PERM-005", "OW-VAL-009" }, retired);

            foreach (var code in retired)
            {
                Assert.IsTrue(ErrorCodes.IsReserved(code), code);
                Assert.IsFalse(ErrorCodes.IsPublished(code), code);
                Assert.IsNull(ErrorCodes.Find(code), code);
                Assert.Throws<ArgumentException>(() => ErrorCodes.Get(code));
                Assert.Throws<ArgumentException>(() => ErrorCodes.Create(code, "message"));
            }
        }

        [Test]
        public void DocumentationOnlyAliasesCannotBeEmitted()
        {
            var aliases = new[]
            {
                ErrorCodes.OW_OP_BLUEPRINT_UNSUPPORTED,
                ErrorCodes.OW_INTEGRITY_UNSUPPORTED,
            };
            Assert.AreEqual(new[] { "OW-OP-BLUEPRINT-UNSUPPORTED", "OW-INTEGRITY-UNSUPPORTED" }, aliases);

            foreach (var alias in aliases)
            {
                Assert.IsFalse(ErrorCodes.IsPublished(alias), alias);
                Assert.IsNull(ErrorCodes.Find(alias), alias);
                Assert.Throws<ArgumentException>(() => ErrorCodes.Get(alias));
                Assert.Throws<ArgumentException>(() => ErrorCodes.Create(alias, "message"));
            }
        }

        [Test]
        public void UnknownCodeStringsCannotBeEmitted()
        {
            foreach (var code in new[] { "OW-FOO-999", "OW-RUN-999", "ow-run-001", "", "bogus" })
            {
                Assert.IsFalse(ErrorCodes.IsPublished(code), code);
                Assert.IsFalse(ErrorCodes.IsReserved(code), code);
                Assert.Throws<ArgumentException>(() => ErrorCodes.Get(code));
                Assert.Throws<ArgumentException>(() => ErrorCodes.Create(code, "message"));
            }
        }

        [Test]
        public void CreateProducesADiagnosticWithSpecDefaults()
        {
            var retryable = ErrorCodes.Create(ErrorCodes.OW_RUN_001, "Not in Edit Mode.");
            Assert.AreEqual("OW-RUN-001", retryable.Code);
            Assert.AreEqual(Severity.Error, retryable.Severity);
            Assert.IsTrue(retryable.Retryable);
            Assert.AreEqual("Not in Edit Mode.", retryable.Message);

            var warning = ErrorCodes.Create(ErrorCodes.OW_WF_006, "Unknown property.");
            Assert.AreEqual(Severity.Warning, warning.Severity);
            Assert.IsFalse(warning.Retryable);

            var info = ErrorCodes.Create(ErrorCodes.OW_RUN_005, "Cancelled.");
            Assert.AreEqual(Severity.Info, info.Severity);
        }

        [Test]
        public void CreateLetsEmittersOverrideConditionalDefaults()
        {
            // OW-WF-006: strict mode / reserved "$" keys are Error (SPEC §J).
            var strict = ErrorCodes.Create(
                ErrorCodes.OW_WF_006, "Reserved $ key.", severity: Severity.Error);
            Assert.AreEqual(Severity.Error, strict.Severity);

            // OW-RUN-004: retryable only when the SPEC §F5 retry conditions hold.
            var timeout = ErrorCodes.Create(
                ErrorCodes.OW_RUN_004, "Step timed out.", retryable: true);
            Assert.IsTrue(timeout.Retryable);
        }

        [Test]
        public void CreatePassesThroughDiagnosticLocationFields()
        {
            var diagnostic = ErrorCodes.Create(
                ErrorCodes.OW_VAL_005,
                "A required input is not satisfied.",
                nodeId: "n1",
                port: "in",
                jsonPath: "$.nodes[3].parameters.name",
                hint: "Bind a value.");
            Assert.AreEqual("n1", diagnostic.NodeId);
            Assert.AreEqual("in", diagnostic.Port);
            Assert.AreEqual("$.nodes[3].parameters.name", diagnostic.JsonPath);
            Assert.AreEqual("Bind a value.", diagnostic.Hint);
        }

        [Test]
        public void CreateRejectsMissingMessage()
        {
            Assert.Throws<ArgumentException>(() => ErrorCodes.Create(ErrorCodes.OW_WF_001, null!));
            Assert.Throws<ArgumentException>(() => ErrorCodes.Create(ErrorCodes.OW_WF_001, ""));
        }
    }
}
