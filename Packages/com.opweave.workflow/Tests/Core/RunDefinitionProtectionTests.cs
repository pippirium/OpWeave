#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests of the SPEC §D0.3 definition-mutation guard
    /// (local:T-W4.2-06:run-mutation-guard): the state predicate over the
    /// SPEC §E1 terminal/non-terminal partition, the per-Run
    /// protected-definition derivation (used Operation assemblies and
    /// packages, used Type/Codec defining assemblies, the fail-closed
    /// Uncertain flag) and <see cref="RunCoordinator.CollectProtectedRuns"/>
    /// — every non-terminal state collected (Queued, AwaitingApproval,
    /// Running, Suspended, NeedsReview), terminal Runs excluded, the Run's
    /// own compiled Plan passed through. The same sources run under dotnet
    /// and Unity Mono.
    /// </summary>
    internal sealed class RunDefinitionProtectionTests
    {
        private const string Session = "session-protection-test";
        private const string WorkflowId = "example.protection";

        private string _root = null!;
        private readonly List<Fixture> _fixtures = new List<Fixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RunProtection." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.CoordinatorCreated?.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private Fixture NewFixture()
        {
            var fixture = new Fixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        // ── Fixtures ────────────────────────────────────────────────────

        private sealed class FakeEditorState : IEditorState
        {
            public bool IsEditMode { get; set; } = true;
            public bool IsCompiling { get; set; }
            public bool IsUpdating { get; set; }
        }

        private sealed class FakeClock : IClock
        {
            public DateTimeOffset UtcNow { get; set; } =
                new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        }

        private sealed class FakeRunStore : IRunStore
        {
            public Result<Unit> CreateRun(
                string runId, CompiledPlan plan, WorkflowDocument doc) =>
                Result<Unit>.Success(Unit.Value);

            public Result<Unit> Append(
                string runId, JournalRecord record, Durability durability) =>
                Result<Unit>.Success(Unit.Value);

            public Result<RunSnapshot> Load(string runId) =>
                Result<RunSnapshot>.Failure(
                    ErrorCodes.Create(ErrorCodes.OW_STORE_001, "not wired"));

            public Result<IReadOnlyList<RunIndexEntry>> List(RunQuery query) =>
                Result<IReadOnlyList<RunIndexEntry>>.Success(
                    Array.Empty<RunIndexEntry>());

            public Result<Unit> Delete(string runId) =>
                Result<Unit>.Success(Unit.Value);
        }

        private sealed class Fixture
        {
            private readonly string _root;
            internal readonly FakeRunStore Store = new FakeRunStore();
            internal readonly FakeEditorState Editor = new FakeEditorState();
            internal readonly FakeClock Clock = new FakeClock();
            internal readonly List<RegisteredOperation> Operations =
                new List<RegisteredOperation>();

            private RunCoordinator? _coordinator;

            internal Fixture(string root)
            {
                _root = root;
            }

            internal RunCoordinator? CoordinatorCreated => _coordinator;

            internal RunCoordinator Coordinator => _coordinator ??= new RunCoordinator(
                Store,
                (runId, plan, document, actor) => Store.CreateRun(runId, plan, document),
                Editor,
                Clock,
                Snapshot,
                () => CoordinatorRunLock.TryAcquire(_root),
                Session);

            internal RegistrySnapshot Snapshot() =>
                new RegistrySnapshot(
                    "reg_01J00000000000000000000000",
                    new string('f', 64),
                    Operations,
                    DescriptorTestFixtures.CreateRegistry(),
                    Array.Empty<RegistryChange>(),
                    Array.Empty<TypeRegistryFailure>());
        }

        private static string Hash64(char c) => new string(c, 64);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        private static OperationDescriptor Descriptor(string id, int version) =>
            Op.Define(id, version).Summary("test operation").Build();

        private static RegisteredOperation Operation(
            string id, int version, string assembly, string? package = null) =>
            new RegisteredOperation(
                Descriptor(id, version), OperationOrigin.Standard, assembly,
                null, Hash64('d'), Hash64('e'))
            {
                PackageId = package,
            };

        private static PlanStep Step(
            int index, string nodeId, string operationId, int version) =>
            new PlanStep(
                index, nodeId, operationId, version, Hash64('d'), Hash64('e'),
                new Dictionary<string, InputSource>(),
                new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                30, new RetryPolicy(), Idempotency.Unsafe);

        // A minimal plan whose planHashInput carries the §0.6 SG-12
        // fingerprint members the derivation reads.
        private static CompiledPlan Plan(
            PlanStep[]? steps = null,
            string? usedTypeFingerprints = "[]",
            string? usedCodecFingerprints = "[]",
            int approvalCount = 0)
        {
            var approvals = new List<ApprovalRequirement>();
            for (var i = 0; i < approvalCount; i++)
            {
                approvals.Add(new ApprovalRequirement(
                    "node" + i, "asset.modify", "example.op@1"));
            }
            var fingerprintMembers =
                usedTypeFingerprints is null && usedCodecFingerprints is null
                    ? ""
                    : ",\"usedCodecFingerprints\":" + (usedCodecFingerprints ?? "[]") +
                      ",\"usedTypeFingerprints\":" + (usedTypeFingerprints ?? "[]");
            var hashInput = Json(
                "{\"canonicalJsonVersion\":1,\"environmentIdentifiers\":{}," +
                "\"executionInputs\":{},\"requiredEffects\":[]," +
                "\"resolvedWorkflow\":{\"id\":\"" + WorkflowId + "\"},\"retry\":[]," +
                "\"steps\":[],\"timeouts\":[],\"transitions\":[]," +
                "\"usedOperationFingerprints\":[]" + fingerprintMembers + "}");
            return new CompiledPlan(
                "plan_01J00000000000000000000000",
                Hash64('a'), WorkflowId, Hash64('b'), Hash64('c'),
                Json("{}"),
                steps ?? new[] { Step(0, "start", "core.start", 1) },
                Array.Empty<PlanOutput>(),
                Array.Empty<string>(), approvals,
                Array.Empty<PlanSourceMapEntry>(), hashInput);
        }

        private static WorkflowDocument Doc()
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = WorkflowId,
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                },
                ControlEdges = new List<ControlEdge>(),
            });
            Assert.IsTrue(created.Ok, "WorkflowDocument.Create failed.");
            return created.Value!;
        }

        private static RunSnapshot Snapshot(
            string runId, CompiledPlan plan, RunState state, bool ended = false) =>
            new RunSnapshot(
                runId, plan, state, Array.Empty<StepSnapshot>(), null,
                Array.Empty<RunResource>(), false, false,
                null, null, Session, lastSeq: 1, ended: ended);

        // ── SPEC §D0.3 item 1 — the state predicate ─────────────────────

        // Every non-terminal state is protected; the three terminal states
        // are not (SPEC §D0.3 item 1, §E1).
        [Test]
        public void IsProtectedStateSplitsTheTerminalPartition()
        {
            Assert.IsTrue(RunDefinitionProtection.IsProtectedState(RunState.Queued));
            Assert.IsTrue(RunDefinitionProtection.IsProtectedState(RunState.AwaitingApproval));
            Assert.IsTrue(RunDefinitionProtection.IsProtectedState(RunState.Running));
            Assert.IsTrue(RunDefinitionProtection.IsProtectedState(RunState.Suspended));
            Assert.IsTrue(RunDefinitionProtection.IsProtectedState(RunState.NeedsReview));
            Assert.IsFalse(RunDefinitionProtection.IsProtectedState(RunState.Succeeded));
            Assert.IsFalse(RunDefinitionProtection.IsProtectedState(RunState.Failed));
            Assert.IsFalse(RunDefinitionProtection.IsProtectedState(RunState.Cancelled));
        }

        // ── SPEC §D0.3 item 2 — protected-set derivation ────────────────

        // The protected set carries every distinct used Operation's
        // defining assembly and package plus the defining assemblies the
        // persisted Type/Codec fingerprints name — the whole DLL, not the
        // used members only.
        [Test]
        public void ForRunCollectsOperationAndFingerprintDefinitions()
        {
            var registry = new RegistrySnapshot(
                "reg_01J00000000000000000000000",
                new string('f', 64),
                new List<RegisteredOperation>
                {
                    Operation("asset.move", 1, "Example.Operations", "com.example.ops"),
                    Operation("asset.copy", 1, "Example.Operations", "com.example.ops"),
                    Operation("unity.scene.create", 2, "Example.Extra"),
                },
                DescriptorTestFixtures.CreateRegistry(),
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());
            var plan = Plan(
                new[]
                {
                    Step(0, "a", "asset.move", 1),
                    Step(1, "b", "asset.move", 1), // duplicate op — counted once
                    Step(2, "c", "unity.scene.create", 2),
                },
                usedTypeFingerprints:
                    "[{\"id\":\"example.T\",\"version\":1,\"fingerprintHash\":\"" + Hash64('1') +
                    "\",\"definingAssemblyName\":\"Example.Types\"}]",
                usedCodecFingerprints:
                    "[{\"id\":\"example.C\",\"version\":1,\"fingerprintHash\":\"" + Hash64('2') +
                    "\",\"definingAssemblyName\":\"Example.Codecs\"}]");
            var run = new ProtectedRun("run_prot", plan);

            var definitions = RunDefinitionProtection.ForRun(run, registry);

            Assert.IsFalse(definitions.Uncertain);
            Assert.AreEqual("run_prot", definitions.RunId);
            CollectionAssert.AreEquivalent(
                new[] { "Example.Operations", "Example.Extra", "Example.Types", "Example.Codecs" },
                definitions.AssemblyNames);
            CollectionAssert.AreEquivalent(
                new[] { "com.example.ops" }, definitions.PackageIds);
        }

        // SPEC §D0.3 item 3: a used Operation the current Registry no
        // longer resolves leaves the protected set unprovable — Uncertain.
        [Test]
        public void ForRunIsUncertainWhenAUsedOperationNoLongerResolves()
        {
            var registry = new RegistrySnapshot(
                "reg_01J00000000000000000000000",
                new string('f', 64),
                new List<RegisteredOperation>
                {
                    Operation("asset.move", 1, "Example.Operations"),
                },
                DescriptorTestFixtures.CreateRegistry(),
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());
            var plan = Plan(new[]
            {
                Step(0, "a", "asset.move", 1),
                Step(1, "b", "asset.delete", 1), // not registered anymore
            });

            var definitions =
                RunDefinitionProtection.ForRun(new ProtectedRun("run_x", plan), registry);

            Assert.IsTrue(definitions.Uncertain);
            CollectionAssert.AreEquivalent(
                new[] { "Example.Operations" }, definitions.AssemblyNames,
                "the resolvable half is still recorded");
        }

        // SPEC §D0.3 item 3: a fingerprint member that is not an array of
        // {…,"definingAssemblyName":<string>} entries fails closed.
        [Test]
        public void ForRunIsUncertainOnAMalformedFingerprintMember()
        {
            var registry = new RegistrySnapshot(
                "reg_01J00000000000000000000000",
                new string('f', 64),
                new List<RegisteredOperation>
                {
                    Operation("core.start", 1, "OpWeave.Operations"),
                },
                DescriptorTestFixtures.CreateRegistry(),
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());
            var malformed = Plan(usedTypeFingerprints: "[{\"id\":\"example.T\"}]");
            var notAnArray = Plan(usedCodecFingerprints: "{}");

            Assert.IsTrue(RunDefinitionProtection.ForRun(
                new ProtectedRun("run_a", malformed), registry).Uncertain);
            Assert.IsTrue(RunDefinitionProtection.ForRun(
                new ProtectedRun("run_b", notAnArray), registry).Uncertain);
        }

        // A plan whose persisted fingerprint members are absent (an older
        // plan shape) proves an empty used-Type/Codec set, not an
        // unprovable one.
        [Test]
        public void ForRunTreatsAbsentFingerprintMembersAsEmpty()
        {
            var registry = new RegistrySnapshot(
                "reg_01J00000000000000000000000",
                new string('f', 64),
                new List<RegisteredOperation>
                {
                    Operation("core.start", 1, "OpWeave.Operations"),
                },
                DescriptorTestFixtures.CreateRegistry(),
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());
            var plan = Plan(usedTypeFingerprints: null, usedCodecFingerprints: null);

            var definitions =
                RunDefinitionProtection.ForRun(new ProtectedRun("run_old", plan), registry);

            Assert.IsFalse(definitions.Uncertain);
            CollectionAssert.AreEquivalent(
                new[] { "OpWeave.Operations" }, definitions.AssemblyNames);
        }

        // ── RunCoordinator.CollectProtectedRuns ─────────────────────────

        // An empty coordinator contributes nothing.
        [Test]
        public void CollectProtectedRunsOnAnEmptyCoordinatorIsEmpty()
        {
            var fixture = NewFixture();
            var into = new List<ProtectedRun>();

            fixture.Coordinator.CollectProtectedRuns(into);

            Assert.AreEqual(0, into.Count);
        }

        // SPEC §D0.3 item 1: every unfinished Run is collected with the Run
        // id and its own compiled Plan — an accepted Run that no step ever
        // started counts exactly like a Running one.
        [Test]
        public void CollectProtectedRunsCoversEveryNonTerminalState()
        {
            var fixture = NewFixture();
            var queuedPlan = Plan();
            var awaitingPlan = Plan(approvalCount: 1);
            var adoptedPlan = Plan();

            var queued = fixture.Coordinator.Accept(
                queuedPlan, Doc(), ActorKind.Agent, new AcceptOptions(null, false));
            Assert.IsTrue(queued.Ok);
            Assert.AreEqual(RunState.Queued, queued.Value!.State);
            var awaiting = fixture.Coordinator.Accept(
                awaitingPlan, Doc(), ActorKind.Agent, new AcceptOptions(null, false));
            Assert.IsTrue(awaiting.Ok);
            Assert.AreEqual(RunState.AwaitingApproval, awaiting.Value!.State);
            var suspended = fixture.Coordinator.AdoptRecoveredRun(
                Snapshot("run_susp", adoptedPlan, RunState.Suspended));
            Assert.IsTrue(suspended.Ok);

            var into = new List<ProtectedRun>();
            fixture.Coordinator.CollectProtectedRuns(into);

            Assert.AreEqual(3, into.Count);
            var byId = new Dictionary<string, ProtectedRun>(StringComparer.Ordinal);
            foreach (var run in into)
            {
                byId[run.RunId] = run;
            }
            Assert.IsTrue(byId.ContainsKey(queued.Value.RunId));
            Assert.IsTrue(byId.ContainsKey(awaiting.Value.RunId));
            Assert.IsTrue(byId.ContainsKey("run_susp"));
            Assert.AreSame(queuedPlan, byId[queued.Value.RunId].Plan);
            Assert.AreSame(awaitingPlan, byId[awaiting.Value.RunId].Plan);
            Assert.AreSame(adoptedPlan, byId["run_susp"].Plan);
        }

        // A NeedsReview Run stays protected until a human ends it — the
        // §D0.3 state the lock residue alone used to cover.
        [Test]
        public void CollectProtectedRunsIncludesNeedsReview()
        {
            var fixture = NewFixture();
            var adopted = fixture.Coordinator.AdoptRecoveredRun(
                Snapshot("run_review", Plan(), RunState.NeedsReview));
            Assert.IsTrue(adopted.Ok);

            var into = new List<ProtectedRun>();
            fixture.Coordinator.CollectProtectedRuns(into);

            Assert.AreEqual(1, into.Count);
            Assert.AreEqual("run_review", into[0].RunId);
        }

        // SPEC §D0.3 item 1: a terminal Run no longer protects its
        // definitions — its Plan is free to be superseded.
        [Test]
        public void CollectProtectedRunsSkipsTerminalRuns()
        {
            var fixture = NewFixture();
            var adopted = fixture.Coordinator.AdoptRecoveredRun(
                Snapshot("run_done", Plan(), RunState.Succeeded, ended: true));
            Assert.IsTrue(adopted.Ok);
            Assert.AreEqual(RunState.Succeeded, adopted.Value!.State);

            var into = new List<ProtectedRun>();
            fixture.Coordinator.CollectProtectedRuns(into);

            Assert.AreEqual(0, into.Count);
        }

        [Test]
        public void CollectProtectedRunsRejectsANullList()
        {
            var fixture = NewFixture();
            Assert.Throws<ArgumentNullException>(
                () => fixture.Coordinator.CollectProtectedRuns(null!));
        }
    }
}
