#nullable enable

// T-W2.8-01 [Core] tests (local:T-W2.8-01:dry-run, Core half): the D8 Dry
// Run phase of SPEC §D8 runs each active step's Preflight in the D5
// execution order, hands data-edge-fed ports their pending producer
// (ctx.IsPending with the producing nodeId), stamps nodeId onto the
// operation-returned PlannedChange entries, and the DryRun report carries
// the list with guarantee "none" while a Static report carries neither.
// Play Mode, a background thread or a compiler without the environment
// probes is OW-RUN-001 with no Preflight; a thrown exception is
// OW-RUN-006; an Error diagnostic excludes only its node (SPEC §D1
// item 2).
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
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Tests
{
    internal sealed class DryRunValidationPhaseTests
    {
        private static readonly ITypeRegistry s_types = TypeRegistryBuilder.Build(
            new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
            }).Registry;

        private sealed class FakeEditorState : IEditorState
        {
            public bool IsEditMode { get; set; } = true;
            public bool IsCompiling { get; set; }
            public bool IsUpdating { get; set; }
        }

        // The §D8 main-thread gate precedes the Editor-state probe read —
        // the Unity flags behind it are main-thread-only — so this double
        // fails the test if a refused Dry Run reads it at all.
        private sealed class UnreadableEditorState : IEditorState
        {
            public bool IsEditMode =>
                throw new InvalidOperationException("The probe was read.");
            public bool IsCompiling =>
                throw new InvalidOperationException("The probe was read.");
            public bool IsUpdating =>
                throw new InvalidOperationException("The probe was read.");
        }

        private sealed class MapTypeProbe : IStaticTypeProbe
        {
            public bool TypeExists(string typeName) => true;
        }

        // A recording Immediate: captures the Preflight call's pending view
        // and answers the configured diagnostics / planned changes / result.
        private sealed class RecordingOperation : IOperation
        {
            private readonly List<string> _calls;
            private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
            private readonly List<PlannedChange> _changes = new List<PlannedChange>();
            private readonly bool _returnNull;
            private readonly Exception? _throw;

            internal RecordingOperation(
                OperationDescriptor descriptor, List<string> calls,
                bool returnNull = false, Exception? thrown = null)
            {
                Descriptor = descriptor;
                _calls = calls;
                _returnNull = returnNull;
                _throw = thrown;
            }

            public OperationDescriptor Descriptor { get; }
            internal List<bool> PendingSeen { get; } = new List<bool>();
            internal List<string> PendingGetMessages { get; } = new List<string>();
            internal List<string?> ValuesSeen { get; } = new List<string?>();

            internal void AddDiagnostic(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);
            internal void AddChange(PlannedChange change) => _changes.Add(change);

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx)
            {
                _calls.Add(Descriptor.Id);
                if (_throw is not null)
                {
                    throw _throw;
                }
                foreach (var port in Descriptor.Inputs)
                {
                    PendingSeen.Add(ctx.IsPending(port.Name));
                    if (ctx.IsPending(port.Name))
                    {
                        try
                        {
                            ctx.Get<JsonValue>(port.Name);
                        }
                        catch (InvalidOperationException e)
                        {
                            PendingGetMessages.Add(e.Message);
                        }
                    }
                    else
                    {
                        ValuesSeen.Add(ctx.IsPresent(port.Name)
                            ? ctx.Get<JsonValue>(port.Name).CanonicalText
                            : null);
                    }
                }
                return _returnNull
                    ? null!
                    : new PreflightResult(
                        _diagnostics, _changes, JsonValue.Parse("{}").Value!);
            }

            public StepResult Execute(StepContext ctx) => throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) => throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private static JsonValue Json(string text) => JsonValue.Parse(text).Value!;

        private static OperationCandidate Candidate(IOperation operation) =>
            new OperationCandidate(
                operation.Descriptor,
                OperationOrigin.Standard,
                "Acme.Editor",
                "Assets/Acme/Operation.cs",
                new string('a', 64),
                HasPollImplementation: true,
                Implementation: operation);

        private static RegistrySnapshot Snapshot(params OperationCandidate[] candidates) =>
            RegistrySnapshotBuilder.Build(candidates, s_types);

        private static OperationDescriptor StartDescriptor() =>
            Op.Define("core.start", 1)
                .Summary("The workflow entry point.")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();

        private static NodeData Node(
            string id, string operation, int version = 1,
            IDictionary<string, JToken>? parameters = null) =>
            new NodeData { Id = id, Operation = operation, Version = version, Parameters = parameters };

        private static WorkflowDocument Doc(
            string id,
            IReadOnlyList<NodeData> nodes,
            IReadOnlyList<ControlEdge> controlEdges,
            IReadOnlyList<DataEdge>? dataEdges = null)
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = id,
                Nodes = new List<NodeData>(nodes),
                ControlEdges = new List<ControlEdge>(controlEdges),
                DataEdges = dataEdges is null ? null : new List<DataEdge>(dataEdges),
            });
            Assert.IsTrue(created.Ok, "fixture workflow document must be valid");
            return created.Value!;
        }

        // The §D8 item 3 environment probes the internal constructor binds:
        // omitted means Edit Mode on the main thread — a probe-free Dry Run
        // is exercised through the public constructor under a scope that
        // uninstalls the domain's DryRunEnvironment.
        private static ValidationReport DryRun(
            RegistrySnapshot snapshot, WorkflowDocument document,
            JsonValue? inputs = null, IEditorState? editorState = null,
            Func<bool>? isMainThread = null) =>
            new WorkflowCompiler(
                    snapshot,
                    (nodeId, parameters) => new OperationContractFixtures.Static(nodeId, parameters),
                    new MapTypeProbe(),
                    new PlanProvenanceFixtures.Stub(),
                    dryRunContextFactory: null,
                    dryRunEditorState: editorState ?? new FakeEditorState(),
                    dryRunIsMainThread: isMainThread ?? (static () => true))
                .Validate(new ValidationRequest(
                    document, inputs, ValidationMode.DryRun, ActorKind.Agent));

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));

        // ── SPEC §D8 ────────────────────────────────────────────────────

        // Dry Run calls Preflight in execution order, the edge-fed port is a
        // pending value naming its producer, constants arrive resolved, the
        // report lists the stamped planned changes with guarantee "none",
        // and the Plan still builds for an error-free document.
        [Test]
        public void DryRunRunsPreflightInOrderWithPendingValuesAndReportsChanges()
        {
            var calls = new List<string>();
            var start = new RecordingOperation(StartDescriptor(), calls);
            var source = new RecordingOperation(
                Op.Define("test.d8.source", 1)
                    .Summary("Produce a value.")
                    .Output("out", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                calls);
            var sink = new RecordingOperation(
                Op.Define("test.d8.sink", 1)
                    .Summary("Consume a value.")
                    .Input("fed", "core.String", required: true)
                    .Input("name", "core.String", required: true)
                    .Effects(EffectNames.AssetCreate)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                calls);
            sink.AddChange(new PlannedChange(
                "whoever", "create", "Assets/d8/out.txt", EffectNames.AssetCreate));
            var snapshot = Snapshot(
                Candidate(start), Candidate(source), Candidate(sink));
            var doc = Doc(
                "example.dryrun",
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "test.d8.source"),
                    Node("b", "test.d8.sink",
                        parameters: new Dictionary<string, JToken>
                        {
                            ["name"] = new JValue("artifact"),
                        }),
                },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[]
                {
                    new DataEdge(new PortRef("a", "out"), new PortRef("b", "fed")),
                });

            var report = DryRun(snapshot, doc);

            Assert.IsTrue(report.Ok, "dry run failed: " + Describe(report.Errors));
            Assert.IsNotNull(report.Plan, "an error-free dry run still builds the Plan");

            // SPEC §D8: Preflight ran once per step, in execution order.
            Assert.That(calls, Is.EqualTo(
                new[] { "core.start", "test.d8.source", "test.d8.sink" }));

            // §D8 item 1: the edge-fed port was pending with its producing
            // nodeId; the constant arrived as a normal present value.
            Assert.That(sink.PendingSeen, Is.EqualTo(new[] { true, false }));
            Assert.That(sink.PendingGetMessages, Has.Count.EqualTo(1));
            Assert.That(sink.PendingGetMessages[0], Does.Contain("'a'"),
                "the pending value must carry its producing node id");
            Assert.That(sink.ValuesSeen, Is.EqualTo(new string?[] { "\"artifact\"" }));

            // §D8 items 2-3: the report lists each predicted change stamped
            // with the step's node id, with guarantee "none".
            Assert.IsNotNull(report.PlannedChanges);
            Assert.That(report.PlannedChanges!, Has.Count.EqualTo(1));
            var change = report.PlannedChanges![0];
            Assert.That(change.NodeId, Is.EqualTo("b"));
            Assert.That(change.Kind, Is.EqualTo("create"));
            Assert.That(change.Target, Is.EqualTo("Assets/d8/out.txt"));
            Assert.That(change.Effect, Is.EqualTo("asset.create"));
            Assert.That(report.Guarantee, Is.EqualTo("none"));
        }

        // A Static validation carries neither planned changes nor a
        // guarantee marker — and calls no Preflight (SPEC §D1 item 1).
        [Test]
        public void StaticModeReportsNoPlannedChangesAndNoGuarantee()
        {
            var calls = new List<string>();
            var start = new RecordingOperation(StartDescriptor(), calls);
            var snapshot = Snapshot(Candidate(start));
            var doc = Doc(
                "example.dryrun-static",
                new[] { Node("start", "core.start") },
                Array.Empty<ControlEdge>());

            var report = new WorkflowCompiler(
                    snapshot,
                    (nodeId, parameters) =>
                        new OperationContractFixtures.Static(nodeId, parameters),
                    new MapTypeProbe(),
                    new PlanProvenanceFixtures.Stub())
                .Validate(new ValidationRequest(
                    doc, null, ValidationMode.Static, ActorKind.Agent));

            Assert.IsTrue(report.Ok, "static validation failed: " + Describe(report.Errors));
            Assert.IsNull(report.PlannedChanges);
            Assert.IsNull(report.Guarantee);
            Assert.IsEmpty(calls, "a Static request never reaches the D8 phase");
        }

        // §D8 item 3: Play Mode refuses the whole Dry Run with OW-RUN-001 —
        // no Preflight runs — and the report still carries the marker list.
        [Test]
        public void DryRunInPlayModeReportsOwRun001AndCallsNoPreflight()
        {
            var calls = new List<string>();
            var start = new RecordingOperation(StartDescriptor(), calls);
            var snapshot = Snapshot(Candidate(start));
            var doc = Doc(
                "example.dryrun-playmode",
                new[] { Node("start", "core.start") },
                Array.Empty<ControlEdge>());
            var editorState = new FakeEditorState { IsEditMode = false };

            var report = DryRun(snapshot, doc, editorState: editorState);

            Assert.IsFalse(report.Ok);
            var error = report.Errors.SingleOrDefault(d => d.Code == ErrorCodes.OW_RUN_001);
            Assert.IsNotNull(error,
                "expected OW-RUN-001, got: " + Describe(report.Errors));
            Assert.IsEmpty(calls, "no Preflight may run outside Edit Mode");
            Assert.IsNotNull(report.PlannedChanges);
            Assert.IsEmpty(report.PlannedChanges!);
            Assert.That(report.Guarantee, Is.EqualTo("none"));
        }

        // §D8 item 3 fail-closed: the public snapshot-backed constructor
        // binds whatever DryRunEnvironment the domain registered — under
        // the Unity Editor the InitializeOnLoad bootstrap has installed the
        // real probes — so this test holds the uninstalled reading with an
        // override scope: bound to no environment probes, a Dry Run through
        // the public entry point cannot prove the Edit-Mode main-thread
        // boundary and answers OW-RUN-001 with no Preflight, whatever mode
        // the Editor is in.
        [Test]
        public void PublicConstructorWithoutEnvironmentProbesRefusesDryRun()
        {
            var calls = new List<string>();
            var start = new RecordingOperation(StartDescriptor(), calls);
            var snapshot = Snapshot(Candidate(start));
            var doc = Doc(
                "example.dryrun-unprobed",
                new[] { Node("start", "core.start") },
                Array.Empty<ControlEdge>());

            // The constructor reads the registered environment once, so the
            // scope only has to span construction — and under pure .NET,
            // where the bootstrap never installed one, it is a no-op.
            using (DryRunEnvironment.OverrideForTest(null))
            {
                var report = new WorkflowCompiler(
                        snapshot,
                        (nodeId, parameters) =>
                            new OperationContractFixtures.Static(nodeId, parameters),
                        new MapTypeProbe(),
                        new PlanProvenanceFixtures.Stub())
                    .Validate(new ValidationRequest(
                        doc, null, ValidationMode.DryRun, ActorKind.Agent));

                Assert.IsFalse(report.Ok);
                var error = report.Errors.SingleOrDefault(d => d.Code == ErrorCodes.OW_RUN_001);
                Assert.IsNotNull(error,
                    "expected OW-RUN-001, got: " + Describe(report.Errors));
                Assert.IsEmpty(calls, "no Preflight may run without the environment gate");
                Assert.IsNotNull(report.PlannedChanges);
                Assert.IsEmpty(report.PlannedChanges!);
                Assert.That(report.Guarantee, Is.EqualTo("none"));
            }
        }

        // §D8 item 3: off the main thread the Dry Run answers OW-RUN-001
        // and no Preflight runs — and the probe read itself never happens
        // on the wrong thread (UnreadableEditorState throws if touched).
        [Test]
        public void DryRunOffMainThreadReportsOwRun001AndCallsNoPreflight()
        {
            var calls = new List<string>();
            var start = new RecordingOperation(StartDescriptor(), calls);
            var snapshot = Snapshot(Candidate(start));
            var doc = Doc(
                "example.dryrun-offmain",
                new[] { Node("start", "core.start") },
                Array.Empty<ControlEdge>());

            var report = DryRun(
                snapshot, doc,
                editorState: new UnreadableEditorState(),
                isMainThread: () => false);

            Assert.IsFalse(report.Ok);
            var error = report.Errors.SingleOrDefault(d => d.Code == ErrorCodes.OW_RUN_001);
            Assert.IsNotNull(error,
                "expected OW-RUN-001, got: " + Describe(report.Errors));
            Assert.IsEmpty(calls, "no Preflight may run off the main thread");
            Assert.IsNotNull(report.PlannedChanges);
            Assert.IsEmpty(report.PlannedChanges!);
            Assert.That(report.Guarantee, Is.EqualTo("none"));
        }

        // §D1 item 2 / §D8: an Error-severity Preflight diagnostic excludes
        // its node — the remaining steps are still preflighted.
        [Test]
        public void PreflightErrorExcludesOnlyItsNode()
        {
            var calls = new List<string>();
            var start = new RecordingOperation(StartDescriptor(), calls);
            var failing = new RecordingOperation(
                Op.Define("test.d8.failing", 1)
                    .Summary("Reports a precondition violation.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                calls);
            failing.AddDiagnostic(ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target path is already occupied."));
            var after = new RecordingOperation(
                Op.Define("test.d8.after", 1)
                    .Summary("Runs after the failing step.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                calls);
            var snapshot = Snapshot(
                Candidate(start), Candidate(failing), Candidate(after));
            var doc = Doc(
                "example.dryrun-failure",
                new[]
                {
                    Node("start", "core.start"),
                    Node("bad", "test.d8.failing"),
                    Node("next", "test.d8.after"),
                },
                new[]
                {
                    new ControlEdge("start", "bad"),
                    new ControlEdge("bad", "next"),
                });

            var report = DryRun(snapshot, doc);

            Assert.IsFalse(report.Ok);
            var error = report.Errors.Single(d => d.Code == ErrorCodes.OW_UNITY_005);
            Assert.That(error.NodeId, Is.EqualTo("bad"),
                "the phase stamps the node id onto node-less preflight diagnostics");
            Assert.That(calls, Is.EqualTo(
                new[] { "core.start", "test.d8.failing", "test.d8.after" }),
                "the remaining nodes stay checked (SPEC §D1 item 2)");
        }

        // §0.6 SG-06: an Operation exception is OW-RUN-006; a missing result
        // is OW-RUN-006 too — neither fabricates planned changes.
        [Test]
        public void PreflightThrowOrNullResultIsOwRun006()
        {
            var calls = new List<string>();
            var start = new RecordingOperation(StartDescriptor(), calls);
            var throwing = new RecordingOperation(
                Op.Define("test.d8.throwing", 1)
                    .Summary("Throws from Preflight.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                calls,
                thrown: new InvalidOperationException("fixture"));
            var snapshot = Snapshot(Candidate(start), Candidate(throwing));
            var doc = Doc(
                "example.dryrun-throw",
                new[] { Node("start", "core.start"), Node("boom", "test.d8.throwing") },
                new[] { new ControlEdge("start", "boom") });

            var report = DryRun(snapshot, doc);

            var error = report.Errors.Single(d => d.Code == ErrorCodes.OW_RUN_006);
            Assert.That(error.NodeId, Is.EqualTo("boom"));
            Assert.That(error.Message, Does.Not.Contain("fixture"),
                "the exception text may carry secrets and never crosses");

            var nullResult = new RecordingOperation(
                Op.Define("test.d8.nullresult", 1)
                    .Summary("Returns no Preflight result.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
                calls,
                returnNull: true);
            var snapshot2 = Snapshot(Candidate(start), Candidate(nullResult));
            var doc2 = Doc(
                "example.dryrun-null",
                new[] { Node("start", "core.start"), Node("none", "test.d8.nullresult") },
                new[] { new ControlEdge("start", "none") });

            var report2 = DryRun(snapshot2, doc2);
            Assert.That(
                report2.Errors.Count(d => d.Code == ErrorCodes.OW_RUN_006), Is.EqualTo(1));
        }
    }
}
