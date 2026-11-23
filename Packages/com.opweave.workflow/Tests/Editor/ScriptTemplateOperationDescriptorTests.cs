#nullable enable

// T-W4.2-01 [Edit] tests: local:T-W4.2-01:descriptors-examples —
// unity.script.create_from_template, discovered through TypeCache in the
// OpWeave.Operations assembly, registers through the real §C3 stages 1–8
// as origin Standard with no OW-OP-003 (SPEC §C1 test 1), its Descriptor
// matches the SPEC §I.4 row, and every Example is a complete WF document
// the snapshot-backed static Validator accepts with ok:true and no
// warning (SPEC §C1 item 4, §H0 item 1).
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
    /// local:T-W4.2-01:descriptors-examples — standard registration, SPEC
    /// §I.4 row conformance and Example validation for
    /// <c>unity.script.create_from_template</c>.
    /// </summary>
    internal sealed class ScriptTemplateOperationDescriptorTests
    {
        private const string OperationsAssembly = "OpWeave.Operations";

        private static readonly string[] OperationIds =
        {
            CreateScriptFromTemplateOperation.OperationId,
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
            public override T Get<T>(string port)
            {
                if (_parameters.TryGetValue(port, out var input) && input.IsPresent)
                {
                    var token = input.Value!.ToToken();
                    if (typeof(T) == typeof(string) &&
                        token.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    {
                        return (T)(object)((string)((Newtonsoft.Json.Linq.JValue)token).Value!);
                    }
                }
                throw new NotSupportedException("unavailable static value: " + port);
            }
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

        // SPEC §C1 test 1 / §C3 stages 6–8: registers as Standard without
        // OW-OP-003 or any other registration failure.
        [Test]
        public void ScriptOperation_RegistersAsStandard_WithoutOwOp003()
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

            var registered = Registered(build, CreateScriptFromTemplateOperation.OperationId);
            Assert.That(registered.Origin, Is.EqualTo(OperationOrigin.Standard));
            Assert.That(registered.AssemblyName, Is.EqualTo(OperationsAssembly));
            Assert.That(registered.Implementation, Is.InstanceOf<IOperation>());
            Assert.That(registered.DescriptorHash,
                Is.EqualTo(DescriptorHash.Compute(registered.Descriptor)));
            Assert.That(registered.ImplementationHash, Does.Match("^[0-9a-f]{64}$"));
        }

        // §I.4 row: template* (enum), className* and namespace String with
        // identifier patterns, folder* AssetPath in; script AssetRef<
        // MonoScript> and typeName TypeName out; code.template +
        // asset.create; Immediate; Undo None; Reconcilable.
        [Test]
        public void Descriptor_MatchesSpecI4Row()
        {
            var build = Build(out _);
            var descriptor = Registered(
                build, CreateScriptFromTemplateOperation.OperationId).Descriptor;

            Assert.That(
                descriptor.Inputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(new[]
                {
                    ("template", "core.String", true),
                    ("className", "core.String", true),
                    ("namespace", "core.String", false),
                    ("folder", "unity.AssetPath", true),
                }),
                "input ports (§I.4 入力 column)");
            Assert.That(
                descriptor.Outputs.Select(p => (p.Name, p.Type, p.Required)).ToArray(),
                Is.EqualTo(new[]
                {
                    ("script", "unity.AssetRef<unity.MonoScript>", false),
                    ("typeName", "unity.TypeName", false),
                }),
                "output ports (§I.4 出力 column)");
            Assert.That(descriptor.SideEffects,
                Is.EqualTo(new[] { EffectNames.CodeTemplate, EffectNames.AssetCreate }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Reconcilable));
            Assert.That(descriptor.Destructive, Is.False);
            Assert.That(descriptor.Requirements,
                Is.EqualTo(new[] { new Requirement("path_writable", "folder") }));

            var template = descriptor.Inputs[0];
            Assert.That(template.Constraints?.Enum,
                Is.EqualTo(new[]
                {
                    "mono_behaviour", "scriptable_object", "plain_class",
                }),
                "the §I.4 enum column for template");
            Assert.That(descriptor.Inputs[1].Constraints?.Pattern, Is.Not.Null,
                "className carries the identifier constraint");
            Assert.That(descriptor.Inputs[2].Constraints?.Pattern, Is.Not.Null,
                "namespace carries the identifier constraint");
        }

        [Test]
        public void Descriptor_DescribesItselfInEnglishAndJapanese()
        {
            var build = Build(out _);
            var descriptor = Registered(
                build, CreateScriptFromTemplateOperation.OperationId).Descriptor;

            Assert.That(descriptor.Kind, Is.EqualTo(OperationKind.Native));
            Assert.That(descriptor.DisplayName, Is.Not.Empty);
            Assert.That(descriptor.Summary, Is.Not.Empty);
            Assert.That(descriptor.Summary.Length,
                Is.LessThanOrEqualTo(OperationDescriptorValidator.SummaryMaxLength));
            Assert.That(descriptor.Description, Is.Not.Empty);
            Assert.That(descriptor.Category, Is.EqualTo("Script"));
            Assert.That(descriptor.Keywords, Is.Not.Empty);
            Assert.That(descriptor.Tags, Is.Not.Empty);
            Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }));
            Assert.That(descriptor.OutputRefinement, Is.Null);
            Assert.That(descriptor.Localized.ContainsKey("ja"), Is.True);
            Assert.That(descriptor.Localized["ja"].DisplayName, Is.Not.Empty);
            Assert.That(descriptor.Localized["ja"].Summary, Is.Not.Empty);
            Assert.That(descriptor.Examples, Is.Not.Empty);
        }

        // SPEC §C1 item 4 / §H0 item 1: the Example is a complete WF
        // document the static Validator accepts with ok:true and no
        // warning. No fixtureManifest: the operation creates its own asset.
        [Test]
        public void Example_IsACompleteWorkflow_ValidatingWithoutWarnings()
        {
            var build = Build(out _);
            var compiler = Compiler(build);
            var descriptor = Registered(
                build, CreateScriptFromTemplateOperation.OperationId).Descriptor;

            Assert.That(descriptor.Examples, Has.Count.EqualTo(1));
            var example = descriptor.Examples[0];
            Assert.That(example.Title, Is.Not.Empty);
            Assert.That(example.FixtureManifest, Is.Null);

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

        // The Example runs this Operation once with every required input
        // supplied and a control edge reaching it.
        [Test]
        public void Example_IsWiredToTheOperationItIllustrates()
        {
            var build = Build(out _);
            var descriptor = Registered(
                build, CreateScriptFromTemplateOperation.OperationId).Descriptor;
            var example = descriptor.Examples[0];

            var read = WorkflowSerializer.Read(example.WorkflowJson.CanonicalText);
            Assert.That(read.Ok, Is.True);
            var document = read.Value!;
            var illustrated = document.Nodes
                .Where(n => n.Operation == CreateScriptFromTemplateOperation.OperationId)
                .ToList();

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
