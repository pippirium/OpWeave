#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    /// [Core] tests for the RunCoordinator of SPEC §E2 items 1–4 and §1.4
    /// (local:T-W1.8-03:run-coordinator): acceptance into
    /// AwaitingApproval/Queued, OW-RUN-003 on expectedPlanHash mismatch,
    /// OW-RUN-001 outside Edit Mode, OW-RUN-002 on a second mutating Run and
    /// a full waiting queue, the Queued→Running start gate (Edit Mode, not
    /// compiling, not updating, coordinator.lock acquired) with lock.json
    /// written inside the held critical section, the OW-RUN-011 Registry
    /// fingerprint check at start, and the empty-lock.json-before-release
    /// ordering at Run end. Fixtures use an in-memory store, fake editor
    /// state, a fake registry provider and the real CoordinatorRunLock over a
    /// temp root (plus a FileRunStore integration pass). The same sources run
    /// under dotnet and Unity Mono.
    /// </summary>
    internal sealed class RunCoordinatorTests
    {
        private const string Session = "session-coordinator-test";
        private const string WorkflowId = "example.runcoordinator";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RunCoordinator." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            // Drop every coordinator-held OS exclusion first — an abandoned
            // handle keeps coordinator.lock undeletable (SPEC §E2 crash
            // semantics: Dispose leaves the lock.json residue, which is what
            // recovery reconciles).
            foreach (var fixture in _fixtures)
            {
                fixture.CoordinatorCreated?.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private readonly List<Fixture> _fixtures = new List<Fixture>();

        private Fixture NewFixture()
        {
            var fixture = new Fixture();
            // The non-queued accept probes the OS exclusion (SPEC §E2 items
            // 2–3), so the default is the real lock over this test's root —
            // not the "not wired" failure.
            fixture.AcquireLock = () => CoordinatorRunLock.TryAcquire(_root);
            // The lock-residue verdict of SPEC §E2 item 3 goes through the
            // real §F1 journal fold, so the fake store's Load is wired to a
            // FileRunStore over this test's root — where the fixtures create
            // the residue Runs.
            fixture.Store.LoadHandler = new FileRunStore(_root, Session).Load;
            _fixtures.Add(fixture);
            return fixture;
        }

        // ── Fixtures ────────────────────────────────────────────────────────

        private sealed class FakeEditorState : IEditorState
        {
            public bool IsEditMode { get; set; } = true;
            public bool IsCompiling { get; set; }
            public bool IsUpdating { get; set; }
        }

        private sealed class FakeClock : IClock
        {
            public DateTimeOffset UtcNow { get; set; } =
                new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        }

        private sealed class CreatedRun
        {
            internal CreatedRun(
                string runId, CompiledPlan plan, WorkflowDocument document, ActorKind actor)
            {
                RunId = runId;
                Plan = plan;
                Document = document;
                Actor = actor;
            }

            internal string RunId { get; }
            internal CompiledPlan Plan { get; }
            internal WorkflowDocument Document { get; }
            internal ActorKind Actor { get; }
        }

        // The in-memory store of the verification method: records creates and
        // appended journal records; failures are injected per call.
        private sealed class FakeRunStore : IRunStore
        {
            internal readonly List<CreatedRun> Created = new List<CreatedRun>();
            internal readonly List<JournalRecord> Appended = new List<JournalRecord>();
            internal readonly List<Durability> AppendedDurability = new List<Durability>();
            internal Result<Unit>? NextCreateResult;
            internal Result<Unit>? NextAppendResult;

            public Result<Unit> CreateRun(string runId, CompiledPlan plan, WorkflowDocument doc) =>
                CreateRun(runId, plan, doc, ActorKind.Human);

            internal Result<Unit> CreateRun(
                string runId, CompiledPlan plan, WorkflowDocument document, ActorKind actor)
            {
                if (NextCreateResult is { } result)
                {
                    NextCreateResult = null;
                    return result;
                }
                Created.Add(new CreatedRun(runId, plan, document, actor));
                return Result<Unit>.Success(Unit.Value);
            }

            public Result<Unit> Append(string runId, JournalRecord record, Durability durability)
            {
                if (NextAppendResult is { } result)
                {
                    NextAppendResult = null;
                    return result;
                }
                Appended.Add(record);
                AppendedDurability.Add(durability);
                return Result<Unit>.Success(Unit.Value);
            }

            // The §E2 item 3 residue verdict and Get go through Load; the
            // fixture wires it to a real FileRunStore over the test root.
            internal Func<string, Result<RunSnapshot>> LoadHandler = runId =>
                Result<RunSnapshot>.Failure(
                    ErrorCodes.Create(ErrorCodes.OW_STORE_001, "not wired"));

            public Result<RunSnapshot> Load(string runId) => LoadHandler(runId);

            public Result<IReadOnlyList<RunIndexEntry>> List(RunQuery query) =>
                throw new NotSupportedException("The fake store does not list.");

            public Result<Unit> Delete(string runId) =>
                throw new NotSupportedException("The fake store does not delete.");
        }

        private sealed class Fixture
        {
            internal readonly FakeRunStore Store = new FakeRunStore();
            internal readonly FakeEditorState Editor = new FakeEditorState();
            internal readonly FakeClock Clock = new FakeClock();
            internal readonly List<RegisteredOperation> Operations = new List<RegisteredOperation>();
            internal Func<Result<CoordinatorRunLock>> AcquireLock = () =>
                Result<CoordinatorRunLock>.Failure(
                    ErrorCodes.Create(ErrorCodes.OW_RUN_002, "not wired"));

            private RunCoordinator? _coordinator;

            internal RunCoordinator? CoordinatorCreated => _coordinator;

            internal RunCoordinator Coordinator => _coordinator ??= new RunCoordinator(
                Store,
                (runId, plan, document, actor) => Store.CreateRun(runId, plan, document, actor),
                Editor,
                Clock,
                () => Snapshot(),
                AcquireLock,
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
            string id, int version, string descriptorHash, string implementationHash) =>
            new RegisteredOperation(
                Descriptor(id, version), OperationOrigin.Standard, "Example.Operations",
                null, descriptorHash, implementationHash);

        private static PlanStep Step(
            int index, string nodeId, string operationId, int version,
            string descriptorHash, string implementationHash) =>
            new PlanStep(
                index, nodeId, operationId, version, descriptorHash, implementationHash,
                new Dictionary<string, InputSource>(),
                new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                30, new RetryPolicy(), Idempotency.Unsafe);

        private static PlanStep StartStep() =>
            Step(0, "start", "core.start", 1, Hash64('d'), Hash64('e'));

        private static CompiledPlan Plan(int approvalCount = 0, PlanStep[]? steps = null)
        {
            var approvals = new List<ApprovalRequirement>();
            for (var i = 0; i < approvalCount; i++)
            {
                approvals.Add(new ApprovalRequirement(
                    "node" + i, "asset.modify", "example.op@1"));
            }
            var hashInput = Json(
                "{\"canonicalJsonVersion\":1,\"environmentIdentifiers\":{}," +
                "\"executionInputs\":{},\"requiredEffects\":[]," +
                "\"resolvedWorkflow\":{\"id\":\"" + WorkflowId + "\"},\"retry\":[]," +
                "\"steps\":[],\"timeouts\":[],\"transitions\":[]," +
                "\"usedCodecFingerprints\":[],\"usedOperationFingerprints\":[]," +
                "\"usedTypeFingerprints\":[]}");
            return new CompiledPlan(
                "plan_01J00000000000000000000000",
                Hash64('a'), WorkflowId, Hash64('b'), Hash64('c'),
                Json("{}"),
                steps ?? new[] { StartStep() },
                Array.Empty<PlanOutput>(),
                Array.Empty<string>(), approvals,
                Array.Empty<PlanSourceMapEntry>(), hashInput);
        }

        // A self-contained but fully verifiable Plan for the residues the
        // §E2 item 3 verdict Loads and for the Get query: the SG-12
        // twelve-member planHashInput carries the shapes PlanJsonCodec.Read
        // revalidates and planHash is its real SHA-256, so the §D0.1
        // revalidation decodes it (the same fixture shape
        // FileRunStoreLoadTests established).
        private static CompiledPlan VerifiablePlan()
        {
            var hashInput = JsonValue.From(new JObject
            {
                ["canonicalJsonVersion"] = 1,
                ["environmentIdentifiers"] = new JObject
                {
                    ["unityVersion"] = "6000.0.0f1",
                    ["editorOS"] = "windows",
                    ["activeBuildTarget"] = "windows",
                    ["scriptingBackend"] = "mono",
                    ["apiCompatibilityLevel"] = "netstandard2.1",
                    ["scriptingDefineSymbols"] = new JArray(),
                },
                ["executionInputs"] = new JObject(),
                ["requiredEffects"] = new JObject
                {
                    ["steps"] = new JArray(),
                    ["approvals"] = new JArray(),
                },
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = WorkflowId,
                    ["inputs"] = new JArray(),
                    ["outputs"] = new JArray(),
                },
                ["retry"] = new JArray(),
                ["steps"] = new JArray(),
                ["timeouts"] = new JArray(),
                ["transitions"] = new JArray(),
                ["usedCodecFingerprints"] = new JArray(),
                ["usedOperationFingerprints"] = new JArray(),
                ["usedTypeFingerprints"] = new JArray(),
            });
            return new CompiledPlan(
                "plan_01J00000000000000000000000",
                CanonicalJson.Sha256(hashInput.ToToken()),
                WorkflowId, Hash64('b'), Hash64('c'),
                Json("{}"),
                Array.Empty<PlanStep>(), Array.Empty<PlanOutput>(),
                Array.Empty<string>(), new List<ApprovalRequirement>(),
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

        private static AcceptOptions Options(bool queue = false, string? expectedPlanHash = null) =>
            new AcceptOptions(expectedPlanHash, queue);

        private static Result<RunHandle> Accept(
            Fixture fixture, CompiledPlan? plan = null, AcceptOptions? options = null) =>
            fixture.Coordinator.Accept(
                plan ?? Plan(), Doc(), ActorKind.Agent, options ?? Options());

        private string LockPath => Path.Combine(_root, CoordinatorRunLock.LockFileName);
        private string StatePath => Path.Combine(_root, CoordinatorRunLock.StateFileName);

        // Raw OS-level open attempt on coordinator.lock — the same denied open
        // a foreign process sees (mirrors CoordinatorRunLockTests).
        private static bool OsOpenDenied(string lockPath)
        {
            try
            {
                using (new FileStream(
                    lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return false;
                }
            }
            catch (Exception e) when (
                e is IOException || e is UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static void AssertDiagnostic<T>(
            Result<T> result, string code, string? because = null)
        {
            Assert.IsFalse(result.Ok, because ?? "expected a failure");
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual(code, result.Diagnostics[0].Code);
        }

        // A started run against the real lock over the temp root: registers
        // the fixture operation so the fingerprint check passes, accepts a
        // plan and drives the start gate once.
        private RunHandle Started(Fixture fixture)
        {
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            fixture.AcquireLock = () => CoordinatorRunLock.TryAcquire(_root);
            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);
            var started = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(started.Ok && started.Value is not null,
                "expected the queued run to start");
            return started.Value!;
        }

        // ── Acceptance (SPEC §E2 item 1) ────────────────────────────────────

        // A Plan without approvals is accepted into Queued and persisted
        // through the injected store with plan, document and actor.
        [Test]
        public void AcceptWithoutApprovalsIsQueuedAndPersisted()
        {
            var fixture = NewFixture();
            var plan = Plan();
            var accepted = Accept(fixture, plan);

            Assert.IsTrue(accepted.Ok);
            Assert.AreEqual(RunState.Queued, accepted.Value!.State);
            StringAssert.StartsWith("run_", accepted.Value.RunId);
            Assert.AreEqual(1, fixture.Store.Created.Count);
            Assert.AreSame(plan, fixture.Store.Created[0].Plan);
            Assert.AreEqual(accepted.Value.RunId, fixture.Store.Created[0].RunId);
            Assert.AreEqual(ActorKind.Agent, fixture.Store.Created[0].Actor);
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(accepted.Value.RunId));
        }

        // A Plan with approvals is accepted into AwaitingApproval — it does
        // not occupy a waiting-queue slot (SPEC §E2 item 1).
        [Test]
        public void AcceptWithApprovalsIsAwaitingApproval()
        {
            var fixture = NewFixture();
            var accepted = Accept(fixture, Plan(approvalCount: 2));

            Assert.IsTrue(accepted.Ok);
            Assert.AreEqual(RunState.AwaitingApproval, accepted.Value!.State);
            Assert.AreEqual(
                RunState.AwaitingApproval,
                fixture.Coordinator.StateOf(accepted.Value.RunId));
        }

        // SPEC §D7 item 7 / §D0.1: a mismatched expectedPlanHash rejects with
        // OW-RUN-003 and nothing is persisted.
        [Test]
        public void AcceptWithMismatchedExpectedPlanHashFailsOwRun003()
        {
            var fixture = NewFixture();
            var accepted = Accept(
                fixture, Plan(), Options(expectedPlanHash: Hash64('9')));

            AssertDiagnostic(accepted, "OW-RUN-003");
            Assert.AreEqual(0, fixture.Store.Created.Count, "nothing is persisted");
        }

        // SPEC §1.4 row 1: outside Edit Mode the execute request is rejected
        // with retryable OW-RUN-001.
        [Test]
        public void AcceptOutsideEditModeFailsOwRun001()
        {
            var fixture = NewFixture();
            fixture.Editor.IsEditMode = false;

            AssertDiagnostic(Accept(fixture), "OW-RUN-001");
            Assert.AreEqual(0, fixture.Store.Created.Count);
            Assert.IsTrue(
                fixture.Coordinator is IRunCoordinator,
                "the coordinator implements the SPEC E2 service interface");
        }

        // SPEC §E2 test 1: a second mutating Run while one is Running is
        // rejected with OW-RUN-002 unless queue: true.
        [Test]
        public void SecondRunWhileRunningFailsOwRun002UnlessQueued()
        {
            var fixture = NewFixture();
            var first = Started(fixture);
            Assert.AreEqual(RunState.Running, first.State);

            var rejected = Accept(fixture, Plan(), Options(queue: false));
            AssertDiagnostic(rejected, "OW-RUN-002");
            Assert.AreEqual(1, fixture.Store.Created.Count);

            var queued = Accept(fixture, Plan(), Options(queue: true));
            Assert.IsTrue(queued.Ok);
            Assert.AreEqual(RunState.Queued, queued.Value!.State);
        }

        // SPEC §E2 item 2: the waiting queue holds at most 5 — the sixth
        // Queued acceptance is OW-RUN-002.
        [Test]
        public void SixthQueuedRunFailsOwRun002()
        {
            var fixture = NewFixture();
            Started(fixture);
            for (var i = 0; i < RunCoordinator.MaxQueuedRuns; i++)
            {
                Assert.IsTrue(
                    Accept(fixture, Plan(), Options(queue: true)).Ok,
                    $"queued acceptance {i + 1} failed");
            }
            AssertDiagnostic(
                Accept(fixture, Plan(), Options(queue: true)), "OW-RUN-002",
                "the sixth queued run must be rejected");
        }

        // SPEC §E2 items 2–3: a non-queued execute must be refused while ANY
        // process holds the exclusion — the process-local run list alone
        // cannot see a second Editor's live owner — and nothing may be
        // persisted. queue: true keeps the waiting-queue behaviour.
        [Test]
        public void NonQueuedAcceptFailsWhileForeignOwnerHoldsLock()
        {
            var fixture = NewFixture();
            var foreign = CoordinatorRunLock.TryAcquire(_root).Value;
            try
            {
                var rejected = Accept(fixture, Plan(), Options(queue: false));
                AssertDiagnostic(rejected, "OW-RUN-002");
                Assert.AreEqual(0, fixture.Store.Created.Count,
                    "nothing is persisted while a foreign owner holds the lock");
                Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Runs")),
                    "no Run directory is created");

                var queued = Accept(fixture, Plan(), Options(queue: true));
                Assert.IsTrue(queued.Ok, "queue:true keeps the specified behaviour");
                Assert.AreEqual(RunState.Queued, queued.Value!.State);
            }
            finally
            {
                foreign!.Dispose();
            }
        }

        // The document hand-off of SPEC §E2 item 1: the public interface
        // member resolves the bound document by planId and completes the
        // same acceptance path — Queued without approvals, persisted
        // through the injected store.
        [Test]
        public void InterfaceAcceptWithBoundDocumentIsQueued()
        {
            var fixture = NewFixture();
            IRunCoordinator coordinator = fixture.Coordinator;
            var plan = Plan();
            fixture.Coordinator.BindDocument(plan, Doc());

            var accepted = coordinator.Accept(plan, ActorKind.Agent, Options());
            Assert.IsTrue(accepted.Ok);
            Assert.AreEqual(RunState.Queued, accepted.Value!.State);
            Assert.AreEqual(1, fixture.Store.Created.Count);
            Assert.AreSame(plan, fixture.Store.Created[0].Plan);
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(accepted.Value.RunId));
        }

        // A bound Plan with approvals is accepted through the interface into
        // AwaitingApproval (SPEC §E2 item 1).
        [Test]
        public void InterfaceAcceptWithApprovalsIsAwaitingApproval()
        {
            var fixture = NewFixture();
            IRunCoordinator coordinator = fixture.Coordinator;
            var plan = Plan(approvalCount: 2);
            fixture.Coordinator.BindDocument(plan, Doc());

            var accepted = coordinator.Accept(plan, ActorKind.Agent, Options());
            Assert.IsTrue(accepted.Ok);
            Assert.AreEqual(RunState.AwaitingApproval, accepted.Value!.State);
        }

        // A store failure propagates through the interface as a failure
        // Result — never an exception.
        [Test]
        public void InterfaceAcceptPropagatesStoreFailure()
        {
            var fixture = NewFixture();
            IRunCoordinator coordinator = fixture.Coordinator;
            var plan = Plan();
            fixture.Coordinator.BindDocument(plan, Doc());
            fixture.Store.NextCreateResult = Result<Unit>.Failure(
                ErrorCodes.Create(ErrorCodes.OW_STORE_001, "injected"));

            AssertDiagnostic(
                coordinator.Accept(plan, ActorKind.Agent, Options()), "OW-STORE-001");
        }

        // The §E2 signature cannot carry the workflow document, so an accept
        // without the BindDocument hand-off is a malformed request — a
        // failure Result (OW-API-001, SPEC §J), never an exception and never
        // a persisted Run.
        [Test]
        public void InterfaceAcceptWithoutBoundDocumentFailsOwApi001()
        {
            var fixture = NewFixture();
            IRunCoordinator coordinator = fixture.Coordinator;

            Result<RunHandle> accepted = default;
            Assert.DoesNotThrow(() =>
                accepted = coordinator.Accept(Plan(), ActorKind.Agent, Options()));
            AssertDiagnostic(accepted, "OW-API-001");
            Assert.AreEqual(0, fixture.Store.Created.Count);
        }

        // ── Start conditions (SPEC §E2 item 4) ──────────────────────────────

        // All conditions met: the queued Run transitions to Running, a Flushed
        // required run_started record is journaled inside the held critical
        // section and lock.json records runId/editorSessionId/acquiredAt.
        [Test]
        public void TryStartNextStartsQueuedRunWhenConditionsMet()
        {
            var fixture = NewFixture();
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            fixture.AcquireLock = () => CoordinatorRunLock.TryAcquire(_root);
            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);

            var started = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(started.Ok);
            Assert.AreEqual(RunState.Running, started.Value!.State);
            Assert.AreEqual(accepted.Value!.RunId, started.Value.RunId);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(accepted.Value.RunId));

            Assert.IsTrue(OsOpenDenied(LockPath), "coordinator.lock must be held exclusively");
            var lockJson = File.ReadAllText(StatePath);
            var document = (JObject)CanonicalJson.Parse(lockJson).Value!;
            Assert.AreEqual(accepted.Value.RunId, (string?)document["runId"]);
            Assert.AreEqual(Session, (string?)document["editorSessionId"]);
            StringAssert.IsMatch(
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$",
                (string?)document["acquiredAt"]!);

            Assert.AreEqual(1, fixture.Store.Appended.Count);
            var record = fixture.Store.Appended[0];
            Assert.AreEqual(JournalRecordType.RunStarted, record.Type);
            Assert.IsTrue(record.Required, "run_started is required:true (SPEC §F2)");
            Assert.AreEqual(Durability.Flushed, fixture.Store.AppendedDurability[0]);
            Assert.AreEqual(2, record.Seq, "RunAccepted is seq 1");
            Assert.AreEqual(Session, record.Session);
        }

        // Editor conditions unmet keep the head of the queue Queued without
        // touching the lock or the journal (SPEC §E2 item 4, §1.4).
        [Test]
        public void TryStartNextWaitsWhileCompilingUpdatingOrPlaying()
        {
            foreach (var mutate in new Action<FakeEditorState>[]
            {
                e => e.IsCompiling = true,
                e => e.IsUpdating = true,
                e => e.IsEditMode = false,
            })
            {
                var fixture = NewFixture();
                fixture.Operations.Add(
                    Operation("core.start", 1, Hash64('d'), Hash64('e')));
                var lockCalls = 0;
                fixture.AcquireLock = () => { lockCalls++; return CoordinatorRunLock.TryAcquire(_root); };
                var accepted = Accept(fixture);
                Assert.IsTrue(accepted.Ok);
                mutate(fixture.Editor);
                // The non-queued accept legitimately probed the exclusion
                // once; the assertion below covers only the start gate.
                lockCalls = 0;

                var attempt = fixture.Coordinator.TryStartNext();
                Assert.IsTrue(attempt.Ok);
                Assert.IsNull(attempt.Value, "nothing may start while conditions are unmet");
                Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(accepted.Value!.RunId));
                Assert.AreEqual(0, lockCalls, "the lock is not probed before the Editor checks pass");
                Assert.AreEqual(0, fixture.Store.Appended.Count);
            }
        }

        // SPEC §E2 item 3: a live foreign owner keeps the queued Run Queued;
        // the denial is reported with OW-RUN-002 and the lock is never taken.
        [Test]
        public void TryStartNextStaysQueuedWhileForeignOwnerHoldsLock()
        {
            var fixture = NewFixture();
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            fixture.AcquireLock = () => CoordinatorRunLock.TryAcquire(_root);
            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);

            var foreign = CoordinatorRunLock.TryAcquire(_root).Value;
            try
            {
                var attempt = fixture.Coordinator.TryStartNext();
                Assert.IsTrue(attempt.Ok);
                Assert.IsNull(attempt.Value);
                Assert.AreEqual("OW-RUN-002", attempt.Diagnostics[0].Code);
                Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(accepted.Value!.RunId));
                Assert.AreEqual(0, fixture.Store.Appended.Count);
            }
            finally
            {
                foreign!.Dispose();
            }
        }

        // SPEC §E2 item 3: inside the held critical section the recorded
        // owner state is read before any write — a foreign-session record of
        // an unfinished Run fails closed: lock.json is not overwritten, the
        // queued Run does not start, OW-RUN-002 is reported and the residue
        // stays for the recovery work (T-W1.8-08).
        [Test]
        public void TryStartNextFailsClosedOnUnfinishedForeignResidue()
        {
            var fixture = NewFixture();
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            var residueRunId = Identifiers.NewRunId();
            // The crashed Editor's store: a Run whose journal ends without a
            // terminal record.
            var residueStore = new FileRunStore(_root, "other-session");
            Assert.IsTrue(
                residueStore.CreateRun(residueRunId, VerifiablePlan(), Doc()).Ok);
            var crashed = CoordinatorRunLock.TryAcquire(_root).Value!;
            Assert.IsTrue(crashed.WriteState(new RunLockState(
                residueRunId, "other-session", UtcTimestamp.Now())).Ok);
            crashed.Dispose(); // abnormal end: handle dropped, lock.json kept

            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);

            var attempt = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(attempt.Ok);
            Assert.IsNull(attempt.Value);
            Assert.AreEqual("OW-RUN-002", attempt.Diagnostics[0].Code);
            Assert.AreEqual(
                RunState.Queued, fixture.Coordinator.StateOf(accepted.Value!.RunId));

            var state = (JObject)CanonicalJson.Parse(File.ReadAllText(StatePath)).Value!;
            Assert.AreEqual(residueRunId, (string?)state["runId"],
                "the recorded residue is left for recovery, not overwritten");
            Assert.IsFalse(OsOpenDenied(LockPath),
                "the exclusion is dropped without touching the recorded state");
            Assert.AreEqual(0, fixture.Store.Appended.Count);
        }

        // SPEC §E2 item 3: an unreadable lock.json fails closed the same way —
        // the bytes are never overwritten and nothing starts.
        [Test]
        public void TryStartNextFailsClosedOnCorruptLockJson()
        {
            var fixture = NewFixture();
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            File.WriteAllText(StatePath, "{ not a lock document");
            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);

            var attempt = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(attempt.Ok);
            Assert.IsNull(attempt.Value);
            Assert.AreEqual("OW-RUN-002", attempt.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-002", attempt.Diagnostics[1].Code,
                "the unreadable recorded state is reported too");
            Assert.AreEqual(
                RunState.Queued, fixture.Coordinator.StateOf(accepted.Value!.RunId));
            Assert.AreEqual("{ not a lock document", File.ReadAllText(StatePath),
                "a corrupt lock.json is never overwritten");
            Assert.IsFalse(OsOpenDenied(LockPath));
            Assert.AreEqual(0, fixture.Store.Appended.Count);
        }

        // SPEC §E2 item 3 ("Editor 再起動後"): a recorded Run whose journal
        // verifiably reached a terminal state is stale crash residue — the
        // new owner overwrites lock.json and starts.
        [Test]
        public void TryStartNextOverwritesVerifiablyFinishedResidue()
        {
            var fixture = NewFixture();
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            var residueRunId = Identifiers.NewRunId();
            var residueStore = new FileRunStore(_root, "other-session");
            Assert.IsTrue(
                residueStore.CreateRun(residueRunId, VerifiablePlan(), Doc()).Ok);
            Assert.IsTrue(residueStore.Append(
                residueRunId,
                new JournalRecord(
                    2, UtcTimestamp.Now(), "other-session",
                    JournalRecordType.RunFinished, Step: null, Attempt: null,
                    JsonValue.From(new JObject
                    {
                        ["state"] = "succeeded",
                        ["outputs"] = new JObject(),
                        ["summary"] = new JObject(),
                    })),
                Durability.Flushed).Ok);
            var crashed = CoordinatorRunLock.TryAcquire(_root).Value!;
            Assert.IsTrue(crashed.WriteState(new RunLockState(
                residueRunId, "other-session", UtcTimestamp.Now())).Ok);
            crashed.Dispose();

            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);

            var attempt = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(attempt.Ok);
            Assert.AreEqual(accepted.Value!.RunId, attempt.Value!.RunId);
            Assert.AreEqual(RunState.Running, attempt.Value.State);

            var state = (JObject)CanonicalJson.Parse(File.ReadAllText(StatePath)).Value!;
            Assert.AreEqual(accepted.Value.RunId, (string?)state["runId"],
                "a verifiably terminal residue is overwritten by the new owner");
            Assert.AreEqual(Session, (string?)state["editorSessionId"]);
            Assert.IsTrue(OsOpenDenied(LockPath));
        }

        // SPEC §E2 item 3: a decoded run_finished record is not proof of
        // termination by itself — the §F1 fold must reach a terminal state.
        // A run_finished whose payload carries no terminal state folds to
        // NeedsReview (OW-STORE-002), so the residue fails closed like any
        // unverifiable Run: lock.json is not overwritten and nothing starts.
        [Test]
        public void TryStartNextFailsClosedOnMalformedRunFinishedResidue()
        {
            var fixture = NewFixture();
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            var residueRunId = Identifiers.NewRunId();
            var residueStore = new FileRunStore(_root, "other-session");
            Assert.IsTrue(
                residueStore.CreateRun(residueRunId, VerifiablePlan(), Doc()).Ok);
            Assert.IsTrue(residueStore.Append(
                residueRunId,
                new JournalRecord(
                    2, UtcTimestamp.Now(), "other-session",
                    JournalRecordType.RunFinished, Step: null, Attempt: null,
                    JsonValue.From(new JObject
                    {
                        ["state"] = "not_a_state",
                        ["outputs"] = new JObject(),
                        ["summary"] = new JObject(),
                    })),
                Durability.Flushed).Ok);
            var crashed = CoordinatorRunLock.TryAcquire(_root).Value!;
            Assert.IsTrue(crashed.WriteState(new RunLockState(
                residueRunId, "other-session", UtcTimestamp.Now())).Ok);
            crashed.Dispose();

            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);

            var attempt = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(attempt.Ok);
            Assert.IsNull(attempt.Value);
            Assert.AreEqual("OW-RUN-002", attempt.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-002", attempt.Diagnostics[1].Code,
                "the uninterpretable journal record is reported with the refusal");
            Assert.AreEqual(
                RunState.Queued, fixture.Coordinator.StateOf(accepted.Value!.RunId));

            var state = (JObject)CanonicalJson.Parse(File.ReadAllText(StatePath)).Value!;
            Assert.AreEqual(residueRunId, (string?)state["runId"],
                "a NeedsReview residue is left for recovery, not overwritten");
            Assert.IsFalse(OsOpenDenied(LockPath));
            Assert.AreEqual(0, fixture.Store.Appended.Count);
        }

        // SPEC §E2 item 3 with §F2 item 2: even after a verifiably terminal
        // run_finished, a later unknown required record (a future writer's
        // record v1 cannot interpret) makes the whole journal NeedsReview —
        // the residue fails closed the same way.
        [Test]
        public void TryStartNextFailsClosedOnUnknownRequiredRecordAfterFinish()
        {
            var fixture = NewFixture();
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            var residueRunId = Identifiers.NewRunId();
            var residueStore = new FileRunStore(_root, "other-session");
            Assert.IsTrue(
                residueStore.CreateRun(residueRunId, VerifiablePlan(), Doc()).Ok);
            Assert.IsTrue(residueStore.Append(
                residueRunId,
                new JournalRecord(
                    2, UtcTimestamp.Now(), "other-session",
                    JournalRecordType.RunFinished, Step: null, Attempt: null,
                    JsonValue.From(new JObject
                    {
                        ["state"] = "succeeded",
                        ["outputs"] = new JObject(),
                        ["summary"] = new JObject(),
                    })),
                Durability.Flushed).Ok);
            // The v1 codec can only write known record types, so the unknown
            // required line is appended with a valid envelope and CRC — only
            // the record type is foreign.
            File.AppendAllText(
                Path.Combine(_root, "Runs", residueRunId, "journal.ndjson"),
                UnknownRequiredRecordLine(3) + "\n");
            var crashed = CoordinatorRunLock.TryAcquire(_root).Value!;
            Assert.IsTrue(crashed.WriteState(new RunLockState(
                residueRunId, "other-session", UtcTimestamp.Now())).Ok);
            crashed.Dispose();

            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);

            var attempt = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(attempt.Ok);
            Assert.IsNull(attempt.Value);
            Assert.AreEqual("OW-RUN-002", attempt.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-002", attempt.Diagnostics[1].Code,
                "the unknown required record is reported with the refusal");
            Assert.AreEqual(
                RunState.Queued, fixture.Coordinator.StateOf(accepted.Value!.RunId));

            var state = (JObject)CanonicalJson.Parse(File.ReadAllText(StatePath)).Value!;
            Assert.AreEqual(residueRunId, (string?)state["runId"],
                "a NeedsReview residue is left for recovery, not overwritten");
            Assert.IsFalse(OsOpenDenied(LockPath));
            Assert.AreEqual(0, fixture.Store.Appended.Count);
        }

        // A complete, checksum-valid journal envelope of a record type v1
        // does not know, with required: true on the NeedsReview side of SPEC
        // §F2 item 2 — built by hand because the v1 writer only emits known
        // types.
        private static string UnknownRequiredRecordLine(long seq)
        {
            var envelope = new JObject
            {
                ["seq"] = seq,
                ["ts"] = UtcTimestamp.Now(),
                ["session"] = "other-session",
                ["type"] = "future_required",
                ["required"] = true,
                ["data"] = new JObject(),
            };
            envelope["crc"] = Crc32.ToLowerHex(Crc32.Compute(
                new UTF8Encoding(false).GetBytes(
                    CanonicalJson.Serialize(envelope))));
            return CanonicalJson.Serialize(envelope);
        }

        // SPEC §1.4 row 4 / §E2 item 5 analogue for the start gate: a used
        // Operation whose fingerprints no longer match the current Registry
        // cancels the head of the queue with OW-RUN-011 — the only non-Running
        // transition §E1 allows from Queued — journaled as run_finished.
        [Test]
        public void TryStartNextCancelsOnFingerprintMismatchOwRun011()
        {
            var fixture = NewFixture();
            // The plan carries hashes 'd'/'e' but the current registry reports
            // different fingerprints for core.start v1.
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('7'), Hash64('8')));
            var lockCalls = 0;
            fixture.AcquireLock = () => { lockCalls++; return CoordinatorRunLock.TryAcquire(_root); };
            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);
            // The non-queued accept legitimately probed the exclusion once;
            // the assertion below covers only the start gate.
            lockCalls = 0;

            var attempt = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(attempt.Ok);
            Assert.AreEqual(RunState.Cancelled, attempt.Value!.State);
            Assert.AreEqual(1, attempt.Diagnostics.Count);
            Assert.AreEqual("OW-RUN-011", attempt.Diagnostics[0].Code);
            Assert.AreEqual("start", attempt.Diagnostics[0].NodeId);
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(accepted.Value!.RunId));
            Assert.AreEqual(0, lockCalls, "a doomed run never touches the lock");

            Assert.AreEqual(1, fixture.Store.Appended.Count);
            var record = fixture.Store.Appended[0];
            Assert.AreEqual(JournalRecordType.RunFinished, record.Type);
            Assert.IsTrue(record.Required);
            var data = (JObject)record.Data.ToToken();
            Assert.AreEqual("cancelled", (string?)data["state"]);
            Assert.AreEqual(
                "OW-RUN-011", (string?)((JObject)data["summary"]!)["code"]);
        }

        // A deleted Operation (absent from the current registry) is the same
        // OW-RUN-011 cancellation.
        [Test]
        public void TryStartNextCancelsWhenUsedOperationIsMissing()
        {
            var fixture = NewFixture(); // empty registry
            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);

            var attempt = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(attempt.Ok);
            Assert.AreEqual(RunState.Cancelled, attempt.Value!.State);
            Assert.AreEqual("OW-RUN-011", attempt.Diagnostics[0].Code);
        }

        // ── Run end: lock.json emptied before release (SPEC §E2 item 3) ─────

        // At Run end the coordinator journals run_finished, empties lock.json
        // inside the still-held critical section, then drops the OS exclusion;
        // the next queued Run can then start.
        [Test]
        public void EndRunEmptiesLockJsonBeforeReleasingTheExclusion()
        {
            var fixture = NewFixture();
            var first = Started(fixture);
            var second = Accept(fixture, Plan(), Options(queue: true));
            Assert.IsTrue(second.Ok);

            var ended = fixture.Coordinator.EndRun(first.RunId, RunState.Succeeded);
            Assert.IsTrue(ended.Ok);

            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(first.RunId));
            Assert.AreEqual(0, new FileInfo(StatePath).Length,
                "lock.json is emptied before the exclusion is released");
            Assert.IsFalse(OsOpenDenied(LockPath), "the OS exclusion is released");

            var finished = fixture.Store.Appended[
                fixture.Store.Appended.Count - 1];
            Assert.AreEqual(JournalRecordType.RunFinished, finished.Type);
            Assert.AreEqual(
                "succeeded", (string?)((JObject)finished.Data.ToToken())["state"]);

            var next = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(next.Ok);
            Assert.AreEqual(second.Value!.RunId, next.Value!.RunId);
            Assert.AreEqual(RunState.Running, next.Value.State);
            Assert.IsTrue(OsOpenDenied(LockPath), "the next run takes the exclusion");
        }

        // ── Cancel requests (SPEC §E6 item 2, journaled only) ───────────────

        // Cancel journals a Flushed required cancel_requested record and marks
        // the pending flag; processing on the next Tick is T-W1.8-05/-06.
        [Test]
        public void CancelJournalsCancelRequested()
        {
            var fixture = NewFixture();
            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);

            var cancelled = fixture.Coordinator.Cancel(accepted.Value!.RunId, "user request");
            Assert.IsTrue(cancelled.Ok);
            Assert.AreEqual(RunState.Queued, cancelled.Value!.State,
                "the state changes on the next Tick, not at request time");
            Assert.IsTrue(fixture.Coordinator.HasPendingCancel(accepted.Value.RunId));

            Assert.AreEqual(1, fixture.Store.Appended.Count);
            var record = fixture.Store.Appended[0];
            Assert.AreEqual(JournalRecordType.CancelRequested, record.Type);
            Assert.IsTrue(record.Required);
            Assert.AreEqual(2, record.Seq);
            Assert.AreEqual(
                "user request", (string?)((JObject)record.Data.ToToken())["reason"]);
        }

        [Test]
        public void CancelUnknownRunFailsOwRun010()
        {
            var fixture = NewFixture();
            AssertDiagnostic(
                fixture.Coordinator.Cancel("run_01J00000000000000000000000", "x"),
                "OW-RUN-010");
        }

        [Test]
        public void CancelTerminalRunFailsOwRun014()
        {
            var fixture = NewFixture();
            var first = Started(fixture);
            Assert.IsTrue(fixture.Coordinator.EndRun(first.RunId, RunState.Succeeded).Ok);
            AssertDiagnostic(
                fixture.Coordinator.Cancel(first.RunId, "x"), "OW-RUN-014");
        }

        // A queued run with a pending cancel is skipped by the start gate —
        // its cancellation is the loop's work, not a fresh start.
        [Test]
        public void TryStartNextSkipsRunsWithPendingCancel()
        {
            var fixture = NewFixture();
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            fixture.AcquireLock = () => CoordinatorRunLock.TryAcquire(_root);
            var first = Accept(fixture);
            Assert.IsTrue(fixture.Coordinator.Cancel(first.Value!.RunId, "x").Ok);
            var second = Accept(fixture, Plan(), Options(queue: true));
            Assert.IsTrue(second.Ok);

            var started = fixture.Coordinator.TryStartNext();
            Assert.IsTrue(started.Ok);
            Assert.AreEqual(second.Value!.RunId, started.Value!.RunId,
                "the run with the pending cancel must not start");
        }

        // ── Store failure propagation ───────────────────────────────────────

        [Test]
        public void AcceptPropagatesStoreFailure()
        {
            var fixture = NewFixture();
            fixture.Store.NextCreateResult = Result<Unit>.Failure(
                ErrorCodes.Create(ErrorCodes.OW_STORE_001, "injected"));
            var accepted = Accept(fixture);
            AssertDiagnostic(accepted, "OW-STORE-001");
            Assert.IsNull(fixture.Coordinator.StateOf("anything"),
                "nothing is tracked");
        }

        [Test]
        public void TryStartNextReportsJournalWriteFailureAndEndsRunFailed()
        {
            var fixture = NewFixture();
            fixture.Operations.Add(
                Operation("core.start", 1, Hash64('d'), Hash64('e')));
            fixture.AcquireLock = () => CoordinatorRunLock.TryAcquire(_root);
            var accepted = Accept(fixture);
            Assert.IsTrue(accepted.Ok);
            fixture.Store.NextAppendResult = Result<Unit>.Failure(
                ErrorCodes.Create(ErrorCodes.OW_STORE_001, "injected"));

            var attempt = fixture.Coordinator.TryStartNext();
            Assert.IsFalse(attempt.Ok);
            Assert.AreEqual("OW-STORE-001", attempt.Diagnostics[0].Code);
            Assert.AreEqual(
                RunState.Failed, fixture.Coordinator.StateOf(accepted.Value!.RunId),
                "a refused required record ends the Run Failed on the SPEC §F1 " +
                "store-failure path — never left Queued for a later retry");
            Assert.AreEqual(0, new FileInfo(StatePath).Length,
                "the recorded lock state is emptied before the handle drops");
            Assert.IsFalse(OsOpenDenied(LockPath), "the exclusion is released");

            // The store-failure path journals run_finished{failed} exactly
            // once (the one-shot refusal was consumed by run_started).
            Assert.AreEqual(1, fixture.Store.Appended.Count);
            var record = fixture.Store.Appended[0];
            Assert.AreEqual(JournalRecordType.RunFinished, record.Type);
            Assert.AreEqual(
                "failed", (string?)((JObject)record.Data.ToToken())["state"]);
        }

        // ── Get (SPEC §E2) ─────────────────────────────────────────────────

        // SPEC §E2: Get answers from the §F1 journal reconstruction — the
        // accepted Run loads back Queued with its reverified Plan, and an
        // unknown Run is null rather than a fabricated snapshot.
        [Test]
        public void GetReturnsTheRecordedSnapshot()
        {
            var store = new FileRunStore(_root, Session);
            var editor = new FakeEditorState();
            var clock = new FakeClock();
            RegistrySnapshot Snapshot() => new RegistrySnapshot(
                "reg_01J00000000000000000000000", new string('f', 64),
                new List<RegisteredOperation>(),
                DescriptorTestFixtures.CreateRegistry(),
                Array.Empty<RegistryChange>(), Array.Empty<TypeRegistryFailure>());
            var coordinator = new RunCoordinator(
                store,
                (runId, plan, document, actor) =>
                    store.CreateRun(runId, plan, document, actor),
                editor, clock, Snapshot,
                () => CoordinatorRunLock.TryAcquire(_root),
                Session);
            var accepted = coordinator.Accept(
                VerifiablePlan(), Doc(), ActorKind.Agent, Options());
            Assert.IsTrue(accepted.Ok);

            var snapshot = ((IRunCoordinator)coordinator).Get(accepted.Value!.RunId);
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(accepted.Value.RunId, snapshot!.RunId);
            Assert.AreEqual(RunState.Queued, snapshot.State);
            Assert.IsNotNull(snapshot.Plan, "the reverified Plan is folded back");
            Assert.IsNull(
                ((IRunCoordinator)coordinator).Get(Identifiers.NewRunId()),
                "an unknown Run has no snapshot");
        }

        // ── FileRunStore integration ────────────────────────────────────────

        // The coordinator drives the real FileRunStore: the RunAccepted
        // journal line records the actor, and the start/end path persists
        // run_started / run_finished while emptying lock.json at the end.
        [Test]
        public void RealStoreIntegrationCoversTheE2Lifecycle()
        {
            var store = new FileRunStore(_root, Session);
            var operations = new List<RegisteredOperation>
            {
                Operation("core.start", 1, Hash64('d'), Hash64('e')),
            };
            var editor = new FakeEditorState();
            var clock = new FakeClock();
            RegistrySnapshot Snapshot() => new RegistrySnapshot(
                "reg_01J00000000000000000000000", new string('f', 64), operations,
                DescriptorTestFixtures.CreateRegistry(),
                Array.Empty<RegistryChange>(), Array.Empty<TypeRegistryFailure>());
            var coordinator = new RunCoordinator(
                store,
                (runId, plan, document, actor) =>
                    store.CreateRun(runId, plan, document, actor),
                editor, clock, Snapshot,
                () => CoordinatorRunLock.TryAcquire(_root),
                Session);

            var plan = Plan();
            var accepted = coordinator.Accept(
                plan, Doc(), ActorKind.Agent, Options());
            Assert.IsTrue(accepted.Ok);
            var runId = accepted.Value!.RunId;

            var runDir = Path.Combine(_root, "Runs", runId);
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "plan.json")));
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "workflow.json")));
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "journal.ndjson")));

            var started = coordinator.TryStartNext();
            Assert.IsTrue(started.Ok && started.Value is not null);
            Assert.IsTrue(OsOpenDenied(LockPath));
            var lockJson = (JObject)CanonicalJson.Parse(
                File.ReadAllText(StatePath)).Value!;
            Assert.AreEqual(runId, (string?)lockJson["runId"]);

            Assert.IsTrue(coordinator.EndRun(runId, RunState.Succeeded).Ok);
            Assert.AreEqual(0, new FileInfo(StatePath).Length);
            Assert.IsFalse(OsOpenDenied(LockPath));

            var lines = File.ReadAllLines(Path.Combine(runDir, "journal.ndjson"));
            Assert.AreEqual(4, lines.Length, "header + accepted + started + finished");
            var header = JournalCodec.ReadHeader(lines[0]);
            Assert.AreEqual(JournalLineStatus.Decoded, header.Status);
            Assert.AreEqual(runId, header.Header!.RunId);
            Assert.AreEqual(plan.PlanHash, header.Header.PlanHash);

            var acceptedRecord = JournalCodec.ReadRecord(lines[1], expectedSeq: 1);
            Assert.AreEqual(JournalRecordType.RunAccepted, acceptedRecord.Record!.Type);
            Assert.AreEqual(
                "agent", (string?)((JObject)acceptedRecord.Record.Data.ToToken())["actor"]);
            Assert.AreEqual(Session, acceptedRecord.Record.Session);
            Assert.IsTrue(acceptedRecord.Record.Required);

            var startedRecord = JournalCodec.ReadRecord(lines[2], expectedSeq: 2);
            Assert.AreEqual(JournalRecordType.RunStarted, startedRecord.Record!.Type);
            Assert.IsTrue(startedRecord.Record.Required);

            var finishedRecord = JournalCodec.ReadRecord(lines[3], expectedSeq: 3);
            Assert.AreEqual(JournalRecordType.RunFinished, finishedRecord.Record!.Type);
            Assert.AreEqual(
                "succeeded",
                (string?)((JObject)finishedRecord.Record.Data.ToToken())["state"]);
        }
    }
}
