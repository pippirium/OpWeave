#nullable enable

// T-W2.5-01 [Edit] tests: local:T-W2.5-01:descriptors-examples — the four
// §I.1 Scene Operations of this ticket, discovered through TypeCache in the
// OpWeave.Operations assembly, register through the real §C3 stages 1–8 as
// origin Standard with no OW-OP-003 (SPEC §C1 test 1), each Descriptor
// matches its SPEC §I.1 row (ports with their `*` required marks and `=`
// defaults, effects, mode, undo, idempotency), and every Example of every
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
    /// local:T-W2.5-01:descriptors-examples — standard registration, SPEC
    /// §I.1 row conformance and Example validation for
    /// <c>unity.scene.open</c>, <c>unity.scene.create</c>,
    /// <c>unity.scene.save</c> and <c>unity.scene.close</c>.
    /// </summary>
    internal sealed class SceneOperationDescriptorTests
    {
        private const string OperationsAssembly = "OpWeave.Operations";

        private static readonly string[] OperationIds =
        {
            OpenSceneOperation.OperationId,
            CreateSceneOperation.OperationId,
            SaveSceneOperation.OperationId,
            CloseSceneOperation.OperationId,
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
                throw new NotSupportedException(
                    "No Scene Operation reads a port in ValidateStatic.");
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
            Assert.That(build.Stale, Is.False, "the registry build must not be stale");
            Assert.That(build.Snapshot, Is.Not.Null);
            var registered = build.Snapshot!.Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            return registered!;
        }

        // SPEC §C1 test 1 / §C3 stages 6–8: none of the four is rejected at
        // registration — no OW-OP-003 and no other registration failure names
        // any of them, and each lands in the published snapshot as Standard.
        [Test]
        public void SceneOperations_RegisterAsStandard_WithoutOwOp003()
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

        [Test]
        public void EverySceneOperation_DescribesItselfInEnglishAndJapanese()
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
                Assert.That(descriptor.Category, Is.EqualTo("Scene"), id);
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

        // §I.1 row: path* AssetPath, mode enum =single, dirtyScenes enum
        // =fail in; scene SceneRef out; scene.load; Immediate; Undo None;
        // Idempotent.
        [Test]
        public void Open_DescriptorMatchesSpecI1Row()
        {
            var build = Build(out _);
            var registered = Registered(build, OpenSceneOperation.OperationId);
            var descriptor = registered.Descriptor;

            AssertPorts(descriptor,
                new[]
                {
                    ("path", "unity.AssetPath", true),
                    ("mode", "core.String", false),
                    ("dirtyScenes", "core.String", false),
                },
                new[] { ("scene", "unity.SceneRef", false) });
            Assert.That(descriptor.SideEffects, Is.EqualTo(new[] { EffectNames.SceneLoad }));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Idempotent));
            Assert.That(descriptor.Requirements, Is.Empty);
            Assert.That(descriptor.Inputs[1].Constraints?.Enum,
                Is.EqualTo(new[] { "single", "additive" }));
            Assert.That(descriptor.Inputs[1].Default?.CanonicalText, Is.EqualTo("\"single\""));
            Assert.That(descriptor.Inputs[2].Constraints?.Enum,
                Is.EqualTo(new[] { "fail", "save" }));
            Assert.That(descriptor.Inputs[2].Default?.CanonicalText, Is.EqualTo("\"fail\""));
            Assert.That(registered.Implementation, Is.InstanceOf<IEffectResolver>(),
                "the (save 指定時 +scene.save) column entry is the resolver's answer");
        }

        // §I.1 row: path* AssetPath(.unity), setup enum =default, mode enum
        // =additive in; scene SceneRef out; asset.create + scene.load;
        // Immediate; Undo None; Reconcilable.
        [Test]
        public void Create_DescriptorMatchesSpecI1Row()
        {
            var build = Build(out _);
            var descriptor = Registered(build, CreateSceneOperation.OperationId).Descriptor;

            AssertPorts(descriptor,
                new[]
                {
                    ("path", "unity.AssetPath", true),
                    ("setup", "core.String", false),
                    ("mode", "core.String", false),
                },
                new[] { ("scene", "unity.SceneRef", false) });
            Assert.That(descriptor.SideEffects,
                Is.EqualTo(new[] { EffectNames.AssetCreate, EffectNames.SceneLoad }));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(descriptor.Requirements,
                Is.EqualTo(new[] { new Requirement("path_writable", "path") }));
            Assert.That(descriptor.Inputs[1].Constraints?.Enum,
                Is.EqualTo(new[] { "empty", "default" }));
            Assert.That(descriptor.Inputs[1].Default?.CanonicalText, Is.EqualTo("\"default\""));
            Assert.That(descriptor.Inputs[2].Constraints?.Enum,
                Is.EqualTo(new[] { "single", "additive" }));
            Assert.That(descriptor.Inputs[2].Default?.CanonicalText, Is.EqualTo("\"additive\""));
        }

        // §I.1 row: optional scene SceneRef (Active when omitted) and
        // optional path AssetPath in; scene SceneRef out; scene.save;
        // Immediate; Undo None; Idempotent.
        [Test]
        public void Save_DescriptorMatchesSpecI1Row()
        {
            var build = Build(out _);
            var descriptor = Registered(build, SaveSceneOperation.OperationId).Descriptor;

            AssertPorts(descriptor,
                new[]
                {
                    ("scene", "unity.SceneRef", false),
                    ("path", "unity.AssetPath", false),
                },
                new[] { ("scene", "unity.SceneRef", false) });
            Assert.That(descriptor.SideEffects, Is.EqualTo(new[] { EffectNames.SceneSave }));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Idempotent));
            Assert.That(descriptor.Requirements,
                Is.EqualTo(new[] { new Requirement("path_writable", "path") }));
        }

        // §I.1 row: scene* SceneRef and dirtyScenes enum =fail in; no
        // outputs; scene.load; Immediate; Undo None; Idempotent.
        [Test]
        public void Close_DescriptorMatchesSpecI1Row()
        {
            var build = Build(out _);
            var descriptor = Registered(build, CloseSceneOperation.OperationId).Descriptor;

            AssertPorts(descriptor,
                new[]
                {
                    ("scene", "unity.SceneRef", true),
                    ("dirtyScenes", "core.String", false),
                },
                Array.Empty<(string, string, bool)>());
            Assert.That(descriptor.SideEffects, Is.EqualTo(new[] { EffectNames.SceneLoad }));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Idempotent));
            Assert.That(descriptor.Inputs[1].Constraints?.Enum,
                Is.EqualTo(new[] { "fail", "save" }));
            Assert.That(descriptor.Inputs[1].Default?.CanonicalText, Is.EqualTo("\"fail\""));
        }

        // The §C4 IEffectResolver contract of the "(save 指定時 +scene.save)"
        // column: a constant "save" adds scene.save to the actual effects of
        // both dirtyScenes Operations, a constant or defaulted "fail" does not.
        [Test]
        public void DirtyScenesSave_ResolvesSceneSaveEffect()
        {
            var open = new OpenSceneOperation();
            var close = new CloseSceneOperation();
            var save = new Dictionary<string, InputValue>
            {
                ["dirtyScenes"] = InputValue.Present(JsonValue.Parse("\"save\"").Value!),
            };
            var fail = new Dictionary<string, InputValue>
            {
                ["dirtyScenes"] = InputValue.Present(JsonValue.Parse("\"fail\"").Value!),
            };
            var empty = new Dictionary<string, InputValue>();

            foreach (var resolver in new IEffectResolver[] { open, close })
            {
                Assert.That(resolver.ResolveEffects(new StubStaticContext("n", save)),
                    Is.EqualTo(new[] { EffectNames.SceneLoad, EffectNames.SceneSave }),
                    resolver.GetType().Name + ": 'save' adds scene.save");
                Assert.That(resolver.ResolveEffects(new StubStaticContext("n", fail)),
                    Is.EqualTo(new[] { EffectNames.SceneLoad }),
                    resolver.GetType().Name + ": 'fail' keeps scene.load only");
                Assert.That(resolver.ResolveEffects(new StubStaticContext("n", empty)),
                    Is.EqualTo(new[] { EffectNames.SceneLoad }),
                    resolver.GetType().Name + ": the default is 'fail'");
            }
        }

        // SPEC §C1 item 4 / §H0 item 1: every Example of every Descriptor is a
        // complete WF document the static Validator accepts with ok:true and
        // no warning — and it is wired as the workflow it claims to be: one
        // core.start, exactly one node of the publishing Operation, every
        // required input supplied by parameter or data edge.
        [Test]
        public void EveryExample_IsACompleteWorkflow_ValidatingWithoutWarnings()
        {
            var build = Build(out _);
            var compiler = Compiler(build);
            var seen = new List<string>();

            foreach (var id in OperationIds)
            {
                var descriptor = Registered(build, id).Descriptor;
                foreach (var example in descriptor.Examples)
                {
                    var what = id + " / " + example.Title;
                    seen.Add(what);
                    Assert.That(example.Title, Is.Not.Empty, id);
                    Assert.That(example.FixtureManifest, Is.Null,
                        what + ": no fixture is required, so none is declared (§H0 item 1)");

                    var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
                    Assert.That(read.Ok, Is.True, () => what + ": " + string.Join(
                        "; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)));
                    var document = read.Value!;

                    var report = compiler.Validate(new ValidationRequest(
                        document, null, ValidationMode.Static, ActorKind.Agent));
                    Assert.That(report.Ok, Is.True, () => what + ": " + string.Join(
                        "; ", report.Errors.Select(d => d.Code + " " + d.Message + " " + d.JsonPath)));
                    Assert.That(report.Warnings, Is.Empty, () => what + ": " + string.Join(
                        "; ", report.Warnings.Select(
                            d => d.Code + " " + d.Message + " " + d.JsonPath)));

                    Assert.That(
                        document.Nodes.Count(n => n.Operation == "core.start"),
                        Is.EqualTo(1), what + ": an Example carries exactly one Start node");
                    var illustrated = document.Nodes.Where(n => n.Operation == id).ToList();
                    Assert.That(illustrated, Has.Count.EqualTo(1),
                        what + ": the Example runs the publishing Operation once");
                    Assert.That(illustrated[0].Version, Is.EqualTo(1), what);
                    Assert.That(
                        document.ControlEdges.Any(e =>
                            string.Equals(e.To, illustrated[0].Id, StringComparison.Ordinal)),
                        Is.True, what + ": a control edge must reach the illustrated node");
                    foreach (var port in descriptor.Inputs.Where(p => p.Required))
                    {
                        var supplied = illustrated[0].Parameters.ContainsKey(port.Name) ||
                            document.DataEdges.Any(e =>
                                string.Equals(e.To.Node, illustrated[0].Id, StringComparison.Ordinal) &&
                                string.Equals(e.To.Port, port.Name, StringComparison.Ordinal));
                        Assert.That(supplied, Is.True,
                            () => what + ": required input '" + port.Name + "' has no value");
                    }
                }
            }

            Assert.That(seen, Has.Count.EqualTo(OperationIds.Length),
                "each of the four Operations publishes exactly one Example");
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
