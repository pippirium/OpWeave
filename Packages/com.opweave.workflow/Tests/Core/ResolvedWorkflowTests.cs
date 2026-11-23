#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the Resolve stage's <see cref="ResolvedWorkflow"/>
    /// construction (local:T-W1.6-06:resolved-workflow; SPEC §D0.2, §B7,
    /// §B7.1): built-in typed parameters become their codec-normalized
    /// canonical values (<c>unity.Vector3</c> integer and decimal spellings
    /// resolve identically), <c>core.Json</c> positions keep the written
    /// tokens, a codec rejection produces no ResolvedWorkflow and reports
    /// OW-TYPE-003, and execution inputs convert with the declared WF input
    /// type codecs. The stage is exercised against a synthetic operation
    /// source and a test-local type registry — the production RegistrySnapshot
    /// wiring is the registry work's — and the same sources run under dotnet
    /// and Unity Mono.
    /// </summary>
    internal sealed class ResolvedWorkflowTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────

        // A codec that validates everything but throws inside Normalize, with a
        // fake secret in the message to prove sanitization (SPEC §B7.1 item 4).
        // Behaves on the registration probe inputs so the fixture still
        // registers under the §B7.1 registration screen (T-W2.12-03).
        private sealed class ThrowingNormalizeCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) =>
                CodecProbeTokens.IsProbe(value)
                    ? value
                    : throw new InvalidOperationException("upstream token 'sk-live-0042' expired");
        }

        private sealed class TestTypeProvider : ITypeProvider
        {
            private readonly TypeDefinition[] _types;

            internal TestTypeProvider(params TypeDefinition[] types) => _types = types;

            public IEnumerable<TypeDefinition> GetTypes() => _types;
        }

        private sealed class UnityShapesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => UnityShapeTypes.All;
        }

        private sealed class MapOperationSource : IOperationSource
        {
            private readonly Dictionary<(string Id, int Version), OperationDescriptor> _map =
                new Dictionary<(string Id, int Version), OperationDescriptor>();

            internal MapOperationSource(params OperationDescriptor[] descriptors)
            {
                foreach (var descriptor in descriptors)
                {
                    _map.Add((descriptor.Id, descriptor.Version), descriptor);
                }
            }

            public OperationDescriptor? Find(string id, int version) =>
                _map.TryGetValue((id, version), out var descriptor) ? descriptor : null;
        }

        private sealed class ProbePhase : IValidationPhase
        {
            private readonly Action<ValidationContext> _probe;

            internal ProbePhase(ValidationPhaseKind kind, Action<ValidationContext> probe)
            {
                Kind = kind;
                _probe = probe;
            }

            public ValidationPhaseKind Kind { get; }

            public void Run(ValidationContext context) => _probe(context);
        }

        private static readonly ITypeRegistry s_registry = BuildRegistry(
            new TypeDefinition(
                "acme.Throws", TypeKind.Value, 0, Array.Empty<string>(), null,
                "a custom test type whose codec throws in Normalize.",
                new ThrowingNormalizeCodec()));

        private static ITypeRegistry BuildRegistry(params TypeDefinition[] custom)
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "acme", new TestTypeProvider(custom), OperationOrigin.Custom),
            });
            Assert.AreEqual(0, build.Failures.Count,
                "registry build failed: " + (build.Failures.Count > 0 ? build.Failures[0].Detail : string.Empty));
            return build.Registry;
        }

        private static OperationDescriptor PlaceOperation() =>
            Op.Define("example.place", 1)
                .Summary("Place an object at a position.")
                .Input("position", "unity.Vector3", required: true)
                .Input("payload", "core.Json", required: false)
                .Input("note", "core.String", required: false)
                .Build();

        private static OperationDescriptor CountOperation() =>
            Op.Define("example.count", 1)
                .Summary("Count to a number.")
                .Input("count", "core.Int", required: true)
                .Input("amount", "core.Float", required: false)
                .Build();

        private static OperationDescriptor ThrowingOperation() =>
            Op.Define("example.throws", 1)
                .Summary("Normalize throws.")
                .Input("value", "acme.Throws", required: true)
                .Build();

        private static readonly MapOperationSource s_operations = new MapOperationSource(
            PlaceOperation(), CountOperation(), ThrowingOperation());

        private static NodeData Node(
            string id, string operation, IDictionary<string, JToken>? parameters = null) =>
            new NodeData { Id = id, Operation = operation, Version = 1, Parameters = parameters };

        private static WorkflowDocumentData LinearData() => new WorkflowDocumentData
        {
            SchemaVersion = "1.0",
            Id = "example.resolve",
            Nodes = new List<NodeData>
            {
                Node("start", "core.start"),
                Node("a", "example.place"),
                Node("b", "example.count"),
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

        private static JToken Token(string json) => JToken.Parse(json);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "JsonValue.Parse failed for " + text);
            return parsed.Value!;
        }

        private static ValidationContext ContextFor(
            WorkflowDocument document, JsonValue? inputs = null) =>
            new ValidationContext(new ValidationRequest(
                document, inputs, ValidationMode.Static, ActorKind.Agent));

        // Runs only the Resolve stage on a context and returns it.
        private static ValidationContext Resolved(
            WorkflowDocument document, JsonValue? inputs = null)
        {
            var context = ContextFor(document, inputs);
            new ResolveStage(s_operations, s_registry).Run(context);
            return context;
        }

        // Runs the full pipeline with the Resolve environment injected; the
        // phase-out callback observes context.ResolvedWorkflow after the stage.
        private static (ValidationReport Report, ResolvedWorkflow? Resolved) Compile(
            WorkflowDocument document, JsonValue? inputs = null)
        {
            ResolvedWorkflow? seen = null;
            var compiler = new WorkflowCompiler(
                new IValidationPhase[]
                {
                    new StructuralValidationPhase(),
                    new PendingValidationPhase(ValidationPhaseKind.D3),
                    new PendingValidationPhase(ValidationPhaseKind.D4),
                    new GraphValidationPhase(),
                    new ProbePhase(ValidationPhaseKind.D6, c => seen = c.ResolvedWorkflow),
                },
                null,
                s_operations,
                s_registry);
            var report = compiler.Validate(new ValidationRequest(
                document, inputs, ValidationMode.Static, ActorKind.Agent));
            return (report, seen);
        }

        private static WorkflowDocument PlaceDocument(JToken position, JToken? payload = null)
        {
            var data = LinearData();
            var parameters = new Dictionary<string, JToken> { ["position"] = position };
            if (payload is not null)
            {
                parameters["payload"] = payload;
            }
            data.Nodes![1].Parameters = parameters;
            return MustCreate(data);
        }

        // ── SPEC §D0.2 — node parameters ────────────────────────────────────────

        // SPEC §D0.2 fixed test row 1 / §B7 test: integer and decimal spellings
        // of the same Vector3 resolve to one canonical form — the planHash
        // input no longer distinguishes them while the WorkflowHash does.
        [Test]
        public void Vector3IntegersAndDecimalsResolveIdentically()
        {
            var ints = Resolved(PlaceDocument(Token("{\"x\":0,\"y\":1,\"z\":0}")));
            var decimals = Resolved(PlaceDocument(Token("{\"x\":0.0,\"y\":1.0,\"z\":0.0}")));

            Assert.IsNotNull(ints.ResolvedWorkflow);
            Assert.IsNotNull(decimals.ResolvedWorkflow);
            Assert.AreEqual(
                ints.ResolvedWorkflow!.CanonicalText,
                decimals.ResolvedWorkflow!.CanonicalText);

            // The document identity stays distinct — WorkflowHash is never the
            // planHash input.
            var canonicalInts = CanonicalWorkflow.From(PlaceDocument(Token("{\"x\":0,\"y\":1,\"z\":0}")));
            var canonicalDecimals = CanonicalWorkflow.From(PlaceDocument(Token("{\"x\":0.0,\"y\":1.0,\"z\":0.0}")));
            Assert.AreNotEqual(canonicalInts.CanonicalText, canonicalDecimals.CanonicalText);

            var resolved = ints.ResolvedWorkflow!.ParametersFor("a");
            Assert.IsNotNull(resolved);
            Assert.AreEqual(
                "{\"x\":0e0,\"y\":1e0,\"z\":0e0}",
                resolved!["position"].CanonicalText);
        }

        // SPEC §D0.2 fixed test row 2 / §B7 item 4: a core.Json port keeps the
        // written tokens — 1 and 1.0 stay distinct resolved forms.
        [Test]
        public void CoreJsonParameterKeepsWrittenTokens()
        {
            var one = Resolved(PlaceDocument(Token("{\"x\":0,\"y\":1,\"z\":0}"), Token("1")));
            var onePoint = Resolved(PlaceDocument(Token("{\"x\":0,\"y\":1,\"z\":0}"), Token("1.0")));

            Assert.AreEqual("1", one.ResolvedWorkflow!.ParametersFor("a")!["payload"].CanonicalText);
            Assert.AreEqual("1e0", onePoint.ResolvedWorkflow!.ParametersFor("a")!["payload"].CanonicalText);
            Assert.AreNotEqual(
                one.ResolvedWorkflow!.CanonicalText,
                onePoint.ResolvedWorkflow!.CanonicalText);
        }

        // SPEC §D0.2 fixed test row 3 / §B7 test: a fraction token on an Int64
        // port is a codec rejection — no ResolvedWorkflow, OW-TYPE-003.
        [Test]
        public void Int64PortRejectsFractionToken()
        {
            var data = LinearData();
            data.Nodes![2].Parameters = new Dictionary<string, JToken> { ["count"] = Token("1.0") };
            var document = MustCreate(data);

            var (report, resolved) = Compile(document);

            Assert.IsNull(resolved);
            Assert.IsFalse(report.Ok);
            var error = report.Errors.Single(d => d.Code == ErrorCodes.OW_TYPE_003);
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("count", error.Port);
            Assert.AreEqual("$.nodes[2].parameters.count", error.JsonPath);
        }

        // SPEC §B7 item 1: an integer token on a Double position converts.
        [Test]
        public void FloatParameterConvertsIntegerTokenToDouble()
        {
            var data = LinearData();
            data.Nodes![2].Parameters = new Dictionary<string, JToken> { ["amount"] = Token("0") };
            var context = Resolved(MustCreate(data));

            Assert.IsNotNull(context.ResolvedWorkflow);
            Assert.AreEqual(
                "0e0",
                context.ResolvedWorkflow!.ParametersFor("b")!["amount"].CanonicalText);
        }

        // SPEC §D0.2 fixed test row 4: spelling differences that carry no value
        // difference — here reordered object members — resolve identically.
        [Test]
        public void ReorderedParameterMembersResolveIdentically()
        {
            var first = Resolved(PlaceDocument(Token("{\"x\":0,\"y\":1,\"z\":0}")));
            var reordered = Resolved(PlaceDocument(Token("{\"z\":0,\"y\":1,\"x\":0}")));

            Assert.AreEqual(
                first.ResolvedWorkflow!.CanonicalText,
                reordered.ResolvedWorkflow!.CanonicalText);
        }

        // A codec contract breach (SPEC §B7.1 item 4) fails closed as a single
        // sanitized OW-TYPE-003 — never the exception text — and produces no
        // ResolvedWorkflow.
        [Test]
        public void CodecContractViolationReportsSanitizedOwType003()
        {
            var data = LinearData();
            data.Nodes![1] = Node("a", "example.throws",
                new Dictionary<string, JToken> { ["value"] = Token("1") });
            var (report, resolved) = Compile(MustCreate(data));

            Assert.IsNull(resolved);
            Assert.IsFalse(report.Ok);
            var error = report.Errors.Single(d => d.Code == ErrorCodes.OW_TYPE_003);
            Assert.AreEqual("a", error.NodeId);
            Assert.AreEqual("value", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.value", error.JsonPath);
            StringAssert.DoesNotContain("sk-live", error.Message);
        }

        // Positions the stage cannot resolve keep the written tokens without a
        // diagnostic: the unknown Operation is D3's OW-OP-001, not Resolve's.
        [Test]
        public void UnknownOperationKeepsWrittenParameters()
        {
            var data = LinearData();
            data.Nodes![1] = Node("a", "example.unknown",
                new Dictionary<string, JToken> { ["position"] = Token("0") });
            var context = Resolved(MustCreate(data));

            Assert.IsEmpty(context.Diagnostics);
            Assert.IsNotNull(context.ResolvedWorkflow);
            Assert.AreEqual(
                "0",
                context.ResolvedWorkflow!.ParametersFor("a")!["position"].CanonicalText);
        }

        // ── SPEC §D0.2 — execution inputs and declared defaults ────────────────

        // Execution inputs convert with the declared WF input type's Codec;
        // core.Json keeps the written tokens and undeclared members pass through.
        [Test]
        public void ExecutionInputsConvertWithDeclaredTypes()
        {
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("factor", "core.Float", true),
                new WorkflowInput("payload", "core.Json", false),
            };
            var context = Resolved(
                MustCreate(data),
                Json("{\"factor\":0,\"payload\":1.0,\"extra\":true}"));

            Assert.IsNotNull(context.ResolvedInputs);
            var token = (JObject)context.ResolvedInputs!.ToToken();
            Assert.AreEqual(
                "0e0", CanonicalJson.Serialize(token["factor"]!));
            Assert.AreEqual(
                "1e0", CanonicalJson.Serialize(token["payload"]!));
            Assert.AreEqual(
                "true", CanonicalJson.Serialize(token["extra"]!));
        }

        // A rejected execution input is an OW-TYPE-003 and produces no
        // ResolvedWorkflow — the planHash input must hold normalized values only.
        [Test]
        public void RejectedExecutionInputProducesNoResolvedWorkflow()
        {
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("count", "core.Int", true),
            };
            var context = Resolved(MustCreate(data), Json("{\"count\":1.0}"));

            Assert.IsNull(context.ResolvedWorkflow);
            Assert.IsNull(context.ResolvedInputs);
            var error = context.Diagnostics.Single(d => d.Code == ErrorCodes.OW_TYPE_003);
            Assert.AreEqual("count", error.Port);
            Assert.AreEqual("$.inputs.count", error.JsonPath);
        }

        // A declared WF-input default is a typed value stored in
        // ResolvedWorkflow, so it resolves to the canonical form like a
        // parameter: 0 and 0.0 written defaults resolve identically.
        [Test]
        public void DeclaredInputDefaultNormalizesWithDeclaredType()
        {
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("factor", "core.Float", false, Json("0")),
            };
            var context = Resolved(MustCreate(data));

            var inputs = (JArray)context.ResolvedWorkflow!.ToToken()["inputs"]!;
            var factor = inputs
                .Cast<JObject>()
                .Single(i => (string)i["id"]! == "factor");
            Assert.AreEqual("0e0", CanonicalJson.Serialize(factor["default"]!));
        }

        // ── Stage boundary ─────────────────────────────────────────────────────

        // Without an operation source and type registry the stage converts
        // nothing: the snapshot-free production compiler stays inert until the
        // registry work wires the real source.
        [Test]
        public void StageWithoutEnvironmentIsInert()
        {
            var context = ContextFor(PlaceDocument(Token("{\"x\":0,\"y\":1,\"z\":0}")));
            new ResolveStage(null, null).Run(context);
            Assert.IsNull(context.ResolvedWorkflow);
            Assert.IsNull(context.ResolvedInputs);
            Assert.IsEmpty(context.Diagnostics);
        }

        [Test]
        public void DefaultCompilerConvertsNothing()
        {
            var data = LinearData();
            data.Nodes![2].Parameters = new Dictionary<string, JToken> { ["count"] = Token("1.0") };
            var report = new WorkflowCompiler().Validate(new ValidationRequest(
                MustCreate(data), null, ValidationMode.Static, ActorKind.Agent));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(report.Errors);
        }
    }
}
