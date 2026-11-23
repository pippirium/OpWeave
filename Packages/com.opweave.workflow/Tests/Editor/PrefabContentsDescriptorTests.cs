#nullable enable

// T-W2.6-03 [Edit] tests (local:T-W2.6-03:descriptors-examples, SPEC §C1
// test 1 for the standard set, §I.2 rows, §C8): the two Prefab-content
// Operations of the OpWeave.Operations assembly register through the real
// §C3 stages 1–8 as origin Standard with no OW-OP-003, each Descriptor is the
// §I.2 row (ports, effects, Immediate, Undo None, Reconcilable, the English
// and Japanese text and one complete-WF Example), and every published Example
// passes the canonical static Validator with ok:true and no warnings — after
// the test applied the fixture the Example declares.
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// local:T-W2.6-03:descriptors-examples — standard registration, the
    /// SPEC §I.2 Descriptor rows and the Example workflows of
    /// <c>unity.prefab.open_contents</c> and <c>unity.prefab.save_contents</c>.
    /// </summary>
    internal sealed class PrefabContentsDescriptorTests
    {
        private const string OpenId = "unity.prefab.open_contents";
        private const string SaveId = "unity.prefab.save_contents";

        // The standard Operations container (SPEC §I, §0.5). TypeCache yields
        // every [OpWeaveOperation] class in the project, the test fixtures
        // included, so the scan is narrowed to the real standard set.
        private const string OperationsAssembly = "OpWeave.Operations";

        [SetUp]
        public void SetUp() => PrefabContentsFixture.CreateExampleFixture();

        [TearDown]
        public void TearDown() => PrefabContentsFixture.Cleanup();

        // A minimal StaticContext (Core hosts no implementation, SPEC §0.6
        // SG-06); neither Operation reports static findings.
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

        // The real stage 1–8 pipeline over the standard Operations assembly
        // only (the CoreStartOperationTests pipeline).
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

        private static ValidationReport Validate(
            RegistryBuildResult build, WorkflowDocument document) =>
            Compiler(build).Validate(new ValidationRequest(
                document, null, ValidationMode.Static, ActorKind.Agent));

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

        // SPEC §C1 test 1 / §C3 stages 6–7: both Operations register through
        // the real pipeline as Standard, with no OW-OP-003 and no registration
        // failure of any kind naming them.
        [Test]
        public void ContentsOperations_RegisterAsStandard_WithoutOwOp003()
        {
            var build = Build(out var publication);

            Assert.That(publication.Snapshot, Is.SameAs(build.Snapshot),
                "stage 8 publishes the complete snapshot atomically");
            Assert.That(build.OperationFailures, Is.Empty,
                () => string.Join("; ", build.OperationFailures.SelectMany(
                    f => f.TypeFullName + ": " + string.Join(
                        ",", f.Diagnostics.Select(d => d.Code + " " + d.Message)))));
            Assert.That(build.Snapshot!.Failures, Is.Empty,
                () => string.Join("; ", build.Snapshot.Failures.SelectMany(
                    f => f.OperationId + ": " + string.Join(
                        ",", f.Diagnostics.Select(d => d.Code + " " + d.Message)))));

            foreach (var id in new[] { OpenId, SaveId })
            {
                var registered = Registered(build, id);
                Assert.That(registered.Origin, Is.EqualTo(OperationOrigin.Standard), id);
                Assert.That(registered.AssemblyName, Is.EqualTo(OperationsAssembly), id);
                Assert.That(registered.Implementation, Is.InstanceOf<IOperation>(), id);
                Assert.That(registered.DescriptorHash,
                    Is.EqualTo(DescriptorHash.Compute(registered.Descriptor)), id);
                Assert.That(registered.ImplementationHash, Does.Match("^[0-9a-f]{64}$"), id);
                Assert.That(
                    build.Snapshot!.Failures.Any(f =>
                        f.OperationId == id && f.Diagnostics.Any(
                            d => d.Code == ErrorCodes.OW_OP_003)),
                    Is.False, id + " must not report OW-OP-003");
            }
        }

        // The §I.2 row of unity.prefab.open_contents: prefab* PrefabAssetRef
        // in, root GameObjectRef out, read (+ the Run resource), Immediate,
        // Undo None, Reconcilable — plus the §C1 text and one Example.
        [Test]
        public void OpenContents_DescriptorMatchesSpecRow()
        {
            var descriptor = DescriptorOf(OpenId);

            Assert.That(descriptor.Id, Is.EqualTo(OpenId));
            Assert.That(descriptor.Version, Is.EqualTo(1));
            Assert.That(descriptor.Kind, Is.EqualTo(OperationKind.Native));
            Assert.That(descriptor.Inputs, Has.Count.EqualTo(1));
            Assert.That(descriptor.Outputs, Has.Count.EqualTo(1));
            Assert.That(Port(descriptor.Inputs, "prefab").Type,
                Is.EqualTo("unity.PrefabAssetRef"));
            Assert.That(Port(descriptor.Inputs, "prefab").Required, Is.True,
                "prefab* is required (§I.2)");
            Assert.That(Port(descriptor.Outputs, "root").Type,
                Is.EqualTo("unity.GameObjectRef"));
            Assert.That(Port(descriptor.Outputs, "root").Required, Is.False,
                "an output port is never required");
            Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(descriptor.SideEffects, Is.EqualTo(new[] { EffectNames.Read }));
            Assert.That(descriptor.Destructive, Is.False);
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(descriptor.DefaultTimeoutSeconds,
                Is.EqualTo(OperationDescriptor.DefaultImmediateTimeoutSeconds));
            Assert.That(descriptor.Requirements, Is.Empty);
            Assert.That(descriptor.OutputRefinement, Is.Null);
            AssertThatDescriptorTextIsPublished(descriptor);
        }

        // The §I.2 row of unity.prefab.save_contents: root* GameObjectRef (the
        // open_contents output) in, prefab PrefabAssetRef out, asset.modify,
        // Immediate, Undo None, Reconcilable.
        [Test]
        public void SaveContents_DescriptorMatchesSpecRow()
        {
            var descriptor = DescriptorOf(SaveId);

            Assert.That(descriptor.Id, Is.EqualTo(SaveId));
            Assert.That(descriptor.Version, Is.EqualTo(1));
            Assert.That(descriptor.Kind, Is.EqualTo(OperationKind.Native));
            Assert.That(descriptor.Inputs, Has.Count.EqualTo(1));
            Assert.That(descriptor.Outputs, Has.Count.EqualTo(1));
            Assert.That(Port(descriptor.Inputs, "root").Type,
                Is.EqualTo("unity.GameObjectRef"));
            Assert.That(Port(descriptor.Inputs, "root").Required, Is.True,
                "root* is required (§I.2)");
            Assert.That(Port(descriptor.Outputs, "prefab").Type,
                Is.EqualTo("unity.PrefabAssetRef"));
            Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(
                descriptor.SideEffects, Is.EqualTo(new[] { EffectNames.AssetModify }));
            Assert.That(descriptor.Destructive, Is.False);
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(descriptor.Requirements, Is.Empty);
            Assert.That(descriptor.OutputRefinement, Is.Null);
            AssertThatDescriptorTextIsPublished(descriptor);
        }

        // SPEC §C1 items 1 and 4: publishable English text within the
        // registration limit, a Japanese display entry, and Examples that are
        // complete WF documents — never a node fragment — naming their fixture.
        private static void AssertThatDescriptorTextIsPublished(
            OperationDescriptor descriptor)
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
            var example = descriptor.Examples[0];
            Assert.That(example.Title, Is.Not.Empty);
            Assert.That(example.FixtureManifest, Is.Not.Null,
                "the Example declares the Prefab fixture it needs (SPEC §C1)");
            var declared = example.FixtureManifest!.ToToken()
                .SelectToken("prefabs[0].path")?.ToString();
            Assert.That(declared, Is.EqualTo(PrefabContentsFixture.ExamplePath),
                "the manifest names the fixture the Example's Ref carries");
        }

        // SPEC §C1 item 4, §C8: the published Example is a complete WF that
        // the canonical Validator accepts as written, with ok:true and no
        // warnings — the declared fixture having been applied by SetUp.
        [Test]
        public void EveryExample_ValidatesWithNoWarnings()
        {
            var build = Build(out _);

            foreach (var id in new[] { OpenId, SaveId })
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

                    var report = Validate(build, read.Value!);

                    Assert.That(report.Ok, Is.True, () => id + " " + example.Title +
                        ": " + string.Join("; ", report.Errors.Select(
                            d => d.Code + " " + d.Message)));
                    Assert.That(report.Warnings, Is.Empty, () => id + " " +
                        example.Title + ": " + string.Join("; ",
                            report.Warnings.Select(d => d.Code + " " + d.Message)));
                    Assert.That(report.Plan, Is.Not.Null,
                        "an ok validation carries the built plan");
                }
            }
        }

        // The Example's shape is a runnable workflow, not a fragment: one
        // Start, both Operation nodes, the control chain, the data edge that
        // hands the editing root to the save step and a declared output.
        [Test]
        public void ExampleWorkflow_IsACompleteValidatableDocument()
        {
            var build = Build(out _);
            var example = Registered(build, OpenId).Descriptor.Examples[0];
            var document = WorkflowSerializer.Read(
                example.WorkflowJson.CanonicalText).Value!;

            Assert.That(document.SchemaVersion, Is.EqualTo("1.0"));
            Assert.That(
                document.Nodes.Count(n => n.Operation == "core.start"), Is.EqualTo(1));
            Assert.That(
                document.Nodes.Any(n => n.Operation == OpenId &&
                    n.Parameters.ContainsKey("prefab")),
                Is.True, "the open node must carry its required constant input");
            Assert.That(
                document.Nodes.Any(n => n.Operation == SaveId), Is.True);
            Assert.That(
                document.ControlEdges.Any(e => e.From == "start" && e.To == "open"),
                Is.True);
            Assert.That(
                document.ControlEdges.Any(e => e.From == "open" && e.To == "save"),
                Is.True);
            Assert.That(
                document.DataEdges.Any(e =>
                    e.From.Node == "open" && e.From.Port == "root" &&
                    e.To.Node == "save" && e.To.Port == "root"),
                Is.True, "the editing root must feed the save step");
            Assert.That(document.Outputs, Has.Count.EqualTo(1));
        }
    }
}
