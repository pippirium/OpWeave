#nullable enable

// T-W2.5-03 [Edit] tests: local:T-W2.5-03:descriptors-examples — the two
// §I.1 Component Operations discovered through TypeCache in the
// OpWeave.Operations assembly register through the real §C3 stages 1–8 as
// origin Standard with no OW-OP-003 (SPEC §C1 test 1), each Descriptor
// matches its SPEC §I.1 row (including the component.add OutputRefinement),
// and every Example is a complete WF document the static Validator accepts
// with ok:true and no warnings.
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
    /// local:T-W2.5-03:descriptors-examples — registration, §I.1 row
    /// conformance and Example validation for the Component Operations.
    /// </summary>
    internal sealed class ComponentOperationDescriptorTests
    {
        private const string OperationsAssembly = "OpWeave.Operations";

        private static readonly string[] OperationIds =
        {
            ComponentAddOperation.OperationId,
            ComponentRemoveOperation.OperationId,
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
        public void ComponentOperations_RegisterAsStandard_WithoutOwOp003()
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

            var add = Registered(build, ComponentAddOperation.OperationId).Descriptor;
            AssertPorts(add,
                new[]
                {
                    ("target", "unity.GameObjectRef", true),
                    ("componentType", "unity.TypeName", true),
                },
                new[] { ("component", "unity.ComponentRef<unity.Component>", false) });
            Assert.That(add.SideEffects, Is.EqualTo(new[] { EffectNames.SceneModify }));
            Assert.That(add.Undo, Is.EqualTo(UndoSupport.Full));
            Assert.That(add.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(add.Destructive, Is.False);
            Assert.That(add.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(add.Requirements,
                Is.EqualTo(new[] { new Requirement("type_exists", "componentType") }));
            Assert.That(add.OutputRefinement,
                Is.EqualTo(new TypeRefinement(
                    "componentType", "component", "unity.ComponentRef<{0}>")),
                "§C1: a constant componentType narrows the output type");

            var remove = Registered(build, ComponentRemoveOperation.OperationId).Descriptor;
            AssertPorts(remove,
                new[] { ("component", "unity.ComponentRef<unity.Component>", true) },
                Array.Empty<(string, string, bool)>());
            Assert.That(remove.SideEffects, Is.EqualTo(new[] { EffectNames.SceneModify }));
            Assert.That(remove.Undo, Is.EqualTo(UndoSupport.Full));
            Assert.That(remove.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(remove.Destructive, Is.True);
            Assert.That(remove.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
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
                Assert.That(descriptor.Category, Is.EqualTo("Component"), id);
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

        // local:T-W2.5-03:component-ops — the OutputRefinement fixture: a
        // constant componentType UnityEngine.Animator refines the step's
        // output type to unity.ComponentRef<unity.Animator> in the Plan.
        [Test]
        public void AddExample_CompilesWithOutputRefinedToUnityAnimator()
        {
            var build = Build(out _);
            var compiler = Compiler(build);
            var example = Registered(build, ComponentAddOperation.OperationId)
                .Descriptor.Examples[0];
            var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
            Assert.That(read.Ok, Is.True);

            var report = compiler.Validate(new ValidationRequest(
                read.Value!, null, ValidationMode.Static, ActorKind.Agent));

            Assert.That(report.Ok, Is.True, () => string.Join("; ",
                report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.That(report.Plan, Is.Not.Null);
            var step = report.Plan!.Steps.First(s => s.NodeId == "add");
            Assert.That(step.OutputTypes["component"],
                Is.EqualTo("unity.ComponentRef<unity.Animator>"));
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
