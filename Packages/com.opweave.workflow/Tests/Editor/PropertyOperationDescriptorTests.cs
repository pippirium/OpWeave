#nullable enable

// T-W2.5-04 [Edit] tests: local:T-W2.5-04:descriptors-examples — the two
// §I.1 property Operations discovered through TypeCache in the
// OpWeave.Operations assembly register through the real §C3 stages 1–8 as
// origin Standard with no OW-OP-003 (SPEC §C1 test 1), each Descriptor
// matches its SPEC §I.1 row, every Example is a complete WF document the
// static Validator accepts with ok:true and no warnings, and the §C4 test
// 2 property.set AssetRef → asset.modify resolution is exercised through
// the real D6 phase with the real resolver.
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
    /// local:T-W2.5-04:descriptors-examples — registration, §I.1 row
    /// conformance, Example validation and §C4 effect resolution for the
    /// property Operations.
    /// </summary>
    internal sealed class PropertyOperationDescriptorTests
    {
        private const string OperationsAssembly = "OpWeave.Operations";

        private static readonly string[] OperationIds =
        {
            PropertyGetOperation.OperationId,
            PropertySetOperation.OperationId,
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

        // A settings-map IPermissionPolicy fixture — the same shape the
        // [Core] permission tests use, so the D6 policy decision can be
        // steered to expose the resolver's answer.
        private sealed class FixturePolicy : IPermissionPolicy
        {
            private readonly IReadOnlyDictionary<string, PermissionLevel> _levels;

            internal FixturePolicy(
                PolicyActor actor,
                IReadOnlyDictionary<string, PermissionLevel> levels)
            {
                Actor = actor;
                _levels = levels;
            }

            public PolicyActor Actor { get; }

            public bool DestructiveRequiresAsk => true;

            public PermissionLevel LevelFor(string effect) =>
                _levels.TryGetValue(effect, out var level) ? level : PermissionLevel.Deny;
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

        private static WorkflowCompiler Compiler(
            RegistryBuildResult build, IPermissionPolicy? policy = null)
        {
            Func<ActorKind, IPermissionPolicy>? policyFor = null;
            if (policy is not null)
            {
                policyFor = _ => policy;
            }
            return new WorkflowCompiler(
                build.Snapshot!,
                (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                new UnityTypeProbe(),
                UnityPlanProvenance.Capture(IntegrityLevel.Weak, build.Providers),
                policyFor: policyFor);
        }

        private static RegisteredOperation Registered(RegistryBuildResult build, string id)
        {
            Assert.That(build.Snapshot, Is.Not.Null);
            var registered = build.Snapshot!.Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            return registered!;
        }

        private static ValidationReport ValidateJson(
            WorkflowCompiler compiler, string json, string what)
        {
            var read = WorkflowSerializer.Read(json);
            Assert.That(read.Ok, Is.True, () => what);
            return compiler.Validate(new ValidationRequest(
                read.Value!, null, ValidationMode.Static, ActorKind.Agent));
        }

        [Test]
        public void PropertyOperations_RegisterAsStandard_WithoutOwOp003()
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

            var get = Registered(build, PropertyGetOperation.OperationId).Descriptor;
            AssertPorts(get,
                new[]
                {
                    ("target", "unity.ObjectRef", true),
                    ("propertyPath", "core.String", true),
                },
                new[] { ("value", "core.Json", false) });
            Assert.That(get.SideEffects, Is.EqualTo(new[] { EffectNames.Read }));
            Assert.That(get.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(get.Idempotency, Is.EqualTo(Idempotency.Idempotent));
            Assert.That(get.Destructive, Is.False);
            Assert.That(get.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(get.Requirements, Is.Empty);

            var set = Registered(build, PropertySetOperation.OperationId).Descriptor;
            AssertPorts(set,
                new[]
                {
                    ("target", "unity.ObjectRef", true),
                    ("propertyPath", "core.String", true),
                    ("value", "core.Json", true),
                },
                Array.Empty<(string, string, bool)>());
            // §I.1 "scene.modify または asset.modify（target の型で決定）" —
            // the declared list carries both and the §C4 IEffectResolver
            // narrows it per node.
            Assert.That(set.SideEffects,
                Is.EqualTo(new[] { EffectNames.SceneModify, EffectNames.AssetModify }));
            // §I.1 "Scene は Full、Asset は None": the target's kind selects
            // the Undo behaviour at run time, so the single declared field
            // is Partial.
            Assert.That(set.Undo, Is.EqualTo(UndoSupport.Partial));
            Assert.That(set.Idempotency, Is.EqualTo(Idempotency.Idempotent));
            Assert.That(set.Destructive, Is.False);
            Assert.That(set.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(set.Requirements, Is.Empty);
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
                    var report = ValidateJson(
                        compiler, example.WorkflowJson.CanonicalText, what);
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

        // SPEC §C4 test 2 through the real D6 phase and the real resolver:
        // a target fed by an AssetRef output resolves property.set's actual
        // effect to asset.modify — visible in the Plan approvals when the
        // policy makes it ask — while the denied declared counterpart
        // cannot pass in its place.
        [Test]
        public void PropertySet_AssetRefTarget_ResolvesToAssetModifyThroughD6()
        {
            var build = Build(out _);
            var json = AssetTargetWorkflow();
            var ask = new FixturePolicy(PolicyActor.Agent, AllowAll(
                (EffectNames.AssetModify, PermissionLevel.Ask)));

            var report = ValidateJson(Compiler(build, ask), json, "asset target + ask");
            Assert.That(report.Ok, Is.True, () => string.Join("; ",
                report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.That(report.Plan, Is.Not.Null);
            Assert.That(report.Plan!.Approvals
                    .Where(a => a.NodeId == "set")
                    .Select(a => a.Effect),
                Is.EqualTo(new[] { EffectNames.AssetModify }));

            var deny = new FixturePolicy(PolicyActor.Agent, AllowAll(
                (EffectNames.AssetModify, PermissionLevel.Deny)));
            var denied = ValidateJson(Compiler(build, deny), json, "asset target + deny");
            Assert.That(denied.Ok, Is.False);
            var error = denied.Errors.Single(d => d.Code == ErrorCodes.OW_PERM_001);
            Assert.That(error.NodeId, Is.EqualTo("set"));
        }

        // The scene-object counterpart of SPEC §C4 test 2: a GameObjectRef
        // supply resolves to scene.modify — a deny of the declared
        // asset.modify does not fail, and asking scene.modify lands the
        // approval.
        [Test]
        public void PropertySet_SceneTarget_ResolvesToSceneModifyThroughD6()
        {
            var build = Build(out _);
            var json = SceneTargetWorkflow();
            var denyAsset = new FixturePolicy(PolicyActor.Agent, AllowAll(
                (EffectNames.AssetModify, PermissionLevel.Deny)));

            var report = ValidateJson(Compiler(build, denyAsset), json, "scene target");
            Assert.That(report.Ok, Is.True, () => string.Join("; ",
                report.Errors.Select(d => d.Code + " " + d.Message)));

            var askScene = new FixturePolicy(PolicyActor.Agent, AllowAll(
                (EffectNames.SceneModify, PermissionLevel.Ask)));
            var asked = ValidateJson(Compiler(build, askScene), json, "scene target + ask");
            Assert.That(asked.Ok, Is.True);
            Assert.That(asked.Plan, Is.Not.Null);
            Assert.That(asked.Plan!.Approvals
                    .Where(a => a.NodeId == "set")
                    .Select(a => a.Effect),
                Is.EqualTo(new[] { EffectNames.SceneModify }));
        }

        private static string AssetTargetWorkflow() =>
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.property-set-asset\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"mat\",\"operation\":\"unity.asset.create_material\",\"version\":1," +
            "\"parameters\":{\"path\":\"Assets/OpWeaveTesting/property-set-target.mat\"," +
            "\"shader\":\"Standard\"}}," +
            "{\"id\":\"set\",\"operation\":\"unity.property.set\",\"version\":1," +
            "\"parameters\":{\"propertyPath\":\"m_Name\",\"value\":\"Renamed\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"mat\"}," +
            "{\"from\":\"mat\",\"to\":\"set\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"mat\",\"port\":\"material\"}," +
            "\"to\":{\"node\":\"set\",\"port\":\"target\"}}]}";

        private static string SceneTargetWorkflow() =>
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.property-set-scene\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"Renamed\"}}," +
            "{\"id\":\"set\",\"operation\":\"unity.property.set\",\"version\":1," +
            "\"parameters\":{\"propertyPath\":\"m_Name\",\"value\":\"New\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
            "{\"from\":\"create\",\"to\":\"set\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"create\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"set\",\"port\":\"target\"}}]}";

        // The §C4 default table with the named overrides — every other
        // effect stays Allow so only the steered one shows in the result.
        private static IReadOnlyDictionary<string, PermissionLevel> AllowAll(
            params (string Effect, PermissionLevel Level)[] overrides)
        {
            var levels = new Dictionary<string, PermissionLevel>(StringComparer.Ordinal);
            foreach (var effect in EffectNames.All)
            {
                levels[effect] = PermissionLevel.Allow;
            }
            foreach (var (effect, level) in overrides)
            {
                levels[effect] = level;
            }
            return levels;
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
