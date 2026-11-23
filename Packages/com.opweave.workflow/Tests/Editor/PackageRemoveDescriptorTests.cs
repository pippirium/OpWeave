#nullable enable

// T-W4.2-05 [Edit] tests: local:T-W4.2-05:descriptors-examples — the
// §I.4 unity.package.remove Operation, discovered through TypeCache in
// the OpWeave.Operations assembly, registers through the real §C3
// stages 1-8 as origin Standard with no OW-OP-003 (SPEC §C1 test 1),
// its Descriptor matches its SPEC §I.4 row (name* String in, no
// outputs, package.modify + compile, Wait mode, Undo None,
// Reconcilable, destructive) and its Example is a complete WF the
// static Validator accepts with ok:true and no warnings (SPEC §C1
// item 4, §H0 item 1). Mirrors PackageAddDescriptorTests.
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
    /// local:T-W4.2-05:descriptors-examples — standard registration,
    /// SPEC §I.4 row conformance and Example validation for
    /// <c>unity.package.remove</c>.
    /// </summary>
    internal sealed class PackageRemoveDescriptorTests
    {
        private const string OperationsAssembly = "OpWeave.Operations";

        // The real stage 1-8 pipeline over the standard Operations
        // assembly only — the same seam as PackageAddDescriptorTests.
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
                    "unity.package.remove's ValidateStatic reads Parameters only.");
        }

        private static RegisteredOperation Registered(RegistryBuildResult build)
        {
            Assert.That(build.Stale, Is.False, "the registry build must not be stale");
            Assert.That(build.Snapshot, Is.Not.Null);
            var registered = build.Snapshot!.Find(UnityPackageRemoveOperation.OperationId, 1);
            Assert.That(registered, Is.Not.Null,
                UnityPackageRemoveOperation.OperationId + " v1 must register");
            return registered!;
        }

        // SPEC §C1 test 1 / §C3 stages 6-7: the Operation registers with
        // no OW-OP-003 and lands in the published snapshot as Standard.
        [Test]
        public void PackageRemove_RegistersAsStandard_WithoutOwOp003()
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

            var registered = Registered(build);
            Assert.That(registered.Origin, Is.EqualTo(OperationOrigin.Standard));
            Assert.That(registered.AssemblyName, Is.EqualTo(OperationsAssembly));
            Assert.That(registered.Implementation, Is.InstanceOf<IOperation>());
            Assert.That(registered.DescriptorHash,
                Is.EqualTo(DescriptorHash.Compute(registered.Descriptor)));
            Assert.That(registered.ImplementationHash, Does.Match("^[0-9a-f]{64}$"));
        }

        // The §I.4 row: name* String in, no outputs, package.modify +
        // compile, Wait mode, Undo None, Reconcilable, destructive —
        // plus the §C1 self-description.
        [Test]
        public void PackageRemove_DescriptorMatchesSpecI4Row()
        {
            var build = Build(out _);
            var descriptor = Registered(build).Descriptor;

            Assert.That(descriptor.Id, Is.EqualTo(UnityPackageRemoveOperation.OperationId));
            Assert.That(descriptor.Version, Is.EqualTo(1));
            Assert.That(descriptor.Kind, Is.EqualTo(OperationKind.Native));
            Assert.That(
                descriptor.Inputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(new[] { ("name", "core.String", true) }),
                "input ports (§I.4 入力 column)");
            Assert.That(descriptor.Inputs[0].Constraints?.ConstantOnly,
                Is.Not.True,
                "the name port needs no ConstantOnly — a package name " +
                "carries no credential (SPEC §G8.1 scopes the rule to id)");
            Assert.That(descriptor.Outputs, Is.Empty,
                "output ports (§I.4 出力 column: —)");
            Assert.That(descriptor.SideEffects,
                Is.EqualTo(new[] { EffectNames.PackageModify, EffectNames.Compile }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Waitable),
                "§I.4 Mode column: Wait");
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(descriptor.Destructive, Is.True,
                "§I.4 注記: destructive");
            Assert.That(descriptor.Requirements, Is.Empty);
            Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }));
            Assert.That(descriptor.OutputRefinement, Is.Null);
            Assert.That(descriptor.DefaultTimeoutSeconds,
                Is.EqualTo(OperationDescriptor.DefaultWaitableTimeoutSeconds),
                "the §E6 item 1 Waitable default timeout");

            Assert.That(descriptor.DisplayName, Is.Not.Empty);
            Assert.That(descriptor.Summary, Is.Not.Empty);
            Assert.That(descriptor.Summary.Length,
                Is.LessThanOrEqualTo(OperationDescriptorValidator.SummaryMaxLength));
            Assert.That(descriptor.Description, Is.Not.Empty);
            Assert.That(descriptor.Category, Is.Not.Empty);
            Assert.That(descriptor.Keywords, Is.Not.Empty);
            Assert.That(descriptor.Tags, Is.Not.Empty);
            Assert.That(descriptor.Localized.ContainsKey("ja"), Is.True,
                "a Japanese display entry is required (SPEC §C1 item 1, R8)");
            Assert.That(descriptor.Localized["ja"].DisplayName, Is.Not.Empty);
            Assert.That(descriptor.Localized["ja"].Summary, Is.Not.Empty);
            Assert.That(descriptor.Examples, Is.Not.Empty,
                "a Descriptor without a complete-WF Example is incomplete (§C1 item 4)");
        }

        // SPEC §C1 item 4 / §H0 item 1: the Example is a complete WF the
        // snapshot-backed static Validator accepts with ok:true and no
        // warnings — the constant name satisfies the name grammar and a
        // no-output node declares no workflow outputs.
        [Test]
        public void Example_IsACompleteWorkflow_ValidatingWithoutWarnings()
        {
            var build = Build(out _);
            var compiler = Compiler(build);
            var examples = Registered(build).Descriptor.Examples;
            Assert.That(examples, Has.Count.EqualTo(1));
            var example = examples[0];

            Assert.That(example.Title, Is.Not.Empty);

            var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
            Assert.That(read.Ok, Is.True, () => string.Join(
                "; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)));

            var report = compiler.Validate(new ValidationRequest(
                read.Value!, null, ValidationMode.Static, ActorKind.Agent));

            Assert.That(report.Ok, Is.True, () => string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message + " " + d.JsonPath)));
            Assert.That(report.Warnings, Is.Empty, () => string.Join(
                "; ", report.Warnings.Select(d => d.Code + " " + d.Message + " " + d.JsonPath)));
        }

        // The Example is wired as the workflow it claims to be: one
        // core.start, one unity.package.remove node with its required
        // input supplied, and a control edge reaching it.
        [Test]
        public void Example_IsWiredToTheOperationItIllustrates()
        {
            var build = Build(out _);
            var descriptor = Registered(build).Descriptor;

            foreach (var example in descriptor.Examples)
            {
                var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
                Assert.That(read.Ok, Is.True);
                var document = read.Value!;
                var illustrated =
                    document.Nodes.Where(n => n.Operation == descriptor.Id).ToList();

                Assert.That(
                    document.Nodes.Count(n => n.Operation == "core.start"), Is.EqualTo(1));
                Assert.That(illustrated, Has.Count.EqualTo(1));
                Assert.That(illustrated[0].Version, Is.EqualTo(1));
                Assert.That(
                    document.ControlEdges.Any(e =>
                        string.Equals(e.To, illustrated[0].Id, StringComparison.Ordinal)),
                    Is.True, "a control edge must reach the illustrated node");
                foreach (var port in descriptor.Inputs.Where(p => p.Required))
                {
                    Assert.That(illustrated[0].Parameters.ContainsKey(port.Name), Is.True,
                        "the required input '" + port.Name + "' has no value in the Example");
                }
            }
        }
    }
}
