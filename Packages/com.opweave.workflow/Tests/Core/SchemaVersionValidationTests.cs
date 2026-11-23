#nullable enable

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §D2 schemaVersion read rule on programmatic
    /// documents (local:T-W1.6-09:d2-schema-version): an unsupported or malformed
    /// version is an OW-WF-002 error at <c>$.schemaVersion</c> that stops the
    /// pipeline after D2 with no Plan, a same-major newer minor reads with the
    /// serializer's OW-WF-002 warning and validation continues to later phases,
    /// the supported 1.0 is unchanged, and the serializer and the compiler emit
    /// identical diagnostics for every version string. The same sources run
    /// under dotnet and Unity Mono.
    /// </summary>
    internal sealed class SchemaVersionValidationTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────

        private static NodeData Node(string id, string operation = "example.operation") =>
            new NodeData { Id = id, Operation = operation, Version = 1 };

        private static WorkflowDocumentData LinearData(string schemaVersion) => new WorkflowDocumentData
        {
            SchemaVersion = schemaVersion,
            Id = "example.schemaversion",
            Nodes = new List<NodeData>
            {
                Node("start", "core.start"),
                Node("a"),
                Node("b"),
            },
            ControlEdges = new List<ControlEdge>
            {
                new ControlEdge("start", "a"),
                new ControlEdge("a", "b"),
            },
        };

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        private static ValidationRequest RequestFor(WorkflowDocument document) =>
            new ValidationRequest(document, null, ValidationMode.Static, ActorKind.Agent);

        // A later phase that would report a diagnostic: seeing — or not seeing —
        // this error proves whether validation continued past D2.
        private const string LaterPhaseMessage = "Later-phase spy diagnostic.";

        private sealed class LaterPhaseSpy : IValidationPhase
        {
            public ValidationPhaseKind Kind { get; }

            public LaterPhaseSpy(ValidationPhaseKind kind) => Kind = kind;

            public void Run(ValidationContext context) => context.Report(
                ErrorCodes.Create(ErrorCodes.OW_OP_001, LaterPhaseMessage, nodeId: "a"));
        }

        private static WorkflowCompiler CompilerWithLaterPhase() =>
            new WorkflowCompiler(new IValidationPhase[]
            {
                new StructuralValidationPhase(),
                new LaterPhaseSpy(ValidationPhaseKind.D3),
            });

        // The JSON text of the same document with a different schemaVersion, for
        // the serializer side of the parity comparison.
        private static string JsonFor(string version) =>
            WorkflowSerializer.Write(MustCreate(LinearData("1.0"))).Replace(
                "\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"" + version + "\"");

        private static List<Diagnostic> SerializerSchemaVersionDiagnostics(string version) =>
            WorkflowSerializer.Read(JsonFor(version))
                .Diagnostics
                .Where(d => d.JsonPath == "$.schemaVersion")
                .ToList();

        private static List<Diagnostic> CompilerSchemaVersionDiagnostics(string version)
        {
            var report = new WorkflowCompiler().Validate(
                RequestFor(MustCreate(LinearData(version))));
            return report.Errors.Concat(report.Warnings)
                .Where(d => d.JsonPath == "$.schemaVersion")
                .ToList();
        }

        // The serializer suffixes each diagnostic message with " (line L, column
        // C)." naming the source position; a programmatic document has no source
        // positions, so the D2 diagnostic is the identical rule text without it.
        private static string WithoutPositionSuffix(string serializerMessage)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                serializerMessage, @" \(line \d+, column \d+\)\.$");
            Assert.IsTrue(match.Success,
                "serializer message must carry its position suffix: " + serializerMessage);
            return serializerMessage.Substring(0, match.Index) + ".";
        }

        // ── Supported version ───────────────────────────────────────────────────

        [Test]
        public void SupportedVersionValidatesAsBefore()
        {
            var report = new WorkflowCompiler().Validate(
                RequestFor(MustCreate(LinearData("1.0"))));
            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(report.Errors);
            Assert.IsEmpty(report.Warnings);
        }

        // ── Same-major newer minor: warning, validation continues (§A7 item 1,
        // §N.3 item 3) ───────────────────────────────────────────────────────────

        [Test]
        public void NewerMinorWarnsAndLaterPhasesStillRun()
        {
            var report = CompilerWithLaterPhase().Validate(
                RequestFor(MustCreate(LinearData("1.1"))));

            var warning = report.Warnings.Single();
            Assert.AreEqual(ErrorCodes.OW_WF_002, warning.Code);
            Assert.AreEqual(Severity.Warning, warning.Severity);
            Assert.AreEqual("$.schemaVersion", warning.JsonPath);
            Assert.AreEqual(
                "This version of OpWeave does not support branching or other post-1.0 features.",
                warning.Hint);

            // The warning does not stop the pipeline (SPEC §D1 item 2): the D3
            // spy's diagnostic is reported.
            Assert.AreEqual(1, report.Errors.Count);
            Assert.AreEqual(LaterPhaseMessage, report.Errors[0].Message);
        }

        // ── Unsupported / malformed versions: OW-WF-002 error, stop after D2 ────

        [Test]
        public void UnsupportedMajorErrorsAndStopsAfterD2()
        {
            var report = CompilerWithLaterPhase().Validate(
                RequestFor(MustCreate(LinearData("2.0"))));

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan);
            var error = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_WF_002, error.Code);
            Assert.AreEqual(Severity.Error, error.Severity);
            Assert.AreEqual("$.schemaVersion", error.JsonPath);
            Assert.IsEmpty(report.Warnings);
        }

        [Test]
        public void OlderMajorErrorsAndStopsAfterD2()
        {
            var report = CompilerWithLaterPhase().Validate(
                RequestFor(MustCreate(LinearData("0.9"))));

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan);
            var error = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_WF_002, error.Code);
            Assert.AreEqual("$.schemaVersion", error.JsonPath);
            StringAssert.Contains("requires migration", error.Message);
        }

        [TestCase("abc")]
        [TestCase("1")]
        [TestCase("1.0.1")]
        [TestCase("v1.0")]
        public void MalformedVersionErrorsAndStopsAfterD2(string version)
        {
            var report = CompilerWithLaterPhase().Validate(
                RequestFor(MustCreate(LinearData(version))));

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan);
            var error = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_WF_002, error.Code);
            Assert.AreEqual("$.schemaVersion", error.JsonPath);
        }

        // ── Serializer / compiler parity (§D2 applies the §A3 read diagnostics) ──

        [TestCase("1.0")]
        [TestCase("1.1")]
        [TestCase("2.0")]
        [TestCase("0.9")]
        [TestCase("abc")]
        public void SerializerAndCompilerDiagnosticsMatch(string version)
        {
            var serializer = SerializerSchemaVersionDiagnostics(version);
            var compiler = CompilerSchemaVersionDiagnostics(version);

            Assert.AreEqual(serializer.Count, compiler.Count,
                "diagnostic count must match for schemaVersion '" + version + "'");
            for (var i = 0; i < serializer.Count; i++)
            {
                var s = serializer[i];
                var c = compiler[i];
                Assert.AreEqual(s.Code, c.Code, version);
                Assert.AreEqual(s.Severity, c.Severity, version);
                Assert.AreEqual(s.JsonPath, c.JsonPath, version);
                Assert.AreEqual(s.Hint, c.Hint, version);
                Assert.AreEqual(s.NodeId, c.NodeId, version);
                Assert.AreEqual(s.Port, c.Port, version);
                Assert.AreEqual(s.Retryable, c.Retryable, version);
                Assert.AreEqual(
                    WithoutPositionSuffix(s.Message), c.Message,
                    "the rule text must be identical for schemaVersion '" + version + "'");
            }
        }
    }
}
