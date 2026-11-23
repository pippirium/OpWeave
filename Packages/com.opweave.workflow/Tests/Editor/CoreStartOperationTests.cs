#nullable enable

// T-W2.5-05 [Edit] tests: local:T-W2.5-05:core-start — the core.start
// standard Operation discovered through TypeCache in the OpWeave.Operations
// assembly registers through the real §C3 stages 1–8 as origin Standard with
// no OW-OP-003 (SPEC §C1 test 1 for the standard set), its Descriptor matches
// the SPEC §I.1 row (read / Immediate / Idempotent, no ports, ["next"]), and
// the canonical snapshot-backed Validator accepts a one-Start workflow and
// the published complete-WF example while rejecting zero- and two-Start
// workflows with the SPEC-defined OW-VAL-001 outcome (SPEC §A2, §D5 item 1).
// The Start-node shape stays with the existing parser/Validator phases; this
// fixture only proves the new descriptor flows through them.
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
    /// local:T-W2.5-05:core-start — descriptor, standard registration and
    /// single-start validation for <c>core.start</c>.
    /// </summary>
    internal sealed class CoreStartOperationTests
    {
        // The assembly name of the standard Operations container (SPEC §I,
        // §0.5): TypeCache yields every [OpWeaveOperation] class in the
        // project, including the fixtures living in this test assembly, so
        // the scan is narrowed to the real standard set — the §C1 test-1
        // contract covers whatever standard Operations the assembly holds.
        private const string OperationsAssembly = "OpWeave.Operations";

        // A minimal StaticContext (Core hosts no implementation, SPEC §0.6
        // SG-06); core.start's own ValidateStatic reports nothing.
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
                throw new NotSupportedException("core.start declares no input ports.");
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

        private static ValidationReport Validate(RegistryBuildResult build, WorkflowDocument document) =>
            Compiler(build).Validate(new ValidationRequest(
                document, null, ValidationMode.Static, ActorKind.Agent));

        private static WorkflowDocument Document(
            string id,
            IEnumerable<NodeData> nodes,
            IEnumerable<ControlEdge> controlEdges)
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = id,
                Nodes = new List<NodeData>(nodes),
                ControlEdges = new List<ControlEdge>(controlEdges),
            });
            Assert.That(created.Ok, Is.True,
                "fixture document must create: " + string.Join(
                    "; ", created.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return created.Value!;
        }

        private static NodeData Node(string id, string operation) =>
            new NodeData { Id = id, Operation = operation, Version = 1 };

        private static RegisteredOperation RegisteredStart(RegistryBuildResult build)
        {
            Assert.That(build.Stale, Is.False, "the registry build must not be stale");
            Assert.That(build.Snapshot, Is.Not.Null);
            var registered = build.Snapshot!.Find("core.start", 1);
            Assert.That(registered, Is.Not.Null, "core.start v1 must register");
            return registered!;
        }

        // SPEC §C1 test 1 / §C3 stage 6–7: the standard Operation registers
        // through the real pipeline with no OW-OP-003 — no registration
        // failure of any kind names it.
        [Test]
        public void CoreStart_RegistersAsStandard_WithoutOwOp003()
        {
            var build = Build(out var publication);

            Assert.That(publication.Snapshot, Is.SameAs(build.Snapshot),
                "stage 8 publishes the complete snapshot atomically");
            Assert.That(build.OperationFailures, Is.Empty,
                () => string.Join("; ", build.OperationFailures.SelectMany(
                    f => f.Diagnostics.Select(d => f.TypeFullName + ": " + d.Code + " " + d.Message))));
            Assert.That(build.Snapshot!.Failures, Is.Empty,
                () => string.Join("; ", build.Snapshot.Failures.SelectMany(
                    f => f.Diagnostics.Select(d => f.OperationId + ": " + d.Code + " " + d.Message))));

            var registered = RegisteredStart(build);
            Assert.That(registered.Origin, Is.EqualTo(OperationOrigin.Standard));
            Assert.That(registered.AssemblyName, Is.EqualTo(OperationsAssembly));
            Assert.That(registered.Implementation, Is.InstanceOf<IOperation>());
            Assert.That(registered.DescriptorHash,
                Is.EqualTo(DescriptorHash.Compute(registered.Descriptor)));
            Assert.That(registered.ImplementationHash, Does.Match("^[0-9a-f]{64}$"));
        }

        // SPEC §I.1 row: read-only, Immediate, Idempotent, no ports, v1
        // ["next"] control output — plus the §C1 English/Japanese text and
        // the complete-WF Example.
        [Test]
        public void CoreStart_DescriptorMatchesSpec()
        {
            var descriptor = RegisteredStart(Build(out _)).Descriptor;

            Assert.That(descriptor.Id, Is.EqualTo("core.start"));
            Assert.That(descriptor.Version, Is.EqualTo(1));
            Assert.That(descriptor.Kind, Is.EqualTo(OperationKind.Native));
            Assert.That(descriptor.Inputs, Is.Empty);
            Assert.That(descriptor.Outputs, Is.Empty);
            Assert.That(descriptor.ControlOutputs, Is.EqualTo(new[] { "next" }));
            Assert.That(descriptor.ExecutionMode, Is.EqualTo(ExecutionMode.Immediate));
            Assert.That(descriptor.SideEffects, Is.EqualTo(new[] { EffectNames.Read }));
            Assert.That(descriptor.Destructive, Is.False);
            Assert.That(descriptor.Undo, Is.EqualTo(UndoSupport.None));
            Assert.That(descriptor.Idempotency, Is.EqualTo(Idempotency.Idempotent));
            Assert.That(descriptor.Requirements, Is.Empty);
            Assert.That(descriptor.OutputRefinement, Is.Null);

            Assert.That(descriptor.DisplayName, Is.Not.Empty);
            Assert.That(descriptor.Summary, Is.Not.Empty);
            Assert.That(descriptor.Summary.Length,
                Is.LessThanOrEqualTo(OperationDescriptorValidator.SummaryMaxLength));
            Assert.That(descriptor.Description, Is.Not.Empty);
            Assert.That(descriptor.Keywords, Is.Not.Empty);
            Assert.That(descriptor.Localized.ContainsKey("ja"), Is.True,
                "a Japanese display entry is required (SPEC §C1 item 1, R8)");
            Assert.That(descriptor.Localized["ja"].DisplayName, Is.Not.Empty);
            Assert.That(descriptor.Localized["ja"].Summary, Is.Not.Empty);

            Assert.That(descriptor.Examples, Has.Count.EqualTo(1));
            Assert.That(descriptor.Examples[0].Title, Is.Not.Empty);
        }

        // The Validator accepts the complete-WF fixture carrying exactly one
        // Start node — the §D5 item 1 invariant holds end to end through the
        // registered descriptor.
        [Test]
        public void OneStartWorkflow_Validates()
        {
            var build = Build(out _);
            var document = Document(
                "example.one-start",
                new[] { Node("start", "core.start") },
                Array.Empty<ControlEdge>());

            var report = Validate(build, document);

            Assert.That(report.Ok, Is.True,
                () => string.Join("; ", report.Errors.Select(d => d.Code + " " + d.Message)));
        }

        // The Descriptor's published Example is a complete WF that passes
        // the canonical Validator as written (SPEC §C1 item 4, §C8).
        [Test]
        public void PublishedExample_Validates()
        {
            var build = Build(out _);
            var examples = RegisteredStart(build).Descriptor.Examples;
            Assert.That(examples, Is.Not.Empty);

            var read = WorkflowSerializer.Read(examples[0].WorkflowJson.CanonicalText);
            Assert.That(read.Ok, Is.True,
                () => string.Join("; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)));

            var report = Validate(build, read.Value!);

            Assert.That(report.Ok, Is.True,
                () => string.Join("; ", report.Errors.Select(d => d.Code + " " + d.Message)));
        }

        // SPEC §A2 / §D5 item 1: zero Start nodes is OW-VAL-001 — the
        // Validator rejects the workflow; the Operation introduces neither a
        // duplicate Start nor a new code.
        [Test]
        public void ZeroStartWorkflow_RejectedWithOwVal001()
        {
            var build = Build(out _);
            var document = Document(
                "example.zero-start",
                new[] { Node("a", "example.placeholder") },
                Array.Empty<ControlEdge>());

            var report = Validate(build, document);

            Assert.That(report.Ok, Is.False);
            Assert.That(
                report.Errors.Any(d => d.Code == ErrorCodes.OW_VAL_001), Is.True,
                "zero Start nodes must fail with OW-VAL-001");
        }

        [Test]
        public void TwoStartWorkflow_RejectedWithOwVal001()
        {
            var build = Build(out _);
            var document = Document(
                "example.two-start",
                new[] { Node("start", "core.start"), Node("second", "core.start") },
                Array.Empty<ControlEdge>());

            var report = Validate(build, document);

            Assert.That(report.Ok, Is.False);
            var diagnostic = report.Errors.Single(d => d.Code == ErrorCodes.OW_VAL_001);
            Assert.That(diagnostic.NodeId, Is.EqualTo("second"));
        }
    }
}
