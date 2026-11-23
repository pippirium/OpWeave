#nullable enable

// T-W2.7-03 [Edit] tests: local:T-W2.7-03:descriptors-examples — the two
// §I.3 Asset Operations of this ticket, discovered through TypeCache in the
// OpWeave.Operations assembly, register through the real §C3 stages 1–8 as
// origin Standard with no OW-OP-003 (SPEC §C1 test 1), each Descriptor
// matches its SPEC §I.3 row (ports with their `*` required marks, effects,
// mode, undo, idempotency, requirements), and every Example of every
// Descriptor is a complete WF document the snapshot-backed static Validator
// accepts with ok:true and no warning (SPEC §C1 item 4, §H0 item 1).
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
    /// local:T-W2.7-03:descriptors-examples — standard registration, SPEC §I.3
    /// row conformance and Example validation for
    /// <c>unity.asset.create_material</c> and
    /// <c>unity.asset.create_scriptable_object</c>.
    /// </summary>
    internal sealed class AssetCreateOperationDescriptorTests
    {
        // The assembly name of the standard Operations container (SPEC §I,
        // §0.5). TypeCache yields every [OpWeaveOperation] class in the
        // project, so the scan is narrowed to the real standard set.
        private const string OperationsAssembly = "OpWeave.Operations";

        private static readonly string[] OperationIds =
        {
            CreateMaterialOperation.OperationId,
            CreateScriptableObjectOperation.OperationId,
        };

        // A minimal StaticContext (Core hosts no implementation, SPEC §0.6
        // SG-06); neither of these Operations reads its parameters in
        // ValidateStatic — they keep the SPEC §C2 default.
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
                    "No Asset-create Operation reads a port in ValidateStatic.");
        }

        // The real stage 1–8 pipeline over the standard Operations assembly
        // only: every other seam (provenance, manifest reader, exclusion,
        // class relations) is the production one.
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
            Assert.That(build.Stale, Is.False, "the registry build must not be stale");
            Assert.That(build.Snapshot, Is.Not.Null);
            var registered = build.Snapshot!.Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            return registered!;
        }

        // SPEC §C1 test 1 / §C3 stage 6–7: neither is rejected at
        // registration — no OW-OP-003 and no other registration failure names
        // them, and each lands in the published snapshot as Standard.
        [Test]
        public void AssetCreateOperations_RegisterAsStandard_WithoutOwOp003()
        {
            var build = Build(out var publication);

            Assert.That(publication.Snapshot, Is.SameAs(build.Snapshot),
                "stage 8 publishes the complete snapshot atomically");
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
                Assert.That(registered.Implementation, Is.InstanceOf<IOperation>(), id);
                Assert.That(registered.DescriptorHash,
                    Is.EqualTo(DescriptorHash.Compute(registered.Descriptor)), id);
                Assert.That(registered.ImplementationHash, Does.Match("^[0-9a-f]{64}$"), id);
            }
        }

        // The Descriptor text a human or Agent reads: English DisplayName,
        // Summary within the §C1 120-character limit, Description, Keywords,
        // the Localized["ja"] pair and at least one Example, on every row.
        [Test]
        public void EveryAssetCreateOperation_DescribesItselfInEnglishAndJapanese()
        {
            var build = Build(out _);

            foreach (var id in OperationIds)
            {
                var descriptor = Registered(build, id).Descriptor;

                Assert.That(descriptor.Id, Is.EqualTo(id));
                Assert.That(descriptor.Version, Is.EqualTo(1));
                Assert.That(descriptor.Kind, Is.EqualTo(OperationKind.Native), id);
                Assert.That(descriptor.DisplayName, Is.Not.Empty, id);
                Assert.That(descriptor.Summary, Is.Not.Empty, id);
                Assert.That(
                    descriptor.Summary.Length,
                    Is.LessThanOrEqualTo(OperationDescriptorValidator.SummaryMaxLength), id);
                Assert.That(descriptor.Description, Is.Not.Empty, id);
                Assert.That(descriptor.Category, Is.EqualTo("Asset"), id);
                Assert.That(descriptor.Keywords, Is.Not.Empty, id);
                Assert.That(descriptor.Tags, Is.Not.Empty, id);
                Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }), id);
                Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate), id);
                Assert.That(descriptor.Destructive, Is.False, id);
                Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None), id);
                Assert.That(descriptor.OutputRefinement, Is.Null, id);
                Assert.That(descriptor.Localized.ContainsKey("ja"), Is.True,
                    id + ": a Japanese display entry is required (SPEC §C1 item 1, R8)");
                Assert.That(descriptor.Localized["ja"].DisplayName, Is.Not.Empty, id);
                Assert.That(descriptor.Localized["ja"].Summary, Is.Not.Empty, id);
                Assert.That(descriptor.Examples, Is.Not.Empty,
                    id + ": a Descriptor without a complete-WF Example is incomplete (§C1 item 4)");
            }
        }

        // §I.3 row: path* AssetPath(.mat), shader* String, properties Json in;
        // material AssetRef<Material> out; asset.create, Immediate, Undo None,
        // Reconcilable, note "シェーダー未発見は OW-UNITY-006". The write port
        // carries the §G1 item 4 path_writable requirement (§G7 item 1).
        [Test]
        public void CreateMaterial_DescriptorMatchesSpecI3Row()
        {
            var build = Build(out _);
            var descriptor =
                Registered(build, CreateMaterialOperation.OperationId).Descriptor;

            AssertPorts(descriptor,
                new[]
                {
                    ("path", "unity.AssetPath", true),
                    ("shader", "core.String", true),
                    ("properties", "core.Json", false),
                },
                new[] { ("material", "unity.AssetRef<unity.Material>", false) });
            Assert.That(descriptor.SideEffects, Is.EqualTo(new[] { EffectNames.AssetCreate }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(
                descriptor.Requirements,
                Is.EqualTo(new[] { new Requirement("path_writable", "path") }),
                "the allowed-paths and writability half of §G7 item 1 is this requirement");
        }

        // §I.3 row: path* AssetPath(.asset), type* TypeName, values Json in;
        // asset AssetRef<ScriptableObject> out; asset.create, Immediate, Undo
        // None, Reconcilable, note "type_exists" — plus the path_writable
        // requirement of §G1 item 4 on the write port.
        [Test]
        public void CreateScriptableObject_DescriptorMatchesSpecI3Row()
        {
            var build = Build(out _);
            var descriptor =
                Registered(build, CreateScriptableObjectOperation.OperationId).Descriptor;

            AssertPorts(descriptor,
                new[]
                {
                    ("path", "unity.AssetPath", true),
                    ("type", "unity.TypeName", true),
                    ("values", "core.Json", false),
                },
                new[] { ("asset", "unity.AssetRef<unity.ScriptableObject>", false) });
            Assert.That(descriptor.SideEffects, Is.EqualTo(new[] { EffectNames.AssetCreate }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(
                descriptor.Requirements,
                Is.EqualTo(new[]
                {
                    new Requirement("type_exists", "type"),
                    new Requirement("path_writable", "path"),
                }),
                "§I.3's named precondition plus the §G1 item 4 write-path half");
        }

        // SPEC §C1 item 4 / §H0 item 1: every Example of every Descriptor is
        // a complete WF document the snapshot-backed static Validator accepts
        // with ok:true and not one warning. Neither declares a
        // fixtureManifest: static validation judges the document against the
        // Registry and the type snapshot only, and both Operations create
        // their asset rather than require one — the example's ScriptableObject
        // type is the always-present UnityEngine.ScriptableObject.
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
                    Assert.That(example.Title, Is.Not.Empty, id);
                    Assert.That(example.FixtureManifest, Is.Null,
                        what + ": no fixture is required, so none is declared (§H0 item 1)");

                    var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
                    Assert.That(read.Ok, Is.True, () => what + ": " + string.Join(
                        "; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)) +
                        " (line-level diagnostics of the Example document)");

                    var report = compiler.Validate(new ValidationRequest(
                        read.Value!, null, ValidationMode.Static, ActorKind.Agent));

                    Assert.That(report.Ok, Is.True, () => what + ": " + string.Join(
                        "; ", report.Errors.Select(d => d.Code + " " + d.Message + " " + d.JsonPath)));
                    Assert.That(report.Warnings, Is.Empty, () => what + ": " + string.Join(
                        "; ", report.Warnings.Select(
                            d => d.Code + " " + d.Message + " " + d.JsonPath)));
                }
            }

            Assert.That(seen, Has.Count.EqualTo(OperationIds.Length),
                "each of the two Operations publishes exactly one Example");
        }

        // The Example must also be wired as the workflow it claims to be: one
        // core.start (§D5 item 1), exactly one node running the Operation
        // that publishes it, every required input supplied, and a control
        // edge that reaches it.
        [Test]
        public void EveryExample_IsWiredToTheOperationItIllustrates()
        {
            var build = Build(out _);

            foreach (var id in OperationIds)
            {
                var descriptor = Registered(build, id).Descriptor;
                foreach (var example in descriptor.Examples)
                {
                    var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
                    Assert.That(read.Ok, Is.True, () => id + ": " + string.Join(
                        "; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)));
                    var document = read.Value!;
                    var illustrated =
                        document.Nodes.Where(n => n.Operation == id).ToList();

                    Assert.That(
                        document.Nodes.Count(n => n.Operation == "core.start"), Is.EqualTo(1),
                        id + ": an Example carries exactly one Start node");
                    Assert.That(illustrated, Has.Count.EqualTo(1),
                        id + ": the Example runs the Descriptor that publishes it, once");
                    Assert.That(illustrated[0].Version, Is.EqualTo(1), id);
                    Assert.That(
                        document.ControlEdges.Any(e =>
                            string.Equals(e.To, illustrated[0].Id, StringComparison.Ordinal)),
                        Is.True, id + ": a control edge must reach the illustrated node");

                    foreach (var port in descriptor.Inputs.Where(p => p.Required))
                    {
                        Assert.That(illustrated[0].Parameters.ContainsKey(port.Name), Is.True,
                            () => id + ": the required input '" + port.Name +
                                "' has no value in the Example");
                    }
                }
            }
        }

        // The §I.3 input/output columns: same names in declared order, same
        // Type IDs, and only the `*`-marked ports required.
        private static void AssertPorts(
            OperationDescriptor descriptor,
            (string Name, string Type, bool Required)[] inputs,
            (string Name, string Type, bool Required)[] outputs)
        {
            Assert.That(
                descriptor.Inputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(inputs.Select(p => (p.Name, p.Type, p.Required)).ToArray()),
                "input ports (§I.3 入力 column)");
            Assert.That(
                descriptor.Outputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(outputs.Select(p => (p.Name, p.Type, p.Required)).ToArray()),
                "output ports (§I.3 出力 column)");
        }
    }
}
