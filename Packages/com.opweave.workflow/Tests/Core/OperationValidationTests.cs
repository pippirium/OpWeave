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
using OpWeave.Core.Registry;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §D3 operation-validation phase
    /// (local:T-W1.6-02:operation-validation): (id, version) resolution in a
    /// synthetic RegistrySnapshot (unknown → OW-OP-001 with the removed
    /// Operation's previous-source hint from the registry diff, older version
    /// → OW-OP-002 with the migrate_workflow hint, unknown reserved core.* →
    /// OW-OP-001 with the no-branching hint, blueprint kind → OW-OP-010),
    /// unknown parameter keys → OW-OP-005, enum/PortConstraints violations →
    /// OW-OP-006 with the allowed-values list (SPEC §D3 test), codec-level
    /// type mismatches → OW-TYPE-003, and IOperation.ValidateStatic called
    /// last. The RegistrySnapshot-backed production
    /// <see cref="WorkflowCompiler"/> runs the Resolve stage for real, so the
    /// typed Int64/Vector3 cases go through the public constructor.
    /// </summary>
    internal sealed class OperationValidationTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────

        private sealed class UnityShapesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => UnityShapeTypes.All;
        }

        private static readonly ITypeRegistry s_types = BuildTypes();

        private static ITypeRegistry BuildTypes()
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
            });
            Assert.AreEqual(0, build.Failures.Count,
                "registry build failed: " + (build.Failures.Count > 0 ? build.Failures[0].Detail : string.Empty));
            return build.Registry;
        }

        // A type registry that also carries custom test types, so Operations
        // can declare ports typed by custom codecs.
        private static ITypeRegistry TypesWith(params TypeDefinition[] custom)
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "acme.custom-types", new CustomTypeProvider(custom), OperationOrigin.Custom),
            });
            Assert.AreEqual(0, build.Failures.Count,
                "registry build failed: " + (build.Failures.Count > 0 ? build.Failures[0].Detail : string.Empty));
            return build.Registry;
        }

        // A recording IOperation whose ValidateStatic proves the "called last"
        // ordering and surfaces the context's resolved parameter values.
        private sealed class RecordingOperation : IOperation
        {
            private readonly IReadOnlyList<Diagnostic> _report;

            internal RecordingOperation(OperationDescriptor descriptor, params Diagnostic[] report)
            {
                Descriptor = descriptor;
                _report = report;
            }

            internal int StaticCalls;
            internal StaticContext? LastContext;

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx)
            {
                StaticCalls++;
                LastContext = ctx;
                return _report;
            }

            public PreflightResult Preflight(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        private sealed class ThrowingStaticOperation : IOperation
        {
            internal ThrowingStaticOperation(OperationDescriptor descriptor) => Descriptor = descriptor;

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                throw new InvalidOperationException("token 'sk-live-0042' rejected");

            public PreflightResult Preflight(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        // A custom Codec enforcing PortConstraints on its own structured
        // representation — {"v": int} bounded by constraints.Max — a rule the
        // phase's primitive keyword evaluation cannot express on an object
        // token (D3-CODEC-01). It records the constraints the framework
        // handed it so the tests can prove they were the port's real ones.
        private sealed class ScaleCodec : IValueCodec
        {
            internal PortConstraints? LastConstraints;

            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
            {
                LastConstraints = constraints;
                return CheckShape(value, constraints);
            }

            public JsonValue Normalize(JsonValue value) => value;

            internal static IReadOnlyList<Diagnostic> CheckShape(
                JsonValue value, PortConstraints? constraints)
            {
                var diagnostics = new List<Diagnostic>();
                var token = value.ToToken();
                var member = token.Type == JTokenType.Object ? token["v"] : null;
                if (member is null || member.Type != JTokenType.Integer)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "Value is not a scale object with an integer 'v'."));
                    return diagnostics;
                }
                if (constraints?.Max is double max && (long)((JValue)member).Value! > max)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "The scale exceeds the maximum bound."));
                }
                return diagnostics;
            }
        }

        // A custom Codec validating the same {"v": int} shape as ScaleCodec,
        // but whose Normalize multiplies 'v' by ten: a written value inside
        // the port's Max bound normalizes to a value outside it — the SPEC
        // §B7.1 contract breach D3-NORMALIZE-01 targets.
        private sealed class EscalatingScaleCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                ScaleCodec.CheckShape(value, constraints);

            public JsonValue Normalize(JsonValue value)
            {
                var token = value.ToToken();
                if (token is not JObject obj ||
                    obj["v"] is not JValue member || member.Type != JTokenType.Integer)
                {
                    return value;
                }
                var escalated = (JObject)obj.DeepClone();
                escalated["v"] = (long)member.Value! * 10;
                return JsonValue.From(escalated);
            }
        }

        private sealed class CustomTypeProvider : ITypeProvider
        {
            private readonly IReadOnlyList<TypeDefinition> _definitions;

            internal CustomTypeProvider(params TypeDefinition[] definitions) =>
                _definitions = definitions;

            public IEnumerable<TypeDefinition> GetTypes() => _definitions;
        }

        // The D6 phase's required type probe; no fixture here declares a
        // type_exists requirement, so the answer is never read.
        private sealed class NoTypeProbe : IStaticTypeProbe
        {
            public bool TypeExists(string typeName) => false;
        }

        private static OperationDescriptor StartDescriptor() =>
            Op.Define("core.start", 1).Summary("Workflow entry point.").Build();

        private static OperationDescriptor PlaceDescriptor() =>
            Op.Define("example.place", 1)
                .Summary("Place an object at a position.")
                .Input("position", "unity.Vector3", required: true)
                .Input("note", "core.String", required: false)
                .Build();

        private static OperationDescriptor CountDescriptor() =>
            Op.Define("example.count", 1)
                .Summary("Count to a number.")
                .Input("count", "core.Int", required: true)
                .Build();

        private static OperationDescriptor ShapeDescriptor() =>
            Op.Define("example.shape", 1)
                .Summary("Make a primitive.")
                .Input("primitive", "core.String", required: true, c => c.Enum("none", "cube", "sphere"))
                .Build();

        private static readonly RecordingOperation s_start = new RecordingOperation(StartDescriptor());
        private static readonly RecordingOperation s_place = new RecordingOperation(PlaceDescriptor());
        private static readonly RecordingOperation s_count = new RecordingOperation(CountDescriptor());
        private static readonly RecordingOperation s_shape = new RecordingOperation(ShapeDescriptor());

        private static OperationCandidate Candidate(IOperation operation, int version = 1) =>
            new OperationCandidate(
                version == 1 ? operation.Descriptor : operation.Descriptor with { Version = version },
                OperationOrigin.Standard,
                "Acme.Editor",
                "Assets/Acme/Operation.cs",
                new string('a', 64),
                HasPollImplementation: true,
                Implementation: operation);

        // A metadata-only candidate (SPEC §C3: candidates may arrive without
        // an instantiated Operation) — the snapshot entry it produces carries
        // no implementation instance.
        private static OperationCandidate MetadataCandidate(IOperation operation, int version = 1) =>
            new OperationCandidate(
                version == 1 ? operation.Descriptor : operation.Descriptor with { Version = version },
                OperationOrigin.Standard,
                "Acme.Editor",
                "Assets/Acme/Operation.cs",
                new string('a', 64),
                HasPollImplementation: true);

        private static RegistrySnapshot Snapshot(params OperationCandidate[] candidates) =>
            RegistrySnapshotBuilder.Build(candidates, s_types);

        private static RegistrySnapshot DefaultSnapshot() =>
            Snapshot(Candidate(s_start), Candidate(s_place), Candidate(s_count), Candidate(s_shape));

        private static StaticContext StaticFactory(
            string nodeId, IReadOnlyDictionary<string, InputValue> parameters) =>
            new OperationContractFixtures.Static(nodeId, parameters);

        private static WorkflowCompiler Compiler(
            RegistrySnapshot? snapshot = null,
            IReadOnlyList<RegistryChange>? changes = null) =>
            new WorkflowCompiler(
                snapshot ?? DefaultSnapshot(), StaticFactory, new NoTypeProbe(),
                new PlanProvenanceFixtures.Stub(), changes);

        private static NodeData Node(
            string id, string operation, int version = 1, IDictionary<string, JToken>? parameters = null) =>
            new NodeData { Id = id, Operation = operation, Version = version, Parameters = parameters };

        private static WorkflowDocumentData LinearData() => new WorkflowDocumentData
        {
            SchemaVersion = "1.0",
            Id = "example.operation-validation",
            Nodes = new List<NodeData>
            {
                Node("start", "core.start"),
                Node("a", "example.place", parameters: new Dictionary<string, JToken>
                {
                    ["position"] = JToken.Parse("{\"x\":0,\"y\":1,\"z\":0}"),
                }),
                Node("b", "example.count", parameters: new Dictionary<string, JToken>
                {
                    ["count"] = JToken.Parse("3"),
                }),
            },
            ControlEdges = new List<ControlEdge>
            {
                new ControlEdge("start", "a"),
                new ControlEdge("a", "b"),
            },
        };

        // The custom-codec fixture set: the "acme.Scale" type registered in
        // a test-local registry, plus an Operation whose "factor" port is
        // typed by it and constrained by Max(10).
        private static (RegistrySnapshot Snapshot, ScaleCodec Codec) ScaleFixture()
        {
            var codec = new ScaleCodec();
            var types = TypesWith(new TypeDefinition(
                "acme.Scale", TypeKind.Value, 0, Array.Empty<string>(), null,
                "a custom structured scale type.", codec));
            var scale = new RecordingOperation(
                Op.Define("example.scale", 1)
                    .Summary("Scale a thing.")
                    .Input("factor", "acme.Scale", required: true, c => c.Max(10))
                    .Build());
            var snapshot = RegistrySnapshotBuilder.Build(
                new[] { Candidate(s_start), Candidate(scale) }, types);
            Assert.IsNotNull(snapshot.Find("example.scale", 1), "fixture registration failed");
            return (snapshot, codec);
        }

        private static WorkflowDocumentData ScaleData(string factorJson) =>
            new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.codec-constraints",
                Nodes = new List<NodeData>
                {
                    Node("start", "core.start"),
                    Node("a", "example.scale",
                        parameters: new Dictionary<string, JToken>
                        {
                            ["factor"] = JToken.Parse(factorJson),
                        }),
                },
                ControlEdges = new List<ControlEdge> { new ControlEdge("start", "a") },
            };

        // The D3-NORMALIZE-01 fixture set: the "acme.EscalatingScale" type
        // whose Codec normalizes in-bounds written values out of bounds,
        // plus an Operation whose "factor" port is typed by it and
        // constrained by Max(10).
        private static RegistrySnapshot EscalatingFixture()
        {
            var types = TypesWith(new TypeDefinition(
                "acme.EscalatingScale", TypeKind.Value, 0, Array.Empty<string>(), null,
                "a custom scale type whose Normalize escalates the value.",
                new EscalatingScaleCodec()));
            var escalate = new RecordingOperation(
                Op.Define("example.escalate", 1)
                    .Summary("Scale a thing beyond its bound.")
                    .Input("factor", "acme.EscalatingScale", required: true, c => c.Max(10))
                    .Build());
            var snapshot = RegistrySnapshotBuilder.Build(
                new[] { Candidate(s_start), Candidate(escalate) }, types);
            Assert.IsNotNull(snapshot.Find("example.escalate", 1), "fixture registration failed");
            return snapshot;
        }

        private static WorkflowDocumentData EscalatingData(string factorJson) =>
            new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.codec-normalize-constraints",
                Nodes = new List<NodeData>
                {
                    Node("start", "core.start"),
                    Node("a", "example.escalate",
                        parameters: new Dictionary<string, JToken>
                        {
                            ["factor"] = JToken.Parse(factorJson),
                        }),
                },
                ControlEdges = new List<ControlEdge> { new ControlEdge("start", "a") },
            };

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        private static ValidationRequest Request(WorkflowDocument document) =>
            new ValidationRequest(document, null, ValidationMode.Static, ActorKind.Agent);

        private static Diagnostic[] Errors(ValidationReport report, string code) =>
            report.Errors.Where(d => d.Code == code).ToArray();

        // ── Normal behavior ─────────────────────────────────────────────────────

        [Test]
        public void ValidNodesPassWithNoDiagnostics()
        {
            var report = Compiler().Validate(Request(MustCreate(LinearData())));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(report.Errors);
            Assert.IsEmpty(report.Warnings);
        }

        // The snapshot-backed production pipeline resolves typed parameters for
        // real: integer and decimal Vector3 spellings produce identical
        // resolved values (mandatory cross-ticket requirement, SPEC §D0.2).
        [Test]
        public void SnapshotBackedCompilerResolvesVector3SpellingsIdentically()
        {
            var decimals = LinearData();
            decimals.Nodes![1].Parameters = new Dictionary<string, JToken>
            {
                ["position"] = JToken.Parse("{\"x\":0.0,\"y\":1.0,\"z\":0.0}"),
            };

            var intsReport = Compiler().Validate(Request(MustCreate(LinearData())));
            var decimalsReport = Compiler().Validate(Request(MustCreate(decimals)));

            Assert.IsTrue(intsReport.Ok);
            Assert.IsTrue(decimalsReport.Ok);

            var intsContext = new ValidationContext(Request(MustCreate(LinearData())));
            var decimalsContext = new ValidationContext(Request(MustCreate(decimals)));
            var snapshot = DefaultSnapshot();
            new ResolveStage(snapshot, snapshot.Types).Run(intsContext);
            new ResolveStage(snapshot, snapshot.Types).Run(decimalsContext);

            Assert.AreEqual(
                intsContext.ResolvedWorkflow!.CanonicalText,
                decimalsContext.ResolvedWorkflow!.CanonicalText);
            Assert.AreEqual(
                "{\"x\":0e0,\"y\":1e0,\"z\":0e0}",
                intsContext.ResolvedWorkflow!.ParametersFor("a")!["position"].CanonicalText);
        }

        // The same production constructor rejects the Int64 port's fraction
        // token at the codec level: OW-TYPE-003 and no ResolvedWorkflow.
        [Test]
        public void Int64PortGivenFractionProducesOwType003AndNoResolvedWorkflow()
        {
            var data = LinearData();
            data.Nodes![2].Parameters = new Dictionary<string, JToken>
            {
                ["count"] = JToken.Parse("1.0"),
            };
            var report = Compiler().Validate(Request(MustCreate(data)));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_TYPE_003).Single();
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("count", error.Port);
            Assert.AreEqual("$.nodes[2].parameters.count", error.JsonPath);
            Assert.IsNull(report.Plan);

            var context = new ValidationContext(Request(MustCreate(data)));
            var snapshot = DefaultSnapshot();
            new ResolveStage(snapshot, snapshot.Types).Run(context);
            Assert.IsNull(context.ResolvedWorkflow);
        }

        // ── (id, version) resolution ────────────────────────────────────────────

        [Test]
        public void UnknownOperationIsOwOp001()
        {
            var data = LinearData();
            data.Nodes![1] = Node("a", "acme.missing.op");
            var report = Compiler().Validate(Request(MustCreate(data)));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_OP_001).Single();
            Assert.AreEqual("a", error.NodeId);
            Assert.AreEqual("$.nodes[1].operation", error.JsonPath);
        }

        [Test]
        public void RemovedOperationIsOwOp001WithPreviousSourceHint()
        {
            var data = LinearData();
            data.Nodes![1] = Node("a", "acme.removed.op");
            var changes = new[]
            {
                new RegistryChange(
                    RegistryChangeKinds.Removed, "acme.removed.op", 2,
                    "Assets/Acme/RemovedOp.cs", Array.Empty<Diagnostic>()),
                new RegistryChange(
                    RegistryChangeKinds.Removed, "acme.removed.op", 1,
                    "Assets/Acme/OlderRemovedOp.cs", Array.Empty<Diagnostic>()),
            };
            var report = Compiler(changes: changes).Validate(Request(MustCreate(data)));

            var error = Errors(report, ErrorCodes.OW_OP_001).Single();
            // The node's version matches the version-1 record's source.
            StringAssert.Contains("Assets/Acme/OlderRemovedOp.cs", error.Hint);
        }

        [Test]
        public void UnknownOperationWithoutDiffDataHasNoSourceHint()
        {
            var data = LinearData();
            data.Nodes![1] = Node("a", "acme.missing.op");
            var report = Compiler().Validate(Request(MustCreate(data)));

            var error = Errors(report, ErrorCodes.OW_OP_001).Single();
            StringAssert.Contains("Register the operation", error.Hint);
        }

        [Test]
        public void OlderNodeVersionIsOwOp002WithMigrateHint()
        {
            var snapshot = Snapshot(
                Candidate(s_start),
                Candidate(s_place),
                Candidate(s_count, version: 2));
            var data = LinearData();
            data.Nodes![2] = Node("b", "example.count", version: 1,
                new Dictionary<string, JToken> { ["count"] = JToken.Parse("3") });
            var report = Compiler(snapshot).Validate(Request(MustCreate(data)));

            var error = Errors(report, ErrorCodes.OW_OP_002).Single();
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("$.nodes[2].operation", error.JsonPath);
            StringAssert.Contains("migrate_workflow", error.Hint);
        }

        [Test]
        public void NewerNodeVersionIsOwOp002WithRegisteredVersions()
        {
            var data = LinearData();
            data.Nodes![2] = Node("b", "example.count", version: 9,
                new Dictionary<string, JToken> { ["count"] = JToken.Parse("3") });
            var report = Compiler().Validate(Request(MustCreate(data)));

            var error = Errors(report, ErrorCodes.OW_OP_002).Single();
            Assert.AreEqual("b", error.NodeId);
            StringAssert.Contains("version(s) 1", error.Hint);
        }

        [Test]
        public void UnknownReservedCoreOperationIsOwOp001WithBranchingHint()
        {
            var data = LinearData();
            data.Nodes![1] = Node("a", "core.branch");
            var report = Compiler().Validate(Request(MustCreate(data)));

            var error = Errors(report, ErrorCodes.OW_OP_001).Single();
            Assert.AreEqual("a", error.NodeId);
            StringAssert.Contains("does not support branching", error.Hint);
        }

        // ── kind=blueprint → OW-OP-010 (SPEC §N.5.5) ────────────────────────────

        [Test]
        public void BlueprintKindIsOwOp010()
        {
            var blueprintDescriptor = Op.Define("acme.blueprint.op", 1)
                .Summary("A blueprint fixture.")
                .Build();
            var blueprint = new RegisteredOperation(
                blueprintDescriptor with { Kind = OperationKind.Blueprint },
                OperationOrigin.Custom,
                "Acme.Editor",
                "Assets/Acme/Blueprint.cs",
                DescriptorHash.Compute(blueprintDescriptor),
                new string('b', 64))
            {
                // The compiler requires an instance on every snapshot entry
                // even though the blueprint check reports before
                // ValidateStatic could reach it.
                Implementation = new RecordingOperation(blueprintDescriptor),
            };
            var snapshot = new RegistrySnapshot(
                "reg_test_blueprint",
                new string('c', 64),
                new[] { blueprint },
                s_types,
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());

            var data = LinearData();
            data.Nodes!.RemoveAt(2);
            data.ControlEdges!.RemoveAt(1);
            data.Nodes[1] = Node("a", "acme.blueprint.op");
            var report = Compiler(snapshot).Validate(Request(MustCreate(data)));

            var error = Errors(report, ErrorCodes.OW_OP_010).Single();
            Assert.AreEqual("a", error.NodeId);
            Assert.AreEqual("$.nodes[1].operation", error.JsonPath);
        }

        // ── Parameter keys and values ───────────────────────────────────────────

        [Test]
        public void UnknownParameterKeyIsOwOp005()
        {
            var data = LinearData();
            data.Nodes![1].Parameters!["bogus"] = JToken.Parse("1");
            var report = Compiler().Validate(Request(MustCreate(data)));

            var error = Errors(report, ErrorCodes.OW_OP_005).Single();
            Assert.AreEqual("a", error.NodeId);
            Assert.AreEqual("bogus", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.bogus", error.JsonPath);
            StringAssert.Contains("position", error.Hint);
        }

        // SPEC §D3 test: a primitive value outside the enum produces OW-OP-006
        // whose hint carries the allowed-values list — and no codec-level
        // OW-TYPE-003 for the same violation.
        [Test]
        public void EnumViolationIsOwOp006WithAllowedValuesHint()
        {
            var data = LinearData();
            data.Nodes![1] = Node("a", "example.shape",
                parameters: new Dictionary<string, JToken>
                {
                    ["primitive"] = JToken.Parse("\"torus\""),
                });
            var report = Compiler().Validate(Request(MustCreate(data)));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("a", error.NodeId);
            Assert.AreEqual("primitive", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.primitive", error.JsonPath);
            StringAssert.Contains("\"none\"", error.Hint);
            StringAssert.Contains("\"cube\"", error.Hint);
            StringAssert.Contains("\"sphere\"", error.Hint);
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_TYPE_003),
                "the constraint violation must not also surface as OW-TYPE-003");
        }

        [Test]
        public void RangeViolationIsOwOp006()
        {
            var ranged = new RecordingOperation(
                Op.Define("example.ranged", 1)
                    .Summary("A ranged count.")
                    .Input("count", "core.Int", required: true, c => c.Min(0).Max(10))
                    .Build());
            var snapshot = Snapshot(Candidate(s_start), Candidate(ranged));
            var data = new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.ranged-check",
                Nodes = new List<NodeData>
                {
                    Node("start", "core.start"),
                    Node("a", "example.ranged",
                        parameters: new Dictionary<string, JToken> { ["count"] = JToken.Parse("42") }),
                },
                ControlEdges = new List<ControlEdge> { new ControlEdge("start", "a") },
            };
            var report = Compiler(snapshot).Validate(Request(MustCreate(data)));

            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("a", error.NodeId);
            Assert.AreEqual("count", error.Port);
        }

        // ── Codec-visible constraints (D3-CODEC-01) ─────────────────────────────

        // A custom Codec's constraint rule is invisible to the phase's
        // primitive keyword evaluation — an object token matches no keyword —
        // so the rejection can only come from the Codec seeing the real
        // PortConstraints (SPEC §B7); it must surface as OW-OP-006.
        [Test]
        public void CustomCodecConstraintRuleRejectsAsOwOp006()
        {
            var (snapshot, codec) = ScaleFixture();
            var report = Compiler(snapshot).Validate(
                Request(MustCreate(ScaleData("{\"v\":42}"))));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("a", error.NodeId);
            Assert.AreEqual("factor", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.factor", error.JsonPath);
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_TYPE_003),
                "a constraint violation must not surface as a codec type error");
            Assert.IsNotNull(codec.LastConstraints,
                "the Codec never saw the port's PortConstraints");
            Assert.IsTrue(codec.LastConstraints!.Max == 10.0);
        }

        [Test]
        public void CustomCodecConstraintRuleAcceptsInsideBounds()
        {
            var (snapshot, _) = ScaleFixture();
            var report = Compiler(snapshot).Validate(
                Request(MustCreate(ScaleData("{\"v\":5}"))));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(report.Errors);
        }

        // D3-NORMALIZE-01 (SPEC §B7.1 item 4): the written {"v":5} satisfies
        // the port's Max(10), but the Codec's Normalize produces {"v":50}.
        // The guarded contract revalidates the normalized result under the
        // port's PortConstraints, so the Resolve stage rejects it as a
        // sanitized OW-TYPE-003 and produces no ResolvedWorkflow — the
        // out-of-bounds value must not reach the planHash input.
        [Test]
        public void CodecNormalizedValueOutsideConstraintsIsOwType003AndNoResolvedWorkflow()
        {
            var snapshot = EscalatingFixture();
            var data = EscalatingData("{\"v\":5}");
            var report = Compiler(snapshot).Validate(Request(MustCreate(data)));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_TYPE_003).Single();
            Assert.AreEqual("a", error.NodeId);
            Assert.AreEqual("factor", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.factor", error.JsonPath);
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_OP_006),
                "the written value satisfied the constraints — the failure is the normalized result");
            Assert.IsNull(report.Plan);

            var context = new ValidationContext(Request(MustCreate(data)));
            new ResolveStage(snapshot, snapshot.Types).Run(context);
            Assert.IsNull(context.ResolvedWorkflow);
        }

        // ── ValidateStatic last ─────────────────────────────────────────────────

        [Test]
        public void ValidateStaticRunsAfterParameterChecksWithResolvedValues()
        {
            var flag = ErrorCodes.Create(
                ErrorCodes.OW_VAL_013, "Static check flagged the node.");
            var staticOp = new RecordingOperation(PlaceDescriptor(), flag);
            var snapshot = Snapshot(Candidate(s_start), Candidate(staticOp), Candidate(s_count));

            var report = Compiler(snapshot).Validate(Request(MustCreate(LinearData())));

            Assert.AreEqual(1, staticOp.StaticCalls);
            Assert.IsNotNull(staticOp.LastContext);
            Assert.AreEqual("a", staticOp.LastContext!.NodeId);
            // The resolved (codec-normalized) value reached the Operation:
            // written ints became Double tokens (SPEC §D0.2).
            Assert.AreEqual(
                "{\"x\":0e0,\"y\":1e0,\"z\":0e0}",
                staticOp.LastContext.Parameters["position"].Value!.CanonicalText);
            Assert.IsTrue(
                report.Warnings.Any(d => d.Code == ErrorCodes.OW_VAL_013 && d.NodeId == "a"),
                "the ValidateStatic diagnostic must surface on the report");
        }

        // A codec failure on one node must not strip another node's
        // normalized values from its ValidateStatic context (SPEC §D1 item 2,
        // §D0.2): node "b"'s fraction token fails the Int codec at the
        // Resolve stage, so no ResolvedWorkflow is published, yet "a"'s
        // ValidateStatic still observes its Float parameter as the normalized
        // double 2e0 — not the written integer 2 (SPEC §B7).
        [Test]
        public void ConversionFailureOnAnotherNodeKeepsNormalizedValuesForValidateStatic()
        {
            var weigh = new RecordingOperation(
                Op.Define("example.weigh", 1)
                    .Summary("Weigh a thing.")
                    .Input("weight", "core.Float", required: true)
                    .Build());
            var snapshot = Snapshot(
                Candidate(s_start), Candidate(weigh), Candidate(s_count));
            var data = new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.normalized-static",
                Nodes = new List<NodeData>
                {
                    Node("start", "core.start"),
                    Node("a", "example.weigh",
                        parameters: new Dictionary<string, JToken>
                        {
                            ["weight"] = JToken.Parse("2"),
                        }),
                    Node("b", "example.count",
                        parameters: new Dictionary<string, JToken>
                        {
                            ["count"] = JToken.Parse("1.0"),
                        }),
                },
                ControlEdges = new List<ControlEdge>
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b"),
                },
            };
            var report = Compiler(snapshot).Validate(Request(MustCreate(data)));

            Assert.IsFalse(report.Ok);
            var typeError = Errors(report, ErrorCodes.OW_TYPE_003).Single();
            Assert.AreEqual("b", typeError.NodeId);
            Assert.AreEqual(1, weigh.StaticCalls);
            Assert.AreEqual(
                "2e0",
                weigh.LastContext!.Parameters["weight"].Value!.CanonicalText,
                "an unaffected node's ValidateStatic must observe the normalized value");
        }

        [Test]
        public void ValidateStaticExceptionIsOwRun006WithoutSecretText()
        {
            var throwing = new ThrowingStaticOperation(PlaceDescriptor());
            var snapshot = Snapshot(Candidate(s_start), Candidate(throwing), Candidate(s_count));

            var report = Compiler(snapshot).Validate(Request(MustCreate(LinearData())));

            var error = Errors(report, ErrorCodes.OW_RUN_006).Single();
            Assert.AreEqual("a", error.NodeId);
            StringAssert.DoesNotContain("sk-live", error.Message);
        }

        // ── Fail-closed static validation at construction (D3-STATIC-02) ──────

        // SPEC §D3 makes IOperation.ValidateStatic part of validating every
        // registered node, and SPEC §J assigns no diagnostic to a compiler or
        // snapshot that cannot run it — OW-OP-008 names a registration-time
        // instantiation failure, not a missing compiler input. The
        // configuration-boundary contract makes such a compiler
        // unconstructable instead: a snapshot entry without an implementation
        // instance fails the public constructor with ArgumentException, and
        // ValidateStatic is never silently skipped.
        [Test]
        public void SnapshotEntryWithoutImplementationIsRejectedAtConstruction()
        {
            var place = new RecordingOperation(PlaceDescriptor());
            var snapshot = Snapshot(
                Candidate(s_start),
                MetadataCandidate(place),
                Candidate(s_count));

            var error = Assert.Throws<ArgumentException>(
                () => new WorkflowCompiler(
                    snapshot, StaticFactory, new NoTypeProbe(), new PlanProvenanceFixtures.Stub()));
            Assert.AreEqual("snapshot", error!.ParamName);
            StringAssert.Contains("example.place", error.Message);
            Assert.AreEqual(0, place.StaticCalls,
                "no instance exists to run ValidateStatic on");
        }

        // A snapshot-backed compiler without a StaticContext factory is the
        // same impossible configuration (SPEC §0.6 SG-06: Core hosts no
        // context implementation): the required constructor argument fails
        // with ArgumentNullException rather than a published diagnostic
        // misreporting instantiated Operations as uninstantiable.
        [Test]
        public void CompilerWithoutStaticContextFactoryIsRejectedAtConstruction()
        {
            var error = Assert.Throws<ArgumentNullException>(
                () => new WorkflowCompiler(
                    DefaultSnapshot(), null!, new NoTypeProbe(), new PlanProvenanceFixtures.Stub()));
            Assert.AreEqual("staticContextFactory", error!.ParamName);
        }

        // ── SPEC §D1 interplay ──────────────────────────────────────────────────

        // SPEC §D1 test shape: an unknown Operation on one node does not hide
        // another node's type error.
        [Test]
        public void UnknownOperationDoesNotHideOtherNodeTypeError()
        {
            var data = LinearData();
            data.Nodes![1] = Node("a", "acme.missing.op");
            data.Nodes[2].Parameters = new Dictionary<string, JToken>
            {
                ["count"] = JToken.Parse("1.0"),
            };
            var report = Compiler().Validate(Request(MustCreate(data)));

            Assert.IsFalse(report.Ok);
            var unknown = Errors(report, ErrorCodes.OW_OP_001).Single();
            var type = Errors(report, ErrorCodes.OW_TYPE_003).Single();
            Assert.AreEqual("a", unknown.NodeId);
            Assert.AreEqual("b", type.NodeId);
            Assert.AreEqual(
                new[] { ErrorCodes.OW_OP_001, ErrorCodes.OW_TYPE_003 },
                report.Errors.Select(d => d.Code).ToArray(),
                "diagnostics stay in node document order");
        }
    }
}
