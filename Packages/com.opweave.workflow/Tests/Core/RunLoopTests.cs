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
    /// [Core] tests for the RunLoop of SPEC §E3 and the write-ahead step
    /// sequence of SPEC §E4 (local:T-W1.8-05:runloop-execution): journal
    /// order (StepStarted Flushed before Execute; StepSucceeded/StepWaiting
    /// after), three Immediate steps in one tick, budget deferral to the
    /// next tick, the §0.4 monotonic budget under a wall clock jumping
    /// backwards and forwards mid-Tick, Transitions-driven sequencing,
    /// OW-RUN-006 on a throwing Execute with the remaining steps Skipped,
    /// OW-RUN-007 on an unset
    /// output, WF-input defaults on an omitted input, absent outputs of
    /// unexecuted nodes, the SPEC §C2 Waiting → Poll → Succeeded path,
    /// the 2026-11-06 <c>not_compiling</c> wait rule (no StepStarted, no
    /// diagnostic, not Failed), Suspended on Play Mode / external compile,
    /// StepProgress on a changed waitingFor under the 5 s throttle, the
    /// SPEC §F1 store-failure path (a refused required append ends the Run
    /// Failed with no further step records and a self-consistent journal;
    /// a refused terminal append is retried once through the marked
    /// store-failure end — a single run_finished{failed} behind the §E6
    /// item 4 release and ahead of the exclusion's release — and stays
    /// non-terminal only when even that record can never be journaled; a
    /// journaled
    /// terminal record whose lock release failed keeps its state in memory
    /// and retries the release on later Ticks) and the
    /// SPEC §F6 masking of ctx.Log records and every free-text journal
    /// field before they persist (credential-named query parameters match
    /// on credential keys only, so a non-credential value can never consume
    /// and hide a later credential).
    /// Fixtures use dummy operations, a fake clock, an in-memory store and a
    /// real CoordinatorRunLock over a temp root. The same sources run under
    /// dotnet and Unity Mono.
    /// </summary>
    internal sealed class RunLoopTests
    {
        private const string Session = "session-runloop-test";
        private const string WorkflowId = "example.runloop";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RunLoop." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            // Drop coordinator-held OS exclusions first — an abandoned handle
            // keeps coordinator.lock undeletable (SPEC §E2 crash semantics).
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
                new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

            /// <summary>Added to every UtcNow read.</summary>
            public int AdvancePerReadMs { get; set; }

            public DateTimeOffset UtcNow
            {
                get
                {
                    _now += TimeSpan.FromMilliseconds(AdvancePerReadMs);
                    return _now;
                }
            }

            public void Advance(TimeSpan delta) => _now += delta;
        }

        /// <summary>
        /// The fake per-Tick monotonic timer (SPEC §0.4): every Elapsed
        /// read advances by the fixture-set amount — drives budget
        /// exhaustion independently of the wall clock.
        /// </summary>
        private sealed class FakeMonotonicTimer : IMonotonicTimer
        {
            private readonly int _advancePerReadMs;
            private TimeSpan _elapsed;

            internal FakeMonotonicTimer(int advancePerReadMs) =>
                _advancePerReadMs = advancePerReadMs;

            public TimeSpan Elapsed
            {
                get
                {
                    _elapsed += TimeSpan.FromMilliseconds(_advancePerReadMs);
                    return _elapsed;
                }
            }
        }

        private sealed class FakeRunStore : IRunStore
        {
            internal readonly List<JournalRecord> Appended = new List<JournalRecord>();
            internal readonly List<Durability> AppendedDurability = new List<Durability>();
            internal Action<JournalRecord>? OnAppend;

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
                OnAppend?.Invoke(record);
                return Result<Unit>.Success(Unit.Value);
            }

            public Result<RunSnapshot> Load(string runId) =>
                Result<RunSnapshot>.Failure(
                    ErrorCodes.Create(ErrorCodes.OW_STORE_001, "not wired"));

            public Result<IReadOnlyList<RunIndexEntry>> List(RunQuery query) =>
                throw new NotSupportedException("The fake store does not list.");

            public Result<Unit> Delete(string runId) =>
                throw new NotSupportedException("The fake store does not delete.");
        }

        private sealed class FakeRequirements : IRequirementEvaluator
        {
            internal readonly Dictionary<string, RequirementEvaluation> ByKind =
                new Dictionary<string, RequirementEvaluation>(StringComparer.Ordinal);

            public RequirementEvaluation Evaluate(Requirement requirement, StepContext context) =>
                ByKind.TryGetValue(requirement.Kind, out var outcome)
                    ? outcome
                    : RequirementEvaluation.Satisfied();
        }

        private sealed class FakeUndo : IDisposable
        {
            private readonly FakeUndoScope _scope;

            internal FakeUndo(FakeUndoScope scope) => _scope = scope;

            public void Dispose() => _scope.Closed++;
        }

        private sealed class FakeUndoScope
        {
            internal readonly List<string> Opened = new List<string>();
            internal int Closed;
            internal Func<string, IDisposable> Open => name =>
            {
                Opened.Add(name);
                return new FakeUndo(this);
            };
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
            internal readonly FakeRequirements Requirements = new FakeRequirements();
            internal readonly FakeUndoScope Undo = new FakeUndoScope();
            internal readonly List<string> Events = new List<string>();
            internal readonly List<RunResource> Released = new List<RunResource>();
            // The Store.Appended count captured at each release-seam call —
            // the journal position the physical release ran at (the
            // resource_released record follows it).
            internal readonly List<int> ReleasedAtJournalCount = new List<int>();
            internal bool OpWeaveBusy;

            /// <summary>
            /// Added to every Elapsed read of the fake monotonic timer the
            /// RunLoop creates for a Tick — drives budget exhaustion.
            /// </summary>
            internal int MonotonicAdvancePerReadMs;
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
                Requirements,
                Undo.Open,
                () => OpWeaveBusy,
                monotonicTimerFactory: () =>
                    new FakeMonotonicTimer(MonotonicAdvancePerReadMs),
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

            /// <summary>
            /// Registers the descriptor + matching fingerprints for the §1.4
            /// start check and keeps the live instance for the resolver.
            /// </summary>
            internal void Register(IOperation op, char hashSeed)
            {
                Ops[op.Descriptor.Id] = op;
                Operations.Add(new RegisteredOperation(
                    op.Descriptor, OperationOrigin.Custom, "Example.Operations",
                    null, new string(hashSeed, 64), new string((char)(hashSeed + 1), 64)));
            }
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        // ── Dummy operations ────────────────────────────────────────────────

        /// <summary>An Immediate marker: logs <c>execute:&lt;id&gt;</c> into the
        /// shared events and sets its <c>mark</c> output.</summary>
        private sealed class MarkerOperation : IOperation
        {
            private readonly List<string> _events;
            private readonly string _id;

            internal MarkerOperation(string id, List<string> events)
            {
                _id = id;
                _events = events;
                Descriptor = Op.Define(id, 1)
                    .Summary("Marks execution order.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            /// <summary>Optional hook run inside Execute — lets a test act
            /// mid-Tick (for example jump the wall clock).</summary>
            internal Action? OnExecute;

            // When set, Execute acquires a Run resource through the
            // context's IRunResourceRegistry — the still-open resource the
            // Run-end cleanup must release (SPEC §E6 item 4).
            internal bool AcquireResource;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                OnExecute?.Invoke();
                _events.Add("execute:" + _id);
                if (AcquireResource)
                {
                    ((IRunResourceRegistry)ctx).Acquire(
                        "editing_contents", Json("{\"guid\":\"abc\"}"));
                }
                ctx.SetOutput("mark", "done:" + _id);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>An Immediate whose Execute throws — the OW-RUN-006 case.</summary>
        private sealed class ThrowingOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.throwing", 1)
                .Summary("Throws in Execute.")
                .Output("out", "core.String")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                throw new InvalidOperationException("The dummy throws.");

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>An Immediate that never sets its declared output — the OW-RUN-007 case.</summary>
        private sealed class SilentOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.silent", 1)
                .Summary("Forgets its output.")
                .Output("out", "core.String")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>A Waitable that reports a changing waitingFor label per
        /// poll — the StepProgress case — then completes.</summary>
        private sealed class LabelWaitableOperation : IOperation
        {
            private int _polls;

            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.label_waitable", 1)
                .Summary("Reports changing progress labels.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build()
                with { ExecutionMode = ExecutionMode.Waitable };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(Json("\"t0\""), "stage-0", 10);

            public StepResult Poll(StepContext ctx)
            {
                _polls++;
                if (_polls < 3)
                {
                    return StepResult.Waiting(
                        Json("\"t" + _polls + "\""), "stage-" + _polls, 10);
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>An Immediate carrying a declared Requirement — the
        /// not_compiling wait and the requirement-violation cases.</summary>
        private sealed class GatedOperation : IOperation
        {
            private readonly List<string> _events;

            internal GatedOperation(string kind, List<string> events)
            {
                _events = events;
                Descriptor = Op.Define("test.dummy.gated", 1)
                    .Summary("Gated by a runtime requirement.")
                    .Input("name", "core.String", required: false)
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Requires(kind)
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
                _events.Add("execute:gated");
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>An Immediate that emits one ctx.Log entry with the given
        /// message and data, then succeeds — the SPEC §F6 masking cases.</summary>
        private sealed class LoggingOperation : IOperation
        {
            private readonly string _message;
            private readonly JsonValue _data;

            internal LoggingOperation(string message, JsonValue data)
            {
                _message = message;
                _data = data;
                Descriptor = Op.Define("test.dummy.logging", 1)
                    .Summary("Emits one log entry.")
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
                ctx.Log.Write(Severity.Warning, _message, data: _data);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>An Immediate whose Preflight reports an error — the E4
        /// step 3 failure case (fails before StepStarted).</summary>
        private sealed class BadPreflightOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.bad_preflight", 1)
                .Summary("Fails its preflight.")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    new[]
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_UNITY_003, "The required Scene is not loaded."),
                    },
                    Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                throw new InvalidOperationException("Execute must not be reached.");

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>An Immediate that fails with a caller-chosen diagnostic —
        /// the SPEC §F6 journal-masking cases.</summary>
        private sealed class FailingOperation : IOperation
        {
            private readonly Diagnostic _error;

            internal FailingOperation(Diagnostic error)
            {
                _error = error;
                Descriptor = Op.Define("test.dummy.failing", 1)
                    .Summary("Fails with a fixed diagnostic.")
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

            public StepResult Execute(StepContext ctx) => StepResult.Failed(_error);

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>A Waitable whose waitingFor labels carry URL credentials —
        /// the StepWaiting/StepProgress SPEC §F6 masking cases.</summary>
        private sealed class UrlWaitableOperation : IOperation
        {
            private int _polls;

            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.url_waitable", 1)
                .Summary("Reports credential-bearing wait labels.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build()
                with { ExecutionMode = ExecutionMode.Waitable };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(
                    Json("\"t0\""), "cloning https://user:pass@git.example/one.git", 10);

            public StepResult Poll(StepContext ctx)
            {
                _polls++;
                if (_polls < 2)
                {
                    return StepResult.Waiting(
                        Json("\"t" + _polls + "\""),
                        "fetching https://user:pass@git.example/two.git", 10);
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // ── Plan/doc builders ───────────────────────────────────────────────

        private static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            IReadOnlyDictionary<string, int>? transitions = null,
            int timeoutSeconds = 30) =>
            new PlanStep(
                index, nodeId, operationId, 1,
                new string(hashSeed, 64), new string((char)(hashSeed + 1), 64),
                inputs ?? new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                transitions ?? new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                timeoutSeconds, new RetryPolicy(), Idempotency.Idempotent);

        private static CompiledPlan Plan(
            PlanStep[] steps,
            PlanOutput[]? outputs = null,
            JsonValue? inputs = null)
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
                inputs ?? Json("{}"),
                steps,
                outputs ?? Array.Empty<PlanOutput>(),
                Array.Empty<string>(), new List<ApprovalRequirement>(),
                Array.Empty<PlanSourceMapEntry>(), hashInput);
        }

        private static WorkflowDocument Doc(params string[] nodeIds)
        {
            var nodes = new List<NodeData>();
            foreach (var id in nodeIds)
            {
                nodes.Add(new NodeData { Id = id, Operation = "test.dummy", Version = 1 });
            }
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = WorkflowId,
                Nodes = nodes,
                ControlEdges = new List<ControlEdge>(),
            });
            Assert.IsTrue(created.Ok, "WorkflowDocument.Create failed.");
            return created.Value!;
        }

        private static AcceptOptions Options() => new AcceptOptions(null, false);

        private static Result<RunHandle> Accept(Fixture fixture, CompiledPlan plan) =>
            fixture.Coordinator.Accept(plan, Doc("n1", "n2", "n3"), ActorKind.Agent, Options());

        /// <summary>Accepts a plan and returns the accepted runId.</summary>
        private static string AcceptRun(Fixture fixture, CompiledPlan plan)
        {
            var accepted = Accept(fixture, plan);
            Assert.IsTrue(accepted.Ok, "acceptance failed");
            return accepted.Value!.RunId;
        }

        private static List<JournalRecord> RecordsOfType(
            Fixture fixture, JournalRecordType type) =>
            fixture.Store.Appended.FindAll(record => record.Type == type);

        private static JObject DataOf(JournalRecord record) =>
            (JObject)record.Data.ToToken();

        private static string EventName(JournalRecord record) =>
            JournalRecordTypes.Of(record.Type).WireName +
            (record.Step is { } step ? ":" + step : "");

        /// <summary>
        /// The reload view of the durable journal (SPEC §F0/§F2): every
        /// appended record folded back into state — a produced journal is
        /// consistent only when each record applies cleanly.
        /// </summary>
        private static JournalFold FoldJournal(Fixture fixture)
        {
            var fold = JournalFold.Empty();
            for (var i = 0; i < fixture.Store.Appended.Count; i++)
            {
                var record = fixture.Store.Appended[i];
                Assert.IsTrue(
                    fold.Apply(record),
                    $"journal record {i} ({record.Type}) must fold cleanly");
            }
            return fold;
        }

        // ── SPEC §E3 test 1: Immediate×3 in one Tick ─────────────────────────

        [Test]
        public void ThreeImmediateStepsCompleteInOneTick()
        {
            var fixture = NewFixture();
            fixture.Store.OnAppend = record => fixture.Events.Add(EventName(record));
            var m1 = new MarkerOperation("test.dummy.m1", fixture.Events);
            var m2 = new MarkerOperation("test.dummy.m2", fixture.Events);
            var m3 = new MarkerOperation("test.dummy.m3", fixture.Events);
            fixture.Register(m1, 'd');
            fixture.Register(m2, 'j');
            fixture.Register(m3, 'p');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.dummy.m2", 'j',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(2, "n3", "test.dummy.m3", 'p',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var plan = Plan(steps, outputs: new[]
            {
                new PlanOutput("result", 2, "mark"),
            });
            var runId = AcceptRun(fixture, plan);

            var ticked = fixture.Loop.Tick();

            Assert.IsTrue(ticked.Ok);
            // Journal order: RunStarted → per step StepStarted(Flushed) →
            // Execute → StepSucceeded → RunFinished (SPEC §E4, §F2).
            var expected = new[]
            {
                "run_started",
                "step_started:0", "execute:test.dummy.m1", "step_succeeded:0",
                "step_started:1", "execute:test.dummy.m2", "step_succeeded:1",
                "step_started:2", "execute:test.dummy.m3", "step_succeeded:2",
                "run_finished",
            };
            CollectionAssert.AreEqual(expected, fixture.Events);
            // StepStarted and StepSucceeded are Flushed (write-ahead, SPEC §F2).
            for (var i = 0; i < fixture.Store.Appended.Count; i++)
            {
                if (fixture.Store.Appended[i].Type == JournalRecordType.StepStarted ||
                    fixture.Store.Appended[i].Type == JournalRecordType.StepSucceeded)
                {
                    Assert.AreEqual(
                        Durability.Flushed, fixture.Store.AppendedDurability[i],
                        $"record {i} must be Flushed");
                }
            }
            var started = RecordsOfType(fixture, JournalRecordType.StepStarted);
            Assert.AreEqual(3, started.Count);
            Assert.AreEqual(1, started[0].Attempt, "the first attempt is numbered 1");
            Assert.IsTrue(
                DataOf(started[0])["snapshot"] is not null &&
                DataOf(started[0])["inputsDigest"] is not null,
                "StepStarted carries snapshot and inputsDigest");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var data = DataOf(finished[0]);
            Assert.AreEqual("succeeded", (string?)data["state"]);
            Assert.AreEqual(
                "done:test.dummy.m3", (string?)data["outputs"]?["result"],
                "the workflow output of the executed last step is reported");
            Assert.AreEqual(
                RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            // E4 steps 5 and 8: one undo scope per step, named
            // "OpWeave: <workflowId>/<nodeId>", closed after the attempt.
            CollectionAssert.AreEqual(
                new[]
                {
                    "OpWeave: example.runloop/n1",
                    "OpWeave: example.runloop/n2",
                    "OpWeave: example.runloop/n3",
                },
                fixture.Undo.Opened);
            Assert.AreEqual(3, fixture.Undo.Closed, "every opened scope is closed");
        }

        // ── SPEC §E3 test 2: budget exhaustion defers to the next Tick ───────

        [Test]
        public void BudgetExhaustionDefersToNextTick()
        {
            var fixture = NewFixture();
            var m1 = new MarkerOperation("test.dummy.m1", fixture.Events);
            var m2 = new MarkerOperation("test.dummy.m2", fixture.Events);
            fixture.Register(m1, 'd');
            fixture.Register(m2, 'j');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.dummy.m2", 'j'),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            // Every monotonic-timer read advances 30 ms: the first budget
            // check still admits step 0; the next sees the 50 ms budget
            // exhausted.
            fixture.MonotonicAdvancePerReadMs = 30;

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepSucceeded).Count,
                "only the first step ran inside the budget");
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepStarted).Count,
                "the second step is deferred");
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            fixture.MonotonicAdvancePerReadMs = 0;
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                2, RecordsOfType(fixture, JournalRecordType.StepSucceeded).Count,
                "the deferred step completes on the next tick");
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §0.4: the budget is a duration — wall-clock jumps do not count ──

        [Test]
        public void TickBudgetFollowsTheMonotonicTimerNotTheWallClock()
        {
            var fixture = NewFixture();
            var m1 = new MarkerOperation("test.dummy.m1", fixture.Events);
            var m2 = new MarkerOperation("test.dummy.m2", fixture.Events);
            var m3 = new MarkerOperation("test.dummy.m3", fixture.Events);
            fixture.Register(m1, 'd');
            fixture.Register(m2, 'j');
            fixture.Register(m3, 'p');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.dummy.m2", 'j',
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(2, "n3", "test.dummy.m3", 'p'),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            // The wall clock is unreliable (SPEC §0.4): step 0's Execute
            // jumps it backwards one hour, step 1's jumps it three hours
            // forwards — mid-Tick, between the budget checks.
            m1.OnExecute = () => fixture.Clock.Advance(TimeSpan.FromHours(-1));
            m2.OnExecute = () => fixture.Clock.Advance(TimeSpan.FromHours(3));
            fixture.MonotonicAdvancePerReadMs = 30;

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            // Tick 1: the monotonic reads are 30 ms then 60 ms — step 0
            // runs and step 1 is deferred; the −1 h wall jump must not
            // re-open the spent budget.
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepSucceeded).Count,
                "the backwards wall-clock jump must not un-exhaust the budget");
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            // Tick 2: the fresh timer stays under the budget, so both
            // remaining steps run — the +3 h jump mid-Tick defers nothing.
            fixture.MonotonicAdvancePerReadMs = 0;
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                3, RecordsOfType(fixture, JournalRecordType.StepSucceeded).Count,
                "the forwards wall-clock jump must not exhaust the budget");
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E3 test 3: the Transitions table drives the order ──────────

        [Test]
        public void PermutedTransitionsAreFollowed()
        {
            var fixture = NewFixture();
            var m1 = new MarkerOperation("test.dummy.m1", fixture.Events);
            var m2 = new MarkerOperation("test.dummy.m2", fixture.Events);
            var m3 = new MarkerOperation("test.dummy.m3", fixture.Events);
            fixture.Register(m1, 'd');
            fixture.Register(m2, 'j');
            fixture.Register(m3, 'p');
            // Permuted Transitions: 0 → 2 → 1 → End. The runner must follow
            // the table, never index + 1 (SPEC §E3 item 2, §D7 item 2).
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(1, "n2", "test.dummy.m2", 'j'),
                Step(2, "n3", "test.dummy.m3", 'p',
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var executed = fixture.Events.FindAll(e => e.StartsWith("execute:", StringComparison.Ordinal));
            CollectionAssert.AreEqual(
                new[] { "execute:test.dummy.m1", "execute:test.dummy.m3", "execute:test.dummy.m2" },
                executed);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E4 test 1: throwing Execute → Failed/OW-RUN-006, rest Skipped

        [Test]
        public void ThrowingExecuteFailsOwRun006AndSkipsRemaining()
        {
            var fixture = NewFixture();
            var m1 = new MarkerOperation("test.dummy.m1", fixture.Events);
            var throwing = new ThrowingOperation();
            var m3 = new MarkerOperation("test.dummy.m3", fixture.Events);
            fixture.Register(m1, 'd');
            fixture.Register(throwing, 'j');
            fixture.Register(m3, 'p');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.dummy.throwing", 'j',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(2, "n3", "test.dummy.m3", 'p',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.AreEqual(1, failed[0].Step);
            Assert.AreEqual(
                "OW-RUN-006", (string?)DataOf(failed[0])["error"],
                "the exception surfaces as OW-RUN-006");
            var skipped = RecordsOfType(fixture, JournalRecordType.StepSkipped);
            Assert.AreEqual(1, skipped.Count);
            Assert.AreEqual(2, skipped[0].Step, "the remaining step is Skipped");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual("failed", (string?)DataOf(finished[0])["state"]);
            Assert.AreEqual("OW-RUN-006", (string?)DataOf(finished[0])["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
            Assert.IsFalse(
                fixture.Events.Contains("execute:test.dummy.m3"),
                "the unreached step never executed");
        }

        // ── SPEC §E4 test 2: an unset output is OW-RUN-007 ───────────────────

        [Test]
        public void UnsetOutputFailsOwRun007()
        {
            var fixture = NewFixture();
            var silent = new SilentOperation();
            fixture.Register(silent, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.silent", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.AreEqual("OW-RUN-007", (string?)DataOf(failed[0])["error"]);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepSucceeded).Count,
                "no StepSucceeded is written for the invalid output");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §C2 test 1: Waiting → Poll → Succeeded ──────────────────────

        [Test]
        public void WaitableStepGoesWaitingThenPollSucceeds()
        {
            var fixture = NewFixture();
            fixture.Store.OnAppend = record => fixture.Events.Add(EventName(record));
            var waitable = new OperationContractFixtures.WaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.waitable", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            // Tick 1: Execute returns Waiting → StepWaiting (Flushed) is
            // journaled after StepStarted; the step waits at nextPollAt.
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var waiting = RecordsOfType(fixture, JournalRecordType.StepWaiting);
            Assert.AreEqual(1, waiting.Count);
            var waitingData = DataOf(waiting[0]);
            Assert.AreEqual("fixture_wait", (string?)waitingData["waitingFor"]);
            Assert.IsTrue(waitingData["resumeToken"] is not null);
            Assert.IsTrue(
                DateTimeOffset.TryParse((string?)waitingData["deadline"], out var deadline),
                "the deadline is a recorded UTC timestamp");
            var startedAt = DateTimeOffset.Parse(
                RecordsOfType(fixture, JournalRecordType.StepStarted)[0].Ts);
            Assert.AreEqual(
                30.0, (deadline - startedAt).TotalSeconds, 0.001,
                "deadline = step start + timeoutSeconds");
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            // nextPollAt gates the poll: a tick before it changes nothing.
            var recordCount = fixture.Store.Appended.Count;
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                recordCount, fixture.Store.Appended.Count,
                "no records before nextPollAt");

            // Tick at/after nextPollAt: a fresh Poll with only the journaled
            // resumeToken completes the step (SPEC §C2 items 3–4).
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var succeeded = RecordsOfType(fixture, JournalRecordType.StepSucceeded);
            Assert.AreEqual(1, succeeded.Count);
            Assert.AreEqual(
                true, (bool?)DataOf(succeeded[0])["outputs"]?["done"],
                "the poll's output is journaled");
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            CollectionAssert.AreEqual(
                new[]
                {
                    "run_started", "step_started:0", "step_waiting:0", "step_succeeded:0",
                    "run_finished",
                },
                fixture.Events);
        }

        // ── 2026-11-06 owner decision: not_compiling waits, never fails ──────

        [Test]
        public void NotCompilingWaitDefersWithoutStartingOrFailing()
        {
            var fixture = NewFixture();
            var gated = new GatedOperation("not_compiling", fixture.Events);
            fixture.Register(gated, 'd');
            fixture.Requirements.ByKind["not_compiling"] = RequirementEvaluation.Wait();
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.gated", 'd',
                    transitions: new Dictionary<string, int> { ["next"] = -1 }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            var ticked = fixture.Loop.Tick();

            Assert.IsTrue(ticked.Ok);
            Assert.IsEmpty(
                ticked.Diagnostics,
                "a not_compiling wait emits no diagnostic");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepStarted).Count,
                "no StepStarted is written while waiting");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepFailed).Count,
                "the step is not failed");
            Assert.IsFalse(
                fixture.Events.Contains("execute:gated"),
                "the step never started");
            Assert.AreEqual(
                RunState.Running, fixture.Coordinator.StateOf(runId),
                "the Run stays Running for the next Tick's re-evaluation");

            // Next Tick, satisfied: the step is re-evaluated from E4 step 1
            // and runs.
            fixture.Requirements.ByKind["not_compiling"] = RequirementEvaluation.Satisfied();
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.IsTrue(fixture.Events.Contains("execute:gated"));
            Assert.AreEqual(1, RecordsOfType(fixture, JournalRecordType.StepStarted).Count);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── Requirement violations fail the step (SPEC §1.4, §E4 step 2) ─────

        [Test]
        public void RequirementViolationFailsStepBeforeStepStarted()
        {
            var fixture = NewFixture();
            var gated = new GatedOperation("scene_loaded", fixture.Events);
            fixture.Register(gated, 'd');
            fixture.Requirements.ByKind["scene_loaded"] = RequirementEvaluation.Violated(
                ErrorCodes.Create(ErrorCodes.OW_UNITY_003, "The required Scene is not loaded."));
            var steps = new[] { Step(0, "n1", "test.dummy.gated", 'd') };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.AreEqual("OW-UNITY-003", (string?)DataOf(failed[0])["error"]);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepStarted).Count,
                "the requirement failure precedes the write-ahead record");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // ── Preflight errors fail before the write-ahead record (E4 step 3) ──

        [Test]
        public void PreflightErrorFailsBeforeStepStarted()
        {
            var fixture = NewFixture();
            var bad = new BadPreflightOperation();
            fixture.Register(bad, 'd');
            var steps = new[] { Step(0, "n1", "test.dummy.bad_preflight", 'd') };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepStarted).Count);
            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.AreEqual("OW-UNITY-003", (string?)DataOf(failed[0])["error"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E3 item 4: Play Mode suspends ──────────────────────────────

        [Test]
        public void PlayModeSuspendsTheRun()
        {
            var fixture = NewFixture();
            var waitable = new OperationContractFixtures.WaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.waitable", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            fixture.Editor.IsEditMode = false;
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var suspended = RecordsOfType(fixture, JournalRecordType.RunSuspended);
            Assert.AreEqual(1, suspended.Count);
            Assert.AreEqual("play_mode", (string?)DataOf(suspended[0])["reason"]);
            Assert.AreEqual(RunState.Suspended, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §F1 / §1.4: an unjournaled suspend fails SuspendActive ───────

        [Test]
        public void SuspendActiveReportsTheStoreFailure()
        {
            var fixture = NewFixture();
            var waitable = new OperationContractFixtures.WaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.waitable", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.RunSuspended;
            var result = fixture.Loop.SuspendActive("play_mode");

            Assert.IsFalse(
                result.Ok,
                "a run_suspended record the store refused must not report success");
            var sawStoreError = false;
            foreach (var diagnostic in result.Diagnostics)
            {
                sawStoreError |= diagnostic.Code == ErrorCodes.OW_STORE_001;
            }
            Assert.IsTrue(
                sawStoreError, "the refusal must carry the OW-STORE-001 store diagnostic");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunSuspended).Count);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §E3 item 4 / §1.4: external compile suspends ────────────────

        [Test]
        public void ExternalCompileSuspendsTheRun()
        {
            var fixture = NewFixture();
            var waitable = new OperationContractFixtures.WaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.waitable", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            fixture.Editor.IsCompiling = true;
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var suspended = RecordsOfType(fixture, JournalRecordType.RunSuspended);
            Assert.AreEqual(1, suspended.Count);
            Assert.AreEqual(
                "external_compile_or_import", (string?)DataOf(suspended[0])["reason"]);
            Assert.AreEqual(RunState.Suspended, fixture.Coordinator.StateOf(runId));
        }

        // ── §1.4: a compile an OpWeave node triggered keeps the Run waiting ──

        [Test]
        public void OpWeaveInitiatedCompileDoesNotSuspend()
        {
            var fixture = NewFixture();
            var waitable = new OperationContractFixtures.WaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.waitable", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            fixture.Editor.IsCompiling = true;
            fixture.OpWeaveBusy = true;
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunSuspended).Count,
                "no suspension while OpWeave's own compile runs");
            Assert.AreEqual(
                RunState.Succeeded, fixture.Coordinator.StateOf(runId),
                "the waiting step polled through to completion");
        }

        // ── SPEC §F2 StepProgress: changed label, required:false, ≥5 s ───────

        [Test]
        public void StepProgressFollowsChangedWaitingForUnderFiveSecondThrottle()
        {
            var fixture = NewFixture();
            var waitable = new LabelWaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.label_waitable", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            // Tick 1: Execute → Waiting "stage-0" — the baseline, no progress.
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepProgress).Count);

            // Tick 2: label changes to "stage-1" inside the 5 s interval —
            // throttled, no StepProgress.
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepProgress).Count,
                "a change inside the 5 s interval stays unrecorded");

            // Tick 3: past the interval — the changed label is journaled.
            fixture.Clock.Advance(TimeSpan.FromSeconds(6));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var progress = RecordsOfType(fixture, JournalRecordType.StepProgress);
            Assert.AreEqual(1, progress.Count);
            Assert.AreEqual("stage-2", (string?)DataOf(progress[0])["message"]);
            Assert.IsFalse(progress[0].Required, "step_progress is required:false");
            Assert.AreEqual(
                Durability.Buffered, fixture.Store.AppendedDurability[
                    fixture.Store.Appended.FindIndex(r => ReferenceEquals(r, progress[0]))],
                "step_progress appends Buffered");

            // Tick 4: the wait completes.
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §A6 test 1: an omitted input with a default runs ────────────

        [Test]
        public void OmittedInputWithDefaultRuns()
        {
            var fixture = NewFixture();
            var immediate = new OperationContractFixtures.ImmediateOperation();
            fixture.Register(immediate, 'd');
            // The WF input was omitted at execution; the compiler resolved the
            // declared default into the step's InputSource (SPEC §A6, §D7) —
            // and a second port stays Missing, never a JSON null (§A0.1).
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.immediate", 'd',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["name"] = new Default(Json("\"default-name\"")),
                        ["optional"] = new Missing(),
                    },
                    outputTypes: new Dictionary<string, string> { ["echo"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps, inputs: Json("{}")));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var succeeded = RecordsOfType(fixture, JournalRecordType.StepSucceeded);
            Assert.AreEqual(1, succeeded.Count);
            Assert.AreEqual(
                "echo:default-name", (string?)DataOf(succeeded[0])["outputs"]?["echo"],
                "the omitted input executes with its default");
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §A6 test 2: outputs of unexecuted nodes are omitted ─────────

        [Test]
        public void OutputsOfUnexecutedNodesAreOmitted()
        {
            var fixture = NewFixture();
            var m1 = new MarkerOperation("test.dummy.m1", fixture.Events);
            var m2 = new MarkerOperation("test.dummy.m2", fixture.Events);
            var m3 = new MarkerOperation("test.dummy.m3", fixture.Events);
            fixture.Register(m1, 'd');
            fixture.Register(m2, 'j');
            fixture.Register(m3, 'p');
            // Transitions skip step 1 entirely: 0 → 2 → End.
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(1, "n2", "test.dummy.m2", 'j',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
                Step(2, "n3", "test.dummy.m3", 'p',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var plan = Plan(steps, outputs: new[]
            {
                new PlanOutput("o2", 1, "mark"),
                new PlanOutput("o3", 2, "mark"),
            });
            var runId = AcceptRun(fixture, plan);

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            var outputs = (JObject?)DataOf(finished[0])["outputs"];
            Assert.IsNotNull(outputs);
            Assert.AreEqual(
                "done:test.dummy.m3", (string?)outputs!["o3"],
                "the executed node's output is reported");
            Assert.IsFalse(
                outputs!.ContainsKey("o2"),
                "the unexecuted node's output is omitted (SPEC §A6)");
            var skipped = RecordsOfType(fixture, JournalRecordType.StepSkipped);
            Assert.AreEqual(1, skipped.Count);
            Assert.AreEqual(1, skipped[0].Step, "the unreached step is Skipped");
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
        }

        // ── SPEC §F1: a refused required append ends Failed, never the
        // interrupted outcome, and stops every further step record ──────────

        [Test]
        public void StepFailedAppendFailureEndsFailedWithoutStepRecords()
        {
            var fixture = NewFixture();
            var throwing = new ThrowingOperation();
            var m2 = new MarkerOperation("test.dummy.m2", fixture.Events);
            fixture.Register(throwing, 'd');
            fixture.Register(m2, 'j');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.throwing", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.dummy.m2", 'j',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.StepFailed;

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            // The refused StepFailed never became durable — and no further
            // step record was attempted: the step's in-memory state keeps
            // mirroring the journal, the remaining step stays Pending on
            // both sides (SPEC §F1).
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepFailed).Count);
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.StepSkipped).Count,
                "a failed required append stops all further step records");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"],
                "the store diagnostic is the Run's error, not the step's OW-RUN-006");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
            // The folded journal tells the same story: step 0 reached only
            // StepStarted, and the Run ends Failed — never the step's
            // unjournaled outcome.
            var fold = FoldJournal(fixture);
            Assert.AreEqual(RunState.Failed, fold.State);
            var snapshots = fold.Steps();
            Assert.AreEqual(1, snapshots.Count);
            Assert.AreEqual(0, snapshots[0].Index);
            Assert.AreEqual(
                StepState.Running, snapshots[0].State,
                "with no StepFailed record the step folds to its journaled state");
        }

        // ── SPEC §F1: a refused StepSkipped can never leave RunFinished
        // claiming the original outcome ─────────────────────────────────────

        [Test]
        public void StepSkippedAppendFailureNeverClaimsTheOriginalState()
        {
            var fixture = NewFixture();
            var m1 = new MarkerOperation("test.dummy.m1", fixture.Events);
            var m2 = new MarkerOperation("test.dummy.m2", fixture.Events);
            var m3 = new MarkerOperation("test.dummy.m3", fixture.Events);
            fixture.Register(m1, 'd');
            fixture.Register(m2, 'j');
            fixture.Register(m3, 'p');
            // Transitions skip step 1 entirely: 0 → 2 → End.
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(1, "n2", "test.dummy.m2", 'j',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
                Step(2, "n3", "test.dummy.m3", 'p',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.StepSkipped;

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            // The durable journal still shows step 1 Pending — a RunFinished
            // claiming Succeeded would contradict it, so the Run can only
            // end Failed with the store diagnostic (SPEC §F1).
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"], "never a success");
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
            var fold = FoldJournal(fixture);
            Assert.AreEqual(RunState.Failed, fold.State);
            var snapshots = fold.Steps();
            Assert.AreEqual(2, snapshots.Count);
            Assert.AreEqual(StepState.Succeeded, snapshots[0].State);
            Assert.AreEqual(StepState.Succeeded, snapshots[1].State);
        }

        // ── SPEC §F1: a refused StepSkipped on an already-Failed Run still
        // leaves the journal self-consistent ────────────────────────────────

        [Test]
        public void StepSkippedAppendFailureOnFailedRunKeepsTheJournalConsistent()
        {
            var fixture = NewFixture();
            var throwing = new ThrowingOperation();
            var m2 = new MarkerOperation("test.dummy.m2", fixture.Events);
            fixture.Register(throwing, 'd');
            fixture.Register(m2, 'j');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.throwing", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.dummy.m2", 'j',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.StepSkipped;

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            // The step's failure IS durable; only the skip is missing — the
            // step stays Pending on both sides and the terminal record is
            // Failed with the store diagnostic (SPEC §F1).
            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.StepFailed).Count);
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual("OW-STORE-001", (string?)data["summary"]?["code"]);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
            var fold = FoldJournal(fixture);
            Assert.AreEqual(RunState.Failed, fold.State);
            var snapshots = fold.Steps();
            Assert.AreEqual(1, snapshots.Count);
            Assert.AreEqual(StepState.Failed, snapshots[0].State);
        }

        // ── SPEC §F1: when even the terminal record cannot be written the
        // journal stays non-terminal for the §F4 recovery engine ────────────

        [Test]
        public void TerminalAppendFailureLeavesTheJournalNonTerminal()
        {
            var fixture = NewFixture();
            var m1 = new MarkerOperation("test.dummy.m1", fixture.Events);
            fixture.Register(m1, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            fixture.Store.ShouldFail =
                record => record.Type == JournalRecordType.RunFinished;

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "no terminal record is durable — §F4 recovery decides");
            Assert.AreEqual(
                RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the in-memory Run leaves Running so it cannot spin");
            var fold = FoldJournal(fixture);
            Assert.AreEqual(
                RunState.Running, fold.State,
                "the journal deliberately stays non-terminal — never a success");
        }

        // ── SPEC §F1: a refused terminal append takes the same store-
        // failure end as every other refused required record — the Run is
        // marked Failed and the next Tick's Run-end cleanup journals the
        // single run_finished{failed} behind the §E6 item 4 release and
        // frees the still-held exclusion ────────────────────────────────

        [Test]
        public void RefusedSucceededTerminalEndsThroughTheStoreFailureCleanup()
        {
            var fixture = NewFixture();
            var m1 = new MarkerOperation("test.dummy.m1", fixture.Events)
            {
                AcquireResource = true,
            };
            fixture.Register(m1, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            // The store refuses the Run's own terminal record only — the
            // store-failure end's run_finished{failed} still lands.
            fixture.Store.ShouldFail = record =>
                record.Type == JournalRecordType.RunFinished &&
                string.Equals(
                    (string?)DataOf(record)["state"], "succeeded",
                    StringComparison.Ordinal);

            var ticked = fixture.Loop.Tick();

            Assert.IsTrue(ticked.Ok);
            Assert.AreEqual(
                "OW-STORE-001", ticked.Diagnostics[0].Code,
                "the refused terminal record's diagnostic is reported");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "the refused record never became durable");
            Assert.AreEqual(
                RunState.Failed, fixture.Coordinator.StateOf(runId),
                "the marked Run's in-memory state is already Failed (SPEC §F1)");
            // The §E6 item 4 release already ran ahead of the refused
            // terminal record — the journaled resource_released stands.
            Assert.AreEqual(1, fixture.Released.Count);
            Assert.AreEqual("editing_contents", fixture.Released[0].Kind);
            var released = RecordsOfType(fixture, JournalRecordType.ResourceReleased);
            Assert.AreEqual(1, released.Count);
            var releasedIndex = fixture.Store.Appended.IndexOf(released[0]);
            Assert.AreEqual(
                fixture.ReleasedAtJournalCount[0], releasedIndex,
                "the physical release precedes its resource_released record");
            Assert.IsFalse(
                CoordinatorRunLock.TryAcquire(_root).Ok,
                "the exclusion stays held until the end is journaled");

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                1, fixture.Released.Count,
                "the already-released resource is never released twice");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var data = DataOf(finished[0]);
            Assert.AreEqual(
                "failed", (string?)data["state"],
                "the store-failure end journals run_finished{failed}, never the refused outcome");
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"],
                "the terminal record carries the store diagnostic");
            Assert.Less(
                releasedIndex, fixture.Store.Appended.IndexOf(finished[0]),
                "resource_released is journaled before the terminal record");
            var folded = FoldJournal(fixture);
            Assert.AreEqual(
                RunState.Failed, folded.State,
                "the journal's own fold reconstructs Failed");
            Assert.AreEqual(
                0, folded.OpenResources.Count,
                "the journaled release leaves no open resource");
            var reacquired = CoordinatorRunLock.TryAcquire(_root);
            Assert.IsTrue(
                reacquired.Ok && reacquired.Value is not null,
                "the exclusion is released");
            reacquired.Value!.Dispose();

            // A following Run acquires the freed exclusion and completes.
            fixture.Store.ShouldFail = null;
            var second = AcceptRun(fixture, Plan(steps));
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(
                RunState.Succeeded, fixture.Coordinator.StateOf(second),
                "a following Run acquires the freed lock and runs to its end");
        }

        // The same path when the interrupted outcome is already Failed:
        // the step's own run_finished{failed} is refused once, then the
        // store-failure end's retry is journaled — never a duplicate and
        // never a different terminal state.
        [Test]
        public void RefusedFailedTerminalEndsThroughTheStoreFailureCleanup()
        {
            var fixture = NewFixture();
            var throwing = new ThrowingOperation();
            fixture.Register(throwing, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.throwing", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));
            // Refuse the first terminal append only — the step's own
            // run_finished{failed}; the store-failure end's retry lands.
            var terminalRefusals = 0;
            fixture.Store.ShouldFail = record =>
                record.Type == JournalRecordType.RunFinished && terminalRefusals++ == 0;

            var ticked = fixture.Loop.Tick();

            Assert.IsTrue(ticked.Ok);
            Assert.AreEqual("OW-RUN-006", ticked.Diagnostics[0].Code);
            Assert.AreEqual(
                "OW-STORE-001", ticked.Diagnostics[1].Code,
                "the refused terminal record's diagnostic is reported");
            Assert.AreEqual(
                0, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "the refused record never became durable");
            Assert.AreEqual(
                RunState.Failed, fixture.Coordinator.StateOf(runId));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(
                1, finished.Count,
                "exactly one terminal record is durable — never a duplicate");
            var data = DataOf(finished[0]);
            Assert.AreEqual("failed", (string?)data["state"]);
            Assert.AreEqual(
                "OW-STORE-001", (string?)data["summary"]?["code"],
                "the terminal record carries the store diagnostic");
            var folded = FoldJournal(fixture);
            Assert.AreEqual(
                RunState.Failed, folded.State,
                "the journal's own fold reconstructs Failed");
            var snapshots = folded.Steps();
            Assert.AreEqual(1, snapshots.Count);
            Assert.AreEqual(StepState.Failed, snapshots[0].State);
            var reacquired = CoordinatorRunLock.TryAcquire(_root);
            Assert.IsTrue(
                reacquired.Ok && reacquired.Value is not null,
                "the exclusion is released for a following Run");
            reacquired.Value!.Dispose();
        }

        // ── SPEC §F1: a journaled RunFinished whose lock release failed
        // keeps the journaled terminal state in memory and retries the
        // release on later Ticks — never a second RunFinished ────────────

        [Test]
        public void FailedReleaseAfterJournaledEndRetriesOnLaterTicks()
        {
            var fixture = NewFixture();
            var waitable = new OperationContractFixtures.WaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.waitable", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps));

            // Tick 1: the Run starts — the exclusion is held and lock.json
            // recorded — and the step waits.
            Assert.IsTrue(fixture.Loop.Tick().Ok);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            // Occupying lock.json's path with a directory makes the
            // terminal Release's atomic replace fail on every platform —
            // but only after the RunFinished append already reached the
            // store (EndRun's release half).
            var lockJsonPath = Path.Combine(_root, CoordinatorRunLock.StateFileName);
            File.Delete(lockJsonPath);
            Directory.CreateDirectory(lockJsonPath);
            try
            {
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
                Assert.IsTrue(fixture.Loop.Tick().Ok);

                var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
                Assert.AreEqual(1, finished.Count);
                Assert.AreEqual(
                    "succeeded", (string?)DataOf(finished[0])["state"],
                    "the journaled terminal record is the step's real outcome");
                Assert.AreEqual(
                    RunState.Succeeded, fixture.Coordinator.StateOf(runId),
                    "the in-memory state follows the journal — never a fabricated Failed");
                Assert.IsFalse(
                    CoordinatorRunLock.TryAcquire(_root).Ok,
                    "the failed release keeps the exclusion held");

                // Still blocked: a later Tick retries the release and
                // fails again — journaling nothing further.
                Assert.IsTrue(fixture.Loop.Tick().Ok);
                Assert.AreEqual(
                    1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                    "no second run_finished is written while the release pends");
                Assert.IsFalse(
                    CoordinatorRunLock.TryAcquire(_root).Ok,
                    "a still-failed retry keeps the exclusion held");
            }
            finally
            {
                Directory.Delete(lockJsonPath);
            }

            // The path is free: the next Tick's retry releases it.
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(
                1, RecordsOfType(fixture, JournalRecordType.RunFinished).Count,
                "the terminal record is never written twice");
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(
                RunState.Succeeded, FoldJournal(fixture).State,
                "journal and in-memory state agree on the terminal state");
            var reacquired = CoordinatorRunLock.TryAcquire(_root);
            Assert.IsTrue(
                reacquired.Ok && reacquired.Value is not null,
                "the retried release freed the exclusion");
            reacquired.Value!.Dispose();
        }

        // ── SPEC §F6: credential keys and URL userinfo are masked before
        // anything is persisted ─────────────────────────────────────────────

        [Test]
        public void LogRecordMasksCredentialKeysAndUrlUserinfo()
        {
            var fixture = NewFixture();
            var logging = new LoggingOperation(
                "fetching https://user:pass@git.example/org/repo.git now",
                Json("{\"password\":\"p4ss\",\"nested\":{\"client_secret\":\"s3cr\",\"apiKey\":\"k3y\"}," +
                     "\"list\":[{\"authorization\":\"Bearer abc\"},\"plain\"]," +
                     "\"repo\":\"https://user:pass@git.example/org/repo.git?access_token=t0k&path=sub\"}"));
            fixture.Register(logging, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.logging", 'd'),
            };
            AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var logs = RecordsOfType(fixture, JournalRecordType.Log);
            Assert.AreEqual(1, logs.Count);
            var data = DataOf(logs[0]);
            Assert.AreEqual("warning", (string?)data["level"]);
            Assert.AreEqual(
                "fetching https://***@git.example/org/repo.git now",
                (string?)data["message"],
                "the message's URL userinfo is masked before persisting");
            var payload = (JObject?)data["data"];
            Assert.IsNotNull(payload);
            Assert.AreEqual("***", (string?)payload!["password"]);
            Assert.AreEqual("***", (string?)payload!["nested"]?["client_secret"]);
            Assert.AreEqual(
                "***", (string?)payload!["nested"]?["apiKey"],
                "the camelCase form of a credential key is masked too");
            Assert.AreEqual("***", (string?)payload!["list"]?[0]?["authorization"]);
            Assert.AreEqual(
                "plain", (string?)payload!["list"]?[1],
                "non-credential content is preserved");
            Assert.AreEqual(
                "https://***@git.example/org/repo.git?access_token=***&path=sub",
                (string?)payload!["repo"],
                "userinfo and credential query parameters are masked; path is kept");
            // Nothing the sink persisted still carries the original secrets.
            var text = logs[0].Data.CanonicalText;
            StringAssert.DoesNotContain("p4ss", text);
            StringAssert.DoesNotContain("s3cr", text);
            StringAssert.DoesNotContain("k3y", text);
            StringAssert.DoesNotContain("Bearer", text);
            StringAssert.DoesNotContain("user:pass", text);
            StringAssert.DoesNotContain("t0k", text);
        }

        // ── SPEC §F6: CredentialMask itself — key forms, URL forms ─────────

        [Test]
        public void CredentialMaskCoversKeyAndUrlForms()
        {
            // Every listed key form — case variants and snake/kebab/camel —
            // collapses to "***"; harmless keys pass through.
            var obj = (JObject)CredentialMask.Mask(Json(
                "{\"Password\":\"a\",\"TOKEN\":\"b\",\"x-secret\":\"c\",\"authorization\":\"d\"," +
                "\"access_token\":\"e\",\"api_key\":\"f\",\"apikey\":\"g\",\"clientSecret\":\"h\"," +
                "\"private-key\":\"i\",\"credentials\":\"j\",\"note\":\"keep\"}")).ToToken();
            foreach (var property in obj.Properties())
            {
                Assert.AreEqual(
                    property.Name == "note" ? "keep" : "***",
                    (string?)property.Value,
                    $"{property.Name} must be {(property.Name == "note" ? "kept" : "masked")}");
            }

            Assert.AreEqual(
                "https://***@host/org/repo.git",
                CredentialMask.MaskText("https://user:pass@host/org/repo.git"));
            Assert.AreEqual(
                "https://***@host/org/repo.git",
                CredentialMask.MaskText("https://token@host/org/repo.git"),
                "a bare-token userinfo is masked too");
            Assert.AreEqual(
                "no url here",
                CredentialMask.MaskText("no url here"),
                "text without a scheme URL is unchanged");
            Assert.AreEqual(
                "https://host/org/repo.git",
                CredentialMask.MaskText("https://host/org/repo.git"),
                "a credential-free URL is unchanged");
        }

        // ── SPEC §F6: the mask is scheme- and marker-agnostic — userinfo on
        //    any RFC 3986 scheme and credential-named parameters behind '?',
        //    '&', ';' and '#' alike (T-W2.12-03 round-5 correction) ─────────

        // Every scheme form loses its userinfo, every marker form loses a
        // credential-named value, and credential-free text — a bare
        // authority, an SCP shape, a non-credential fragment or parameter —
        // is returned unchanged.
        [TestCase("ftp://user:pass@host/x", "ftp://***@host/x")]
        [TestCase("FTP://User:Pass@host/x", "FTP://***@host/x")]
        [TestCase("custom+scheme://t0k@host/x", "custom+scheme://***@host/x")]
        [TestCase("a.b-c://u:p@h/", "a.b-c://***@h/")]
        [TestCase("v9://t0k@h/x", "v9://***@h/x")]
        [TestCase("x?token=v", "x?token=***")]
        [TestCase("u?a=1&secret=v", "u?a=1&secret=***")]
        [TestCase("u?a=1;pwd=v", "u?a=1;pwd=***")]
        [TestCase("x#token=v", "x#token=***")]
        [TestCase("x#SIG=v", "x#SIG=***")]
        [TestCase("x#client-secret=v", "x#client-secret=***")]
        [TestCase("x#api.key=v", "x#api.key=***")]
        [TestCase("u?a=1#access_token=v", "u?a=1#access_token=***")]
        [TestCase("https://h/t#access_token=v", "https://h/t#access_token=***")]
        [TestCase("https://u:p@h/t#token=v", "https://***@h/t#token=***")]
        [TestCase("u?next=https://a:b@h/x#key=v", "u?next=https://***@h/x#key=***")]
        [TestCase("https://host/r.git", "https://host/r.git")]
        [TestCase("ftp://host/x", "ftp://host/x")]
        [TestCase("custom+scheme://host/x", "custom+scheme://host/x")]
        [TestCase("https://host/r.git#v1.2.3", "https://host/r.git#v1.2.3")]
        [TestCase("x#path=/sub", "x#path=/sub")]
        [TestCase("x#count=3", "x#count=3")]
        [TestCase("a#b=c", "a#b=c")]
        [TestCase("# token=v", "# token=v")]
        [TestCase("git@host:org/repo.git", "git@host:org/repo.git")]
        [TestCase("user:pass@host:org/repo.git", "user:pass@host:org/repo.git")]
        public void CredentialMaskCoversEverySchemeAndMarker(
            string text, string expected) =>
            Assert.AreEqual(expected, CredentialMask.MaskText(text));

        // ── SPEC §F6: every '<scheme>://' start is examined — a consumed
        // span must never hide a later credential ────────────────────────────

        [Test]
        public void MaskTextExaminesEveryUrlStartInAToken()
        {
            Assert.AreEqual(
                "https://first.example/repo,https://***@second.example/repo",
                CredentialMask.MaskText(
                    "https://first.example/repo,https://user:pass@second.example/repo"),
                "a comma-separated second URL is masked too");
            Assert.AreEqual(
                "see (https://first.example/x(https://***@second.example/y))",
                CredentialMask.MaskText(
                    "see (https://first.example/x(https://user:pass@second.example/y))"),
                "a second URL after '(' is masked too");
            Assert.AreEqual(
                "\"https://first.example/x\",\"https://***@second.example/y\"",
                CredentialMask.MaskText(
                    "\"https://first.example/x\",\"https://user:pass@second.example/y\""),
                "a second URL after a quote is masked too");
            Assert.AreEqual(
                "{\"a\":\"https://***@first.example/x\",\"b\":\"https://***@second.example/y\"}",
                CredentialMask.MaskText(
                    "{\"a\":\"https://user:pass@first.example/x\"," +
                    "\"b\":\"https://user:pass@second.example/y\"}"),
                "URLs adjacent inside JSON string values are both masked");
            Assert.AreEqual(
                "https://***@first.example/x,https://***@second.example/y",
                CredentialMask.MaskText(
                    "https://user:pass@first.example/x,https://user:pass@second.example/y"),
                "both userinfos collapse when both URLs carry credentials");
            Assert.AreEqual(
                "ssh://***@git.example/one.git,git://***@git.example/two.git",
                CredentialMask.MaskText(
                    "ssh://user:pass@git.example/one.git,git://user:pass@git.example/two.git"),
                "the ssh/git scheme forms are examined the same way");
            Assert.AreEqual(
                "https://host/p?next=https://***@other.example/x",
                CredentialMask.MaskText(
                    "https://host/p?next=https://user:pass@other.example/x"),
                "a URL nested in a query value is masked too");
            Assert.AreEqual(
                "https://first.example/+https://***@second.example/repo",
                CredentialMask.MaskText(
                    "https://first.example/+https://user:pass@second.example/repo"),
                "a '+' before the scheme does not hide the userinfo");
            Assert.AreEqual(
                "https://first.example/-https://***@second.example/repo",
                CredentialMask.MaskText(
                    "https://first.example/-https://user:pass@second.example/repo"),
                "a '-' before the scheme does not hide the userinfo");
            Assert.AreEqual(
                "prefix://https://***@after.example/repo",
                CredentialMask.MaskText(
                    "prefix://https://user:pass@after.example/repo"),
                "a URL directly after '://' text is masked");
            Assert.AreEqual(
                "https://first.examplehttps://***@second.example/repo",
                CredentialMask.MaskText(
                    "https://first.examplehttps://user:pass@second.example/repo"),
                "a URL abutting another without a separator is masked");
        }

        // ── SPEC §F6: the same URL-start forms are masked in the durable
        // journal — nothing appended keeps the credential ───────────────────

        [Test]
        public void MaskedUrlFormsPersistWithoutCredentials()
        {
            var fixture = NewFixture();
            var logging = new LoggingOperation(
                "comma https://user:pass@one.example/a,https://user:pass@two.example/b " +
                "paren (https://user:pass@three.example/c) " +
                "quote \"https://user:pass@four.example/d\"",
                Json("{\"plus\":\"https://first.example/+https://user:pass@plus.example/repo\"," +
                     "\"minus\":\"https://first.example/-https://user:pass@minus.example/repo\"," +
                     "\"afterScheme\":\"prefix://https://user:pass@after.example/repo\"," +
                     "\"adjacent\":\"https://first.examplehttps://user:pass@second.example/repo\"}"));
            fixture.Register(logging, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.logging", 'd'),
            };
            AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var logs = RecordsOfType(fixture, JournalRecordType.Log);
            Assert.AreEqual(1, logs.Count);
            var data = DataOf(logs[0]);
            Assert.AreEqual(
                "comma https://***@one.example/a,https://***@two.example/b " +
                "paren (https://***@three.example/c) " +
                "quote \"https://***@four.example/d\"",
                (string?)data["message"],
                "comma-, paren- and quote-adjacent userinfos are masked before persisting");
            var payload = (JObject?)data["data"];
            Assert.IsNotNull(payload);
            Assert.AreEqual(
                "https://first.example/+https://***@plus.example/repo",
                (string?)payload!["plus"],
                "a '+' before the scheme does not hide the userinfo");
            Assert.AreEqual(
                "https://first.example/-https://***@minus.example/repo",
                (string?)payload!["minus"],
                "a '-' before the scheme does not hide the userinfo");
            Assert.AreEqual(
                "prefix://https://***@after.example/repo",
                (string?)payload!["afterScheme"],
                "a URL directly after '://' text is masked before persisting");
            Assert.AreEqual(
                "https://first.examplehttps://***@second.example/repo",
                (string?)payload!["adjacent"],
                "a URL abutting another without a separator is masked");
            // No durable record keeps the credential anywhere.
            foreach (var record in fixture.Store.Appended)
            {
                StringAssert.DoesNotContain("user:pass", record.Data.CanonicalText);
            }
        }

        // ── SPEC §F6: the query scan matches credential keys only, so a
        // non-credential parameter's value can never consume and hide a
        // later credential — nested URLs, credentials after plain
        // parameters and multiple credentials all persist masked ──────────

        [Test]
        public void CredentialQueryParametersMaskWhereverTheyAppear()
        {
            var fixture = NewFixture();
            var logging = new LoggingOperation(
                "redirect https://outer.example/?next=https://inner.example/?token=s3cr",
                Json("{\"after\":\"https://h.example/p?a=1&token=t0k\"," +
                     "\"pair\":\"https://h.example/p?access_token=a1&client_secret=c2\"}"));
            fixture.Register(logging, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.logging", 'd'),
            };
            AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var logs = RecordsOfType(fixture, JournalRecordType.Log);
            Assert.AreEqual(1, logs.Count);
            var data = DataOf(logs[0]);
            Assert.AreEqual(
                "redirect https://outer.example/?next=https://inner.example/?token=***",
                (string?)data["message"],
                "a credential nested inside a non-credential parameter's value is masked");
            var payload = (JObject?)data["data"];
            Assert.IsNotNull(payload);
            Assert.AreEqual(
                "https://h.example/p?a=1&token=***",
                (string?)payload!["after"],
                "a credential parameter after a non-credential one is masked");
            Assert.AreEqual(
                "https://h.example/p?access_token=***&client_secret=***",
                (string?)payload!["pair"],
                "two credential parameters in one URL are both masked");
            // No durable record keeps any of the credential values.
            foreach (var record in fixture.Store.Appended)
            {
                StringAssert.DoesNotContain("s3cr", record.Data.CanonicalText);
                StringAssert.DoesNotContain("t0k", record.Data.CanonicalText);
                StringAssert.DoesNotContain("=a1", record.Data.CanonicalText);
                StringAssert.DoesNotContain("=c2", record.Data.CanonicalText);
            }
        }

        // ── SPEC §F6: StepFailed and the RunFinished summary mask before
        // persisting ────────────────────────────────────────────────────────

        [Test]
        public void StepFailureAndRunSummaryMaskUrlCredentials()
        {
            var fixture = NewFixture();
            var failing = new FailingOperation(ErrorCodes.Create(
                ErrorCodes.OW_RUN_006,
                "Clone failed for https://user:pass@git.example/org/repo.git.",
                nodeId: "n1"));
            fixture.Register(failing, 'd');
            var steps = new[] { Step(0, "n1", "test.dummy.failing", 'd') };
            var runId = AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var failed = RecordsOfType(fixture, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.AreEqual("OW-RUN-006", (string?)DataOf(failed[0])["error"]);
            Assert.AreEqual(
                "Clone failed for https://***@git.example/org/repo.git.",
                (string?)DataOf(failed[0])["message"],
                "the StepFailed message is masked before persisting");
            var finished = RecordsOfType(fixture, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual(
                "Clone failed for https://***@git.example/org/repo.git.",
                (string?)DataOf(finished[0])["summary"]?["message"],
                "the RunFinished summary message is masked before persisting");
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
            // No durable record keeps the credential anywhere.
            foreach (var record in fixture.Store.Appended)
            {
                StringAssert.DoesNotContain("user:pass", record.Data.CanonicalText);
                StringAssert.DoesNotContain("pass@", record.Data.CanonicalText);
            }
        }

        // ── SPEC §F6: StepWaiting waitingFor and the StepProgress message
        // mask before persisting ────────────────────────────────────────────

        [Test]
        public void WaitingLabelsMaskUrlCredentials()
        {
            var fixture = NewFixture();
            var waitable = new UrlWaitableOperation();
            fixture.Register(waitable, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.url_waitable", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            AcceptRun(fixture, Plan(steps));

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            var waiting = RecordsOfType(fixture, JournalRecordType.StepWaiting);
            Assert.AreEqual(1, waiting.Count);
            Assert.AreEqual(
                "cloning https://***@git.example/one.git",
                (string?)DataOf(waiting[0])["waitingFor"],
                "the journaled waitingFor is masked before persisting");

            // The changed label past the 5 s interval emits a StepProgress —
            // masked the same way (SPEC §F6).
            fixture.Clock.Advance(TimeSpan.FromSeconds(6));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            waiting = RecordsOfType(fixture, JournalRecordType.StepWaiting);
            Assert.AreEqual(2, waiting.Count);
            Assert.AreEqual(
                "fetching https://***@git.example/two.git",
                (string?)DataOf(waiting[1])["waitingFor"]);
            var progress = RecordsOfType(fixture, JournalRecordType.StepProgress);
            Assert.AreEqual(1, progress.Count);
            Assert.AreEqual(
                "fetching https://***@git.example/two.git",
                (string?)DataOf(progress[0])["message"],
                "the StepProgress message is masked before persisting");
            // No durable record keeps the credential anywhere.
            foreach (var record in fixture.Store.Appended)
            {
                StringAssert.DoesNotContain("user:pass", record.Data.CanonicalText);
                StringAssert.DoesNotContain("pass@", record.Data.CanonicalText);
            }
        }
    }
}
