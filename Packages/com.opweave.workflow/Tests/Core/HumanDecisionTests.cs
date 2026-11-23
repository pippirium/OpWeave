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
    /// [Core] tests for the human-only decisions and the approval-wait
    /// invalidation of SPEC §E2 (item 5), §E1 and §H4 —
    /// local:T-W1.8-09:human-decisions: one Approve call journals one
    /// approval_granted per approval the Plan recorded and moves the Run
    /// AwaitingApproval → Queued; Reject journals
    /// approval_rejected and ends the Run Cancelled with OW-PERM-003;
    /// ResolveReview(retry_step) journals review_resolved, moves the Run
    /// NeedsReview → Running and re-runs the reviewed step as a fresh
    /// attempt; ResolveReview(abort) cancels the reviewed step, skips the
    /// still-Pending ones, releases the still-open Run resources and ends
    /// the Run Cancelled; a Registry change
    /// during the approval wait cancels the Run with OW-RUN-011 — inside
    /// Approve at the decisive moment and on the per-Tick invalidation
    /// pass. A decision on a Run in any other state asks for a transition
    /// the §E1 table does not contain — bug detection throws
    /// InvalidOperationException before anything is journaled; unknown
    /// runIds report OW-RUN-010. Fixtures use an
    /// in-memory store (the journal fold is exercised through its
    /// FoldOnLoad reconstruction), a fake clock/editor state, fake
    /// operations and a real CoordinatorRunLock over a temp root. The
    /// same sources run under dotnet and Unity Mono.
    /// </summary>
    internal sealed class HumanDecisionTests
    {
        private const string Session = "session-humandecision-test";
        private const string WorkflowId = "example.humandecision";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.HumanDecisions." + Guid.NewGuid().ToString("N"));
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

        private readonly List<Fixture> _fixtures = new List<Fixture>();

        private Fixture NewFixture()
        {
            var fixture = new Fixture
            {
                AcquireLock = () => CoordinatorRunLock.TryAcquire(_root),
            };
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
                new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
        }

        private sealed class FakeRunStore : IRunStore
        {
            internal readonly List<JournalRecord> Appended = new List<JournalRecord>();
            internal readonly List<Durability> AppendedDurability = new List<Durability>();

            /// <summary>
            /// When set, an append this predicate accepts is refused with
            /// OW-STORE-001 and nothing becomes durable — <see cref="Appended"/>
            /// then holds exactly the journal a reload would see (SPEC §F1).
            /// </summary>
            internal Func<JournalRecord, bool>? ShouldFail;

            public Result<Unit> CreateRun(string runId, CompiledPlan plan, WorkflowDocument doc) =>
                Result<Unit>.Success(Unit.Value);

            internal Result<Unit> CreateRun(
                string runId, CompiledPlan plan, WorkflowDocument document, ActorKind actor) =>
                CreateRun(runId, plan, document);

            public Result<Unit> Append(string runId, JournalRecord record, Durability durability)
            {
                if (ShouldFail?.Invoke(record) == true)
                {
                    return Result<Unit>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_STORE_001, "The fake store refused the append."));
                }
                Appended.Add(record);
                AppendedDurability.Add(durability);
                return Result<Unit>.Success(Unit.Value);
            }

            /// <summary>
            /// When true, <see cref="Load"/> folds the recorded journal into a
            /// <see cref="RunSnapshot"/> — the reload reconstruction the
            /// Run Store performs.
            /// </summary>
            internal bool FoldOnLoad = true;

            public Result<RunSnapshot> Load(string runId)
            {
                if (!FoldOnLoad)
                {
                    return Result<RunSnapshot>.Failure(
                        ErrorCodes.Create(ErrorCodes.OW_STORE_001, "not wired"));
                }
                var fold = JournalFold.Empty();
                foreach (var record in Appended)
                {
                    fold.Apply(record);
                }
                return Result<RunSnapshot>.Success(new RunSnapshot(
                    runId, null, fold.State, fold.Steps(), fold.Outputs,
                    fold.OpenResources, fold.CancelPending,
                    fold.RecoveryDecisionRequired, fold.CreatedAt, fold.UpdatedAt,
                    fold.Session, fold.LastSeq, fold.Ended, fold.LastStepIndex,
                    fold.ReviewRetryStep, fold.CancelReason));
            }

            public Result<IReadOnlyList<RunIndexEntry>> List(RunQuery query) =>
                throw new NotSupportedException("The fake store does not list.");

            public Result<Unit> Delete(string runId) =>
                throw new NotSupportedException("The fake store does not delete.");
        }

        private sealed class FakeRequirements : IRequirementEvaluator
        {
            public RequirementEvaluation Evaluate(Requirement requirement, StepContext context) =>
                RequirementEvaluation.Satisfied();
        }

        private sealed class Fixture
        {
            internal readonly FakeRunStore Store = new FakeRunStore();
            internal readonly FakeEditorState Editor = new FakeEditorState();
            internal readonly FakeClock Clock = new FakeClock();
            internal readonly List<RegisteredOperation> Operations =
                new List<RegisteredOperation>();
            internal readonly Dictionary<string, IOperation> Ops =
                new Dictionary<string, IOperation>(StringComparer.Ordinal);
            internal readonly List<RunResource> Released = new List<RunResource>();
            // The Store.Appended count captured at each release-seam call —
            // the journal position the physical release ran at (the
            // resource_released record follows it).
            internal readonly List<int> ReleasedAtJournalCount = new List<int>();

            internal Func<Result<CoordinatorRunLock>> AcquireLock = () =>
                Result<CoordinatorRunLock>.Failure(
                    ErrorCodes.Create(ErrorCodes.OW_RUN_002, "not wired"));

            private RunCoordinator? _coordinator;
            private RunLoop? _loop;

            internal RunCoordinator? CoordinatorCreated => _coordinator;

            internal RunCoordinator Coordinator => _coordinator ??= new RunCoordinator(
                Store,
                (runId, plan, document, actor) => Store.CreateRun(runId, plan, document, actor),
                Editor,
                Clock,
                () => Snapshot(),
                AcquireLock,
                Session);

            internal RunLoop Loop => _loop ??= new RunLoop(
                Coordinator,
                Clock,
                Editor,
                DescriptorTestFixtures.CreateRegistry(),
                Snapshot(),
                Resolve,
                new FakeRequirements(),
                _ => new UndoScope(),
                () => false,
                monotonicTimerFactory: null,
                releaseResource: (_, resource) =>
                {
                    Released.Add(resource);
                    ReleasedAtJournalCount.Add(Store.Appended.Count);
                    return Result<Unit>.Success(Unit.Value);
                });

            internal RegistrySnapshot Snapshot() =>
                new RegistrySnapshot(
                    "reg_01J00000000000000000000000",
                    new string('f', 64),
                    Operations,
                    DescriptorTestFixtures.CreateRegistry(),
                    Array.Empty<RegistryChange>(),
                    Array.Empty<TypeRegistryFailure>());

            private Result<IOperation> Resolve(PlanStep step) =>
                Ops.TryGetValue(step.OperationId, out var op)
                    ? Result<IOperation>.Success(op)
                    : Result<IOperation>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_RUN_011,
                        "The step's Operation cannot be resolved.",
                        nodeId: step.NodeId));

            internal void Register(IOperation op, char hashSeed)
            {
                Ops[op.Descriptor.Id] = op;
                Operations.Add(new RegisteredOperation(
                    op.Descriptor, OperationOrigin.Custom, "Example.Operations",
                    null, new string(hashSeed, 64), new string((char)(hashSeed + 1), 64)));
            }
        }

        private sealed class UndoScope : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        // ── Dummy operations ────────────────────────────────────────────

        /// <summary>An Immediate that succeeds without outputs — the
        /// approval-wait fixtures' inert step body.</summary>
        private sealed class ImmediateOperation : IOperation
        {
            internal ImmediateOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Succeeds immediately.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>A Reconcilable Immediate whose first attempt fails with a
        /// retryable diagnostic and whose scripted Reconcile verdict answers
        /// the §F5 consult — an Unknown verdict drives the Run to
        /// NeedsReview; a later attempt succeeds.</summary>
        private sealed class ReconcilableOperation : IOperation
        {
            private readonly Queue<ReconcileResult> _verdicts;
            private readonly Diagnostic _error;

            internal ReconcilableOperation(string id, IEnumerable<ReconcileResult> verdicts)
            {
                _verdicts = new Queue<ReconcileResult>(verdicts);
                _error = ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true);
                Descriptor = Op.Define(id, 1)
                    .Summary("Reconciles after failure.")
                    .Output("out", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Reconcilable)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            internal int ExecuteCalls;
            internal int ReconcileCalls;
            // When set, Execute acquires a Run resource through the
            // context's IRunResourceRegistry — the still-open resource the
            // abort and store-failure ends must release (SPEC §E6 item 4).
            internal bool AcquireResource;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    Json("{\"seen\":true}"));

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                if (AcquireResource)
                {
                    ((IRunResourceRegistry)ctx).Acquire(
                        "editing_contents", Json("{\"guid\":\"abc\"}"));
                }
                if (ExecuteCalls == 1)
                {
                    return StepResult.Failed(_error);
                }
                ctx.SetOutput("out", "fresh");
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                ReconcileCalls++;
                return _verdicts.Count == 0
                    ? new ReconcileResult.Unknown("No scripted verdict left.")
                    : _verdicts.Dequeue();
            }

            public void Cancel(StepContext ctx)
            {
            }
        }

        // ── Plan/doc builders ───────────────────────────────────────────

        private static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            IReadOnlyDictionary<string, int>? transitions = null,
            RetryPolicy? retry = null,
            Idempotency idempotency = Idempotency.Idempotent) =>
            new PlanStep(
                index, nodeId, operationId, 1,
                new string(hashSeed, 64), new string((char)(hashSeed + 1), 64),
                new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                transitions ?? new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                30, retry ?? new RetryPolicy(), idempotency);

        private static CompiledPlan Plan(
            IReadOnlyList<PlanStep> steps, int approvalCount = 0)
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
                new string('a', 64), WorkflowId, new string('b', 64), new string('c', 64),
                Json("{}"),
                steps,
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
                    new NodeData { Id = "n1", Operation = "test.dummy", Version = 1 },
                },
                ControlEdges = new List<ControlEdge>(),
            });
            Assert.IsTrue(created.Ok, "WorkflowDocument.Create failed.");
            return created.Value!;
        }

        private static string AcceptRun(Fixture fixture, CompiledPlan plan)
        {
            var accepted = fixture.Coordinator.Accept(
                plan, Doc(), ActorKind.Agent, new AcceptOptions(null, false));
            Assert.IsTrue(accepted.Ok, "acceptance failed");
            return accepted.Value!.RunId;
        }

        private static List<JournalRecord> RecordsOfType(
            Fixture fixture, JournalRecordType type) =>
            fixture.Store.Appended.FindAll(record => record.Type == type);

        private static JObject DataOf(JournalRecord record) =>
            (JObject)record.Data.ToToken();

        private string LockPath => Path.Combine(_root, CoordinatorRunLock.LockFileName);
        private string StatePath => Path.Combine(_root, CoordinatorRunLock.StateFileName);

        // Raw OS-level open attempt on coordinator.lock — the same denied
        // open a foreign process sees.
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

        // A run driven to NeedsReview: the Reconcilable Operation's first
        // attempt fails retryably and its scripted Reconcile answers
        // Unknown, so the §F5 consult ends the step NeedsReview.
        private static string NeedsReviewRun(Fixture fixture, out ReconcilableOperation op)
        {
            op = new ReconcilableOperation(
                "test.review.reconcilable",
                new[] { (ReconcileResult)new ReconcileResult.Unknown("State cannot be proven.") });
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.review.reconcilable", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.NeedsReview, fixture.Coordinator.StateOf(runId),
                "the fixture must reach NeedsReview");
            return runId;
        }

        // ── Approve (SPEC §E2/§H4): AwaitingApproval → Queued ───────────

        [Test]
        public void ApproveMovesAwaitingRunToQueued()
        {
            var fixture = NewFixture();
            fixture.Register(new ImmediateOperation("test.op"), 'd');
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Coordinator.StateOf(runId));

            var approved = fixture.Coordinator.Approve(runId, "human");

            Assert.IsTrue(approved.Ok);
            Assert.AreEqual(RunState.Queued, approved.Value!.State);
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(runId));
            var granted = RecordsOfType(fixture, JournalRecordType.ApprovalGranted);
            Assert.AreEqual(1, granted.Count);
            Assert.IsTrue(granted[0].Required, "approval_granted is required:true (SPEC §F2)");
            Assert.AreEqual(Durability.Flushed, granted[0].Durability);
            Assert.AreEqual("human", (string?)DataOf(granted[0])["approver"]);
            Assert.AreEqual(0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "approval is not a terminal record");
        }

        // SPEC §H4: the human approves the Run's asks together — one
        // Approve call is the AwaitingApproval → Queued decision, so every
        // required approval is granted in the single call: one
        // approval_granted record per approval the Plan recorded (the
        // per-record count the §F2 fold applies), then the Run queues.
        [Test]
        public void SingleApproveGrantsEveryRequiredApproval()
        {
            var fixture = NewFixture();
            fixture.Register(new ImmediateOperation("test.op"), 'd');
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 2));
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Coordinator.StateOf(runId));

            var approved = fixture.Coordinator.Approve(runId, "human");

            Assert.IsTrue(approved.Ok);
            Assert.AreEqual(RunState.Queued, approved.Value!.State);
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(runId));
            var granted = RecordsOfType(fixture, JournalRecordType.ApprovalGranted);
            Assert.AreEqual(2, granted.Count,
                "one approval_granted per recorded approval — the §F2 fold's count");
            foreach (var record in granted)
            {
                Assert.IsTrue(record.Required, "approval_granted is required:true (SPEC §F2)");
                Assert.AreEqual("human", (string?)DataOf(record)["approver"]);
            }
            // The journal's own fold agrees: a reload reconstruction is Queued.
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.Get(runId)!.State);
            // A second decision asks for a transition the §E1 table does
            // not contain — bug detection throws, no extra record.
            var recordsBefore = fixture.Store.Appended.Count;
            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.Approve(runId, "human2"));
            Assert.AreEqual(recordsBefore, fixture.Store.Appended.Count);
        }

        // A granted Run is Queued, not AwaitingApproval: a further Approve
        // asks for a transition the §E1 table does not contain — bug
        // detection throws, state unchanged, no extra record.
        [Test]
        public void ApproveOnAlreadyGrantedRunThrows()
        {
            var fixture = NewFixture();
            fixture.Register(new ImmediateOperation("test.op"), 'd');
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));
            Assert.IsTrue(fixture.Coordinator.Approve(runId, "human").Ok);

            var recordsBefore = fixture.Store.Appended.Count;
            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.Approve(runId, "human2"));
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(recordsBefore, fixture.Store.Appended.Count,
                "an inapplicable decision writes nothing");
        }

        // ── Reject (SPEC §E2/§H4): AwaitingApproval → Cancelled ─────────

        [Test]
        public void RejectCancelsAwaitingRunWithOwPerm003()
        {
            var fixture = NewFixture();
            var steps = new[]
            {
                Step(0, "n1", "test.op", 'd', transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.op", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));

            var rejected = fixture.Coordinator.Reject(runId, "human", "not safe");

            Assert.IsTrue(rejected.Ok);
            Assert.AreEqual(RunState.Cancelled, rejected.Value!.State);
            Assert.AreEqual("OW-PERM-003", rejected.Diagnostics[0].Code);
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
            var rejectedRecords = RecordsOfType(fixture, JournalRecordType.ApprovalRejected);
            Assert.AreEqual(1, rejectedRecords.Count);
            Assert.IsTrue(rejectedRecords[0].Required);
            Assert.AreEqual(Durability.Flushed, rejectedRecords[0].Durability);
            Assert.AreEqual("human", (string?)DataOf(rejectedRecords[0])["approver"]);
            Assert.AreEqual("not safe", (string?)DataOf(rejectedRecords[0])["reason"]);
            // SPEC §E1: every still-Pending step is journaled step_skipped
            // before the terminal record.
            var skipped = RecordsOfType(fixture, JournalRecordType.StepSkipped);
            Assert.AreEqual(2, skipped.Count);
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("cancelled", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-PERM-003", (string?)DataOf(finished[0])["summary"]?["code"]);
            // The journal's own fold agrees: the reconstruction is Cancelled.
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.Get(runId)!.State);
        }

        // The marked SPEC §F1 store-failure end reaches the SAME single
        // run_finished writer (T-W2.4-02 §F7): the bound artifact finalizer
        // ran exactly once — in the first refused end attempt — and is never
        // invoked again when the marked end journals its single
        // run_finished{failed}, which still ends the Run Failed with the
        // store diagnostic.
        [Test]
        public void TheStoreFailedEndRunsTheArtifactFinalizerOnceBeforeTheRecord()
        {
            var fixture = NewFixture();
            fixture.Register(new ImmediateOperation("test.op"), 'd');
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps));
            var calls = new List<string>();
            fixture.Coordinator.BindRunEndArtifactFinalizer(
                id =>
                {
                    calls.Add(id);
                    return new[]
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_STORE_001,
                            "The Run artifacts could not be written."),
                    };
                });
            var refusedOnce = false;
            fixture.Store.ShouldFail = record =>
            {
                if (record.Type == JournalRecordType.RunFinished && !refusedOnce)
                {
                    refusedOnce = true;
                    return true;
                }
                return false;
            };

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                new[] { runId }, calls,
                "the finalizer runs exactly once per Run end, whichever end path reaches the writer");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)DataOf(finished[0])["summary"]?["code"]);
        }

        [Test]
        public void RejectOnNonAwaitingRunThrows()
        {
            var fixture = NewFixture();
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(runId));

            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.Reject(runId, "human", "no"));
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(0, fixture.Store.Appended.Count);
        }

        // ── Wrong state / unknown Run — no transition is possible ───────

        [Test]
        public void DecisionsOnUnknownRunFailOwRun010()
        {
            var fixture = NewFixture();
            var runId = Identifiers.NewRunId();
            AssertDiagnostic(fixture.Coordinator.Approve(runId, "h"), "OW-RUN-010");
            AssertDiagnostic(fixture.Coordinator.Reject(runId, "h", "r"), "OW-RUN-010");
            AssertDiagnostic(
                fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort), "OW-RUN-010");
            AssertDiagnostic(
                fixture.Coordinator.ResolveReview(runId, ReviewDecision.RetryStep), "OW-RUN-010");
        }

        [Test]
        public void DecisionsOnNonWaitingStatesThrow()
        {
            var fixture = NewFixture();
            var op = new ImmediateOperation("test.op.immediate");
            fixture.Register(op, 'd');
            var steps = new[] { Step(0, "n1", "test.op.immediate", 'd') };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));

            // A terminal Run admits no decision — every entry point asks
            // for a transition the §E1 table does not contain; bug
            // detection throws and the state is unchanged.
            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.Approve(runId, "h"));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.Reject(runId, "h", "r"));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.ResolveReview(runId, ReviewDecision.RetryStep));
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        [Test]
        public void ResolveReviewOnAwaitingApprovalRunThrows()
        {
            var fixture = NewFixture();
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));

            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.ResolveReview(runId, ReviewDecision.RetryStep));
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(0, fixture.Store.Appended.Count);
        }

        // An undeclared ReviewDecision value asks for a transition the §E1
        // table does not contain — bug detection throws before anything is
        // journaled, never a guessed resolution.
        [Test]
        public void ResolveReviewRejectsUndeclaredDecision()
        {
            var fixture = NewFixture();
            var runId = NeedsReviewRun(fixture, out _);
            var recordsBefore = fixture.Store.Appended.Count;
            Assert.Throws<ArgumentOutOfRangeException>(
                () => fixture.Coordinator.ResolveReview(runId, (ReviewDecision)99));
            Assert.AreEqual(RunState.NeedsReview, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(recordsBefore, fixture.Store.Appended.Count,
                "bug detection throws before anything is journaled");
        }

        // ── ResolveReview(abort) (SPEC §E1): NeedsReview → Cancelled ────

        [Test]
        public void ResolveReviewAbortCancelsReviewedStepAndRun()
        {
            var fixture = NewFixture();
            var op = new ReconcilableOperation(
                "test.review.reconcilable",
                new[] { (ReconcileResult)new ReconcileResult.Unknown("State cannot be proven.") });
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.review.reconcilable", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable),
                Step(1, "n2", "test.review.reconcilable", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.NeedsReview, fixture.Coordinator.StateOf(runId));
            Assert.IsTrue(OsOpenDenied(LockPath), "the active Run holds the exclusion");

            var resolved = fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort);

            Assert.IsTrue(resolved.Ok);
            Assert.AreEqual(RunState.Cancelled, resolved.Value!.State);
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
            var review = RecordsOfType(fixture, JournalRecordType.ReviewResolved);
            Assert.AreEqual(1, review.Count);
            Assert.IsTrue(review[0].Required, "review_resolved is required:true (SPEC §F2)");
            Assert.AreEqual(Durability.Flushed, review[0].Durability);
            Assert.AreEqual("abort", (string?)DataOf(review[0])["decision"]);
            // SPEC §E0: the NeedsReview step is cancelled; the still-Pending
            // one is skipped (SPEC §E1); the Run ends Cancelled.
            var cancelled = RecordsOfType(fixture, JournalRecordType.StepCancelled);
            Assert.AreEqual(1, cancelled.Count);
            Assert.AreEqual(0, cancelled[0].Step);
            var skipped = RecordsOfType(fixture, JournalRecordType.StepSkipped);
            Assert.AreEqual(1, skipped.Count);
            Assert.AreEqual(1, skipped[0].Step);
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("cancelled", (string?)DataOf(finished[0])["state"]);
            // The exclusion is released at Run end (SPEC §E2 item 3).
            Assert.IsFalse(OsOpenDenied(LockPath), "abort releases the exclusion");
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.Get(runId)!.State);
            var stepSnapshots = fixture.Coordinator.Get(runId)!.Steps;
            Assert.AreEqual(StepState.Cancelled, stepSnapshots[0].State,
                "the journal fold records the reviewed step cancelled");
            Assert.AreEqual(StepState.Skipped, stepSnapshots[1].State);
        }

        // A cancelled Run accepts no further decision — the abort is
        // terminal.
        [Test]
        public void AbortedRunAcceptsNoFurtherDecision()
        {
            var fixture = NewFixture();
            var runId = NeedsReviewRun(fixture, out _);
            Assert.IsTrue(
                fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort).Ok);

            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.ResolveReview(runId, ReviewDecision.RetryStep));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.Approve(runId, "h"));
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(1, RecordsOfType(fixture, JournalRecordType.ReviewResolved).Count);
        }

        // SPEC §E6 item 4: a NeedsReview Run may still hold open Run
        // resources — a successful abort releases them through the run
        // loop's own terminal cleanup: the physical release first, then the
        // journaled resource_released, then run_finished and the lock
        // release.
        [Test]
        public void ResolveReviewAbortReleasesOpenRunResources()
        {
            var fixture = NewFixture();
            var op = new ReconcilableOperation(
                "test.review.reconcilable",
                new[] { (ReconcileResult)new ReconcileResult.Unknown("State cannot be proven.") })
            {
                AcquireResource = true,
            };
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.review.reconcilable", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.NeedsReview, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ResourceAcquired).Count);
            Assert.IsTrue(OsOpenDenied(LockPath), "the active Run holds the exclusion");

            var resolved = fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort);

            Assert.IsTrue(resolved.Ok);
            Assert.AreEqual(RunState.Cancelled, resolved.Value!.State);
            Assert.AreEqual(1, fixture.Released.Count,
                "the release seam released the still-open resource at Run end");
            Assert.AreEqual("editing_contents", fixture.Released[0].Kind);
            var released = RecordsOfType(fixture, JournalRecordType.ResourceReleased);
            Assert.AreEqual(1, released.Count);
            var releasedIndex = fixture.Store.Appended.IndexOf(released[0]);
            Assert.AreEqual(fixture.ReleasedAtJournalCount[0], releasedIndex,
                "the physical release precedes its resource_released record");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("cancelled", (string?)DataOf(finished[0])["state"]);
            Assert.Less(releasedIndex, fixture.Store.Appended.IndexOf(finished[0]),
                "resource_released is journaled before the terminal record");
            Assert.IsFalse(OsOpenDenied(LockPath), "abort releases the exclusion");
            var snapshot = fixture.Coordinator.Get(runId)!;
            Assert.AreEqual(RunState.Cancelled, snapshot.State);
            Assert.AreEqual(0, snapshot.OpenResources.Count,
                "the journal's fold shows no open resource");
        }

        // ── ResolveReview(retry_step) (SPEC §E1): NeedsReview → Running ─

        [Test]
        public void ResolveReviewRetryStepRerunsTheReviewedStep()
        {
            var fixture = NewFixture();
            var runId = NeedsReviewRun(fixture, out var op);

            var resolved = fixture.Coordinator.ResolveReview(runId, ReviewDecision.RetryStep);

            Assert.IsTrue(resolved.Ok);
            Assert.AreEqual(RunState.Running, resolved.Value!.State);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));
            var review = RecordsOfType(fixture, JournalRecordType.ReviewResolved);
            Assert.AreEqual(1, review.Count);
            Assert.AreEqual("retry_step", (string?)DataOf(review[0])["decision"]);
            Assert.AreEqual(1, op.ExecuteCalls, "the decision itself never re-executes");
            Assert.IsTrue(OsOpenDenied(LockPath),
                "a retrying Run keeps the exclusion it held through NeedsReview");

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            // The step re-entered E4 step 1 as a fresh attempt: a new
            // step_started{attempt:2} and its success, then the Run's end.
            Assert.AreEqual(2, op.ExecuteCalls, "retry_step re-runs the step");
            var started = RecordsOfType(fixture, JournalRecordType.StepStarted);
            Assert.AreEqual(2, started.Count);
            Assert.AreEqual(2, started[1].Attempt);
            var succeeded = RecordsOfType(fixture, JournalRecordType.StepSucceeded);
            Assert.AreEqual(1, succeeded.Count);
            Assert.AreEqual("fresh", (string?)DataOf(succeeded[0])["outputs"]?["out"]);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual("succeeded", (string?)DataOf(finished[0])["state"]);
        }

        // ── SPEC §E2 item 5: approval-wait invalidation (OW-RUN-011) ────

        // A Registry change during the approval wait cancels the Run: the
        // per-Tick pass journals the pending steps' step_skipped and the
        // terminal record with the OW-RUN-011 summary.
        [Test]
        public void RegistryChangeDuringApprovalWaitCancelsRun()
        {
            var fixture = NewFixture();
            var op = new ImmediateOperation("test.op.immediate");
            fixture.Register(op, 'd');
            var steps = new[] { Step(0, "n1", "test.op.immediate", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Coordinator.StateOf(runId));

            // The Operation changed since the Plan recorded its
            // fingerprints — a different implementation hash is reported.
            fixture.Operations.Clear();
            fixture.Operations.Add(new RegisteredOperation(
                op.Descriptor, OperationOrigin.Custom, "Example.Operations",
                null, new string('d', 64), new string('z', 64)));

            var ticked = fixture.Loop.Tick();
            Assert.IsTrue(ticked.Ok);

            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count);
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("cancelled", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-RUN-011", (string?)DataOf(finished[0])["summary"]?["code"]);
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.Get(runId)!.State);

            // The cancelled Run accepts no approval — no other transition
            // is possible (SPEC §E1 bug detection throws).
            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.Approve(runId, "h"));
        }

        // The same check guards the decisive moment: an approval granted
        // after the Registry changed never reaches the journal — the Run
        // is cancelled with OW-RUN-011 instead.
        [Test]
        public void ApproveAfterRegistryChangeCancelsWithOwRun011()
        {
            var fixture = NewFixture();
            var op = new ImmediateOperation("test.op.immediate");
            fixture.Register(op, 'd');
            var steps = new[] { Step(0, "n1", "test.op.immediate", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));

            fixture.Operations.Clear();

            var approved = fixture.Coordinator.Approve(runId, "human");

            Assert.IsTrue(approved.Ok, "the decision applies — as the cancellation");
            Assert.AreEqual(RunState.Cancelled, approved.Value!.State);
            Assert.AreEqual("OW-RUN-011", approved.Diagnostics[0].Code);
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ApprovalGranted).Count,
                "no approval is granted for changed code");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual("cancelled", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-RUN-011", (string?)DataOf(finished[0])["summary"]?["code"]);
        }

        // Only the Run whose used Operation changed is invalidated: an
        // unaffected approval wait survives and approves normally.
        [Test]
        public void InvalidationCancelsOnlyTheAffectedAwaitingRun()
        {
            var fixture = NewFixture();
            var changed = new ImmediateOperation("test.op.changed");
            var stable = new ImmediateOperation("test.op.stable");
            fixture.Register(changed, 'd');
            fixture.Register(stable, 'm');
            var affected = AcceptRun(
                fixture,
                Plan(new[] { Step(0, "n1", "test.op.changed", 'd') }, approvalCount: 1));
            var unaffected = AcceptRun(
                fixture,
                Plan(new[] { Step(0, "n1", "test.op.stable", 'm') }, approvalCount: 1));

            fixture.Operations.RemoveAt(0);

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(affected));
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Coordinator.StateOf(unaffected));
            var approved = fixture.Coordinator.Approve(unaffected, "human");
            Assert.IsTrue(approved.Ok);
            Assert.AreEqual(RunState.Queued, approved.Value!.State);
        }

        // An unchanged Registry invalidates nothing — the approval wait
        // survives its Tick checks.
        [Test]
        public void UnchangedRegistryKeepsTheApprovalWait()
        {
            var fixture = NewFixture();
            var op = new ImmediateOperation("test.op.immediate");
            fixture.Register(op, 'd');
            var steps = new[] { Step(0, "n1", "test.op.immediate", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.AwaitingApproval, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(0, fixture.Store.Appended.Count,
                "an awaiting Run writes no journal while the Registry matches");
        }

        // SPEC §F1: a refused decision record never reaches the store —
        // the approval never durably happened and the Run ends Failed
        // once through the store-failure path, never left to approve or
        // start again.
        [Test]
        public void RefusedApprovalRecordEndsRunFailed()
        {
            var fixture = NewFixture();
            fixture.Register(new ImmediateOperation("test.op"), 'd');
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));
            fixture.Store.ShouldFail = record => record.Type == JournalRecordType.ApprovalGranted;

            var approved = fixture.Coordinator.Approve(runId, "human");

            Assert.IsFalse(approved.Ok);
            Assert.AreEqual("OW-STORE-001", approved.Diagnostics[0].Code);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the refused required record ends the Run once (SPEC §F1)");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
        }

        // ── SPEC §F1: a decision whose records never reach the store ────

        // A step_skipped the store refuses during the rejection's
        // cancelled end ends the Run Failed once through the per-Run
        // store-failure path — the journaled approval_rejected stands but
        // the rejection's remaining records never became durable, so the
        // call reports the refusal, never a fabricated Cancelled success.
        [Test]
        public void RejectWithRefusingStoreEndsRunFailedAndFails()
        {
            var fixture = NewFixture();
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));
            fixture.Store.ShouldFail = record => record.Type == JournalRecordType.StepSkipped;

            var rejected = fixture.Coordinator.Reject(runId, "human", "not safe");

            Assert.IsFalse(rejected.Ok, "the refused records report a failure");
            Assert.AreEqual("OW-PERM-003", rejected.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-001", rejected.Diagnostics[1].Code);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ApprovalRejected).Count,
                "the decision itself was journaled");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count,
                "the refused step record never became durable");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the refused records take the §F1 store-failure path");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"],
                "the store-failure end journals run_finished{failed}, never Cancelled");
        }

        // The same refusal inside the fingerprint-mismatch cancellation:
        // the approval never reaches the journal, the Run ends Failed and
        // the call reports the store failure.
        [Test]
        public void ApproveFingerprintMismatchWithRefusingStoreFails()
        {
            var fixture = NewFixture();
            var op = new ImmediateOperation("test.op.immediate");
            fixture.Register(op, 'd');
            var steps = new[] { Step(0, "n1", "test.op.immediate", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));
            fixture.Operations.Clear();
            fixture.Store.ShouldFail = record => record.Type == JournalRecordType.StepSkipped;

            var approved = fixture.Coordinator.Approve(runId, "human");

            Assert.IsFalse(approved.Ok, "the refused records report a failure");
            Assert.AreEqual("OW-RUN-011", approved.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-001", approved.Diagnostics[1].Code);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ApprovalGranted).Count,
                "no approval is granted for changed code");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
        }

        // The abort's step consequences refused by the store: the NeedsReview
        // Run is marked for the store-failure end (it may still hold §E6
        // item 4 resources), the call reports the refusal and the run loop
        // finishes the single run_finished{failed} on the next Tick.
        [Test]
        public void ResolveReviewAbortWithRefusingStoreFails()
        {
            var fixture = NewFixture();
            var runId = NeedsReviewRun(fixture, out _);
            fixture.Store.ShouldFail = record => record.Type == JournalRecordType.StepCancelled;

            var resolved = fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort);

            Assert.IsFalse(resolved.Ok, "the refused records report a failure");
            Assert.AreEqual("OW-RUN-005", resolved.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-001", resolved.Diagnostics[1].Code);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ReviewResolved).Count,
                "the decision itself was journaled");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepCancelled).Count,
                "the refused step record never became durable");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the marked Run's in-memory state is already Failed (SPEC §F1)");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "a NeedsReview Run's terminal record follows the loop's cleanup");

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"],
                "the marked Run ends Failed through the §E6 item 4 cleanup");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // SPEC §F1: when the journal cannot be reconstructed after the
        // review_resolved append, the step consequences are undecidable —
        // ending the Run Cancelled would leave its Pending steps never
        // journaled step_skipped (SPEC §E1), so the Run is marked Failed
        // with the load diagnostics and the loop finishes it.
        [Test]
        public void ResolveReviewAbortWithFailedLoadEndsRunFailed()
        {
            var fixture = NewFixture();
            var runId = NeedsReviewRun(fixture, out _);
            fixture.Store.FoldOnLoad = false;

            var resolved = fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort);

            Assert.IsFalse(resolved.Ok, "the unreconstructable journal reports a failure");
            Assert.AreEqual("OW-RUN-005", resolved.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-001", resolved.Diagnostics[1].Code,
                "the load diagnostics ride on the refusal");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ReviewResolved).Count,
                "the decision itself was journaled");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the Run takes the §F1 store-failure path, never Cancelled");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepCancelled).Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "no Cancelled end is journaled without the step consequences");

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"],
                "the marked Run ends Failed through the §E6 item 4 cleanup");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // The abort's Cancelled terminal record refused by the store takes
        // the centralized §F1 store-failure path like every other refused
        // required append: the Run is marked Failed — never left with its
        // step consequences journaled and no terminal record — the run
        // loop's §E6 item 4 cleanup releases the still-open resources and
        // journals run_finished{failed} on the next Tick, and the exclusion
        // is released.
        [Test]
        public void ResolveReviewAbortTerminalRefusalEndsRunFailedThroughCleanup()
        {
            var fixture = NewFixture();
            var op = new ReconcilableOperation(
                "test.review.reconcilable",
                new[] { (ReconcileResult)new ReconcileResult.Unknown("State cannot be proven.") })
            {
                AcquireResource = true,
            };
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.review.reconcilable", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.NeedsReview, fixture.Coordinator.StateOf(runId));
            // The store refuses the Cancelled terminal record only — the
            // following run_finished{failed} still lands.
            fixture.Store.ShouldFail = record =>
                record.Type == JournalRecordType.RunFinished &&
                string.Equals(
                    (string?)DataOf(record)["state"], "cancelled",
                    StringComparison.Ordinal);

            var resolved = fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort);

            Assert.IsFalse(resolved.Ok, "the refused terminal record reports a failure");
            Assert.AreEqual("OW-RUN-005", resolved.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-001", resolved.Diagnostics[1].Code);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the marked Run's in-memory state is already Failed (SPEC §F1)");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "the refused record never became durable");
            // The §E6 item 4 release already ran ahead of the refused
            // terminal record — the journaled resource_released stands.
            Assert.AreEqual(1, fixture.Released.Count);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ResourceReleased).Count);
            Assert.IsTrue(OsOpenDenied(LockPath), "the exclusion is still held");

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(1, fixture.Released.Count,
                "the already-released resource is never released twice");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)DataOf(finished[0])["summary"]?["code"],
                "the terminal record carries the store diagnostic");
            Assert.IsFalse(OsOpenDenied(LockPath), "the exclusion is released");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.Get(runId)!.State);
        }

        // The per-Tick invalidation pass reports the same refusal: an
        // AwaitingApproval Run whose cancellation records never reach the
        // store ends Failed and the pass returns failure, never success.
        [Test]
        public void InvalidateStaleApprovalsWithRefusingStoreFails()
        {
            var fixture = NewFixture();
            var op = new ImmediateOperation("test.op.immediate");
            fixture.Register(op, 'd');
            var steps = new[] { Step(0, "n1", "test.op.immediate", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));
            fixture.Operations.Clear();
            fixture.Store.ShouldFail = record => record.Type == JournalRecordType.StepSkipped;

            var invalidated = fixture.Coordinator.InvalidateStaleApprovals();

            Assert.IsFalse(invalidated.Ok, "the refused records report a failure");
            Assert.AreEqual("OW-RUN-011", invalidated.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-001", invalidated.Diagnostics[1].Code);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the refused records take the §F1 store-failure path");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
        }

        // The rejection's Cancelled terminal record refused by the store
        // takes the same centralized §F1 store-failure path as a refused
        // step record: one run_finished{failed} attempt with the store
        // diagnostic — which this store accepts — the call reports the
        // refusal (never a fabricated success), and the journal's own
        // fold reconstructs Failed. An approval wait holds no §E6 item 4
        // resource and no exclusion, so nothing stays open or held.
        [Test]
        public void RejectTerminalRefusalEndsRunFailedThroughStoreFailureEnd()
        {
            var fixture = NewFixture();
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));
            fixture.Store.ShouldFail = record =>
                record.Type == JournalRecordType.RunFinished &&
                string.Equals(
                    (string?)DataOf(record)["state"], "cancelled",
                    StringComparison.Ordinal);

            var rejected = fixture.Coordinator.Reject(runId, "human", "not safe");

            Assert.IsFalse(rejected.Ok, "the refused terminal record reports a failure");
            Assert.AreEqual("OW-PERM-003", rejected.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-001", rejected.Diagnostics[1].Code);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ApprovalRejected).Count,
                "the decision itself was journaled");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count,
                "the step consequences were journaled");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the refused terminal record takes the §F1 store-failure path");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"],
                "the store-failure end journals run_finished{failed}, never Cancelled");
            Assert.AreEqual("OW-STORE-001", (string?)DataOf(finished[0])["summary"]?["code"],
                "the terminal record carries the store diagnostic");
            var snapshot = fixture.Coordinator.Get(runId)!;
            Assert.AreEqual(RunState.Failed, snapshot.State,
                "the journal's own fold reconstructs Failed");
            Assert.AreEqual(0, snapshot.OpenResources.Count,
                "an approval wait holds no open Run resource");
            Assert.IsFalse(OsOpenDenied(LockPath), "no exclusion is held");
            // The Failed end is terminal: no further decision is possible.
            Assert.Throws<InvalidOperationException>(
                () => fixture.Coordinator.Approve(runId, "h"));
        }

        // The same terminal refusal inside the per-Tick invalidation: the
        // AwaitingApproval Run ends Failed through one run_finished{failed}
        // carrying the store diagnostic — the pass reports failure, the
        // journal fold reconstructs Failed and nothing stays held.
        [Test]
        public void InvalidateStaleApprovalsTerminalRefusalEndsRunFailed()
        {
            var fixture = NewFixture();
            var op = new ImmediateOperation("test.op.immediate");
            fixture.Register(op, 'd');
            var steps = new[] { Step(0, "n1", "test.op.immediate", 'd') };
            var runId = AcceptRun(fixture, Plan(steps, approvalCount: 1));
            fixture.Operations.Clear();
            fixture.Store.ShouldFail = record =>
                record.Type == JournalRecordType.RunFinished &&
                string.Equals(
                    (string?)DataOf(record)["state"], "cancelled",
                    StringComparison.Ordinal);

            var invalidated = fixture.Coordinator.InvalidateStaleApprovals();

            Assert.IsFalse(invalidated.Ok, "the refused terminal record reports a failure");
            Assert.AreEqual("OW-RUN-011", invalidated.Diagnostics[0].Code);
            Assert.AreEqual("OW-STORE-001", invalidated.Diagnostics[1].Code);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count,
                "the step consequences were journaled");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the refused terminal record takes the §F1 store-failure path");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)DataOf(finished[0])["summary"]?["code"]);
            var snapshot = fixture.Coordinator.Get(runId)!;
            Assert.AreEqual(RunState.Failed, snapshot.State,
                "the journal's own fold reconstructs Failed");
            Assert.AreEqual(0, snapshot.OpenResources.Count);
            Assert.IsFalse(OsOpenDenied(LockPath), "no exclusion is held");
        }

        // A pending cancel's Cancelled terminal record refused by the
        // store takes the same §F1 path in the inactive-cancel pass: one
        // run_finished{failed} attempt with the store diagnostic — never
        // left spinning in Queued for the start gate.
        [Test]
        public void InactiveCancelTerminalRefusalEndsRunFailed()
        {
            var fixture = NewFixture();
            var steps = new[] { Step(0, "n1", "test.op", 'd') };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(runId));
            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "stop").Ok);
            fixture.Store.ShouldFail = record =>
                record.Type == JournalRecordType.RunFinished &&
                string.Equals(
                    (string?)DataOf(record)["state"], "cancelled",
                    StringComparison.Ordinal);

            var processed = fixture.Coordinator.ProcessInactiveCancels();

            Assert.IsTrue(processed.Ok);
            Assert.AreEqual("OW-STORE-001", processed.Diagnostics[0].Code,
                "the refused terminal record's diagnostic is reported");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count,
                "the step consequences were journaled");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the refused terminal record takes the §F1 store-failure path");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)DataOf(finished[0])["summary"]?["code"]);
            var snapshot = fixture.Coordinator.Get(runId)!;
            Assert.AreEqual(RunState.Failed, snapshot.State,
                "the journal's own fold reconstructs Failed");
            Assert.AreEqual(0, snapshot.OpenResources.Count);
            Assert.IsFalse(OsOpenDenied(LockPath), "no exclusion is held");
        }
    }
}
