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
    /// [Core] tests for the SPEC §D6 permission and static-precondition phase
    /// (local:T-W1.6-05:permission-static, local:T-W1.6-05:integrated-pipeline):
    /// actual effects via the §C4 declaration and a stub
    /// <see cref="IEffectResolver"/> (§C4 test 2 mechanism), allow/ask/deny from
    /// an injected policy (OW-PERM-001, the Plan approvals, the §H4 floors,
    /// SPEC §D6 tests 1 and 3 / §C4 test 3 / §H4 test 3), constant
    /// <c>path_writable</c> paths against the allowed paths (OW-PERM-002),
    /// <c>type_exists</c> through an injected <see cref="IStaticTypeProbe"/>
    /// (OW-UNITY-006 or the OW-VAL-015 warning), OW-VAL-012 inside a Prefab
    /// contents session, OW-VAL-013 after code generation without a compile
    /// wait, and the §D1 test — one report carrying OW-OP-001 and OW-TYPE-002
    /// from the real D2–D6 pipeline. The same sources run under dotnet and
    /// Unity Mono.
    /// </summary>
    internal sealed class PermissionValidationTests
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

        // A settings-map IPermissionPolicy fixture, like the evaluator tests'.
        private sealed class FixturePolicy : IPermissionPolicy
        {
            private readonly IReadOnlyDictionary<string, PermissionLevel> _levels;

            internal FixturePolicy(
                PolicyActor actor,
                IReadOnlyDictionary<string, PermissionLevel> levels,
                bool destructiveRequiresAsk = true)
            {
                Actor = actor;
                _levels = levels;
                DestructiveRequiresAsk = destructiveRequiresAsk;
            }

            public PolicyActor Actor { get; }
            public bool DestructiveRequiresAsk { get; }

            public PermissionLevel LevelFor(string effect) =>
                _levels.TryGetValue(effect, out var level) ? level : PermissionLevel.Deny;
        }

        // The §C4 default table for the Agent as a fixture map.
        private static FixturePolicy AgentDefaults(bool destructiveRequiresAsk = true) =>
            new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.Read] = PermissionLevel.Allow,
                [EffectNames.SceneLoad] = PermissionLevel.Allow,
                [EffectNames.SceneModify] = PermissionLevel.Allow,
                [EffectNames.SceneSave] = PermissionLevel.Allow,
                [EffectNames.AssetCreate] = PermissionLevel.Allow,
                [EffectNames.AssetModify] = PermissionLevel.Allow,
                [EffectNames.AssetMove] = PermissionLevel.Ask,
                [EffectNames.AssetDelete] = PermissionLevel.Ask,
                [EffectNames.CodeTemplate] = PermissionLevel.Allow,
                [EffectNames.CodeWrite] = PermissionLevel.Ask,
                [EffectNames.Compile] = PermissionLevel.Allow,
                [EffectNames.PackageModify] = PermissionLevel.Ask,
            }, destructiveRequiresAsk);

        private sealed class MapTypeProbe : IStaticTypeProbe
        {
            private readonly HashSet<string> _known;

            internal MapTypeProbe(params string[] known) =>
                _known = new HashSet<string>(known, StringComparer.Ordinal);

            public bool TypeExists(string typeName) => _known.Contains(typeName);
        }

        private class StubOperation : IOperation
        {
            internal StubOperation(OperationDescriptor descriptor) => Descriptor = descriptor;

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

        // The stub IEffectResolver of SPEC §C4 test 2: an AssetRef-shaped
        // "target" constant makes the actual effect asset.modify, anything
        // else keeps scene.modify.
        private sealed class ResolverOperation : StubOperation, IEffectResolver
        {
            internal ResolverOperation(OperationDescriptor descriptor) : base(descriptor)
            {
            }

            public IReadOnlyList<string> ResolveEffects(StaticContext context)
            {
                if (context.Parameters.TryGetValue("target", out var input) &&
                    input.IsPresent &&
                    input.Value!.ToToken() is JObject target &&
                    string.Equals(
                        (string?)target["kind"], "asset", StringComparison.Ordinal))
                {
                    return new[] { EffectNames.AssetModify };
                }
                return new[] { EffectNames.SceneModify };
            }
        }

        // An operation whose resolver violates the contract by throwing.
        private sealed class ThrowingResolverOperation : StubOperation, IEffectResolver
        {
            internal ThrowingResolverOperation(OperationDescriptor descriptor) : base(descriptor)
            {
            }

            public IReadOnlyList<string> ResolveEffects(StaticContext context) =>
                throw new InvalidOperationException("token 'sk-live-0042' rejected");
        }

        // A stub resolver that reads the statically resolved type of its
        // 'target' input (SPEC §C4, §0.6 SG-06 — 2026-11-06): an AssetRef
        // supply means asset.modify, anything else keeps scene.modify.
        private sealed class TypeDrivenResolverOperation : StubOperation, IEffectResolver
        {
            internal TypeDrivenResolverOperation(OperationDescriptor descriptor) : base(descriptor)
            {
            }

            public IReadOnlyList<string> ResolveEffects(StaticContext context)
            {
                var type = context.StaticTypeOf("target");
                return type is not null &&
                    string.Equals(type.Name, "unity.AssetRef", StringComparison.Ordinal)
                        ? new[] { EffectNames.AssetModify }
                        : new[] { EffectNames.SceneModify };
            }
        }

        // A resolver that records the StaticTypeOf answer of every declared
        // input port, so the tests can inspect each supply kind's type.
        private sealed class StaticTypeSpyOperation : StubOperation, IEffectResolver
        {
            internal readonly Dictionary<string, TypeRef?> Captured =
                new Dictionary<string, TypeRef?>(StringComparer.Ordinal);

            internal StaticTypeSpyOperation(OperationDescriptor descriptor) : base(descriptor)
            {
            }

            public IReadOnlyList<string> ResolveEffects(StaticContext context)
            {
                foreach (var port in context.Parameters.Keys)
                {
                    Captured[port] = context.StaticTypeOf(port);
                }
                return new[] { EffectNames.SceneModify };
            }
        }

        // A spy phase appended after the real D6 phase so the tests can read
        // the context slots the Build Plan stage will consume.
        private sealed class CapturePhase : IValidationPhase
        {
            internal ValidationContext? Context;
            public ValidationPhaseKind Kind => ValidationPhaseKind.D8;
            public void Run(ValidationContext context) => Context = context;
        }

        // ── Descriptors ─────────────────────────────────────────────────────────

        private static OperationDescriptor StartDescriptor() =>
            Op.Define("core.start", 1).Summary("Workflow entry point.").Build();

        private static OperationDescriptor DeleteDescriptor() =>
            Op.Define("example.asset_delete", 1)
                .Summary("Delete an asset.")
                .Effects(EffectNames.AssetDelete)
                .Build();

        private static OperationDescriptor MoveDescriptor() =>
            Op.Define("example.asset_move", 1)
                .Summary("Move an asset.")
                .Effects(EffectNames.AssetMove)
                .Build();

        private static OperationDescriptor WriteCodeDescriptor() =>
            Op.Define("example.write_code", 1)
                .Summary("Write arbitrary C# code.")
                .Effects(EffectNames.CodeWrite)
                .Build();

        private static OperationDescriptor TemplateDescriptor() =>
            Op.Define("example.write_template", 1)
                .Summary("Generate C# from a template.")
                .Effects(EffectNames.CodeTemplate)
                .Build();

        private static OperationDescriptor PackageModifyDescriptor() =>
            Op.Define("example.package_modify", 1)
                .Summary("Modify the package manifest.")
                .Effects(EffectNames.PackageModify)
                .Build();

        private static OperationDescriptor CompileDescriptor() =>
            Op.Define("unity.script.compile", 1)
                .Summary("Wait for script compilation.")
                .Effects(EffectNames.Compile)
                .Build();

        private static OperationDescriptor OpenContentsDescriptor() =>
            Op.Define("unity.prefab.open_contents", 1)
                .Summary("Open prefab contents for editing.")
                .Effects(EffectNames.Read)
                .Build();

        private static OperationDescriptor SaveContentsDescriptor() =>
            Op.Define("unity.prefab.save_contents", 1)
                .Summary("Save prefab contents.")
                .Effects(EffectNames.AssetModify)
                .Build();

        private static OperationDescriptor WritePathDescriptor() =>
            Op.Define("example.write_path", 1)
                .Summary("Write an asset at a path.")
                .Input("path", "unity.AssetPath", required: true)
                .Effects(EffectNames.AssetCreate)
                .Requires("path_writable", "path")
                .Build();

        // The codec-free counterpart: a core.String path port reaches the D6
        // allowed-paths check with rooted spellings an AssetPath codec would
        // already reject upstream (custom Operations may declare plain
        // string paths).
        private static OperationDescriptor WriteStringPathDescriptor() =>
            Op.Define("example.write_string_path", 1)
                .Summary("Write a file at a plain string path.")
                .Input("path", "core.String", required: true)
                .Effects(EffectNames.AssetCreate)
                .Requires("path_writable", "path")
                .Build();

        private static OperationDescriptor DefaultPathDescriptor() =>
            Op.Define("example.default_path", 1)
                .Summary("Write an asset at a defaulted path.")
                .Input("path", "unity.AssetPath", required: false,
                    @default: Json("\"Packages/outside/default.txt\""))
                .Effects(EffectNames.AssetCreate)
                .Requires("path_writable", "path")
                .Build();

        private static OperationDescriptor PathSourceDescriptor() =>
            Op.Define("example.path_source", 1)
                .Summary("Produce an asset path.")
                .Output("path", "unity.AssetPath")
                .Build();

        private static OperationDescriptor TypeSourceDescriptor() =>
            Op.Define("example.type_source", 1)
                .Summary("Produce a type name.")
                .Output("typeName", "unity.TypeName")
                .Build();

        private static OperationDescriptor UseTypeDescriptor() =>
            Op.Define("example.use_type", 1)
                .Summary("Use a component type.")
                .Input("componentType", "unity.TypeName", required: true)
                .Effects(EffectNames.SceneModify)
                .Requires("type_exists", "componentType")
                .Build();

        private static OperationDescriptor PropertySetDescriptor() =>
            Op.Define("example.property_set", 1)
                .Summary("Set a property on a target.")
                .Input("target", "core.Json", required: true)
                .Input("value", "core.Json", required: false)
                .Effects(EffectNames.SceneModify)
                .Build();

        private static OperationDescriptor DestructiveDescriptor() =>
            Op.Define("example.destructive_edit", 1)
                .Summary("Destructive asset edit.")
                .Effects(EffectNames.AssetModify)
                .Undo(UndoSupport.Partial)
                .Build()
                with { Destructive = true };

        private static OperationDescriptor PrefabSourceDescriptor() =>
            Op.Define("example.prefab_source", 1)
                .Summary("Produce a prefab asset reference.")
                .Output("prefab", "unity.PrefabAssetRef")
                .Build();

        private static OperationDescriptor GameObjectSinkDescriptor() =>
            Op.Define("example.go_sink", 1)
                .Summary("Consume a GameObject reference.")
                .Input("target", "unity.GameObjectRef", required: true)
                .Effects(EffectNames.SceneModify)
                .Build();

        private static OperationDescriptor AssetSourceDescriptor() =>
            Op.Define("example.asset_source", 1)
                .Summary("Produce an asset reference.")
                .Output("asset", "unity.AssetRef<unity.Texture2D>")
                .Build();

        private static OperationDescriptor GameObjectSourceDescriptor() =>
            Op.Define("example.go_source", 1)
                .Summary("Produce a GameObject reference.")
                .Output("gameObject", "unity.GameObjectRef")
                .Build();

        private static OperationDescriptor TypedSetDescriptor() =>
            Op.Define("example.typed_set", 1)
                .Summary("Set a property on a typed target.")
                .Input("target", "core.Json", required: true)
                .Effects(EffectNames.SceneModify)
                .Build();

        private static OperationDescriptor TypedSinkDescriptor() =>
            Op.Define("example.typed_sink", 1)
                .Summary("Consume inputs of every supply kind.")
                .Input("target", "core.Json", required: true)
                .Input("bound", "core.Json", required: true)
                .Input("constant", "core.String", required: true)
                .Input("defaulted", "core.String", required: false,
                    @default: Json("\"fallback\""))
                .Input("optional", "core.String", required: false)
                .Effects(EffectNames.SceneModify)
                .Build();

        // ── Snapshot / document helpers ─────────────────────────────────────────

        private static JsonValue Json(string text) => JsonValue.Parse(text).Value!;

        private static OperationCandidate Candidate(IOperation operation) =>
            new OperationCandidate(
                operation.Descriptor,
                OperationOrigin.Standard,
                "Acme.Editor",
                "Assets/Acme/Operation.cs",
                new string('a', 64),
                HasPollImplementation: true,
                Implementation: operation);

        private static RegistrySnapshot Snapshot(params OperationCandidate[] candidates) =>
            RegistrySnapshotBuilder.Build(candidates, s_types);

        private static RegistrySnapshot Snapshot(params OperationDescriptor[] descriptors) =>
            Snapshot(descriptors.Select(d => Candidate(new StubOperation(d))).ToArray());

        private static NodeData Node(
            string id, string operation, int version = 1,
            IDictionary<string, JToken>? parameters = null) =>
            new NodeData { Id = id, Operation = operation, Version = version, Parameters = parameters };

        private static WorkflowDocumentData LinearData(params NodeData[] nodes)
        {
            var edges = new List<ControlEdge>();
            for (var i = 0; i + 1 < nodes.Length; i++)
            {
                edges.Add(new ControlEdge(nodes[i].Id!, nodes[i + 1].Id!));
            }
            return new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.permission-validation",
                Nodes = new List<NodeData>(nodes),
                ControlEdges = edges,
            };
        }

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        private static ValidationRequest Request(
            WorkflowDocument document, ActorKind actor = ActorKind.Agent) =>
            new ValidationRequest(document, null, ValidationMode.Static, actor);

        private static StaticContext StaticFactory(
            string nodeId, IReadOnlyDictionary<string, InputValue> parameters) =>
            new OperationContractFixtures.Static(nodeId, parameters);

        // The production constructor's pipeline — real D2–D6 — plus a spy
        // that exposes the context for the approvals assertions.
        private static WorkflowCompiler Pipeline(
            RegistrySnapshot snapshot,
            CapturePhase capture,
            Func<ActorKind, IPermissionPolicy>? policyFor = null,
            IStaticTypeProbe? typeProbe = null,
            IReadOnlyList<string>? allowedPaths = null) =>
            new WorkflowCompiler(
                new IValidationPhase[]
                {
                    new StructuralValidationPhase(),
                    new OperationValidationPhase(snapshot, StaticFactory),
                    new DataBindingValidationPhase(snapshot, snapshot.Types),
                    new GraphValidationPhase(),
                    new PermissionValidationPhase(
                        snapshot,
                        StaticFactory,
                        policyFor ?? PermissionValidationPhase.DefaultPolicyFor,
                        typeProbe ?? new MapTypeProbe(),
                        allowedPaths ?? PermissionValidationPhase.DefaultAllowedPaths),
                    capture,
                });

        private static Diagnostic[] Errors(ValidationReport report, string code) =>
            report.Errors.Where(d => d.Code == code).ToArray();

        private static Diagnostic[] Warnings(ValidationReport report, string code) =>
            report.Warnings.Where(d => d.Code == code).ToArray();

        // ── SPEC §D6 item 1 — effect policy and approvals ──────────────────────

        // SPEC §D6 test 1: asset.delete validates into the Plan approvals.
        [Test]
        public void AssetDeleteLandsOnApprovalsForAgent()
        {
            var snapshot = Snapshot(StartDescriptor(), DeleteDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("del", "example.asset_delete")));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsTrue(report.Ok, "ask effects approve, they do not fail");
            Assert.IsEmpty(report.Errors);
            Assert.That(
                capture.Context!.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("del", EffectNames.AssetDelete) }));
        }

        // SPEC §D6 test 3 / §C4 test 3 / §H4 test 3: code.write configured to
        // allow is still at least ask for the Agent.
        [Test]
        public void AgentCodeWriteConfiguredAllowStillLandsOnApprovals()
        {
            var snapshot = Snapshot(StartDescriptor(), WriteCodeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("write", "example.write_code")));
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.CodeWrite] = PermissionLevel.Allow,
            });

            var report = Pipeline(snapshot, capture, _ => policy).Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.That(
                capture.Context!.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("write", EffectNames.CodeWrite) }));
        }

        [Test]
        public void DeniedEffectProducesOwPerm001OnTheNode()
        {
            var snapshot = Snapshot(StartDescriptor(), DeleteDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("del", "example.asset_delete")));
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.AssetDelete] = PermissionLevel.Deny,
            });

            var report = Pipeline(snapshot, capture, _ => policy).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_PERM_001).Single();
            Assert.AreEqual("del", error.NodeId);
            Assert.AreEqual("$.nodes[1]", error.JsonPath);
            Assert.AreEqual(EffectNames.AssetDelete, error.Hint);
        }

        // SPEC §H4 item 2: a destructive operation without Undo=Full is at
        // least ask even when its effect is configured allow.
        [Test]
        public void DestructiveWithoutFullUndoRequiresApproval()
        {
            var snapshot = Snapshot(StartDescriptor(), DestructiveDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("edit", "example.destructive_edit")));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.That(
                capture.Context!.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("edit", EffectNames.AssetModify) }));
        }

        // The actor selects the policy: the human default allows asset.move
        // where the Agent default asks.
        [Test]
        public void ActorSelectsThePolicy()
        {
            var snapshot = Snapshot(StartDescriptor(), MoveDescriptor());
            var agentCapture = new CapturePhase();
            var humanCapture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("move", "example.asset_move")));

            Pipeline(snapshot, agentCapture).Validate(Request(document, ActorKind.Agent));
            Pipeline(snapshot, humanCapture).Validate(Request(document, ActorKind.Human));

            Assert.That(
                agentCapture.Context!.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("move", EffectNames.AssetMove) }));
            Assert.IsEmpty(humanCapture.Context!.Approvals!);
        }

        [Test]
        public void DefaultPolicyMatchesTheC4Table()
        {
            var agent = DefaultPermissionPolicy.Agent;
            foreach (var effect in EffectNames.All)
            {
                var expected =
                    effect == EffectNames.AssetMove || effect == EffectNames.AssetDelete ||
                    effect == EffectNames.CodeWrite || effect == EffectNames.PackageModify
                        ? PermissionLevel.Ask
                        : PermissionLevel.Allow;
                Assert.AreEqual(expected, agent.LevelFor(effect), $"agent {effect}");
            }

            var human = DefaultPermissionPolicy.Human;
            foreach (var effect in EffectNames.All)
            {
                var expected =
                    effect == EffectNames.AssetDelete || effect == EffectNames.CodeWrite ||
                    effect == EffectNames.PackageModify
                        ? PermissionLevel.Ask
                        : PermissionLevel.Allow;
                Assert.AreEqual(expected, human.LevelFor(effect), $"human {effect}");
            }
        }

        // ── SPEC §C4 test 2 mechanism — IEffectResolver actual effects ─────────

        // A stub resolver maps the AssetRef target to asset.modify; D6 then
        // reports asset.modify, not the declared scene.modify.
        [Test]
        public void EffectResolverActualEffectsDriveTheDecision()
        {
            var snapshot = Snapshot(
                Candidate(new StubOperation(StartDescriptor())),
                Candidate(new ResolverOperation(PropertySetDescriptor())));
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("set", "example.property_set", parameters: new Dictionary<string, JToken>
                {
                    ["target"] = JToken.Parse("{\"kind\":\"asset\",\"guid\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}"),
                })));
            // Deny the declared effect so only the resolved one can pass.
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.SceneModify] = PermissionLevel.Deny,
                [EffectNames.AssetModify] = PermissionLevel.Ask,
            });

            var report = Pipeline(snapshot, capture, _ => policy).Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.That(
                capture.Context!.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("set", EffectNames.AssetModify) }));
        }

        // Negative control: the same node with a scene-shaped target keeps the
        // declared scene.modify — denied under the fixture policy.
        [Test]
        public void EffectResolverSceneTargetKeepsDeclaredEffect()
        {
            var snapshot = Snapshot(
                Candidate(new StubOperation(StartDescriptor())),
                Candidate(new ResolverOperation(PropertySetDescriptor())));
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("set", "example.property_set", parameters: new Dictionary<string, JToken>
                {
                    ["target"] = JToken.Parse("{\"kind\":\"scene\"}"),
                })));
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.SceneModify] = PermissionLevel.Deny,
                [EffectNames.AssetModify] = PermissionLevel.Ask,
            });

            var report = Pipeline(snapshot, capture, _ => policy).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_PERM_001).Single();
            Assert.AreEqual(EffectNames.SceneModify, error.Hint);
            Assert.IsEmpty(capture.Context!.Approvals!);
        }

        // An implementation exception surfaces as OW-RUN-006 without the
        // exception text (SPEC §0.6 SG-06).
        [Test]
        public void ResolverExceptionIsOwRun006WithoutText()
        {
            var snapshot = Snapshot(
                Candidate(new StubOperation(StartDescriptor())),
                Candidate(new ThrowingResolverOperation(PropertySetDescriptor())));
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("set", "example.property_set", parameters: new Dictionary<string, JToken>
                {
                    ["target"] = JToken.Parse("{\"kind\":\"asset\"}"),
                })));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_RUN_006).Single();
            Assert.AreEqual("set", error.NodeId);
            Assert.IsFalse(error.Message.Contains("sk-live-0042"), "exception text must not leak");
        }

        // SPEC §C4 / §0.6 SG-06 (2026-11-06): the resolver reads the connected
        // output's type for an edge-fed port — an AssetRef supply resolves to
        // asset.modify, which the policy denies (OW-PERM-001).
        [Test]
        public void EdgeFedAssetRefDrivesResolverToAssetModify()
        {
            var snapshot = Snapshot(
                Candidate(new StubOperation(StartDescriptor())),
                Candidate(new StubOperation(AssetSourceDescriptor())),
                Candidate(new TypeDrivenResolverOperation(TypedSetDescriptor())));
            var capture = new CapturePhase();
            var data = LinearData(
                Node("start", "core.start"),
                Node("source", "example.asset_source"),
                Node("set", "example.typed_set"));
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("source", "asset"), new PortRef("set", "target")),
            };
            var document = MustCreate(data);
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.AssetModify] = PermissionLevel.Deny,
                [EffectNames.SceneModify] = PermissionLevel.Allow,
            });

            var report = Pipeline(snapshot, capture, _ => policy).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_PERM_001).Single();
            Assert.AreEqual("set", error.NodeId);
            Assert.AreEqual(EffectNames.AssetModify, error.Hint);
        }

        // The GameObjectRef-typed counterpart: the same resolver keeps the
        // declared scene.modify, which the policy allows — no denial.
        [Test]
        public void EdgeFedGameObjectRefKeepsSceneModify()
        {
            var snapshot = Snapshot(
                Candidate(new StubOperation(StartDescriptor())),
                Candidate(new StubOperation(GameObjectSourceDescriptor())),
                Candidate(new TypeDrivenResolverOperation(TypedSetDescriptor())));
            var capture = new CapturePhase();
            var data = LinearData(
                Node("start", "core.start"),
                Node("source", "example.go_source"),
                Node("set", "example.typed_set"));
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(
                    new PortRef("source", "gameObject"), new PortRef("set", "target")),
            };
            var document = MustCreate(data);
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.AssetModify] = PermissionLevel.Deny,
                [EffectNames.SceneModify] = PermissionLevel.Allow,
            });

            var report = Pipeline(snapshot, capture, _ => policy).Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_PERM_001));
            Assert.IsEmpty(capture.Context!.Approvals!);
        }

        // SPEC §0.6 SG-06 (2026-11-06): StaticTypeOf answers each supply kind —
        // the data edge's connected output type from D4, the bound workflow
        // input's declared type, the declared type for a constant or a
        // Descriptor default, and null for a Missing port.
        [Test]
        public void StaticTypeOfAnswersEverySupplyKind()
        {
            var spy = new StaticTypeSpyOperation(TypedSinkDescriptor());
            var snapshot = Snapshot(
                Candidate(new StubOperation(StartDescriptor())),
                Candidate(new StubOperation(AssetSourceDescriptor())),
                Candidate(spy));
            var capture = new CapturePhase();
            var data = LinearData(
                Node("start", "core.start"),
                Node("source", "example.asset_source"),
                Node("set", "example.typed_sink", parameters: new Dictionary<string, JToken>
                {
                    ["constant"] = JToken.Parse("\"fixed\""),
                }));
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("thing", "unity.GameObjectRef", true),
            };
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("source", "asset"), new PortRef("set", "target")),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("thing", new PortRef("set", "bound")),
            };
            var document = MustCreate(data);

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.AreEqual(
                "unity.AssetRef<unity.Texture2D>", spy.Captured["target"]!.ToString());
            Assert.AreEqual("unity.GameObjectRef", spy.Captured["bound"]!.ToString());
            Assert.AreEqual("core.String", spy.Captured["constant"]!.ToString());
            Assert.AreEqual("core.String", spy.Captured["defaulted"]!.ToString());
            Assert.IsNull(spy.Captured["optional"]);
        }

        // ── SPEC §D6 item 2 — constant paths vs allowed paths ──────────────────

        [Test]
        public void ConstantPathInsideAllowedPathsPasses()
        {
            var snapshot = Snapshot(StartDescriptor(), WritePathDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("write", "example.write_path", parameters: new Dictionary<string, JToken>
                {
                    ["path"] = JToken.Parse("\"Assets/Scripts/ok.txt\""),
                })));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsTrue(report.Ok, "inside Assets/ is allowed");
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_PERM_002));
        }

        [Test]
        public void ConstantPathOutsideAllowedPathsIsOwPerm002()
        {
            var snapshot = Snapshot(StartDescriptor(), WritePathDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("write", "example.write_path", parameters: new Dictionary<string, JToken>
                {
                    ["path"] = JToken.Parse("\"Packages/com.example/asset.txt\""),
                })));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_PERM_002).Single();
            Assert.AreEqual("write", error.NodeId);
            Assert.AreEqual("path", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.path", error.JsonPath);
        }

        [Test]
        public void ConstantPathEscapingWithDotDotIsOwPerm002()
        {
            var snapshot = Snapshot(StartDescriptor(), WritePathDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("write", "example.write_path", parameters: new Dictionary<string, JToken>
                {
                    ["path"] = JToken.Parse("\"Assets/../Library/secret.txt\""),
                })));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_PERM_002).Single();
            Assert.AreEqual("write", error.NodeId);
        }

        // SPEC §D6 item 2: a rooted or absolute constant path is outside the
        // allowed paths however it is spelled — leading '/', '\', Windows
        // drive, UNC, device and '~' forms would otherwise collapse to a
        // relative "Assets/..." spelling that slips the prefix check.
        [TestCase("/Assets/out.txt")]
        [TestCase("\\Assets\\out.txt")]
        [TestCase("//server/share/out.txt")]
        [TestCase("\\\\server\\share\\out.txt")]
        [TestCase("\\?\\C:\\Assets\\out.txt")]
        [TestCase("\\.\\device\\out.txt")]
        [TestCase("C:out.txt")]
        [TestCase("C:/Assets/out.txt")]
        [TestCase("C:\\Assets\\out.txt")]
        [TestCase("~/Assets/out.txt")]
        [TestCase("~other/Assets/out.txt")]
        public void RootedConstantPathIsOwPerm002(string path)
        {
            var snapshot = Snapshot(StartDescriptor(), WriteStringPathDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("write", "example.write_string_path", parameters: new Dictionary<string, JToken>
                {
                    ["path"] = new JValue(path),
                })));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_PERM_002).Single();
            Assert.AreEqual("write", error.NodeId);
        }

        // The relative counterpart stays allowed — segment collapsing still
        // runs for non-rooted paths.
        [TestCase("Assets/x.txt")]
        [TestCase("Assets/dir/./x.txt")]
        public void RelativeConstantPathStillPasses(string path)
        {
            var snapshot = Snapshot(StartDescriptor(), WriteStringPathDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("write", "example.write_string_path", parameters: new Dictionary<string, JToken>
                {
                    ["path"] = new JValue(path),
                })));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_PERM_002));
        }

        [Test]
        public void ConstantDefaultPathOutsideAllowedPathsIsOwPerm002()
        {
            var snapshot = Snapshot(StartDescriptor(), DefaultPathDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("write", "example.default_path")));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_PERM_002).Single();
            Assert.AreEqual("write", error.NodeId);
            Assert.AreEqual("$.nodes[1]", error.JsonPath);
        }

        // SPEC §H4 item 5 (T-W3.4-03): writes to the OpWeave settings file
        // are never part of the permitted paths — the constant path is
        // OW-PERM-002 even when a widened allowedPaths covers it, and only
        // that exact file is protected.
        [Test]
        public void SettingsFileConstantPathIsOwPerm002EvenWhenAllowed()
        {
            var snapshot = Snapshot(StartDescriptor(), WriteStringPathDescriptor());
            var capture = new CapturePhase();
            // Widened past the settings folder to the project root: '.' is the
            // match-all prefix, so nothing here can be refused for sitting
            // outside the allowed paths — only §H4 item 5 refuses it.
            var allowedPaths = new List<string> { "Assets/", "ProjectSettings/", "." };

            // Every spelling that opens the same file on Windows: the literal,
            // another case, a '.' segment, a backslash, a trailing '.' or space
            // (Windows strips both at the end of a segment), a folder segment
            // ending in '.', the NTFS stream suffix and an 8.3 alias — of the
            // file, and of the folder too (Windows resolves PROJEC~1 to
            // ProjectSettings). The aliases cover both shapes Windows derives:
            // the classic one truncated to fit eight characters with a one-,
            // two- or three-digit tail, and the hashed one a directory uses
            // once too many names collide.
            foreach (var path in new[]
            {
                "ProjectSettings/OpWeaveSettings.json",
                "projectsettings/opweavesettings.json",
                "ProjectSettings/./OpWeaveSettings.json",
                "ProjectSettings\\OpWeaveSettings.json",
                "ProjectSettings/OpWeaveSettings.json.",
                "ProjectSettings/OpWeaveSettings.json ",
                "ProjectSettings./OpWeaveSettings.json",
                "ProjectSettings/OpWeaveSettings.json::$DATA",
                "projectsettings\\OpWeaveSettings.JSON . ",
                "ProjectSettings/OPWEAV~1.JSO",
                "ProjectSettings/OPWEA~10.JSO",
                "ProjectSettings/OP1F2A~1.JSO",
                "PROJEC~1/OpWeaveSettings.json",
                "PROJE~10/OpWeaveSettings.json",
                "PROJ~100/OpWeaveSettings.json",
                "PR1A2B~1/OpWeaveSettings.json",
                "PROJEC~1/OPWEAV~1.JSO",
                "projec~1\\OpWeaveSettings.json.",
            })
            {
                var document = MustCreate(LinearData(
                    Node("start", "core.start"),
                    Node("write", "example.write_string_path", parameters: new Dictionary<string, JToken>
                    {
                        ["path"] = new JValue(path),
                    })));

                var report = Pipeline(snapshot, capture, allowedPaths: allowedPaths)
                    .Validate(Request(document));

                Assert.IsFalse(report.Ok, path);
                var error = Errors(report, ErrorCodes.OW_PERM_002).Single();
                Assert.AreEqual("write", error.NodeId);
                Assert.AreEqual("path", error.Port);
            }
        }

        [Test]
        public void NeighbouringProjectSettingsPathInsideAllowedStillPasses()
        {
            var snapshot = Snapshot(StartDescriptor(), WriteStringPathDescriptor());
            var capture = new CapturePhase();
            // The protection is the settings file itself — up to its Windows
            // canonical spellings and its 8.3 aliases — never a prefix of the
            // folder: a neighbour and a longer file name keep their normal
            // verdicts, so does a name that only looks short-name-shaped, and
            // so does an unrelated '~<digits>' directory. Windows derives an
            // alias only from a name that could become the guarded one, so
            // 'Data~1' and 'Assets~1' are the project's own folders, and inside
            // ProjectSettings/ neither is 'OpWeaveSettings~1.json' — a longer
            // name no eight-character alias of the settings file can spell.
            foreach (var path in new[]
            {
                "ProjectSettings/OtherSettings.json",
                "ProjectSettings/OpWeaveSettings.jsonx",
                "ProjectSettings/OpWeaveSettings~1.json",
                "ProjectSettings/Foo~1.txt",
                "Assets/Foo~1.txt",
                "Data~1/output.json",
                "Assets~1/x.txt",
            })
            {
                var document = MustCreate(LinearData(
                    Node("start", "core.start"),
                    Node("write", "example.write_string_path", parameters: new Dictionary<string, JToken>
                    {
                        ["path"] = new JValue(path),
                    })));

                var report = Pipeline(
                        snapshot, capture,
                        allowedPaths: new List<string>
                        {
                            "Assets/", "ProjectSettings/", "Data~1/", "Assets~1/",
                        })
                    .Validate(Request(document));

                Assert.IsTrue(report.Ok, path);
                Assert.IsEmpty(Errors(report, ErrorCodes.OW_PERM_002), path);
            }
        }

        // SPEC §D6 item 2: an edge-supplied path defers to the runtime check.
        [Test]
        public void EdgeSuppliedPathDefersToRuntime()
        {
            var snapshot = Snapshot(
                StartDescriptor(), PathSourceDescriptor(), WritePathDescriptor());
            var capture = new CapturePhase();
            var data = LinearData(
                Node("start", "core.start"),
                Node("source", "example.path_source"),
                Node("write", "example.write_path"));
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("source", "path"), new PortRef("write", "path")),
            };
            var document = MustCreate(data);

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsTrue(report.Ok, "edge-fed paths are a runtime check");
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_PERM_002));
        }

        // ── SPEC §D6 items 4 and 6 — type_exists and the compile wait ──────────

        [Test]
        public void TypeExistsProbeMissWithoutCodeGenerationIsOwUnity006()
        {
            var snapshot = Snapshot(StartDescriptor(), UseTypeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("use", "example.use_type", parameters: new Dictionary<string, JToken>
                {
                    ["componentType"] = JToken.Parse("\"Acme.DoesNotExist\""),
                })));

            var report = Pipeline(
                snapshot, capture, typeProbe: new MapTypeProbe("UnityEngine.Animator"))
                .Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_UNITY_006).Single();
            Assert.AreEqual("use", error.NodeId);
            Assert.AreEqual("componentType", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.componentType", error.JsonPath);
            Assert.IsEmpty(Warnings(report, ErrorCodes.OW_VAL_015));
        }

        // The D3-STATIC-02 precedent (T-W1.6-02) applied to the §D6 item-4
        // probe: the snapshot-backed compiler and the D6 phase reject a null
        // probe at construction rather than silently skipping type_exists.
        [Test]
        public void NullTypeProbeIsRejectedAtConstruction()
        {
            var snapshot = Snapshot(StartDescriptor());

            var error = Assert.Throws<ArgumentNullException>(
                () => new WorkflowCompiler(
                    snapshot, StaticFactory, null!, new PlanProvenanceFixtures.Stub()));
            Assert.AreEqual("typeProbe", error!.ParamName);

            error = Assert.Throws<ArgumentNullException>(
                () => new PermissionValidationPhase(
                    snapshot,
                    StaticFactory,
                    PermissionValidationPhase.DefaultPolicyFor,
                    null!,
                    PermissionValidationPhase.DefaultAllowedPaths));
            Assert.AreEqual("typeProbe", error!.ParamName);
        }

        // SPEC §D6 item 4 through the public snapshot-backed constructor: a
        // constant type the probe cannot find, with no earlier generator, is
        // OW-UNITY-006.
        [Test]
        public void MissingTypeThroughPublicConstructorIsOwUnity006()
        {
            var snapshot = Snapshot(StartDescriptor(), UseTypeDescriptor());
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("use", "example.use_type", parameters: new Dictionary<string, JToken>
                {
                    ["componentType"] = JToken.Parse("\"Acme.DoesNotExist\""),
                })));

            var report = new WorkflowCompiler(
                    snapshot, StaticFactory, new MapTypeProbe("UnityEngine.Animator"),
                    new PlanProvenanceFixtures.Stub())
                .Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_UNITY_006).Single();
            Assert.AreEqual("use", error.NodeId);
            Assert.AreEqual("componentType", error.Port);
        }

        // SPEC §D6 items 4 and 6 (2026-11-06): a missing type after
        // code.template without a compile wait meets both warning conditions —
        // OW-VAL-015 (the generated code may define the type) and OW-VAL-013
        // (no unity.script.compile before the type is used) — independently.
        [Test]
        public void TypeExistsProbeMissAfterTemplateWithoutCompileWarnsVal015AndVal013()
        {
            var snapshot = Snapshot(
                StartDescriptor(), TemplateDescriptor(), UseTypeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("gen", "example.write_template"),
                Node("use", "example.use_type", parameters: new Dictionary<string, JToken>
                {
                    ["componentType"] = JToken.Parse("\"Acme.Generated\""),
                })));

            var report = Pipeline(
                snapshot, capture, typeProbe: new MapTypeProbe())
                .Validate(Request(document));

            Assert.IsTrue(report.Ok, "the missing type may come from generated code");
            Assert.IsEmpty(report.Errors);
            var val015 = Warnings(report, ErrorCodes.OW_VAL_015).Single();
            Assert.AreEqual("use", val015.NodeId);
            Assert.AreEqual("componentType", val015.Port);
            var val013 = Warnings(report, ErrorCodes.OW_VAL_013).Single();
            Assert.AreEqual("use", val013.NodeId);
        }

        // SPEC §D6 items 4 and 6 (2026-11-06): the same pair fires after
        // code.write with no compile wait.
        [Test]
        public void TypeExistsProbeMissAfterCodeWriteWithoutCompileWarnsVal015AndVal013()
        {
            var snapshot = Snapshot(
                StartDescriptor(), WriteCodeDescriptor(), UseTypeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("gen", "example.write_code"),
                Node("use", "example.use_type", parameters: new Dictionary<string, JToken>
                {
                    ["componentType"] = JToken.Parse("\"Acme.Generated\""),
                })));

            var report = Pipeline(
                snapshot, capture, typeProbe: new MapTypeProbe())
                .Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(report.Errors);
            var val015 = Warnings(report, ErrorCodes.OW_VAL_015).Single();
            Assert.AreEqual("use", val015.NodeId);
            var val013 = Warnings(report, ErrorCodes.OW_VAL_013).Single();
            Assert.AreEqual("use", val013.NodeId);
        }

        // SPEC §D6 item 4: a missing type after code.template earns OW-VAL-015
        // even when unity.script.compile ran in between — item 6 is satisfied,
        // so OW-VAL-013 does not fire.
        [Test]
        public void TypeExistsProbeMissAfterTemplateAndCompileIsOnlyVal015()
        {
            var snapshot = Snapshot(
                StartDescriptor(), TemplateDescriptor(), CompileDescriptor(), UseTypeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("gen", "example.write_template"),
                Node("compile", "unity.script.compile"),
                Node("use", "example.use_type", parameters: new Dictionary<string, JToken>
                {
                    ["componentType"] = JToken.Parse("\"Acme.Generated\""),
                })));

            var report = Pipeline(
                snapshot, capture, typeProbe: new MapTypeProbe())
                .Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(report.Errors);
            var val015 = Warnings(report, ErrorCodes.OW_VAL_015).Single();
            Assert.AreEqual("use", val015.NodeId);
            Assert.IsEmpty(Warnings(report, ErrorCodes.OW_VAL_013));
        }

        // SPEC §D6 item 4: package.modify may also define the missing type —
        // OW-VAL-015 — but it is not code generation, so item 6 stays silent.
        [Test]
        public void TypeExistsProbeMissAfterPackageModifyIsOnlyVal015()
        {
            var snapshot = Snapshot(
                StartDescriptor(), PackageModifyDescriptor(), UseTypeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("gen", "example.package_modify"),
                Node("use", "example.use_type", parameters: new Dictionary<string, JToken>
                {
                    ["componentType"] = JToken.Parse("\"Acme.Packaged\""),
                })));

            var report = Pipeline(
                snapshot, capture, typeProbe: new MapTypeProbe())
                .Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(report.Errors);
            var val015 = Warnings(report, ErrorCodes.OW_VAL_015).Single();
            Assert.AreEqual("use", val015.NodeId);
            Assert.IsEmpty(Warnings(report, ErrorCodes.OW_VAL_013));
        }

        [Test]
        public void TypeExistsProbeHitPassesWithoutFindings()
        {
            var snapshot = Snapshot(StartDescriptor(), UseTypeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("use", "example.use_type", parameters: new Dictionary<string, JToken>
                {
                    ["componentType"] = JToken.Parse("\"UnityEngine.Animator\""),
                })));

            var report = Pipeline(
                snapshot, capture, typeProbe: new MapTypeProbe("UnityEngine.Animator"))
                .Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(report.Errors);
            Assert.IsEmpty(report.Warnings);
        }

        // SPEC §D6 item 6: type_exists after code generation without an
        // intervening unity.script.compile is OW-VAL-013 — even when the type
        // already resolves.
        [Test]
        public void TypeUseBeforeCompileAfterGenerationWarns()
        {
            var snapshot = Snapshot(
                StartDescriptor(), WriteCodeDescriptor(), UseTypeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("gen", "example.write_code"),
                Node("use", "example.use_type", parameters: new Dictionary<string, JToken>
                {
                    ["componentType"] = JToken.Parse("\"UnityEngine.Animator\""),
                })));

            var report = Pipeline(
                snapshot, capture, typeProbe: new MapTypeProbe("UnityEngine.Animator"))
                .Validate(Request(document));

            Assert.IsTrue(report.Ok);
            var warning = Warnings(report, ErrorCodes.OW_VAL_013).Single();
            Assert.AreEqual("use", warning.NodeId);
            Assert.AreEqual("$.nodes[2]", warning.JsonPath);
        }

        [Test]
        public void TypeUseAfterGenerationAndCompileDoesNotWarn()
        {
            var snapshot = Snapshot(
                StartDescriptor(), WriteCodeDescriptor(), CompileDescriptor(), UseTypeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("gen", "example.write_code"),
                Node("compile", "unity.script.compile"),
                Node("use", "example.use_type", parameters: new Dictionary<string, JToken>
                {
                    ["componentType"] = JToken.Parse("\"Acme.Generated\""),
                })));

            var report = Pipeline(
                snapshot, capture, typeProbe: new MapTypeProbe("Acme.Generated"))
                .Validate(Request(document));

            Assert.IsTrue(report.Ok);
            Assert.IsEmpty(Warnings(report, ErrorCodes.OW_VAL_013));
        }

        // An edge-supplied type name is not statically probeable; the
        // positional rule still applies.
        [Test]
        public void EdgeSuppliedTypeNameDefersTheProbe()
        {
            var snapshot = Snapshot(
                StartDescriptor(), TemplateDescriptor(), TypeSourceDescriptor(), UseTypeDescriptor());
            var capture = new CapturePhase();
            var data = LinearData(
                Node("start", "core.start"),
                Node("source", "example.type_source"),
                Node("use", "example.use_type"));
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("source", "typeName"), new PortRef("use", "componentType")),
            };
            var document = MustCreate(data);

            var report = Pipeline(
                snapshot, capture, typeProbe: new MapTypeProbe())
                .Validate(Request(document));

            Assert.IsEmpty(Errors(report, ErrorCodes.OW_UNITY_006),
                "a runtime-supplied type name is not probed statically");
        }

        // ── SPEC §D6 item 5 — no compile/code/package inside prefab contents ──

        [Test]
        public void CodeNodeBetweenOpenAndSaveContentsIsOwVal012()
        {
            var snapshot = Snapshot(
                StartDescriptor(), OpenContentsDescriptor(), WriteCodeDescriptor(),
                SaveContentsDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("open", "unity.prefab.open_contents"),
                Node("gen", "example.write_code"),
                Node("save", "unity.prefab.save_contents")));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_VAL_012).Single();
            Assert.AreEqual("gen", error.NodeId);
            Assert.AreEqual("$.nodes[2]", error.JsonPath);
        }

        [Test]
        public void CompileNodeBetweenOpenAndSaveContentsIsOwVal012()
        {
            var snapshot = Snapshot(
                StartDescriptor(), OpenContentsDescriptor(), CompileDescriptor(),
                SaveContentsDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("open", "unity.prefab.open_contents"),
                Node("compile", "unity.script.compile"),
                Node("save", "unity.prefab.save_contents")));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_VAL_012).Single();
            Assert.AreEqual("compile", error.NodeId);
        }

        [Test]
        public void CodeNodeOutsideTheContentsSessionIsAllowed()
        {
            var snapshot = Snapshot(
                StartDescriptor(), WriteCodeDescriptor(), OpenContentsDescriptor(),
                SaveContentsDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("gen", "example.write_code"),
                Node("open", "unity.prefab.open_contents"),
                Node("save", "unity.prefab.save_contents")));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsEmpty(Errors(report, ErrorCodes.OW_VAL_012));
        }

        // ── SPEC §D6 item 4 (R6) — generated Operations stay unknown ───────────

        // Code generated in the same Run cannot define the Operation a later
        // node uses: the id is simply absent from the snapshot and D3 reports
        // OW-OP-001.
        [Test]
        public void OperationDefinedByGeneratedCodeIsOwOp001()
        {
            var snapshot = Snapshot(StartDescriptor(), WriteCodeDescriptor());
            var capture = new CapturePhase();
            var document = MustCreate(LinearData(
                Node("start", "core.start"),
                Node("gen", "example.write_code"),
                Node("use", "acme.generated.operation")));

            var report = Pipeline(snapshot, capture).Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var error = Errors(report, ErrorCodes.OW_OP_001).Single();
            Assert.AreEqual("use", error.NodeId);
            Assert.AreEqual("$.nodes[2].operation", error.JsonPath);
        }

        // ── SPEC §D1 test — the integrated real pipeline ───────────────────────

        // local:T-W1.6-05:integrated-pipeline / SPEC §D1 test: a workflow with
        // an unknown Operation still reports another node's type error in the
        // same report, through the snapshot-backed production constructor's
        // real D2–D6 phases.
        [Test]
        public void UnknownOperationDoesNotHideAnotherNodesTypeError()
        {
            var snapshot = Snapshot(
                StartDescriptor(), PrefabSourceDescriptor(), GameObjectSinkDescriptor());
            var data = LinearData(
                Node("start", "core.start"),
                Node("source", "example.prefab_source"),
                Node("sink", "example.go_sink"),
                Node("ghost", "acme.unknown.operation"));
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(new PortRef("source", "prefab"), new PortRef("sink", "target")),
            };
            var document = MustCreate(data);

            var report = new WorkflowCompiler(
                    snapshot, StaticFactory, new MapTypeProbe(), new PlanProvenanceFixtures.Stub())
                .Validate(Request(document));

            Assert.IsFalse(report.Ok);
            var typeError = Errors(report, ErrorCodes.OW_TYPE_002).Single();
            Assert.AreEqual("sink", typeError.NodeId);
            Assert.AreEqual("$.dataEdges[0]", typeError.JsonPath);
            var opError = Errors(report, ErrorCodes.OW_OP_001).Single();
            Assert.AreEqual("ghost", opError.NodeId);
            Assert.AreEqual("$.nodes[3].operation", opError.JsonPath);
        }
    }
}
