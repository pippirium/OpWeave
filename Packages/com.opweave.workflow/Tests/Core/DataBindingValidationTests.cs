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
    /// [Core] tests for the SPEC §D4 type-and-supplier validation phase
    /// (local:T-W1.6-03:supplier-validation): the §A1/R1 supply table (one
    /// supplier used; none + default → default; none + required → OW-VAL-005;
    /// none + optional → Missing; two or more → OW-VAL-006 listing every
    /// supplier JsonPath), §B6 edge assignability after OutputRefinement
    /// (PrefabAssetRef → required GameObjectRef → OW-TYPE-002), ConstantOnly
    /// ports fed by edge/binding → OW-OP-006 without echoing a value, WF input
    /// type assignability (OW-TYPE-002), missing required WF input →
    /// OW-VAL-008, undeclared runtime inputs → one OW-VAL-014 warning each
    /// (SPEC §A6), codec-checked runtime input values, and the recorded
    /// suppliers/output types the Build Plan stage consumes (SPEC §D4
    /// item 6). The same
    /// sources run under dotnet and Unity Mono.
    /// </summary>
    internal sealed class DataBindingValidationTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────

        private sealed class UnityShapesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => UnityShapeTypes.All;
        }

        private sealed class StubClassRelations : IClassRelationProvider
        {
            private readonly Dictionary<string, bool> _answers =
                new Dictionary<string, bool>(StringComparer.Ordinal);

            internal void Answer(string classId, string baseClassId, bool verdict) =>
                _answers[classId + " → " + baseClassId] = verdict;

            public bool? IsSubclass(string classId, string baseClassId) =>
                _answers.TryGetValue(classId + " → " + baseClassId, out var verdict)
                    ? verdict
                    : (bool?)null;
        }

        private static ITypeRegistry BuildTypes(IClassRelationProvider? relations = null)
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
            }, relations);
            Assert.AreEqual(0, build.Failures.Count,
                "registry build failed: " + (build.Failures.Count > 0 ? build.Failures[0].Detail : string.Empty));
            return build.Registry;
        }

        private static readonly ITypeRegistry s_types = BuildTypes();

        private sealed class MapOperations : IOperationSource
        {
            private readonly Dictionary<(string Id, int Version), OperationDescriptor> _map =
                new Dictionary<(string Id, int Version), OperationDescriptor>();

            internal MapOperations(params OperationDescriptor[] descriptors)
            {
                foreach (var descriptor in descriptors)
                {
                    _map.Add((descriptor.Id, descriptor.Version), descriptor);
                }
            }

            public OperationDescriptor? Find(string id, int version) =>
                _map.TryGetValue((id, version), out var descriptor) ? descriptor : null;
        }

        // The D6 phase's required type probe; no fixture here declares a
        // type_exists requirement, so the answer is never read.
        private sealed class NoTypeProbe : IStaticTypeProbe
        {
            public bool TypeExists(string typeName) => false;
        }

        private sealed class TrivialOperation : IOperation
        {
            internal TrivialOperation(OperationDescriptor descriptor) => Descriptor = descriptor;

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) => throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) => throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) => throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        // ── Descriptors ─────────────────────────────────────────────────────────

        private static OperationDescriptor StartDescriptor() =>
            Op.Define("core.start", 1).Summary("Workflow entry point.").Build();

        // Produces the values edges feed from.
        private static OperationDescriptor SourceDescriptor() =>
            Op.Define("example.source", 1)
                .Summary("Produce values.")
                .Output("text", "core.String")
                .Output("gameObject", "unity.GameObjectRef")
                .Output("prefab", "unity.PrefabAssetRef")
                .Output("prefabInstance", "unity.PrefabInstanceRef")
                .Build();

        // The supply-table carrier: a required port, an undefaulted optional
        // port and a defaulted optional port.
        private static OperationDescriptor SinkDescriptor() =>
            Op.Define("example.sink", 1)
                .Summary("Consume values.")
                .Input("target", "unity.GameObjectRef", required: true)
                .Input("text", "core.String", required: false)
                .Input("greeting", "core.String", required: false, @default: "hello")
                .Build();

        private static OperationDescriptor ConstantDescriptor() =>
            Op.Define("example.constant", 1)
                .Summary("Constant input only.")
                .Input("id", "core.String", required: true, c => c.ConstantOnly())
                .Build();

        // The §C1 refinement example: component output narrowed by the
        // constant componentType parameter.
        private static OperationDescriptor ComponentAddDescriptor() =>
            Op.Define("example.component_add", 1)
                .Summary("Add a component.")
                .Input("target", "unity.GameObjectRef", required: true)
                .Input("componentType", "unity.TypeName", required: true)
                .Output("component", "unity.ComponentRef<unity.Component>")
                .Build()
                with
                {
                    OutputRefinement = new TypeRefinement(
                        "componentType", "component", "unity.ComponentRef<{0}>"),
                };

        // The same ports as ComponentAddDescriptor without the refinement:
        // the declared component type drives edge checks.
        private static OperationDescriptor ComponentAddPlainDescriptor() =>
            Op.Define("example.component_add_plain", 1)
                .Summary("Add a component.")
                .Input("target", "unity.GameObjectRef", required: true)
                .Input("componentType", "unity.TypeName", required: true)
                .Output("component", "unity.ComponentRef<unity.Component>")
                .Build();

        private static OperationDescriptor AnimatorConsumerDescriptor() =>
            Op.Define("example.animator_consumer", 1)
                .Summary("Consume an Animator component.")
                .Input("component", "unity.ComponentRef<unity.Animator>", required: true)
                .Build();

        // The AssetRef counterpart of ComponentAddDescriptor: its {0} hole
        // takes the class id of the constant assetType parameter. AssetRef<T>
        // constrains T to unity.Object, which every §B5 built-in class
        // satisfies — including the UnityEditor classes MonoScript and
        // AnimatorController, whose CLR names are not "UnityEngine." +
        // short name (SPEC §B5, §C1).
        private static OperationDescriptor AssetSourceDescriptor() =>
            Op.Define("example.asset_source", 1)
                .Summary("Produce an asset.")
                .Input("assetType", "unity.TypeName", required: true)
                .Output("asset", "unity.AssetRef<unity.Object>")
                .Build()
                with
                {
                    OutputRefinement = new TypeRefinement(
                        "assetType", "asset", "unity.AssetRef<{0}>"),
                };

        private static OperationDescriptor AssetConsumerDescriptor(string classId) =>
            Op.Define("example.asset_consumer", 1)
                .Summary("Consume an asset.")
                .Input("asset", "unity.AssetRef<" + classId + ">", required: true)
                .Build();

        private static IOperation[] Operations(params OperationDescriptor[] descriptors) =>
            descriptors.Select(d => (IOperation)new TrivialOperation(d)).ToArray();

        private static RegistrySnapshot Snapshot(ITypeRegistry types, params IOperation[] operations) =>
            RegistrySnapshotBuilder.Build(
                operations.Select(o => new OperationCandidate(
                    o.Descriptor,
                    OperationOrigin.Standard,
                    "Acme.Editor",
                    "Assets/Acme/Operation.cs",
                    new string('a', 64),
                    HasPollImplementation: true,
                    Implementation: o)).ToArray(),
                types);

        private static MapOperations Source(params OperationDescriptor[] descriptors) =>
            new MapOperations(descriptors);

        private static StaticContext StaticFactory(
            string nodeId, IReadOnlyDictionary<string, InputValue> parameters) =>
            new OperationContractFixtures.Static(nodeId, parameters);

        private static NodeData Node(
            string id, string operation, IDictionary<string, JToken>? parameters = null) =>
            new NodeData { Id = id, Operation = operation, Version = 1, Parameters = parameters };

        private static WorkflowDocumentData Data(
            IEnumerable<NodeData> nodes,
            IEnumerable<ControlEdge>? controlEdges = null,
            IEnumerable<DataEdge>? dataEdges = null,
            IEnumerable<InputBinding>? inputBindings = null,
            IEnumerable<WorkflowInput>? inputs = null) => new WorkflowDocumentData
        {
            SchemaVersion = "1.0",
            Id = "example.data-binding",
            Nodes = new List<NodeData>(nodes),
            ControlEdges =
                controlEdges is null ? null : new List<ControlEdge>(controlEdges),
            DataEdges = dataEdges is null ? null : new List<DataEdge>(dataEdges),
            InputBindings =
                inputBindings is null ? null : new List<InputBinding>(inputBindings),
            Inputs = inputs is null ? null : new List<WorkflowInput>(inputs),
        };

        // The default linear carrier: start → source → sink with the source
        // feeding the sink's required port by a data edge.
        private static WorkflowDocumentData LinearData() => Data(
            new[]
            {
                Node("start", "core.start"),
                Node("a", "example.source"),
                Node("b", "example.sink"),
            },
            new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
            new[]
            {
                new DataEdge(
                    new PortRef("a", "gameObject"), new PortRef("b", "target")),
            });

        private static RegistrySnapshot DefaultSnapshot() =>
            Snapshot(s_types, Operations(
                StartDescriptor(), SourceDescriptor(), SinkDescriptor(), ConstantDescriptor()));

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "JsonValue.Parse failed for " + text);
            return parsed.Value!;
        }

        private static ValidationReport Compile(
            WorkflowDocumentData data,
            JsonValue? inputs = null,
            RegistrySnapshot? snapshot = null) =>
            new WorkflowCompiler(
                    snapshot ?? DefaultSnapshot(), StaticFactory, new NoTypeProbe(),
                    new PlanProvenanceFixtures.Stub())
                .Validate(new ValidationRequest(
                    MustCreate(data), inputs, ValidationMode.Static, ActorKind.Agent));

        // Runs Resolve + the real D4 phase on a context so the recorded
        // suppliers and output types are observable.
        private static ValidationContext RunPhase(
            WorkflowDocumentData data,
            JsonValue? inputs = null,
            MapOperations? operations = null,
            ITypeRegistry? types = null)
        {
            var context = new ValidationContext(new ValidationRequest(
                MustCreate(data), inputs, ValidationMode.Static, ActorKind.Agent));
            var operations_ = operations ?? Source(
                StartDescriptor(), SourceDescriptor(), SinkDescriptor(), ConstantDescriptor());
            var types_ = types ?? s_types;
            new ResolveStage(operations_, types_).Run(context);
            new DataBindingValidationPhase(operations_, types_).Run(context);
            return context;
        }

        private static Diagnostic[] Errors(ValidationReport report, string code) =>
            report.Errors.Where(d => d.Code == code).ToArray();

        private static PortSupplier? Supplier(
            ValidationContext context, string nodeId, string port) =>
            context.PortSuppliers is not null &&
            context.PortSuppliers.TryGetValue(nodeId, out var ports) &&
            ports.TryGetValue(port, out var supplier)
                ? supplier
                : null;

        // ── SPEC §A1 item 2 supply table — one explicit supplier ─────────────

        [Test]
        public void EdgeSupplierIsUsed()
        {
            var context = RunPhase(LinearData());
            Assert.IsEmpty(context.Diagnostics);
            var supplier = Supplier(context, "b", "target");
            Assert.IsInstanceOf<PortSupplier.Edge>(supplier);
            var edge = (PortSupplier.Edge)supplier!;
            Assert.AreEqual(("a", "gameObject"), (edge.Node, edge.Port));
        }

        [Test]
        public void BindingSupplierIsUsed()
        {
            var data = LinearData();
            data.DataEdges = null;
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.GameObjectRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "target")),
            };
            var context = RunPhase(data);
            Assert.IsEmpty(context.Diagnostics);
            var supplier = Supplier(context, "b", "target");
            Assert.AreEqual(new PortSupplier.Input("thing"), supplier);
        }

        [Test]
        public void ParameterSupplierIsUsed()
        {
            var data = LinearData();
            data.DataEdges = null;
            data.Nodes![2].Parameters = new Dictionary<string, JToken>
            {
                ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/Player\"}"),
            };
            var context = RunPhase(data);
            Assert.IsEmpty(context.Diagnostics);
            var supplier = Supplier(context, "b", "target");
            Assert.IsInstanceOf<PortSupplier.Parameter>(supplier);
            Assert.AreEqual(
                "{\"kind\":\"gameObject\",\"path\":\"/Player\"}",
                ((PortSupplier.Parameter)supplier!).Value.CanonicalText);
        }

        // ── SPEC §A1 item 2 supply table — no supplier ────────────────────────

        [Test]
        public void NoSupplierWithDefaultUsesDefault()
        {
            var context = RunPhase(LinearData());
            var supplier = Supplier(context, "b", "greeting");
            Assert.AreEqual(new PortSupplier.Default(Json("\"hello\"")), supplier);
        }

        [Test]
        public void NoSupplierRequiredReportsOwVal005()
        {
            var data = LinearData();
            data.DataEdges = null;
            var report = Compile(data);

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_VAL_005).Single();
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("target", error.Port);
            Assert.AreEqual("$.nodes[2]", error.JsonPath);
        }

        [Test]
        public void NoSupplierOptionalIsMissing()
        {
            var context = RunPhase(LinearData());
            Assert.AreEqual(new PortSupplier.Absent(), Supplier(context, "b", "text"));
        }

        // ── SPEC §A1 item 2 / §D4 item 2 — two or more suppliers → OW-VAL-006 ──

        [Test]
        public void ParameterAndEdgeReportOwVal006WithBothPaths()
        {
            var data = LinearData();
            data.Nodes![2].Parameters = new Dictionary<string, JToken>
            {
                ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/Player\"}"),
            };
            var report = Compile(data);

            var error = Errors(report, ErrorCodes.OW_VAL_006).Single();
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("target", error.Port);
            StringAssert.Contains("$.dataEdges[0]", error.Hint);
            StringAssert.Contains("$.nodes[2].parameters.target", error.Hint);
        }

        [Test]
        public void BindingAndParameterReportOwVal006WithBothPaths()
        {
            var data = LinearData();
            data.DataEdges = null;
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.GameObjectRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "target")),
            };
            data.Nodes![2].Parameters = new Dictionary<string, JToken>
            {
                ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/Player\"}"),
            };
            var report = Compile(data);

            var error = Errors(report, ErrorCodes.OW_VAL_006).Single();
            StringAssert.Contains("$.inputBindings[0]", error.Hint);
            StringAssert.Contains("$.nodes[2].parameters.target", error.Hint);
        }

        [Test]
        public void EdgeAndBindingReportOwVal006WithBothPaths()
        {
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.GameObjectRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "target")),
            };
            var report = Compile(data);

            var error = Errors(report, ErrorCodes.OW_VAL_006).Single();
            StringAssert.Contains("$.dataEdges[0]", error.Hint);
            StringAssert.Contains("$.inputBindings[0]", error.Hint);
        }

        [Test]
        public void ThreeSuppliersListEveryPathAndAdoptNone()
        {
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.GameObjectRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "target")),
            };
            data.Nodes![2].Parameters = new Dictionary<string, JToken>
            {
                ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/Player\"}"),
            };
            var context = RunPhase(data);

            var error = context.Diagnostics.Single(d => d.Code == ErrorCodes.OW_VAL_006);
            StringAssert.Contains("$.dataEdges[0]", error.Hint);
            StringAssert.Contains("$.inputBindings[0]", error.Hint);
            StringAssert.Contains("$.nodes[2].parameters.target", error.Hint);
            // §A1: no supplier is adopted when there are two or more.
            Assert.IsNull(Supplier(context, "b", "target"));
        }

        // ── SPEC §D4 item 1 — edge assignability (OW-TYPE-002) ───────────────

        [Test]
        public void PrefabAssetToGameObjectReportsOwType002()
        {
            var data = LinearData();
            data.DataEdges![0] = new DataEdge(
                new PortRef("a", "prefab"), new PortRef("b", "target"));
            var report = Compile(data);

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_TYPE_002).Single();
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("target", error.Port);
            Assert.AreEqual("$.dataEdges[0]", error.JsonPath);
            StringAssert.Contains("unity.PrefabAssetRef", error.Message);
            StringAssert.Contains("unity.GameObjectRef", error.Message);
        }

        [Test]
        public void PrefabInstanceToGameObjectIsAssignable()
        {
            var data = LinearData();
            data.DataEdges![0] = new DataEdge(
                new PortRef("a", "prefabInstance"), new PortRef("b", "target"));
            var report = Compile(data);

            Assert.IsTrue(report.Ok, string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_TYPE_002));
        }

        [Test]
        public void AnyEdgeToCoreJsonPortIsAssignable()
        {
            // §B6 rule 2: every output type flows to a core.Json input.
            var sink = SinkDescriptor() with
            {
                Inputs = new[]
                {
                    new PortDescriptor("target", "unity.GameObjectRef", true, null, string.Empty, null),
                    new PortDescriptor("payload", "core.Json", false, null, string.Empty, null),
                },
            };
            var snapshot = Snapshot(s_types, Operations(
                StartDescriptor(), SourceDescriptor(), sink, ConstantDescriptor()));
            var data = Data(
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "example.source"),
                    Node("b", "example.sink",
                        new Dictionary<string, JToken>
                        {
                            ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/P\"}"),
                        }),
                },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[]
                {
                    new DataEdge(
                        new PortRef("a", "prefab"), new PortRef("b", "payload")),
                });
            var report = Compile(data, snapshot: snapshot);

            Assert.IsTrue(report.Ok, string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
        }

        // ── SPEC §D4 item 4 — ConstantOnly → OW-OP-006 ────────────────────────

        [Test]
        public void ConstantOnlyPortFedByEdgeReportsOwOp006WithoutValue()
        {
            var data = Data(
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "example.source"),
                    Node("b", "example.constant"),
                },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[]
                {
                    new DataEdge(
                        new PortRef("a", "text"), new PortRef("b", "id")),
                });
            var report = Compile(data);

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("id", error.Port);
            Assert.AreEqual("$.dataEdges[0]", error.JsonPath);
        }

        [Test]
        public void ConstantOnlyPortFedByBindingReportsOwOp006WithoutValue()
        {
            var data = Data(
                new[]
                {
                    Node("start", "core.start"),
                    Node("b", "example.constant"),
                },
                new[] { new ControlEdge("start", "b") },
                inputBindings: new[]
                {
                    new InputBinding("secret", new PortRef("b", "id")),
                },
                inputs: new[]
                {
                    new WorkflowInput("secret", "core.String", true),
                });
            var report = Compile(data);

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("$.inputBindings[0]", error.JsonPath);
            StringAssert.DoesNotContain("secret", error.Message);
        }

        [Test]
        public void ConstantOnlyPortFedByParameterPasses()
        {
            var data = Data(
                new[]
                {
                    Node("start", "core.start"),
                    Node("b", "example.constant",
                        new Dictionary<string, JToken> { ["id"] = JToken.Parse("\"x\"") }),
                },
                new[] { new ControlEdge("start", "b") });
            var report = Compile(data);

            Assert.IsTrue(report.Ok, string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
        }

        // ── Port existence on edges and bindings (OW-OP-005) ─────────────────

        [Test]
        public void EdgeToUnknownInputPortReportsOwOp005()
        {
            var data = LinearData();
            data.DataEdges![0] = new DataEdge(
                new PortRef("a", "gameObject"), new PortRef("b", "ghost"));
            var report = Compile(data);

            var error = Errors(report, ErrorCodes.OW_OP_005).Single();
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("ghost", error.Port);
            Assert.AreEqual("$.dataEdges[0].to.port", error.JsonPath);
        }

        [Test]
        public void EdgeFromUnknownOutputPortReportsOwOp005()
        {
            var data = LinearData();
            data.DataEdges![0] = new DataEdge(
                new PortRef("a", "ghost"), new PortRef("b", "target"));
            var report = Compile(data);

            var error = Errors(report, ErrorCodes.OW_OP_005).Single();
            Assert.AreEqual("a", error.NodeId);
            Assert.AreEqual("$.dataEdges[0].from.port", error.JsonPath);
        }

        [Test]
        public void BindingToUnknownInputPortReportsOwOp005()
        {
            var data = LinearData();
            data.DataEdges = null;
            data.Nodes![2].Parameters = new Dictionary<string, JToken>
            {
                ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/Player\"}"),
            };
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.GameObjectRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "ghost")),
            };
            var report = Compile(data);

            var error = Errors(report, ErrorCodes.OW_OP_005).Single();
            Assert.AreEqual("$.inputBindings[0].to.port", error.JsonPath);
        }

        // ── SPEC §D4 item 5 / §A6 — WF inputs ─────────────────────────────────

        [Test]
        public void BoundInputTypeNotAssignableToPortReportsOwType002()
        {
            var data = LinearData();
            data.DataEdges = null;
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.PrefabAssetRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "target")),
            };
            var report = Compile(data);

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_TYPE_002).Single();
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("target", error.Port);
            Assert.AreEqual("$.inputBindings[0]", error.JsonPath);
        }

        [Test]
        public void BoundInputSubtypeIsAssignable()
        {
            var data = LinearData();
            data.DataEdges = null;
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.PrefabInstanceRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "target")),
            };
            var report = Compile(data);

            Assert.IsEmpty(Errors(report, ErrorCodes.OW_TYPE_002));
        }

        [Test]
        public void MissingRequiredWorkflowInputReportsOwVal008()
        {
            var data = LinearData();
            data.DataEdges = null;
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.GameObjectRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "target")),
            };
            var report = Compile(data, Json("{}"));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_VAL_008).Single();
            Assert.AreEqual("$.inputs[0]", error.JsonPath);
            StringAssert.Contains("thing", error.Message);
        }

        [Test]
        public void RequiredInputWithDefaultAndOptionalInputSatisfyCoverage()
        {
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.GameObjectRef", true, Json("{\"kind\":\"gameObject\",\"path\":\"/P\"}")),
                new WorkflowInput("extra", "core.String", false),
            };
            var report = Compile(data, Json("{}"));

            Assert.IsEmpty(Errors(report, ErrorCodes.OW_VAL_008));
        }

        [Test]
        public void InputlessValidationSkipsCoverageChecks()
        {
            var data = LinearData();
            data.DataEdges = null;
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.GameObjectRef", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "target")),
            };
            var report = Compile(data, null);

            Assert.IsEmpty(Errors(report, ErrorCodes.OW_VAL_008));
        }

        [Test]
        public void UndeclaredRuntimeInputWarnsOwVal014()
        {
            // SPEC §A6: an execution input the workflow does not declare is
            // ignored — the member is not bound and D4 emits one OW-VAL-014
            // warning at $.inputs.<id>.
            var report = Compile(LinearData(), Json("{\"ghost\":1}"));

            Assert.IsTrue(report.Ok, string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
            var warning = report.Warnings.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_014, warning.Code);
            Assert.AreEqual(Severity.Warning, warning.Severity);
            Assert.IsFalse(warning.Retryable);
            Assert.AreEqual("$.inputs.ghost", warning.JsonPath);
            StringAssert.Contains("ghost", warning.Message);
        }

        [Test]
        public void EachUndeclaredRuntimeInputWarnsOwVal014()
        {
            // One warning per undeclared member, in payload order; a declared
            // input is bound and produces none.
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("count", "core.Int", true),
            };
            var report = Compile(data, Json("{\"count\":1,\"ghost\":1,\"spook\":2}"));

            var warnings = report.Warnings
                .Where(d => d.Code == ErrorCodes.OW_VAL_014).ToArray();
            Assert.AreEqual(2, warnings.Length);
            Assert.AreEqual("$.inputs.ghost", warnings[0].JsonPath);
            Assert.AreEqual("$.inputs.spook", warnings[1].JsonPath);
        }

        [Test]
        public void UndeclaredRuntimeInputWithNonSimpleNameUsesBracketedPath()
        {
            // A member name that is not a plain identifier takes the
            // bracketed JsonPath member form (the §0.6 convention).
            var report = Compile(LinearData(), Json("{\"a.b\":1}"));

            var warning = report.Warnings
                .Single(d => d.Code == ErrorCodes.OW_VAL_014);
            Assert.AreEqual("$.inputs[\"a.b\"]", warning.JsonPath);
        }

        [Test]
        public void RuntimeInputValueIsCheckedByCodec()
        {
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("count", "core.Int", true),
            };
            var report = Compile(data, Json("{\"count\":1.0}"));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_TYPE_003).Single();
            Assert.AreEqual("$.inputs.count", error.JsonPath);
        }

        [Test]
        public void UnknownInputTypeReportsOwType001()
        {
            var data = LinearData();
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "bogus.Type", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("b", "text")),
            };
            var report = Compile(data);

            var error = Errors(report, ErrorCodes.OW_TYPE_001).Single();
            Assert.AreEqual("$.inputs[0].type", error.JsonPath);
        }

        // ── OutputRefinement (SPEC §D4 item 1) ────────────────────────────────

        [Test]
        public void OutputRefinementNarrowsEdgeSourceType()
        {
            // The SPEC §C1 example without a class-relation provider: the
            // constant componentType "UnityEngine.Animator" names a built-in
            // class, so the declared ComponentRef<Component> output refines
            // to ComponentRef<unity.Animator> and feeds the
            // ComponentRef<unity.Animator> input.
            var snapshot = Snapshot(s_types, Operations(
                StartDescriptor(), ComponentAddDescriptor(), AnimatorConsumerDescriptor()));
            var data = Data(
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "example.component_add",
                        new Dictionary<string, JToken>
                        {
                            ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/P\"}"),
                            ["componentType"] = JToken.Parse("\"UnityEngine.Animator\""),
                        }),
                    Node("b", "example.animator_consumer"),
                },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[]
                {
                    new DataEdge(
                        new PortRef("a", "component"), new PortRef("b", "component")),
                });
            var report = Compile(data, snapshot: snapshot);

            Assert.IsTrue(report.Ok, string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_TYPE_002));
        }

        [Test]
        public void DeclaredOutputTypeReportsOwType002WithoutRefinement()
        {
            // The control case: the same edge against a descriptor without
            // OutputRefinement — the declared ComponentRef<Component> cannot
            // feed a ComponentRef<Animator> input.
            var types = BuildTypes(new StubClassRelations());
            var snapshot = Snapshot(types, Operations(
                StartDescriptor(), ComponentAddPlainDescriptor(), AnimatorConsumerDescriptor()));
            var data = Data(
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "example.component_add_plain",
                        new Dictionary<string, JToken>
                        {
                            ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/P\"}"),
                            ["componentType"] = JToken.Parse("\"UnityEngine.Animator\""),
                        }),
                    Node("b", "example.animator_consumer"),
                },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[]
                {
                    new DataEdge(
                        new PortRef("a", "component"), new PortRef("b", "component")),
                });
            var report = Compile(data, snapshot: snapshot);

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_TYPE_002).Single();
            Assert.AreEqual("b", error.NodeId);
            Assert.AreEqual("component", error.Port);
        }

        // ── SPEC §D4 item 6 — recorded suppliers and output types ────────────

        [Test]
        public void SuppliersAndOutputTypesAreRecordedForPlanBuilding()
        {
            var context = RunPhase(LinearData());

            Assert.IsNotNull(context.PortSuppliers);
            Assert.AreEqual(
                new PortSupplier.Edge("a", "gameObject"),
                Supplier(context, "b", "target"));
            Assert.AreEqual(
                new PortSupplier.Default(Json("\"hello\"")),
                Supplier(context, "b", "greeting"));
            Assert.AreEqual(
                new PortSupplier.Absent(),
                Supplier(context, "b", "text"));

            Assert.IsNotNull(context.OutputTypes);
            Assert.AreEqual(
                "unity.GameObjectRef",
                context.OutputTypes!["a"]["gameObject"].ToString());
            Assert.AreEqual(
                "unity.PrefabAssetRef",
                context.OutputTypes!["a"]["prefab"].ToString());
        }

        [Test]
        public void RefinedOutputTypeIsRecorded()
        {
            // A built-in class name refines to its §B5 short Type ID — no
            // class-relation provider is consulted (SPEC §C1).
            var context = RunPhase(
                Data(
                    new[]
                    {
                        Node("start", "core.start"),
                        Node("a", "example.component_add",
                            new Dictionary<string, JToken>
                            {
                                ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/P\"}"),
                                ["componentType"] = JToken.Parse("\"UnityEngine.Animator\""),
                            }),
                    },
                    new[] { new ControlEdge("start", "a") }),
                operations: Source(StartDescriptor(), ComponentAddDescriptor()));

            Assert.AreEqual(
                "unity.ComponentRef<unity.Animator>",
                context.OutputTypes!["a"]["component"].ToString());
        }

        [TestCase("UnityEngine.Object", "unity.Object")]
        [TestCase("UnityEngine.Component", "unity.Component")]
        [TestCase("UnityEngine.GameObject", "unity.GameObject")]
        [TestCase("UnityEngine.Transform", "unity.Transform")]
        [TestCase("UnityEngine.Animator", "unity.Animator")]
        [TestCase("UnityEngine.Material", "unity.Material")]
        [TestCase("UnityEngine.Texture2D", "unity.Texture2D")]
        [TestCase("UnityEditor.MonoScript", "unity.MonoScript")]
        [TestCase("UnityEngine.ScriptableObject", "unity.ScriptableObject")]
        [TestCase("UnityEditor.Animations.AnimatorController", "unity.AnimatorController")]
        public void EveryBuiltInClassNameRefinesToItsShortClassId(string clrName, string classId)
        {
            // Every §B5 built-in class — the UnityEditor classes MonoScript
            // and AnimatorController included — refines to its short class
            // ID; nothing falls through to the type: form.
            var context = RunPhase(
                Data(
                    new[]
                    {
                        Node("start", "core.start"),
                        Node("a", "example.asset_source",
                            new Dictionary<string, JToken>
                            {
                                ["assetType"] = JToken.Parse("\"" + clrName + "\""),
                            }),
                    },
                    new[] { new ControlEdge("start", "a") }),
                operations: Source(StartDescriptor(), AssetSourceDescriptor()));

            Assert.AreEqual(
                "unity.AssetRef<" + classId + ">",
                context.OutputTypes!["a"]["asset"].ToString());
        }

        [TestCase("UnityEngine.Object", "unity.Object")]
        [TestCase("UnityEngine.Component", "unity.Component")]
        [TestCase("UnityEngine.GameObject", "unity.GameObject")]
        [TestCase("UnityEngine.Transform", "unity.Transform")]
        [TestCase("UnityEngine.Animator", "unity.Animator")]
        [TestCase("UnityEngine.Material", "unity.Material")]
        [TestCase("UnityEngine.Texture2D", "unity.Texture2D")]
        [TestCase("UnityEditor.MonoScript", "unity.MonoScript")]
        [TestCase("UnityEngine.ScriptableObject", "unity.ScriptableObject")]
        [TestCase("UnityEditor.Animations.AnimatorController", "unity.AnimatorController")]
        public void RefinedBuiltInOutputAssignsToItsShortClassIdPort(string clrName, string classId)
        {
            // s_types carries no IClassRelationProvider, so the refined
            // AssetRef<unity.X> feeds the AssetRef<unity.X> port by the
            // snapshot's own class IDs — a type: class id would end the
            // edge in OW-TYPE-002 (SPEC §B6 rule 4).
            var snapshot = Snapshot(s_types, Operations(
                StartDescriptor(), AssetSourceDescriptor(), AssetConsumerDescriptor(classId)));
            var data = Data(
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "example.asset_source",
                        new Dictionary<string, JToken>
                        {
                            ["assetType"] = JToken.Parse("\"" + clrName + "\""),
                        }),
                    Node("b", "example.asset_consumer"),
                },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[]
                {
                    new DataEdge(
                        new PortRef("a", "asset"), new PortRef("b", "asset")),
                });
            var report = Compile(data, snapshot: snapshot);

            Assert.IsTrue(report.Ok, string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
        }

        [Test]
        public void ArbitraryClassParameterKeepsTypePrefix()
        {
            // A non-built-in class keeps the §B1 type: class-id form; its
            // relation still goes through IClassRelationProvider (§B6 rule 4).
            var context = RunPhase(
                Data(
                    new[]
                    {
                        Node("start", "core.start"),
                        Node("a", "example.component_add",
                            new Dictionary<string, JToken>
                            {
                                ["target"] = JToken.Parse("{\"kind\":\"gameObject\",\"path\":\"/P\"}"),
                                ["componentType"] = JToken.Parse("\"MyGame.PlayerController\""),
                            }),
                    },
                    new[] { new ControlEdge("start", "a") }),
                operations: Source(StartDescriptor(), ComponentAddDescriptor()));

            Assert.AreEqual(
                "unity.ComponentRef<type:MyGame.PlayerController>",
                context.OutputTypes!["a"]["component"].ToString());
        }

        // ── SPEC §D1 item 2 — exclusion ───────────────────────────────────────

        [Test]
        public void ExcludedNodeIsNotChecked()
        {
            // 'b' is excluded by its D3 OW-OP-005 (unknown parameter), so D4
            // reports no OW-VAL-005 for its unfed required port.
            var data = LinearData();
            data.DataEdges = null;
            data.Nodes![2].Parameters = new Dictionary<string, JToken>
            {
                ["ghost"] = JToken.Parse("1"),
            };
            var report = Compile(data);

            Assert.IsFalse(report.Ok);
            Assert.IsTrue(report.Errors.Any(d =>
                d.Code == ErrorCodes.OW_OP_005 && d.NodeId == "b"));
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_VAL_005));
        }
    }
}
