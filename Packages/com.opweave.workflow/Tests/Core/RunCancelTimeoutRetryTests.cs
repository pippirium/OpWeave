#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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
    /// [Core] tests for SPEC §E6 (deadlines, cancellation, Run resources) and
    /// §E7 (failure routing and retries) — local:T-W1.8-06:timeouts-retries:
    /// an expired Waiting calls IOperation.Cancel exactly once then fails
    /// OW-RUN-004; an Immediate overrun is a warning log only; a journaled
    /// cancel_requested is processed on the next Tick (Waiting →
    /// IOperation.Cancel → step_cancelled → Run Cancelled, Pending → Run
    /// Cancelled, a still-Queued Run never starts); two retryable failures
    /// then a success end Succeeded; Unsafe is never retried; a Reconcilable
    /// Reconcile → Completed is adopted with reconciled:true; Unknown →
    /// NeedsReview; an exhausted retry chain ends OW-RUN-008; a required
    /// resource record the store refuses inside IOperation.Cancel — on the
    /// cancel or the timeout path — takes the SPEC §F1 store-failure path
    /// (Run Failed exactly once, no step_cancelled/timeout record); a
    /// refused step_skipped or run_finished on a still-Queued Run's cancel
    /// takes the same path — the Run is never started, ends Failed once
    /// and no journaled step_skipped is appended again. The coordinator's
    /// own required appends — cancel_requested on a Running or Queued Run,
    /// run_started in the start gate and run_finished on the
    /// fingerprint-mismatch cancel — take the same path: the refusal is
    /// reported, the Run ends Failed exactly once and is never started or
    /// continued on later Ticks. A Running Run marked by a refused
    /// append is finished through the §E6 item 4 Run-end cleanup: its
    /// open resource's release seam runs exactly once and
    /// resource_released is journaled before the single run_finished —
    /// or stays outstanding in the fold when that append is refused too. A
    /// resource_acquired the store refuses never becomes durable, yet the
    /// object the Operation holds still reaches the release seam exactly
    /// once at Run end — no unpaired resource_released is journaled. A
    /// parameterized matrix proves the release model over every
    /// combination of an Operation-side release, a refused resource
    /// append and a Run ending. Fixtures use
    /// dummy operations, a fake clock, an in-memory store and a real
    /// CoordinatorRunLock over a temp root. The same sources run under
    /// dotnet and Unity Mono.
    /// </summary>
    internal sealed class RunCancelTimeoutRetryTests
    {
        private const string Session = "session-e6e7-test";
        private const string WorkflowId = "example.e6e7";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.E6E7." + Guid.NewGuid().ToString("N"));
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

        // ── Fixtures ────────────────────────────────────────────────────────

        private sealed class FakeEditorState : IEditorState
        {
            public bool IsEditMode { get; set; } = true;
            public bool IsCompiling { get; set; }
            public bool IsUpdating { get; set; }
        }

        private sealed class FakeClock : IClock
        {
            private DateTimeOffset _now =
                new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

            public DateTimeOffset UtcNow => _now;

            public void Advance(TimeSpan delta) => _now += delta;
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
            /// Run Store performs. Off by default: most fixtures never reload.
            /// </summary>
            internal bool FoldOnLoad;

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

            internal RunLoop Loop => _loop ??= NewLoop();

            /// <summary>
            /// A fresh RunLoop over the same coordinator — the post-reload
            /// projection: the coordinator's Run tracking survives (as
            /// lock.json does across a domain reload) while every in-memory
            /// step/resource projection is rebuilt from the journal.
            /// </summary>
            internal RunLoop NewLoop() => NewLoopSeam((_, resource) =>
            {
                Released.Add(resource);
                return Result<Unit>.Success(Unit.Value);
            });

            /// <summary>
            /// A loop over a different SPEC §E6 item 4 release seam — a
            /// failing handler, or null for the no-seam wiring defect. The seam's
            /// first argument is the Run whose resources it releases; these tests
            /// assert on the resource, so they discard it.
            /// </summary>
            internal RunLoop NewLoopSeam(
                Func<string, RunResource, Result<Unit>>? releaseResource) =>
                new RunLoop(
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
                    releaseResource: releaseResource);

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

        // ── Dummy operations ────────────────────────────────────────────────

        /// <summary>A Waitable that waits forever until cancelled —
        /// counts its IOperation.Cancel calls.</summary>
        private sealed class CountingWaitableOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.e6e7.waiting", 1)
                .Summary("Waits forever.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build()
                with { ExecutionMode = ExecutionMode.Waitable };

            internal int CancelCalls;
            internal int PollCalls;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(Json("\"t0\""), "waiting", 10);

            public StepResult Poll(StepContext ctx)
            {
                PollCalls++;
                return StepResult.Waiting(Json("\"t" + PollCalls + "\""), "waiting", 10);
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx) => CancelCalls++;
        }

        /// <summary>An Immediate (or Unsafe) that fails with a retryable
        /// diagnostic a fixed number of times, then succeeds — counts
        /// Execute calls.</summary>
        private sealed class FlakyOperation : IOperation
        {
            private readonly int _failures;
            private readonly Diagnostic _error;

            internal FlakyOperation(string id, int failures, Idempotency idempotency)
            {
                _failures = failures;
                _error = ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true);
                Descriptor = Op.Define(id, 1)
                    .Summary("Fails then succeeds.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(idempotency)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            internal int ExecuteCalls;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                if (ExecuteCalls <= _failures)
                {
                    return StepResult.Failed(_error);
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>A Reconcilable Immediate whose scripted Reconcile verdict
        /// answers the §F5 consult after its retryable failure.</summary>
        private sealed class ReconcilableOperation : IOperation
        {
            private readonly Queue<ReconcileResult> _verdicts;
            private readonly Diagnostic _error;
            private readonly bool _failForever;

            internal ReconcilableOperation(
                IEnumerable<ReconcileResult> verdicts, bool failForever = false)
            {
                _verdicts = new Queue<ReconcileResult>(verdicts);
                _failForever = failForever;
                _error = ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true);
                Descriptor = Op.Define("test.e6e7.reconcilable", 1)
                    .Summary("Reconciles after failure.")
                    .Output("out", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Reconcilable)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            internal int ExecuteCalls;
            internal int ReconcileCalls;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    Json("{\"seen\":true}"));

            public StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                if (_failForever || ExecuteCalls == 1)
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

        /// <summary>An Immediate that runs a hook inside Execute — used to
        /// jump the fake clock past the step deadline.</summary>
        private sealed class HookedImmediateOperation : IOperation
        {
            private readonly string _id;

            internal HookedImmediateOperation(string id)
            {
                _id = id;
                Descriptor = Op.Define(id, 1)
                    .Summary("Runs a hook then succeeds.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            internal Action? OnExecute;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                OnExecute?.Invoke();
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>An Immediate that acquires — and optionally releases —
        /// Run resources through the context's IRunResourceRegistry.</summary>
        private sealed class ResourceOperation : IOperation
        {
            private readonly string _id;

            internal ResourceOperation(string id)
            {
                _id = id;
                Descriptor = Op.Define(id, 1)
                    .Summary("Acquires a Run resource.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            internal bool ReleaseInExecute;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                var registry = (IRunResourceRegistry)ctx;
                registry.Acquire("editing_contents", Json("{\"guid\":\"abc\"}"));
                if (ReleaseInExecute)
                {
                    registry.Release("editing_contents", Json("{\"guid\":\"abc\"}"));
                }
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>A Waitable that acquires a Run resource during Execute
        /// then waits — holds the Run mid-flight so a "reload" lands between
        /// the acquisition and the Run's end.</summary>
        private sealed class WaitingResourceOperation : IOperation
        {
            internal WaitingResourceOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Acquires a Run resource then waits.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            internal int PollCalls;
            internal int CancelCalls;

            public StepResult Execute(StepContext ctx)
            {
                ((IRunResourceRegistry)ctx).Acquire(
                    "editing_contents", Json("{\"guid\":\"abc\"}"));
                return StepResult.Waiting(Json("\"held\""), "held", 100);
            }

            public StepResult Poll(StepContext ctx)
            {
                PollCalls++;
                return StepResult.Waiting(Json("\"held\""), "held", 100);
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx) => CancelCalls++;
        }

        /// <summary>An Immediate that acquires a Run resource during Execute
        /// then throws — the refused-acquisition vs Operation-exception
        /// ordering case (SPEC §F1 vs E4 step 6).</summary>
        private sealed class ThrowingResourceOperation : IOperation
        {
            internal ThrowingResourceOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Acquires a Run resource then throws.")
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

            public StepResult Execute(StepContext ctx)
            {
                ((IRunResourceRegistry)ctx).Acquire(
                    "editing_contents", Json("{\"guid\":\"abc\"}"));
                throw new InvalidOperationException(
                    "The Operation failed after acquiring.");
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>A Waitable that holds a Run resource from Execute and
        /// releases it inside Cancel — the refused-append cases for the
        /// cancel/timeout SPEC §F1 paths.</summary>
        private sealed class CancellingResourceOperation : IOperation
        {
            internal CancellingResourceOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Releases its Run resource inside Cancel.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }

            internal int CancelCalls;
            internal bool ThrowAfterRelease;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                ((IRunResourceRegistry)ctx).Acquire(
                    "editing_contents", Json("{\"guid\":\"abc\"}"));
                return StepResult.Waiting(Json("\"held\""), "held", 10);
            }

            public StepResult Poll(StepContext ctx) =>
                StepResult.Waiting(Json("\"held\""), "held", 10);

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
                CancelCalls++;
                ((IRunResourceRegistry)ctx).Release(
                    "editing_contents", Json("{\"guid\":\"abc\"}"));
                if (ThrowAfterRelease)
                {
                    throw new InvalidOperationException("The Operation's Cancel threw.");
                }
            }
        }

        /// <summary>A Waitable that acquires two distinctly-kinded Run
        /// resources during Execute, optionally releases the first itself,
        /// optionally acquires a third kind last — the store-failure
        /// ending's own in-Execute refusal target — then returns the
        /// scripted result: the release-model matrix's resource driver.</summary>
        private sealed class MatrixResourceOperation : IOperation
        {
            internal MatrixResourceOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Acquires two Run resources.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }

            internal bool ReleaseA;
            internal bool AcquireC;
            internal StepResult Result = StepResult.Succeeded();
            internal int CancelCalls;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                var registry = (IRunResourceRegistry)ctx;
                registry.Acquire("kind_a", Json("\"a\""));
                registry.Acquire("kind_b", Json("\"b\""));
                if (ReleaseA)
                {
                    registry.Release("kind_a", Json("\"a\""));
                }
                if (AcquireC)
                {
                    registry.Acquire("kind_c", Json("\"c\""));
                }
                return Result;
            }

            public StepResult Poll(StepContext ctx) =>
                StepResult.Waiting(Json("\"held\""), "held", 60000);

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx) => CancelCalls++;
        }

        // ── Plan/doc builders ───────────────────────────────────────────────

        private static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            IReadOnlyDictionary<string, int>? transitions = null,
            int timeoutSeconds = 30,
            RetryPolicy? retry = null,
            Idempotency idempotency = Idempotency.Idempotent) =>
            new PlanStep(
                index, nodeId, operationId, 1,
                new string(hashSeed, 64), new string((char)(hashSeed + 1), 64),
                new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                transitions ?? new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                timeoutSeconds, retry ?? new RetryPolicy(), idempotency);

        private static CompiledPlan Plan(PlanStep[] steps)
        {
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

        // ── Release-model matrix parameters ─────────────────────────────────

        internal enum MatrixEnding
        {
            Success,
            Failure,
            Cancel,
            StoreFailure,
            StoreFailureAfterExecute,
        }

        internal enum MatrixRefusal
        {
            None,
            AcquireA,
            AcquireB,
            ReleaseA,
            ReleaseB,
        }

        private static bool RefusalMatches(MatrixRefusal refusal, JournalRecord record)
        {
            if (refusal == MatrixRefusal.None)
            {
                return false;
            }
            var kind = (string?)((JObject)record.Data.ToToken())["kind"];
            switch (refusal)
            {
                case MatrixRefusal.AcquireA:
                    return record.Type == JournalRecordType.ResourceAcquired &&
                        kind == "kind_a";
                case MatrixRefusal.AcquireB:
                    return record.Type == JournalRecordType.ResourceAcquired &&
                        kind == "kind_b";
                case MatrixRefusal.ReleaseA:
                    return record.Type == JournalRecordType.ResourceReleased &&
                        kind == "kind_a";
                case MatrixRefusal.ReleaseB:
                    return record.Type == JournalRecordType.ResourceReleased &&
                        kind == "kind_b";
                default:
                    return false;
            }
        }

        // The StoreFailure ending's own refusal: the matrix Operation's
        // third acquire — a distinct required record Execute causes while
        // it is still running, independent of the resource-append axis.
        private static bool IsStoreFailureAcquire(JournalRecord record) =>
            record.Type == JournalRecordType.ResourceAcquired &&
            (string?)((JObject)record.Data.ToToken())["kind"] == "kind_c";

        private static int CountKind(IReadOnlyList<RunResource> resources, string kind)
        {
            var count = 0;
            for (var i = 0; i < resources.Count; i++)
            {
                if (resources[i].Kind == kind)
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsTerminal(RunState? state) =>
            state == RunState.Succeeded || state == RunState.Failed ||
            state == RunState.Cancelled;

        // ── SPEC §E6 test 1: an expired Waiting cancels once then OW-RUN-004 ─

        [Test]
        public void ExpiredWaitingCallsCancelOnceThenFailsOwRun004()
        {
            var fixture = NewFixture();
            var waitable = new CountingWaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.waiting", 'd', timeoutSeconds: 10,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(1, RecordsOfType(fixture, JournalRecordType.StepWaiting).Count);

            fixture.Clock.Advance(TimeSpan.FromSeconds(11));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(1, waitable.CancelCalls, "Cancel is called exactly once on expiry");
            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.AreEqual("OW-RUN-004", (string?)DataOf(failed[0])["error"]);
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-RUN-004", (string?)DataOf(finished[0])["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(1, waitable.CancelCalls, "Cancel is never called twice");
            Assert.AreEqual(0, waitable.PollCalls, "an expired step is never polled");
        }

        // ── SPEC §E6 item 1: an Immediate overrun is a warning log only ──────

        [Test]
        public void ImmediateOverrunEmitsWarningOnly()
        {
            var fixture = NewFixture();
            var slow = new HookedImmediateOperation("test.e6e7.slow");
            fixture.Register(slow, 'd');
            slow.OnExecute = () => fixture.Clock.Advance(TimeSpan.FromSeconds(60));
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.slow", 'd', timeoutSeconds: 5),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var logs = RecordsOfType(fixture, JournalRecordType.Log);
            Assert.AreEqual(1, logs.Count);
            var data = DataOf(logs[0]);
            Assert.AreEqual("warning", (string?)data["level"]);
            Assert.AreEqual("OW-RUN-004", (string?)data["code"]);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepSucceeded).Count,
                "the overran Immediate still succeeds — never interrupted");
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E6 item 2: cancel of a Waiting step calls IOperation.Cancel ─

        [Test]
        public void CancelRequestOnWaitingStepCallsCancelThenCancelsRun()
        {
            var fixture = NewFixture();
            var waitable = new CountingWaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.waiting", 'd', timeoutSeconds: 600,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            var cancelled = fixture.Coordinator.Cancel(runId, "stop requested");
            Assert.IsTrue(cancelled.Ok);
            Assert.AreEqual(
                RunState.Running, fixture.Coordinator.StateOf(runId),
                "the request is journaled first — processing is the next Tick");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.CancelRequested).Count);

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(1, waitable.CancelCalls, "the Waiting step is cancelled once");
            var stepCancelled = RecordsOfType(fixture, JournalRecordType.StepCancelled);
            Assert.AreEqual(1, stepCancelled.Count);
            Assert.AreEqual("stop requested", (string?)DataOf(stepCancelled[0])["reason"]);
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual("cancelled", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-RUN-005", (string?)DataOf(finished[0])["summary"]?["code"]);
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E6 item 2: a cancel before start ends the Queued Run ──────

        [Test]
        public void CancelRequestOnQueuedRunCancelsWithoutSteps()
        {
            var fixture = NewFixture();
            var waitable = new CountingWaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.waiting", 'd'),
                Step(1, "n2", "test.e6e7.waiting", 'd',
                    transitions: new Dictionary<string, int> { ["next"] = -1 }),
            };
            steps[0] = Step(0, "n1", "test.e6e7.waiting", 'd',
                transitions: new Dictionary<string, int> { ["next"] = 1 });
            var runId = AcceptRun(fixture, Plan(steps));

            var cancelled = fixture.Coordinator.Cancel(runId, "not needed");
            Assert.IsTrue(cancelled.Ok);
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(runId));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepStarted).Count,
                "a cancelled Queued Run never starts a step");
            Assert.AreEqual(
                2, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count,
                "every Pending step is Skipped at Run end");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual("cancelled", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-RUN-005", (string?)DataOf(finished[0])["summary"]?["code"]);
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E6 item 2: cancel on a Pending step ends the Run ──────────

        [Test]
        public void CancelRequestOnPendingStepCancelsRun()
        {
            var fixture = NewFixture();
            var waitable = new CountingWaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.waiting", 'd', timeoutSeconds: 600,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            // Step goes Waiting on the first tick; the cancel lands while the
            // current step is journaled Waiting — the Pending-shape variant
            // (Running run, Pending current step) uses the same end-of-Run
            // rule and is covered by the Queued case above.
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepWaiting).Count);
            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "halt").Ok);
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                0, waitable.PollCalls, "no poll runs after the cancel");
        }

        // ── SPEC §E7: two retryable failures then success → Succeeded ────────

        [Test]
        public void TwoFailuresThenSuccessSucceedsAfterBackoff()
        {
            var fixture = NewFixture();
            var flaky = new FlakyOperation("test.e6e7.flaky", failures: 2, Idempotency.Idempotent);
            fixture.Register(flaky, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500)),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            // Tick 1: attempt 1 fails; StepFailed{attempt:1} is journaled and
            // the retry is scheduled 500 ms out — never inside the same tick.
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(1, flaky.ExecuteCalls);
            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.AreEqual(1, failed[0].Attempt);
            Assert.AreEqual("OW-UNITY-008", (string?)DataOf(failed[0])["error"]);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            // Inside the backoff nothing happens.
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(200));
            var recordCount = fixture.Store.Appended.Count;
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(recordCount, fixture.Store.Appended.Count);

            // Past 500 ms: attempt 2 fails, retry scheduled at 1000 ms.
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(400));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(2, flaky.ExecuteCalls);
            failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(2, failed.Count);
            Assert.AreEqual(2, failed[1].Attempt);

            // Inside the second backoff (1000 ms): still nothing.
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(600));
            recordCount = fixture.Store.Appended.Count;
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(recordCount, fixture.Store.Appended.Count);

            // Past it: attempt 3 succeeds.
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(500));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(3, flaky.ExecuteCalls);
            var started = RecordsOfType(fixture, JournalRecordType.StepStarted);
            Assert.AreEqual(3, started.Count);
            Assert.AreEqual(3, started[2].Attempt);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepSucceeded).Count);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E7: the retry limit ends OW-RUN-008 ───────────────────────

        [Test]
        public void ExhaustedRetriesEndOwRun008()
        {
            var fixture = NewFixture();
            var flaky = new FlakyOperation(
                "test.e6e7.flaky", failures: 99, Idempotency.Idempotent);
            fixture.Register(flaky, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(MaxAttempts: 2, InitialBackoffMs: 500)),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(600));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(2, failed.Count);
            Assert.AreEqual(
                "OW-RUN-008", (string?)DataOf(failed[1])["error"],
                "the exhausted retry chain ends OW-RUN-008");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-RUN-008", (string?)DataOf(finished[0])["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(2, flaky.ExecuteCalls);
        }

        // ── SPEC §E7 test 1: an Unsafe step is never retried ────────────────

        [Test]
        public void UnsafeStepIsNeverRetried()
        {
            var fixture = NewFixture();
            var unsafeOp = new FlakyOperation(
                "test.e6e7.unsafe", failures: 5, Idempotency.Unsafe);
            fixture.Register(unsafeOp, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.unsafe", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Unsafe),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(1, unsafeOp.ExecuteCalls, "Unsafe never re-executes");
            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.AreEqual(
                "OW-UNITY-008", (string?)DataOf(failed[0])["error"],
                "the step's own error stands — no retry happened");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E7 test 2: Reconcile Completed is adopted, never retried ──

        [Test]
        public void ReconcilableCompletedAdoptsOutputs()
        {
            var fixture = NewFixture();
            var op = new ReconcilableOperation(new[]
            {
                (ReconcileResult)new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue> { ["out"] = Json("\"restored\"") }),
            });
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.reconcilable", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(1, op.ExecuteCalls, "a Completed step is never re-executed");
            Assert.AreEqual(1, op.ReconcileCalls);
            var succeeded = RecordsOfType(fixture, JournalRecordType.StepSucceeded);
            Assert.AreEqual(1, succeeded.Count);
            var data = DataOf(succeeded[0]);
            Assert.AreEqual(true, (bool?)data["reconciled"]);
            Assert.AreEqual("restored", (string?)data["outputs"]?["out"],
                "the proven outputs are adopted");
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E7 test 3: Reconcile Unknown → NeedsReview ────────────────

        [Test]
        public void ReconcilableUnknownGoesNeedsReview()
        {
            var fixture = NewFixture();
            var op = new ReconcilableOperation(new[]
            {
                (ReconcileResult)new ReconcileResult.Unknown("State cannot be proven."),
            });
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.reconcilable", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(1, op.ExecuteCalls, "Unknown is never re-executed");
            var review = RecordsOfType(fixture, JournalRecordType.StepNeedsReview);
            Assert.AreEqual(1, review.Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "NeedsReview is not a terminal record");
            Assert.AreEqual(RunState.NeedsReview, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E7: Reconcile NotApplied retries the step ─────────────────

        [Test]
        public void ReconcilableNotAppliedRetries()
        {
            var fixture = NewFixture();
            var op = new ReconcilableOperation(new[]
            {
                (ReconcileResult)new ReconcileResult.NotApplied(),
            });
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.reconcilable", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(1, op.ReconcileCalls);

            fixture.Clock.Advance(TimeSpan.FromMilliseconds(600));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(2, op.ExecuteCalls);
            var succeeded = RecordsOfType(fixture, JournalRecordType.StepSucceeded);
            Assert.AreEqual(1, succeeded.Count);
            Assert.AreEqual(false, (bool?)DataOf(succeeded[0])["reconciled"]);
            Assert.AreEqual("fresh", (string?)DataOf(succeeded[0])["outputs"]?["out"]);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E6 item 4: Run resources release at Run end ────────────────

        [Test]
        public void RunResourceIsJournaledAndReleasedAtRunEnd()
        {
            var fixture = NewFixture();
            var resourceful = new ResourceOperation("test.e6e7.resourceful");
            fixture.Register(resourceful, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.resourceful", 'd'),
            };
            AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var acquired = RecordsOfType(fixture, JournalRecordType.ResourceAcquired);
            Assert.AreEqual(1, acquired.Count);
            Assert.IsTrue(acquired[0].Required, "resource_acquired is required:true");
            Assert.AreEqual("editing_contents", (string?)DataOf(acquired[0])["kind"]);
            var released = RecordsOfType(fixture, JournalRecordType.ResourceReleased);
            Assert.AreEqual(1, released.Count);
            Assert.AreEqual("editing_contents", (string?)DataOf(released[0])["kind"]);
            Assert.AreEqual(1, fixture.Released.Count, "the release seam ran at Run end");
            Assert.AreEqual("editing_contents", fixture.Released[0].Kind);
            // The release precedes the terminal record.
            var acquireIndex = fixture.Store.Appended.IndexOf(acquired[0]);
            var releaseIndex = fixture.Store.Appended.IndexOf(released[0]);
            var finishIndex = fixture.Store.Appended.IndexOf(
                RecordsOfType(fixture, JournalRecordType.RunFinished)[0]);
            Assert.Less(acquireIndex, releaseIndex);
            Assert.Less(releaseIndex, finishIndex);
        }

        // ── SPEC §E6 item 4: an op-released resource stays released ──────────

        [Test]
        public void OperationReleasedResourceNeedsNoRunEndRelease()
        {
            var fixture = NewFixture();
            var resourceful = new ResourceOperation("test.e6e7.resourceful")
            {
                ReleaseInExecute = true,
            };
            fixture.Register(resourceful, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.resourceful", 'd'),
            };
            AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ResourceAcquired).Count);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ResourceReleased).Count);
            Assert.AreEqual(
                0, fixture.Released.Count,
                "an already-released resource is not released again at Run end");
        }

        // ── SPEC §E7: an unrecognized Idempotency is never retried ─────────

        [Test]
        public void UnrecognizedIdempotencyIsNeverRetried()
        {
            var fixture = NewFixture();
            var flaky = new FlakyOperation(
                "test.e6e7.flaky", failures: 5, Idempotency.Idempotent);
            fixture.Register(flaky, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: (Idempotency)99),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                1, flaky.ExecuteCalls,
                "a Plan Idempotency outside the declared enum never re-executes");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepNeedsReview).Count,
                "the §F5 consult ends the step NeedsReview, not in a retry");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepFailed).Count);
            Assert.AreEqual(RunState.NeedsReview, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E0: a retry-pending StepFailed folds to Running ──────────

        [Test]
        public void RetryPendingStepFailedFoldsToRunningWithPendingAttempt()
        {
            var fixture = NewFixture();
            var flaky = new FlakyOperation(
                "test.e6e7.flaky", failures: 1, Idempotency.Idempotent);
            fixture.Register(flaky, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500)),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            // The attempt's failure is journaled with the decided retry —
            // the durable form of "Running, attempt pending" (SPEC §E0).
            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            var retry = (JObject?)DataOf(failed[0])["retry"];
            Assert.IsNotNull(retry, "a decided retry is recorded on the step_failed data");
            Assert.AreEqual(2, (int?)retry!["nextAttempt"]);
            Assert.IsNotNull((string?)retry["notBefore"]);

            // A reload during the backoff folds the step to Running with the
            // pending attempt — never terminal Failed.
            var fold = JournalFold.Empty();
            foreach (var record in fixture.Store.Appended)
            {
                Assert.IsTrue(fold.Apply(record));
            }
            var step = fold.Steps()[0];
            Assert.AreEqual(StepState.Running, step.State);
            Assert.AreEqual(2, step.Attempt, "the pending attempt survives the fold");

            // Resumption: the pending attempt's records keep folding — the
            // run drives to Succeeded exactly like the uninterrupted one.
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(600));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            var replay = JournalFold.Empty();
            foreach (var record in fixture.Store.Appended)
            {
                Assert.IsTrue(replay.Apply(record));
            }
            Assert.AreEqual(StepState.Succeeded, replay.Steps()[0].State);
            Assert.AreEqual(RunState.Succeeded, replay.State);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E0: a final StepFailed keeps folding to Failed ───────────

        [Test]
        public void TerminalStepFailedFoldsToFailed()
        {
            var fixture = NewFixture();
            var flaky = new FlakyOperation(
                "test.e6e7.flaky", failures: 9, Idempotency.Idempotent);
            fixture.Register(flaky, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(MaxAttempts: 1, InitialBackoffMs: 500)),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.IsNull(
                DataOf(failed[0])["retry"],
                "a decided-not-to-retry failure records no retry member");

            var fold = JournalFold.Empty();
            foreach (var record in fixture.Store.Appended)
            {
                Assert.IsTrue(fold.Apply(record));
            }
            Assert.AreEqual(StepState.Failed, fold.Steps()[0].State);
            Assert.AreEqual(RunState.Failed, fold.State);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E6 item 4: a reload between acquire and end releases once ─

        [Test]
        public void ReloadBetweenAcquireAndRunEndReleasesExactlyOnce()
        {
            var fixture = NewFixture();
            fixture.Store.FoldOnLoad = true;
            var resourceful = new WaitingResourceOperation("test.e6e7.waitingres");
            fixture.Register(resourceful, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.waitingres", 'd', timeoutSeconds: 600),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ResourceAcquired).Count);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            // The "reload": a fresh RunLoop over the same coordinator — the
            // coordinator's Run tracking survives (as lock.json would across
            // a domain reload) while the in-memory projection is rebuilt
            // from the journal fold, open acquisitions included.
            var reloaded = fixture.NewLoop();
            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "halt").Ok);
            Assert.IsTrue(reloaded.Tick().Ok);

            Assert.AreEqual(
                1, fixture.Released.Count,
                "the journal-rebuilt acquisition is released exactly once");
            Assert.AreEqual("editing_contents", fixture.Released[0].Kind);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ResourceReleased).Count);
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E6 item 2: a cancel lands mid-backoff too ──────────────────

        [Test]
        public void CancelDuringRetryBackoffCancelsStepAndRun()
        {
            var fixture = NewFixture();
            var flaky = new FlakyOperation(
                "test.e6e7.flaky", failures: 99, Idempotency.Idempotent);
            fixture.Register(flaky, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500)),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(1, flaky.ExecuteCalls);
            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "halt").Ok);
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var stepCancelled = RecordsOfType(fixture, JournalRecordType.StepCancelled);
            Assert.AreEqual(1, stepCancelled.Count, "the retry-pending step ends Cancelled");
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                1, flaky.ExecuteCalls, "the scheduled retry never ran");
        }

        // ── SPEC §F1: a refused step_cancelled append ends the Run Failed
        // exactly once — the cancellation processing stops on the
        // store-failure path and no second terminal record is attempted ────

        [Test]
        public void RefusedStepCancelledAppendEndsFailedOnceWithoutThrowing()
        {
            var fixture = NewFixture();
            var waitable = new CountingWaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.waiting", 'd', timeoutSeconds: 600,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepWaiting).Count);
            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "halt").Ok);
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.StepCancelled;

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(
                1, waitable.CancelCalls, "the Waiting step is still cancelled once");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepCancelled).Count,
                "the refused step_cancelled never became durable");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual(
                "failed", (string?)data["state"],
                "the store-failure path ends Failed, never a second Cancelled");
            Assert.AreEqual("OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            var recordCount = fixture.Store.Appended.Count;
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count,
                "a later Tick is a no-op — no second terminal record is attempted");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count);
        }

        // ── SPEC §F1: a refused step_skipped on a still-Queued Run's
        // cancel takes the store-failure path — the Run is never started,
        // no Operation is invoked and it ends Failed exactly once ────────

        [Test]
        public void RefusedStepSkippedOnQueuedCancelEndsFailedWithoutStarting()
        {
            var fixture = NewFixture();
            var op = new FlakyOperation("test.e6e7.flaky", failures: 0, Idempotency.Idempotent);
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.e6e7.flaky", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "not needed").Ok);
            Assert.AreEqual(RunState.Queued, fixture.Coordinator.StateOf(runId));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.StepSkipped;

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunStarted).Count,
                "the cancelled Queued Run is never started");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepStarted).Count);
            Assert.AreEqual(0, op.ExecuteCalls, "no Operation is ever invoked");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count,
                "the refused step_skipped never became durable");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual(
                "failed", (string?)data["state"],
                "the store-failure path ends Failed, never Cancelled");
            Assert.AreEqual("OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            var recordCount = fixture.Store.Appended.Count;
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count,
                "a later Tick is a no-op — the Run is never retried or started");
            Assert.AreEqual(0, op.ExecuteCalls);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count);
        }

        // ── SPEC §F1: a refusal on the SECOND step_skipped of a Queued
        // Run's cancel takes the same path — the first step's journaled
        // skip is never appended a second time on later Ticks ───────────

        [Test]
        public void RefusedSecondStepSkippedOnQueuedCancelNeverRepeatsFirst()
        {
            var fixture = NewFixture();
            var op = new FlakyOperation("test.e6e7.flaky", failures: 0, Idempotency.Idempotent);
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.e6e7.flaky", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "not needed").Ok);
            fixture.Store.ShouldFail = record =>
                record.Type == JournalRecordType.StepSkipped && record.Step == 1;

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            var skipped = RecordsOfType(fixture, JournalRecordType.StepSkipped);
            Assert.AreEqual(
                1, skipped.Count, "only the first step's skip reached the journal");
            Assert.AreEqual(0, skipped[0].Step);
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            // Even after the store recovers the Run is never retried —
            // no duplicate step_skipped and never a start.
            fixture.Store.ShouldFail = null;
            var recordCount = fixture.Store.Appended.Count;
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count,
                "an already-written step_skipped is never appended again");
            Assert.AreEqual(0, op.ExecuteCalls);
        }

        // ── SPEC §F1: a refused run_finished on a still-Queued Run's
        // cancel still ends the Run — the journaled skips are never
        // rewritten and no second terminal record is attempted ───────────

        [Test]
        public void RefusedRunFinishedOnQueuedCancelNeverRepeatsSkips()
        {
            var fixture = NewFixture();
            var op = new FlakyOperation("test.e6e7.flaky", failures: 0, Idempotency.Idempotent);
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.e6e7.flaky", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "not needed").Ok);
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.RunFinished;

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(
                2, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count,
                "every Pending step was Skipped before the terminal append");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "the refused run_finished never became durable");
            Assert.AreEqual(
                RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the Run never spins in Queued — in-memory Failed ends processing");

            // Even after the store recovers no record is appended again:
            // no duplicate step_skipped, no second terminal record.
            fixture.Store.ShouldFail = null;
            var recordCount = fixture.Store.Appended.Count;
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(0, op.ExecuteCalls);
        }

        // ── SPEC §F1: a refused cancel_requested on a Running Run takes
        // the store-failure path — the refusal is reported, the Run ends
        // Failed exactly once, the pending cancel is never processed and
        // no further step work happens on later Ticks ──────────────────

        [Test]
        public void RefusedCancelRequestedOnRunningRunEndsFailedOnce()
        {
            var fixture = NewFixture();
            var waitable = new CountingWaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.waiting", 'd', timeoutSeconds: 600,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepWaiting).Count);
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.CancelRequested;

            var refused = fixture.Coordinator.Cancel(runId, "halt");

            Assert.IsFalse(refused.Ok, "the refusal is reported to the caller");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.CancelRequested).Count,
                "the refused cancel_requested never became durable");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "the terminal record follows the Run-end cleanup on the next Tick");
            Assert.AreEqual(
                RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the store-failure end is already decided in memory");

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            // Later Ticks never process a pending cancel and never continue
            // the Run — no poll, no IOperation.Cancel, no second terminal
            // record.
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok);
            }
            Assert.AreEqual(0, waitable.CancelCalls);
            Assert.AreEqual(0, waitable.PollCalls);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepCancelled).Count);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count);

            // The still-held exclusion is released through the
            // terminal-pending-release seam on a later Tick — a fresh Run
            // can take it and start.
            var second = fixture.Coordinator.Accept(
                Plan(steps), Doc(), ActorKind.Agent, new AcceptOptions(null, false));
            Assert.IsTrue(second.Ok, "the released exclusion admits a new Run");
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                RunState.Running, fixture.Coordinator.StateOf(second.Value!.RunId));
            Assert.AreEqual(0, waitable.CancelCalls);
        }

        // ── SPEC §F1/§E6 item 4: a refused cancel_requested on a Running
        // Run holding an open Run resource is finished through the Run-end
        // cleanup — the release seam runs exactly once and
        // resource_released is journaled BEFORE the single terminal
        // run_finished{failed}, never after it ─────────────────────────

        [Test]
        public void RefusedCancelRequestedOnRunningRunReleasesOpenResource()
        {
            var fixture = NewFixture();
            var resourceful = new WaitingResourceOperation("test.e6e7.waitingres");
            fixture.Register(resourceful, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.waitingres", 'd', timeoutSeconds: 600),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ResourceAcquired).Count,
                "the running step holds an open Run resource");
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.CancelRequested;

            var refused = fixture.Coordinator.Cancel(runId, "halt");

            Assert.IsFalse(refused.Ok, "the refusal is reported to the caller");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.CancelRequested).Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "the terminal record never precedes the resource release");
            Assert.AreEqual(
                RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the store-failure end is already decided in memory");

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(
                1, fixture.Released.Count,
                "the open resource's release seam is called exactly once");
            Assert.AreEqual("editing_contents", fixture.Released[0].Kind);
            var releasedIndex = fixture.Store.Appended.FindIndex(
                record => record.Type == JournalRecordType.ResourceReleased);
            var finishedIndex = fixture.Store.Appended.FindIndex(
                record => record.Type == JournalRecordType.RunFinished);
            Assert.IsTrue(
                releasedIndex >= 0 && releasedIndex < finishedIndex,
                "resource_released is journaled before run_finished");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            // Later Ticks never continue the Run — no poll, no
            // IOperation.Cancel, no repeated release and no second
            // terminal record.
            var recordCount = fixture.Store.Appended.Count;
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok);
            }
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count,
                "a later Tick is a no-op — no further record is attempted");
            Assert.AreEqual(0, resourceful.PollCalls);
            Assert.AreEqual(0, resourceful.CancelCalls);
            Assert.AreEqual(
                1, fixture.Released.Count, "the release is never repeated");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count);
        }

        // ── SPEC §F1/§E6 item 4: when the resource_released append of the
        // deferred cleanup is refused too, the release seam still ran once
        // and the resource stays outstanding in the fold — durable with
        // the diagnostic for the §F4 recovery engine ───────────────────

        [Test]
        public void RefusedCancelRequestedWithRefusedReleaseKeepsResourceOutstanding()
        {
            var fixture = NewFixture();
            var resourceful = new WaitingResourceOperation("test.e6e7.waitingres");
            fixture.Register(resourceful, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.waitingres", 'd', timeoutSeconds: 600),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ResourceAcquired).Count);
            fixture.Store.ShouldFail = record =>
                record.Type == JournalRecordType.CancelRequested ||
                record.Type == JournalRecordType.ResourceReleased;

            var refused = fixture.Coordinator.Cancel(runId, "halt");

            Assert.IsFalse(refused.Ok, "the refusal is reported to the caller");

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            var sawStoreError = false;
            foreach (var diagnostic in tick.Diagnostics)
            {
                sawStoreError |= diagnostic.Code == ErrorCodes.OW_STORE_001;
            }
            Assert.IsTrue(
                sawStoreError,
                "the refused resource_released diagnostic rides the Tick");
            Assert.AreEqual(
                1, fixture.Released.Count,
                "the release seam still ran exactly once — only the record was refused");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ResourceReleased).Count,
                "the refused resource_released never became durable");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            // The unreleased resource stays outstanding in the reload
            // fold — the §F4 recovery engine can release it.
            var fold = JournalFold.Empty();
            foreach (var record in fixture.Store.Appended)
            {
                Assert.IsTrue(fold.Apply(record));
            }
            Assert.AreEqual(
                1, fold.OpenResources.Count,
                "the unreleased resource stays durable in the fold");
            Assert.AreEqual("editing_contents", fold.OpenResources[0].Kind);

            // Later Ticks never continue the Run — no poll, no
            // IOperation.Cancel, no repeated release and no second
            // terminal record.
            fixture.Store.ShouldFail = null;
            var recordCount = fixture.Store.Appended.Count;
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok);
            }
            Assert.AreEqual(recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(0, resourceful.PollCalls);
            Assert.AreEqual(0, resourceful.CancelCalls);
            Assert.AreEqual(1, fixture.Released.Count);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count);
        }

        // ── SPEC §F1: a refused cancel_requested on a still-Queued Run
        // takes the store-failure path — the Run ends Failed exactly once,
        // never reaches the start gate and no Operation is invoked ──────

        [Test]
        public void RefusedCancelRequestedOnQueuedRunEndsFailedWithoutStarting()
        {
            var fixture = NewFixture();
            var op = new FlakyOperation("test.e6e7.flaky", failures: 0, Idempotency.Idempotent);
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.CancelRequested;

            var refused = fixture.Coordinator.Cancel(runId, "not needed");

            Assert.IsFalse(refused.Ok, "the refusal is reported to the caller");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            // Even after the store recovers the Run is never started or
            // retried.
            fixture.Store.ShouldFail = null;
            var recordCount = fixture.Store.Appended.Count;
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok);
            }
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunStarted).Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepStarted).Count);
            Assert.AreEqual(0, op.ExecuteCalls, "no Operation is ever invoked");
        }

        // ── SPEC §F1: a refused run_started in the start gate takes the
        // store-failure path — the Run ends Failed exactly once, is never
        // retried on a later Tick and no Operation is invoked ───────────

        [Test]
        public void RefusedRunStartedInTryStartNextEndsFailedWithoutStarting()
        {
            var fixture = NewFixture();
            var op = new FlakyOperation("test.e6e7.flaky", failures: 0, Idempotency.Idempotent);
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.RunStarted;

            Result<Unit> tick = default;
            Assert.DoesNotThrow(
                () => tick = fixture.Loop.Tick(), "no exception escapes the Tick");

            Assert.IsFalse(
                tick.Ok, "the refused append is reported as a Result failure");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunStarted).Count,
                "the refused run_started never became durable");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            // Even after the store recovers the Run is never retried.
            fixture.Store.ShouldFail = null;
            var recordCount = fixture.Store.Appended.Count;
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok);
            }
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunStarted).Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepStarted).Count);
            Assert.AreEqual(0, op.ExecuteCalls, "no Operation is ever invoked");
        }

        // ── SPEC §F1: a refused run_finished on the fingerprint-mismatch
        // cancel still ends the Run — marked Failed in memory, never
        // started, and no terminal record is attempted again ────────────

        [Test]
        public void RefusedRunFinishedOnFingerprintMismatchEndsFailedInMemory()
        {
            // The used Operation is absent from the current Registry — a
            // fingerprint mismatch — but still resolvable as an instance.
            var fixture = NewFixture();
            var op = new FlakyOperation("test.e6e7.flaky", failures: 0, Idempotency.Idempotent);
            fixture.Ops[op.Descriptor.Id] = op;
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.RunFinished;

            Result<Unit> tick = default;
            Assert.DoesNotThrow(
                () => tick = fixture.Loop.Tick(), "no exception escapes the Tick");

            Assert.IsFalse(
                tick.Ok, "the refused append is reported as a Result failure");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "neither terminal record became durable");
            Assert.AreEqual(
                RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the Run never spins in Queued — in-memory Failed ends processing");

            // Even after the store recovers no record is appended again and
            // the Run is never started.
            fixture.Store.ShouldFail = null;
            var recordCount = fixture.Store.Appended.Count;
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok);
            }
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunStarted).Count);
            Assert.AreEqual(0, op.ExecuteCalls, "no Operation is ever invoked");
        }

        // ── SPEC §F1: when only the cancelled terminal record is refused
        // on the fingerprint-mismatch cancel, the store-failure path's own
        // run_finished{failed} is journaled exactly once ────────────────

        [Test]
        public void RefusedCancelledEndOnFingerprintMismatchJournalsFailedOnce()
        {
            var fixture = NewFixture();
            var op = new FlakyOperation("test.e6e7.flaky", failures: 0, Idempotency.Idempotent);
            fixture.Ops[op.Descriptor.Id] = op;
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail = record =>
                record.Type == JournalRecordType.RunFinished &&
                (string?)((JObject)record.Data.ToToken())["state"] == "cancelled";

            Result<Unit> tick = default;
            Assert.DoesNotThrow(
                () => tick = fixture.Loop.Tick(), "no exception escapes the Tick");

            Assert.IsFalse(
                tick.Ok, "the refused append is reported as a Result failure");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual(
                "failed", (string?)data["state"],
                "the store-failure path ends Failed, never Cancelled");
            Assert.AreEqual("OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            fixture.Store.ShouldFail = null;
            var recordCount = fixture.Store.Appended.Count;
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok);
            }
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(0, op.ExecuteCalls, "no Operation is ever invoked");
        }

        // ── SPEC §F1: a refused resource_released inside Cancel takes the
        // store-failure path — no step_cancelled, no cancelled terminal ────

        [Test]
        public void RefusedReleaseInsideCancelEndsFailedWithoutStepCancelled()
        {
            var fixture = NewFixture();
            var op = new CancellingResourceOperation("test.e6e7.cancelres");
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.cancelres", 'd', timeoutSeconds: 600,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepWaiting).Count);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.ResourceAcquired).Count,
                "the Execute-time acquisition is durable and open");
            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "halt").Ok);
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.ResourceReleased;

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(1, op.CancelCalls, "the Waiting step is still cancelled once");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepCancelled).Count,
                "no step_cancelled follows a refused required append");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ResourceReleased).Count,
                "the refused resource_released never became durable");
            Assert.AreEqual(
                0, fixture.Released.Count,
                "the Operation already released the resource — the release " +
                "seam is never called a second time");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual(
                "failed", (string?)data["state"],
                "the store-failure path ends Failed, never Cancelled");
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"],
                "the store diagnostic is the Run's error, not OW-RUN-005");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            // The op-released-but-unrecorded entry stays Acquired —
            // outstanding in the fold for the §F4 recovery engine.
            var fold = JournalFold.Empty();
            foreach (var record in fixture.Store.Appended)
            {
                Assert.IsTrue(fold.Apply(record));
            }
            Assert.AreEqual(
                1, fold.OpenResources.Count,
                "the unrecorded release stays outstanding in the fold");
            Assert.AreEqual("editing_contents", fold.OpenResources[0].Kind);
        }

        // ── SPEC §F1: the store failure wins over a Cancel that throws
        // after the refused append — the exception never escapes the Tick ──

        [Test]
        public void RefusedReleaseInsideThrowingCancelStillEndsFailed()
        {
            var fixture = NewFixture();
            var op = new CancellingResourceOperation("test.e6e7.cancelres")
            {
                ThrowAfterRelease = true,
            };
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.cancelres", 'd', timeoutSeconds: 600,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.IsTrue(fixture.Coordinator.Cancel(runId, "halt").Ok);
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.ResourceReleased;

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(1, op.CancelCalls);
            var sawOperationThrew = false;
            foreach (var diagnostic in tick.Diagnostics)
            {
                sawOperationThrew |= diagnostic.Code == ErrorCodes.OW_RUN_006;
            }
            Assert.IsFalse(
                sawOperationThrew,
                "the store failure wins over the Operation's exception — no OW-RUN-006");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepCancelled).Count);
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"],
                "the store diagnostic is the Run's error, never OW-RUN-006 or OW-RUN-005");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §F1: a refused resource_released inside the timeout's
        // Cancel takes the store-failure path — the OW-RUN-004 timeout is
        // never journaled ────────────────────────────────────────────────

        [Test]
        public void RefusedReleaseInsideExpiredCancelEndsFailedWithoutTimeout()
        {
            var fixture = NewFixture();
            var op = new CancellingResourceOperation("test.e6e7.cancelres");
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.cancelres", 'd', timeoutSeconds: 10,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepWaiting).Count);
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.ResourceReleased;

            fixture.Clock.Advance(TimeSpan.FromSeconds(11));
            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(1, op.CancelCalls, "Cancel is still called once on expiry");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepFailed).Count,
                "no OW-RUN-004 timeout record follows a refused required append");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"],
                "the store diagnostic is the Run's error, not the OW-RUN-004 timeout");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §F1: a refused resource_acquired inside a throwing Execute
        // still takes the store-failure path — the acquisition never became
        // durable, so the Operation's exception is not reported OW-RUN-006 ──

        [Test]
        public void RefusedResourceAcquireWinsOverOperationException()
        {
            var fixture = NewFixture();
            var op = new ThrowingResourceOperation("test.e6e7.throwingres");
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.throwingres", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.ResourceAcquired;

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ResourceAcquired).Count,
                "the refused resource_acquired never became durable");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepFailed).Count,
                "the store-failure path attempts no further step records (SPEC §F1)");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"],
                "the store diagnostic is the Run's error, not the Operation's OW-RUN-006");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §F1/§E6 item 4: a refused resource_acquired never became
        // durable, yet the Operation still holds the object — the
        // store-failure Run-end cleanup releases it through the seam
        // exactly once and journals no unpaired resource_released for it ──

        [Test]
        public void RefusedResourceAcquireStillReleasesHeldResourceAtRunEnd()
        {
            var fixture = NewFixture();
            var resourceful = new ResourceOperation("test.e6e7.resourceful");
            fixture.Register(resourceful, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.resourceful", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.ResourceAcquired;

            var tick = fixture.Loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ResourceAcquired).Count,
                "the refused resource_acquired never became durable");
            Assert.AreEqual(
                1, fixture.Released.Count,
                "the still-held resource's release seam ran exactly once at Run end");
            Assert.AreEqual("editing_contents", fixture.Released[0].Kind);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ResourceReleased).Count,
                "no unpaired resource_released is journaled for an unjournaled acquisition");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"],
                "the store diagnostic is the Run's error");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            // Later Ticks are no-ops: no further record is attempted and
            // the release is never repeated.
            fixture.Store.ShouldFail = null;
            var recordCount = fixture.Store.Appended.Count;
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok);
            }
            Assert.AreEqual(recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(
                1, fixture.Released.Count, "the release is never repeated");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count);
        }

        // ── SPEC §F1/§E6 item 4: when the release seam fails for a held
        // resource whose resource_acquired was refused, the diagnostic is
        // journaled on the terminal record exactly like a journaled
        // release failure — the outcome stays the store failure ────────

        [Test]
        public void RefusedResourceAcquireWithFailingSeamJournalsReleaseDiagnostic()
        {
            var fixture = NewFixture();
            var resourceful = new ResourceOperation("test.e6e7.resourceful");
            fixture.Register(resourceful, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.resourceful", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.ResourceAcquired;
            var seamCalls = 0;
            var loop = fixture.NewLoopSeam((_, resource) =>
            {
                seamCalls++;
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_008, "The release seam failed."));
            });

            var tick = loop.Tick();

            Assert.IsTrue(tick.Ok, "no exception escapes the Tick");
            Assert.AreEqual(
                1, seamCalls, "the release seam still ran exactly once");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ResourceReleased).Count,
                "no unpaired resource_released is journaled");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"],
                "the store diagnostic stays the Run's error, not the seam's");
            var journaled = (JArray?)((JObject?)data["summary"])?["diagnostics"];
            Assert.IsNotNull(
                journaled,
                "the seam failure's diagnostic is journaled on the terminal record");
            var sawJournaledReleaseError = false;
            foreach (var entry in journaled!)
            {
                if ((string?)entry["code"] == ErrorCodes.OW_UNITY_008)
                {
                    sawJournaledReleaseError = true;
                }
            }
            Assert.IsTrue(
                sawJournaledReleaseError,
                "the durable RunFinished summary carries the release diagnostic");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));

            // Later Ticks are no-ops: no further record is attempted and
            // the release is never repeated.
            fixture.Store.ShouldFail = null;
            var recordCount = fixture.Store.Appended.Count;
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(loop.Tick().Ok);
            }
            Assert.AreEqual(recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(
                1, seamCalls, "the release is never repeated");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count);
        }

        // ── SPEC §E6 item 4: a release the seam fails stays open in the
        // journal for recovery — it never rewrites the decided outcome ──────

        [Test]
        public void FailedRunResourceReleaseKeepsOutcomeAndStaysOpen()
        {
            var fixture = NewFixture();
            var resourceful = new ResourceOperation("test.e6e7.resourceful");
            fixture.Register(resourceful, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.resourceful", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            var loop = fixture.NewLoopSeam((_, _) => Result<Unit>.Failure(
                ErrorCodes.Create(ErrorCodes.OW_UNITY_008, "The release seam failed.")));

            var tick = loop.Tick();

            Assert.IsTrue(tick.Ok);
            var sawReleaseError = false;
            foreach (var diagnostic in tick.Diagnostics)
            {
                if (diagnostic.Code == ErrorCodes.OW_UNITY_008)
                {
                    sawReleaseError = true;
                }
            }
            Assert.IsTrue(
                sawReleaseError,
                "the release diagnostic rides on the Tick's diagnostics");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var data = DataOf(finished[0]);
            Assert.AreEqual(
                "succeeded", (string?)data["state"],
                "a failed release never rewrites the Run's decided outcome");
            var journaled = (JArray?)((JObject?)data["summary"])?["diagnostics"];
            Assert.IsNotNull(
                journaled,
                "the release diagnostic is journaled on the terminal record's summary");
            var sawJournaledReleaseError = false;
            foreach (var entry in journaled!)
            {
                if ((string?)entry["code"] == ErrorCodes.OW_UNITY_008)
                {
                    sawJournaledReleaseError = true;
                }
            }
            Assert.IsTrue(
                sawJournaledReleaseError,
                "the durable RunFinished summary carries the release diagnostic");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ResourceReleased).Count,
                "no resource_released is journaled for a failed release");
            var fold = JournalFold.Empty();
            foreach (var record in fixture.Store.Appended)
            {
                Assert.IsTrue(fold.Apply(record));
            }
            Assert.AreEqual(
                1, fold.OpenResources.Count,
                "the reload fold still lists the resource — recovery (T-W1.8-08) can release it");
            Assert.AreEqual("editing_contents", fold.OpenResources[0].Kind);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E6 item 4: open resources with no release seam are a
        // wiring defect — reported, never a silent skip ────────────────────

        [Test]
        public void OpenResourceWithoutReleaseSeamReportsInternalError()
        {
            var fixture = NewFixture();
            var resourceful = new ResourceOperation("test.e6e7.resourceful");
            fixture.Register(resourceful, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.resourceful", 'd'),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            var loop = fixture.NewLoopSeam(null);

            var tick = loop.Tick();

            Assert.IsTrue(tick.Ok);
            var sawInternalError = false;
            foreach (var diagnostic in tick.Diagnostics)
            {
                if (diagnostic.Code == ErrorCodes.OW_API_004)
                {
                    sawInternalError = true;
                }
            }
            Assert.IsTrue(
                sawInternalError,
                "open resources with no release seam record the internal error");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var data = DataOf(finished[0]);
            Assert.AreEqual(
                "succeeded", (string?)data["state"]);
            var journaled = (JArray?)((JObject?)data["summary"])?["diagnostics"];
            Assert.IsNotNull(
                journaled,
                "the missing-seam internal error is journaled on the summary");
            var sawJournaledInternalError = false;
            foreach (var entry in journaled!)
            {
                if ((string?)entry["code"] == ErrorCodes.OW_API_004)
                {
                    sawJournaledInternalError = true;
                }
            }
            Assert.IsTrue(
                sawJournaledInternalError,
                "the durable RunFinished summary carries the internal error");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.ResourceReleased).Count);
            var fold = JournalFold.Empty();
            foreach (var record in fixture.Store.Appended)
            {
                Assert.IsTrue(fold.Apply(record));
            }
            Assert.AreEqual(1, fold.OpenResources.Count);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E6 item 4/§F1: the release-model matrix — every
        // combination of an Operation-side release, a refused resource
        // append and a Run ending releases each still-held resource
        // through the seam exactly once (zero times for one the Operation
        // released itself), journals no resource_released without a
        // matching resource_acquired, leaves exactly the Acquired-but-not-
        // ReleaseRecorded entries outstanding in the fold, ends the Run
        // with exactly one terminal record and makes later Ticks no-ops ──

        [Test]
        public void RunResourceReleaseModelMatrix(
            [Values] bool releaseA,
            [Values] MatrixRefusal refusal,
            [Values] MatrixEnding ending)
        {
            var fixture = NewFixture();
            var op = new MatrixResourceOperation("test.e6e7.matrixres")
            {
                ReleaseA = releaseA,
                AcquireC = ending == MatrixEnding.StoreFailure,
                Result = ending == MatrixEnding.Failure
                    ? StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_008, "The matrix step failed."))
                    : ending == MatrixEnding.Cancel
                        ? StepResult.Waiting(Json("\"held\""), "held", 60000)
                        : StepResult.Succeeded(),
            };
            fixture.Register(op, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.e6e7.matrixres", 'd', timeoutSeconds: 600),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            // The refusal applies from the start: an in-Execute refusal is
            // itself a store-failure ending. ending == StoreFailure refuses
            // the Operation's own third acquire — a distinct required
            // record raised while Execute is still running, independent of
            // the resource-append refusal axis, so the §F1 end comes
            // through the centralized helper; ending ==
            // StoreFailureAfterExecute refuses the step_succeeded append —
            // the §F1 end from a required append after Execute returned.
            fixture.Store.ShouldFail = record =>
                RefusalMatches(refusal, record) ||
                (ending == MatrixEnding.StoreFailure &&
                    IsStoreFailureAcquire(record)) ||
                (ending == MatrixEnding.StoreFailureAfterExecute &&
                    record.Type == JournalRecordType.StepSucceeded);

            Assert.IsTrue(fixture.Loop.Tick().Ok, "tick: no exception escapes");
            if (ending == MatrixEnding.Cancel &&
                fixture.Coordinator.StateOf(runId) == RunState.Running)
            {
                Assert.IsTrue(fixture.Coordinator.Cancel(runId, "halt").Ok);
                Assert.IsTrue(fixture.Loop.Tick().Ok, "tick: no exception escapes");
            }
            for (var i = 0; i < 4 && !IsTerminal(fixture.Coordinator.StateOf(runId)); i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok, "tick: no exception escapes");
            }
            Assert.IsTrue(
                IsTerminal(fixture.Coordinator.StateOf(runId)),
                "the Run reached a terminal state");

            // The release seam ran exactly once per resource still
            // physically held at the end — zero times for one the
            // Operation already released.
            Assert.AreEqual(
                releaseA ? 0 : 1, CountKind(fixture.Released, "kind_a"),
                "kind_a: the seam runs exactly once only while held");
            Assert.AreEqual(
                1, CountKind(fixture.Released, "kind_b"),
                "kind_b: always released through the seam exactly once");
            Assert.AreEqual(
                ending == MatrixEnding.StoreFailure ? 1 : 0,
                CountKind(fixture.Released, "kind_c"),
                "kind_c: the refused in-Execute acquisition still reaches the seam once");

            // No resource_released without a matching resource_acquired —
            // the §F2 fold itself is the pairing proof: every record
            // applies cleanly, and the outstanding set is exactly the
            // Acquired-but-not-ReleaseRecorded entries.
            var fold = JournalFold.Empty();
            foreach (var record in fixture.Store.Appended)
            {
                Assert.IsTrue(
                    fold.Apply(record),
                    "the journal stays fold-clean — no unpaired resource_released");
            }
            Assert.AreEqual(
                refusal == MatrixRefusal.ReleaseA ? 1 : 0,
                CountKind(fold.OpenResources, "kind_a"),
                "kind_a stays outstanding exactly when its release record was refused");
            Assert.AreEqual(
                refusal == MatrixRefusal.ReleaseB ? 1 : 0,
                CountKind(fold.OpenResources, "kind_b"),
                "kind_b stays outstanding exactly when its release record was refused");
            Assert.AreEqual(
                (refusal == MatrixRefusal.ReleaseA ? 1 : 0) +
                (refusal == MatrixRefusal.ReleaseB ? 1 : 0),
                fold.OpenResources.Count,
                "the fold's outstanding set is exactly the unrecorded releases");

            // Exactly one terminal record; a refused required append — or
            // the requested failing/store-failure ending — ends Failed,
            // otherwise the requested outcome stands.
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count, "the Run ends exactly once");
            var expectedState =
                refusal != MatrixRefusal.None ||
                ending == MatrixEnding.Failure ||
                ending == MatrixEnding.StoreFailure ||
                ending == MatrixEnding.StoreFailureAfterExecute
                    ? "failed"
                    : ending == MatrixEnding.Cancel ? "cancelled" : "succeeded";
            Assert.AreEqual(expectedState, (string?)DataOf(finished[0])["state"]);

            // Later Ticks are no-ops: no further record, no repeated
            // release, no second terminal record.
            var recordCount = fixture.Store.Appended.Count;
            var releasedCount = fixture.Released.Count;
            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok, "a later Tick is a no-op");
            }
            Assert.AreEqual(recordCount, fixture.Store.Appended.Count);
            Assert.AreEqual(releasedCount, fixture.Released.Count);
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count);
        }

        // ── SPEC §E6 item 4: the standard Operations assembly is
        // first-party — its grant on the internal IRunResourceRegistry seam
        // must not silently regress ────────────────────────────────────────

        [Test]
        public void CoreGrantsInternalsToStandardOperations()
        {
            var attributes = typeof(IRunResourceRegistry).Assembly.GetCustomAttributes(
                typeof(InternalsVisibleToAttribute), inherit: false);
            var granted = new List<string>();
            foreach (var attribute in attributes)
            {
                granted.Add(((InternalsVisibleToAttribute)attribute).AssemblyName);
            }
            Assert.Contains(
                "OpWeave.Operations", granted,
                "standard Operations register Run resources through the internal " +
                "IRunResourceRegistry seam; external extensions keep public contracts only");
        }

        // ── SPEC §E6 item 1: the timeout defaults are the SPEC values ───────

        [Test]
        public void TimeoutDefaultsAreSpecE6Values()
        {
            Assert.AreEqual(30, OperationDescriptor.DefaultImmediateTimeoutSeconds);
            Assert.AreEqual(600, OperationDescriptor.DefaultWaitableTimeoutSeconds);
            Assert.AreEqual(
                OperationDescriptor.DefaultImmediateTimeoutSeconds,
                Op.Define("test.e6e7.immediate", 1).Summary("x").Build().DefaultTimeoutSeconds);
        }
    }
}
