#nullable enable

// T-W4.2-02 [Edit] tests: local:T-W4.2-02:descriptors-examples —
// unity.script.create and unity.script.compile, discovered through
// TypeCache in the OpWeave.Operations assembly, register through the real
// §C3 stages 1–8 as origin Standard with no OW-OP-003 (SPEC §C1 test 1),
// their Descriptors match the SPEC §I.4 rows, and every Example is a
// complete WF document the snapshot-backed static Validator accepts with
// ok:true and no warning (SPEC §C1 item 4, §H0 item 1).
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
    /// local:T-W4.2-02:descriptors-examples — standard registration, SPEC
    /// §I.4 row conformance and Example validation for
    /// <c>unity.script.create</c> and <c>unity.script.compile</c>.
    /// </summary>
    internal sealed class ScriptOperationDescriptorTests
    {
        private const string OperationsAssembly = "OpWeave.Operations";

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
                throw new NotSupportedException("unavailable static value: " + port);
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

        // SPEC §C1 test 1 / §C3 stages 6–8: both Operations register as
        // Standard without OW-OP-003 or any other registration failure.
        [Test]
        public void ScriptOperations_RegisterAsStandard_WithoutOwOp003()
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

            foreach (var id in new[]
                { CreateScriptOperation.OperationId, CompileScriptsOperation.OperationId })
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

        // §I.4 row of unity.script.create: path* AssetPath, content*
        // String (ConstantOnly) and overwrite Bool (default false) in;
        // script AssetRef<MonoScript> out; code.write + asset.create;
        // Immediate; Undo None; Reconcilable; path_writable on path.
        [Test]
        public void CreateDescriptor_MatchesSpecI4Row()
        {
            var build = Build(out _);
            var descriptor = Registered(
                build, CreateScriptOperation.OperationId).Descriptor;

            Assert.That(
                descriptor.Inputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(new[]
                {
                    ("path", "unity.AssetPath", true),
                    ("content", "core.String", true),
                    ("overwrite", "core.Bool", false),
                }),
                "input ports (§I.4 入力 column)");
            Assert.That(
                descriptor.Outputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(new[]
                {
                    ("script", "unity.AssetRef<unity.MonoScript>", false),
                }),
                "output ports (§I.4 出力 column)");
            Assert.That(descriptor.SideEffects,
                Is.EqualTo(new[] { EffectNames.CodeWrite, EffectNames.AssetCreate }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(descriptor.Destructive, Is.False);
            Assert.That(descriptor.Requirements,
                Is.EqualTo(new[] { new Requirement("path_writable", "path") }));

            // The SPEC §I.4 constraint column: content is ConstantOnly —
            // the full source is a Plan constant the approval shows
            // (§G8 item 2) — and overwrite defaults false.
            Assert.That(descriptor.Inputs[1].Constraints?.ConstantOnly, Is.True);
            Assert.That(
                descriptor.Inputs[2].Default?.ToToken()?.ToObject<bool>(), Is.False);
        }

        // §I.4 row of unity.script.compile: no inputs; result Json out;
        // compile; Waitable; Undo None; Idempotent; default timeout 600 s.
        [Test]
        public void CompileDescriptor_MatchesSpecI4Row()
        {
            var build = Build(out _);
            var descriptor = Registered(
                build, CompileScriptsOperation.OperationId).Descriptor;

            Assert.That(descriptor.Inputs, Is.Empty);
            Assert.That(
                descriptor.Outputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(new[] { ("result", "core.Json", false) }),
                "output ports (§I.4 出力 column)");
            Assert.That(descriptor.SideEffects,
                Is.EqualTo(new[] { EffectNames.Compile }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Waitable));
            Assert.That(descriptor.DefaultTimeoutSeconds, Is.EqualTo(600));
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Idempotent));
            Assert.That(descriptor.Destructive, Is.False);
        }

        [Test]
        public void Descriptors_DescribeThemselvesInEnglishAndJapanese()
        {
            var build = Build(out _);

            foreach (var id in new[]
                { CreateScriptOperation.OperationId, CompileScriptsOperation.OperationId })
            {
                var descriptor = Registered(build, id).Descriptor;
                Assert.That(descriptor.Kind, Is.EqualTo(OperationKind.Native), id);
                Assert.That(descriptor.DisplayName, Is.Not.Empty, id);
                Assert.That(descriptor.Summary, Is.Not.Empty, id);
                Assert.That(descriptor.Summary.Length,
                    Is.LessThanOrEqualTo(
                        OperationDescriptorValidator.SummaryMaxLength), id);
                Assert.That(descriptor.Description, Is.Not.Empty, id);
                Assert.That(descriptor.Category, Is.EqualTo("Script"), id);
                Assert.That(descriptor.Keywords, Is.Not.Empty, id);
                Assert.That(descriptor.Tags, Is.Not.Empty, id);
                Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }), id);
                Assert.That(descriptor.Localized.ContainsKey("ja"), Is.True, id);
                Assert.That(descriptor.Localized["ja"].DisplayName, Is.Not.Empty, id);
                Assert.That(descriptor.Localized["ja"].Summary, Is.Not.Empty, id);
                Assert.That(descriptor.Examples, Is.Not.Empty, id);
            }
        }

        // SPEC §C1 item 4 / §H0 item 1: every Example of both Operations is
        // a complete WF document the static Validator accepts with ok:true
        // and no warning. No fixtureManifest: the examples create their own
        // assets and the constant paths sit inside the default codePaths.
        [Test]
        public void Examples_AreCompleteWorkflows_ValidatingWithoutWarnings()
        {
            var build = Build(out _);
            var compiler = Compiler(build);

            foreach (var id in new[]
                { CreateScriptOperation.OperationId, CompileScriptsOperation.OperationId })
            {
                var descriptor = Registered(build, id).Descriptor;
                Assert.That(descriptor.Examples, Is.Not.Empty, id);
                foreach (var example in descriptor.Examples)
                {
                    Assert.That(example.Title, Is.Not.Empty, id);
                    Assert.That(example.FixtureManifest, Is.Null, id);
                    var read = WorkflowSerializer.Read(
                        example.WorkflowJson.CanonicalText);
                    Assert.That(read.Ok, Is.True, () => string.Join(
                        "; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)));

                    var report = compiler.Validate(new ValidationRequest(
                        read.Value!, null, ValidationMode.Static, ActorKind.Agent));

                    Assert.That(report.Ok, Is.True, () => id + ": " + string.Join(
                        "; ", report.Errors.Select(
                            d => d.Code + " " + d.Message + " " + d.JsonPath)));
                    Assert.That(report.Warnings, Is.Empty, () => id + ": " + string.Join(
                        "; ", report.Warnings.Select(
                            d => d.Code + " " + d.Message + " " + d.JsonPath)));
                }
            }
        }

        // Each Example runs its Operation once with every required input
        // supplied and a control edge reaching it.
        [Test]
        public void Examples_AreWiredToTheOperationsTheyIllustrate()
        {
            var build = Build(out _);

            foreach (var id in new[]
                { CreateScriptOperation.OperationId, CompileScriptsOperation.OperationId })
            {
                var descriptor = Registered(build, id).Descriptor;
                var example = descriptor.Examples[0];

                var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
                Assert.That(read.Ok, Is.True);
                var document = read.Value!;
                var illustrated = document.Nodes
                    .Where(n => n.Operation == id)
                    .ToList();

                Assert.That(
                    document.Nodes.Count(n => n.Operation == "core.start"),
                    Is.EqualTo(1), id);
                Assert.That(illustrated, Has.Count.EqualTo(1), id);
                Assert.That(illustrated[0].Version, Is.EqualTo(1), id);
                Assert.That(
                    document.ControlEdges.Any(e =>
                        string.Equals(e.To, illustrated[0].Id, StringComparison.Ordinal)),
                    Is.True, id + ": a control edge must reach the illustrated node");
                foreach (var port in descriptor.Inputs.Where(p => p.Required))
                {
                    Assert.That(illustrated[0].Parameters.ContainsKey(port.Name), Is.True,
                        id + ": the required input '" + port.Name +
                        "' has no value in the Example");
                }
            }
        }
    }
}
