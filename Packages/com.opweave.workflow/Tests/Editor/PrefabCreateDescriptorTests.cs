#nullable enable

// T-W2.6-02 [Edit] tests (local:T-W2.6-02:descriptors-examples, SPEC §C1
// test 1 for the standard set, §I.2 rows): the three Prefab-creating
// Operations of the OpWeave.Operations assembly register through the real
// §C3 stages 1-8 as origin Standard with no OW-OP-003, each Descriptor is
// its §I.2 row (ports, effects, Immediate, Undo, Reconcilable, the English
// and Japanese text and a complete-WF Example), and every published Example
// passes the canonical static Validator with ok:true and no warnings — after
// the test applied the fixture the Example declares.
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
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.6-02:descriptors-examples — standard registration, the
    /// SPEC §I.2 Descriptor rows and the Example workflows of
    /// <c>unity.prefab.create_from_gameobject</c>,
    /// <c>unity.prefab.instantiate</c> and
    /// <c>unity.prefab.create_variant</c>.
    /// </summary>
    internal sealed class PrefabCreateDescriptorTests
    {
        private const string CreateId = "unity.prefab.create_from_gameobject";
        private const string InstantiateId = "unity.prefab.instantiate";
        private const string VariantId = "unity.prefab.create_variant";
        private const string OperationsAssembly = "OpWeave.Operations";

        [SetUp]
        public void SetUp() => PrefabContentsFixture.CreateExampleFixture();

        [TearDown]
        public void TearDown() => PrefabContentsFixture.Cleanup();

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
                throw new NotSupportedException(
                    "These fixtures declare no statically read ports.");
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

        private static RegisteredOperation Registered(
            RegistryBuildResult build, string id)
        {
            Assert.That(build.Stale, Is.False, "the registry build must not be stale");
            Assert.That(build.Snapshot, Is.Not.Null);
            var registered = build.Snapshot!.Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            return registered!;
        }

        private static OperationDescriptor DescriptorOf(string id) =>
            Registered(Build(out _), id).Descriptor;

        private static PortDescriptor Port(
            IReadOnlyList<PortDescriptor> ports, string name)
        {
            Assert.That(ports.Count(p => p.Name == name), Is.EqualTo(1),
                "the Descriptor must declare the port '" + name + "' exactly once");
            return ports.Single(p => p.Name == name);
        }

        // SPEC §C1 test 1 / §C3 stages 6-7: all three register through the
        // real pipeline as Standard with no OW-OP-003.
        [Test]
        public void CreateOperations_RegisterAsStandard_WithoutOwOp003()
        {
            var build = Build(out var publication);

            Assert.That(publication.Snapshot, Is.SameAs(build.Snapshot));
            Assert.That(build.OperationFailures, Is.Empty,
                () => string.Join("; ", build.OperationFailures.SelectMany(
                    f => f.TypeFullName + ": " + string.Join(
                        ",", f.Diagnostics.Select(d => d.Code + " " + d.Message)))));
            Assert.That(build.Snapshot!.Failures, Is.Empty,
                () => string.Join("; ", build.Snapshot.Failures.SelectMany(
                    f => f.OperationId + ": " + string.Join(
                        ",", f.Diagnostics.Select(d => d.Code + " " + d.Message)))));

            foreach (var id in new[] { CreateId, InstantiateId, VariantId })
            {
                var registered = Registered(build, id);
                Assert.That(registered.Origin, Is.EqualTo(OperationOrigin.Standard), id);
                Assert.That(registered.AssemblyName, Is.EqualTo(OperationsAssembly), id);
                Assert.That(registered.Implementation, Is.InstanceOf<IOperation>(), id);
                Assert.That(registered.DescriptorHash,
                    Is.EqualTo(DescriptorHash.Compute(registered.Descriptor)), id);
                Assert.That(
                    build.Snapshot!.Failures.Any(f =>
                        f.OperationId == id && f.Diagnostics.Any(
                            d => d.Code == ErrorCodes.OW_OP_003)),
                    Is.False, id + " must not report OW-OP-003");
            }
        }

        // The §I.2 row of unity.prefab.create_from_gameobject.
        [Test]
        public void CreateFromGameObject_DescriptorMatchesSpecRow()
        {
            var descriptor = DescriptorOf(CreateId);

            Assert.That(descriptor.Id, Is.EqualTo(CreateId));
            Assert.That(descriptor.Version, Is.EqualTo(1));
            Assert.That(descriptor.Inputs, Has.Count.EqualTo(4));
            Assert.That(descriptor.Outputs, Has.Count.EqualTo(2));
            Assert.That(Port(descriptor.Inputs, "source").Type,
                Is.EqualTo("unity.GameObjectRef"));
            Assert.That(Port(descriptor.Inputs, "source").Required, Is.True);
            Assert.That(Port(descriptor.Inputs, "path").Type,
                Is.EqualTo("unity.AssetPath"));
            Assert.That(Port(descriptor.Inputs, "path").Required, Is.True);
            Assert.That(Port(descriptor.Inputs, "connect").Type,
                Is.EqualTo("core.Bool"));
            Assert.That(Port(descriptor.Inputs, "connect").Required, Is.False);
            Assert.That(
                Port(descriptor.Inputs, "connect").Default!.ToToken().Value<bool>(),
                Is.True, "connect defaults to true (§I.2)");
            Assert.That(Port(descriptor.Inputs, "overwrite").Type,
                Is.EqualTo("core.Bool"));
            Assert.That(
                Port(descriptor.Inputs, "overwrite").Default!.ToToken().Value<bool>(),
                Is.False, "overwrite defaults to false (§I.2)");
            Assert.That(Port(descriptor.Outputs, "prefab").Type,
                Is.EqualTo("unity.PrefabAssetRef"));
            Assert.That(Port(descriptor.Outputs, "instance").Type,
                Is.EqualTo("unity.PrefabInstanceRef"));
            Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(descriptor.SideEffects,
                Is.EqualTo(new[] { EffectNames.AssetCreate, EffectNames.SceneModify }));
            Assert.That(descriptor.Destructive, Is.False);
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.Partial));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            AssertTextIsPublished(descriptor, requiresManifest: false);
        }

        // The §I.2 row of unity.prefab.instantiate.
        [Test]
        public void Instantiate_DescriptorMatchesSpecRow()
        {
            var descriptor = DescriptorOf(InstantiateId);

            Assert.That(descriptor.Inputs, Has.Count.EqualTo(4));
            Assert.That(descriptor.Outputs, Has.Count.EqualTo(1));
            Assert.That(Port(descriptor.Inputs, "prefab").Type,
                Is.EqualTo("unity.PrefabAssetRef"));
            Assert.That(Port(descriptor.Inputs, "prefab").Required, Is.True);
            Assert.That(Port(descriptor.Inputs, "scene").Type,
                Is.EqualTo("unity.SceneRef"));
            Assert.That(Port(descriptor.Inputs, "scene").Required, Is.False);
            Assert.That(Port(descriptor.Inputs, "parent").Type,
                Is.EqualTo("unity.GameObjectRef"));
            Assert.That(Port(descriptor.Inputs, "position").Type,
                Is.EqualTo("unity.Vector3"));
            Assert.That(Port(descriptor.Outputs, "instance").Type,
                Is.EqualTo("unity.PrefabInstanceRef"));
            Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(descriptor.SideEffects,
                Is.EqualTo(new[] { EffectNames.SceneModify }));
            Assert.That(descriptor.Destructive, Is.False);
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.Full));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(descriptor.Requirements.Any(
                    r => r.Kind == "scene_loaded" && r.Port == "scene"), Is.True);
            AssertTextIsPublished(descriptor, requiresManifest: true);
        }

        // The §I.2 row of unity.prefab.create_variant.
        [Test]
        public void CreateVariant_DescriptorMatchesSpecRow()
        {
            var descriptor = DescriptorOf(VariantId);

            Assert.That(descriptor.Inputs, Has.Count.EqualTo(2));
            Assert.That(descriptor.Outputs, Has.Count.EqualTo(1));
            Assert.That(Port(descriptor.Inputs, "base").Type,
                Is.EqualTo("unity.PrefabAssetRef"));
            Assert.That(Port(descriptor.Inputs, "base").Required, Is.True);
            Assert.That(Port(descriptor.Inputs, "path").Type,
                Is.EqualTo("unity.AssetPath"));
            Assert.That(Port(descriptor.Inputs, "path").Required, Is.True);
            Assert.That(Port(descriptor.Outputs, "variant").Type,
                Is.EqualTo("unity.PrefabAssetRef"));
            Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(descriptor.SideEffects,
                Is.EqualTo(new[] { EffectNames.AssetCreate }));
            Assert.That(descriptor.Destructive, Is.False);
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(descriptor.Requirements.Any(
                    r => r.Kind == "path_writable" && r.Port == "path"), Is.True);
            AssertTextIsPublished(descriptor, requiresManifest: true);
        }

        private static void AssertTextIsPublished(
            OperationDescriptor descriptor, bool requiresManifest)
        {
            Assert.That(descriptor.DisplayName, Is.Not.Empty);
            Assert.That(descriptor.Summary, Is.Not.Empty);
            Assert.That(descriptor.Summary.Length,
                Is.LessThanOrEqualTo(OperationDescriptorValidator.SummaryMaxLength));
            Assert.That(descriptor.Description, Is.Not.Empty);
            Assert.That(descriptor.Category, Is.Not.Empty);
            Assert.That(descriptor.Tags, Is.Not.Empty);
            Assert.That(descriptor.Keywords, Is.Not.Empty);
            Assert.That(descriptor.Localized.ContainsKey("ja"), Is.True,
                "a Japanese display entry is required (SPEC §C1 item 1, R8)");
            Assert.That(descriptor.Localized["ja"].DisplayName, Is.Not.Empty);
            Assert.That(descriptor.Localized["ja"].Summary, Is.Not.Empty);
            Assert.That(descriptor.Examples, Has.Count.EqualTo(1));
            Assert.That(descriptor.Examples[0].Title, Is.Not.Empty);
            if (requiresManifest)
            {
                Assert.That(descriptor.Examples[0].FixtureManifest, Is.Not.Null,
                    "the Example declares the Prefab fixture it needs");
                var declared = descriptor.Examples[0].FixtureManifest!.ToToken()
                    .SelectToken("prefabs[0].path")?.ToString();
                Assert.That(declared, Is.EqualTo(PrefabContentsFixture.ExamplePath));
            }
        }

        // SPEC §C1 item 4, §C8: every published Example is a complete WF the
        // canonical Validator accepts as written, ok:true and no warnings.
        [Test]
        public void EveryExample_ValidatesWithNoWarnings()
        {
            var build = Build(out _);

            foreach (var id in new[] { CreateId, InstantiateId, VariantId })
            {
                var examples = Registered(build, id).Descriptor.Examples;
                Assert.That(examples, Is.Not.Empty, id);
                foreach (var example in examples)
                {
                    var read = WorkflowSerializer.Read(
                        example.WorkflowJson.CanonicalText);
                    Assert.That(read.Ok, Is.True, id + " " + example.Title + ": " +
                        string.Join("; ", read.Diagnostics.Select(
                            d => d.Code + " " + d.Message)));

                    var report = Compiler(build).Validate(new ValidationRequest(
                        read.Value!, null, ValidationMode.Static, ActorKind.Agent));

                    Assert.That(report.Ok, Is.True, () => id + " " + example.Title +
                        ": " + string.Join("; ", report.Errors.Select(
                            d => d.Code + " " + d.Message)));
                    Assert.That(report.Warnings, Is.Empty, () => id + " " +
                        example.Title + ": " + string.Join("; ",
                            report.Warnings.Select(d => d.Code + " " + d.Message)));
                    Assert.That(report.Plan, Is.Not.Null);
                }
            }
        }
    }
}
