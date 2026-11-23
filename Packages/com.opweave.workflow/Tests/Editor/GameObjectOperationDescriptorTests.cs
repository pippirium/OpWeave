#nullable enable

// T-W2.5-02 [Edit] tests: local:T-W2.5-02:descriptors-examples — the five
// §I.1 GameObject Operations discovered through TypeCache in the
// OpWeave.Operations assembly register through the real §C3 stages 1–8 as
// origin Standard with no OW-OP-003 (SPEC §C1 test 1), each Descriptor
// matches its SPEC §I.1 row, and every Example is a complete WF document
// the static Validator accepts with ok:true and no warnings.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.5-02:descriptors-examples — registration, §I.1 row
    /// conformance and Example validation for the GameObject Operations.
    /// </summary>
    internal sealed class GameObjectOperationDescriptorTests
    {
        private const string OperationsAssembly = "OpWeave.Operations";

        private static readonly string[] OperationIds =
        {
            GameObjectCreateOperation.OperationId,
            GameObjectFindOperation.OperationId,
            GameObjectDeleteOperation.OperationId,
            GameObjectSetParentOperation.OperationId,
            TransformSetOperation.OperationId,
        };

        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;

            internal StubStaticContext(
                string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }

            public override string NodeId { get; }

            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException("No ValidateStatic reads a port.");
        }

        // A StaticContext that answers StaticTypeOf from a fixed map — the
        // SG-06 answer a data edge or a workflow-input binding produces for
        // an otherwise Missing port.
        private sealed class TypedStubContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;
            private readonly IReadOnlyDictionary<string, TypeRef> _types;

            internal TypedStubContext(
                IReadOnlyDictionary<string, InputValue> parameters,
                IReadOnlyDictionary<string, TypeRef> types)
            {
                _parameters = parameters;
                _types = types;
            }

            public override string NodeId => "node";

            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException("No ValidateStatic reads a port.");

            public override TypeRef? StaticTypeOf(string port) =>
                port is not null && _types.TryGetValue(port, out var type) ? type : null;
        }

        private static IReadOnlyDictionary<string, InputValue> MissingPorts(
            params string[] ports)
        {
            var parameters = new Dictionary<string, InputValue>(StringComparer.Ordinal);
            foreach (var port in ports)
            {
                parameters[port] = InputValue.Missing;
            }
            return parameters;
        }

        private static IReadOnlyDictionary<string, TypeRef> Types(
            params (string Port, string Id)[] entries)
        {
            var types = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            foreach (var (port, id) in entries)
            {
                var parsed = TypeRef.Parse(id);
                Assert.That(parsed.Ok, Is.True, "fixture TypeRef must parse: " + id);
                types[port] = parsed.Value!;
            }
            return types;
        }

        private static RegistryBuildResult Build(out RegistryPublication publication)
        {
            publication = new RegistryPublication();
            var seams = new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => TypeCache
                    .GetTypesWithAttribute<OpWeaveOperationAttribute>()
                    .Where(type => string.Equals(
                        type.Assembly.GetName().Name, OperationsAssembly,
                        StringComparison.Ordinal)),
                ProviderTypes: () =>
                    TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: type => EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());
            return RegistryBuildHost.Rebuild(
                seams, RegistryOperationStage.Production(), publication);
        }

        private static WorkflowCompiler Compiler(RegistryBuildResult build) =>
            new WorkflowCompiler(
                build.Snapshot!,
                (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                new UnityTypeProbe(),
                UnityPlanProvenance.Capture(IntegrityLevel.Weak, build.Providers));

        private static RegisteredOperation Registered(RegistryBuildResult build, string id)
        {
            Assert.That(build.Snapshot, Is.Not.Null);
            var registered = build.Snapshot!.Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            return registered!;
        }

        [Test]
        public void GameObjectOperations_RegisterAsStandard_WithoutOwOp003()
        {
            var build = Build(out var publication);

            Assert.That(publication.Snapshot, Is.SameAs(build.Snapshot));
            Assert.That(build.OperationFailures, Is.Empty,
                () => string.Join("; ", build.OperationFailures.SelectMany(
                    f => f.Diagnostics.Select(
                        d => f.TypeFullName + ": " + d.Code + " " + d.Message))));
            Assert.That(build.Snapshot!.Failures, Is.Empty,
                () => string.Join("; ", build.Snapshot.Failures.SelectMany(
                    f => f.Diagnostics.Select(
                        d => f.OperationId + ": " + d.Code + " " + d.Message))));
            foreach (var id in OperationIds)
            {
                var registered = Registered(build, id);
                Assert.That(registered.Origin, Is.EqualTo(OperationOrigin.Standard), id);
                Assert.That(registered.AssemblyName, Is.EqualTo(OperationsAssembly), id);
            }
        }

        [Test]
        public void EveryDescriptor_MatchesItsSpecI1Row()
        {
            var build = Build(out _);

            var create = Registered(build, GameObjectCreateOperation.OperationId).Descriptor;
            AssertPorts(create,
                new[]
                {
                    ("name", "core.String", true),
                    ("scene", "unity.SceneRef", false),
                    ("parent", "unity.GameObjectRef", false),
                    ("primitive", "core.String", false),
                    ("position", "unity.Vector3", false),
                },
                new[] { ("gameObject", "unity.GameObjectRef", false) });
            Assert.That(create.SideEffects, Is.EqualTo(new[] { EffectNames.SceneModify }));
            Assert.That(create.Undo, Is.EqualTo(UndoSupport.Full));
            Assert.That(create.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(create.Destructive, Is.False);
            Assert.That(create.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(create.Requirements,
                Is.EqualTo(new[] { new Requirement("scene_loaded", "scene") }));

            var find = Registered(build, GameObjectFindOperation.OperationId).Descriptor;
            AssertPorts(find,
                new[]
                {
                    ("scene", "unity.SceneRef", false),
                    ("path", "unity.HierarchyPath", false),
                    ("name", "core.String", false),
                    ("tag", "core.String", false),
                },
                new[] { ("gameObject", "unity.GameObjectRef", false) });
            Assert.That(find.SideEffects, Is.EqualTo(new[] { EffectNames.Read }));
            Assert.That(find.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(find.Idempotency, Is.EqualTo(Idempotency.Idempotent));

            var delete = Registered(build, GameObjectDeleteOperation.OperationId).Descriptor;
            AssertPorts(delete,
                new[] { ("target", "unity.GameObjectRef", true) },
                Array.Empty<(string, string, bool)>());
            Assert.That(delete.SideEffects, Is.EqualTo(new[] { EffectNames.SceneModify }));
            Assert.That(delete.Undo, Is.EqualTo(UndoSupport.Full));
            Assert.That(delete.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(delete.Destructive, Is.True);

            var setParent = Registered(build, GameObjectSetParentOperation.OperationId).Descriptor;
            AssertPorts(setParent,
                new[]
                {
                    ("target", "unity.GameObjectRef", true),
                    ("parent", "unity.GameObjectRef", false),
                    ("worldPositionStays", "core.Bool", false),
                },
                new[] { ("gameObject", "unity.GameObjectRef", false) });
            Assert.That(setParent.SideEffects, Is.EqualTo(new[] { EffectNames.SceneModify }));
            Assert.That(setParent.Undo, Is.EqualTo(UndoSupport.Full));
            Assert.That(setParent.Idempotency, Is.EqualTo(Idempotency.Idempotent));

            var transform = Registered(build, TransformSetOperation.OperationId).Descriptor;
            AssertPorts(transform,
                new[]
                {
                    ("target", "unity.GameObjectRef", true),
                    ("position", "unity.Vector3", false),
                    ("rotation", "unity.Euler", false),
                    ("scale", "unity.Vector3", false),
                    ("space", "core.String", false),
                },
                Array.Empty<(string, string, bool)>());
            Assert.That(transform.SideEffects, Is.EqualTo(new[] { EffectNames.SceneModify }));
            Assert.That(transform.Undo, Is.EqualTo(UndoSupport.Full));
            Assert.That(transform.Idempotency, Is.EqualTo(Idempotency.Idempotent));
        }

        [Test]
        public void EveryDescriptor_DescribesItselfInEnglishAndJapanese()
        {
            var build = Build(out _);

            foreach (var id in OperationIds)
            {
                var descriptor = Registered(build, id).Descriptor;
                Assert.That(descriptor.DisplayName, Is.Not.Empty, id);
                Assert.That(descriptor.Summary, Is.Not.Empty, id);
                Assert.That(descriptor.Summary.Length,
                    Is.LessThanOrEqualTo(OperationDescriptorValidator.SummaryMaxLength), id);
                Assert.That(descriptor.Description, Is.Not.Empty, id);
                Assert.That(descriptor.Category, Is.EqualTo("GameObject"), id);
                Assert.That(descriptor.Keywords, Is.Not.Empty, id);
                Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }), id);
                Assert.That(descriptor.Localized.ContainsKey("ja"), Is.True, id);
                Assert.That(descriptor.Localized["ja"].DisplayName, Is.Not.Empty, id);
                Assert.That(descriptor.Localized["ja"].Summary, Is.Not.Empty, id);
                Assert.That(descriptor.Examples, Is.Not.Empty, id);
            }
        }

        [Test]
        public void EveryExample_IsACompleteWorkflow_ValidatingWithoutWarnings()
        {
            var build = Build(out _);
            var compiler = Compiler(build);
            var seen = new List<string>();

            foreach (var id in OperationIds)
            {
                foreach (var example in Registered(build, id).Descriptor.Examples)
                {
                    var what = id + " / " + example.Title;
                    seen.Add(what);
                    var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
                    Assert.That(read.Ok, Is.True, () => what);
                    var report = compiler.Validate(new ValidationRequest(
                        read.Value!, null, ValidationMode.Static, ActorKind.Agent));
                    Assert.That(report.Ok, Is.True, () => what + ": " + string.Join(
                        "; ", report.Errors.Select(
                            d => d.Code + " " + d.Message + " " + d.JsonPath)));
                    Assert.That(report.Warnings, Is.Empty, () => what + ": " + string.Join(
                        "; ", report.Warnings.Select(
                            d => d.Code + " " + d.Message + " " + d.JsonPath)));
                }
            }

            Assert.That(seen, Has.Count.EqualTo(OperationIds.Length),
                "each Operation publishes exactly one Example");
        }

        // Codex initial-review F2: the "path or name" and "≥1 transform
        // field" checks must accept a port the static type marks supplied —
        // a data edge or a workflow-input binding feeds it without any
        // Parameters value (SPEC §0.6 SG-06) — while a criterion supplied in
        // no way at all still reports OW-OP-006.
        [Test]
        public void Find_ValidateStatic_AcceptsEdgeFedAndBoundCriteria()
        {
            var op = new GameObjectFindOperation();
            var missing = MissingPorts("scene", "path", "name", "tag");

            var edgeFed = new TypedStubContext(
                missing, Types(("path", "unity.HierarchyPath")));
            Assert.That(op.ValidateStatic(edgeFed), Is.Empty,
                "a data-edge-fed path is supplied");
            var bound = new TypedStubContext(
                missing, Types(("name", "core.String")));
            Assert.That(op.ValidateStatic(bound), Is.Empty,
                "a bound name is supplied");

            var absent = op.ValidateStatic(
                new TypedStubContext(missing, new Dictionary<string, TypeRef>()));
            Assert.That(absent.Count, Is.EqualTo(1));
            Assert.That(absent[0].Code, Is.EqualTo(ErrorCodes.OW_OP_006));
        }

        [Test]
        public void TransformSet_ValidateStatic_AcceptsEdgeFedAndBoundFields()
        {
            var op = new TransformSetOperation();
            var missing = MissingPorts("target", "position", "rotation", "scale", "space");

            var edgeFed = new TypedStubContext(
                missing, Types(("position", "unity.Vector3")));
            Assert.That(op.ValidateStatic(edgeFed), Is.Empty,
                "a data-edge-fed field is supplied");
            var bound = new TypedStubContext(
                missing, Types(("scale", "unity.Vector3")));
            Assert.That(op.ValidateStatic(bound), Is.Empty,
                "a bound field is supplied");

            var absent = op.ValidateStatic(
                new TypedStubContext(missing, new Dictionary<string, TypeRef>()));
            Assert.That(absent.Count, Is.EqualTo(1));
            Assert.That(absent[0].Code, Is.EqualTo(ErrorCodes.OW_OP_006));
        }

        // The same acceptance end-to-end: a criterion bound to a workflow
        // input carries that input's declared type through the real D3
        // static check.
        [Test]
        public void Find_BoundCriterion_ValidatesInTheRealPipeline()
        {
            var build = Build(out _);
            var compiler = Compiler(build);
            var json =
                "{\"schemaVersion\":\"1.0\",\"id\":\"example.find-bound\"," +
                "\"inputs\":[{\"id\":\"who\",\"type\":\"core.String\",\"required\":true}]," +
                "\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"find\",\"operation\":\"unity.gameobject.find\",\"version\":1}]," +
                "\"controlEdges\":[{\"from\":\"start\",\"to\":\"find\"}]," +
                "\"inputBindings\":[{\"fromInput\":\"who\",\"to\":{\"node\":\"find\",\"port\":\"name\"}}]," +
                "\"outputs\":[{\"id\":\"gameObject\",\"from\":{\"node\":\"find\",\"port\":\"gameObject\"}}]}";
            var read = WorkflowSerializer.Read(json);
            Assert.That(read.Ok, Is.True);

            var report = compiler.Validate(new ValidationRequest(
                read.Value!, null, ValidationMode.Static, ActorKind.Agent));

            Assert.That(report.Ok, Is.True, () => string.Join("; ",
                report.Errors.Select(d => d.Code + " " + d.Message + " " + d.JsonPath)));
        }

        private static void AssertPorts(
            OperationDescriptor descriptor,
            (string Name, string Type, bool Required)[] inputs,
            (string Name, string Type, bool Required)[] outputs)
        {
            Assert.That(
                descriptor.Inputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(inputs.Select(p => (p.Name, p.Type, p.Required)).ToArray()),
                "input ports (§I.1 入力 column)");
            Assert.That(
                descriptor.Outputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(outputs.Select(p => (p.Name, p.Type, p.Required)).ToArray()),
                "output ports (§I.1 出力 column)");
        }
    }
}
