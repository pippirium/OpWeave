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
    /// [Core] tests for the SPEC §E8 run-event notification and the SPEC §F6
    /// Core log entries (local:T-W1.8-07:run-events): every
    /// <see cref="IRunEvents"/> event is delivered synchronously on the
    /// main thread after the journal record it reports exists; a throwing
    /// subscriber is logged and ignored while the Run still reaches
    /// Succeeded; the single T-W1.5-03 <see cref="RunLogEntry"/> /
    /// <see cref="IRunLogSink"/> contract carries both the journaled
    /// <c>log</c> record and the <see cref="IRunEvents.LogAppended"/>
    /// event; and the injected <c>debugLogging</c> policy (SG-08) drops
    /// debug-level records before the Core log store while leaving info+
    /// unchanged. Fixtures use dummy operations, a fake clock, an
    /// in-memory store and a real CoordinatorRunLock over a temp root. The
    /// same sources run under dotnet and Unity Mono.
    /// </summary>
    internal sealed class RunEventTests
    {
        private const string Session = "session-run-event-test";
        private const string WorkflowId = "example.runevents";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RunEvents." + Guid.NewGuid().ToString("N"));
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
            private DateTimeOffset _now =
                new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

            public DateTimeOffset UtcNow => _now;

            public void Advance(TimeSpan delta) => _now += delta;
        }

        private sealed class FakeMonotonicTimer : IMonotonicTimer
        {
            private TimeSpan _elapsed;

            internal FakeMonotonicTimer(int advancePerReadMs) =>
                _advancePerReadMs = advancePerReadMs;

            private readonly int _advancePerReadMs;

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

            public Result<Unit> CreateRun(string runId, CompiledPlan plan, WorkflowDocument doc) =>
                Result<Unit>.Success(Unit.Value);

            internal Result<Unit> CreateRun(
                string runId, CompiledPlan plan, WorkflowDocument document, ActorKind actor)
            {
                // Mirror FileRunStore: the create journals run_accepted at
                // seq 1 — the record the RunStateChanged(Queued) event must
                // already find written.
                Appended.Add(new JournalRecord(
                    1, "accepted-ts", Session, JournalRecordType.RunAccepted,
                    null, null,
                    JsonValue.From(new JObject { ["approvals"] = new JArray() })));
                AppendedDurability.Add(Durability.Flushed);
                return Result<Unit>.Success(Unit.Value);
            }

            public Result<Unit> Append(string runId, JournalRecord record, Durability durability)
            {
                Appended.Add(record);
                AppendedDurability.Add(durability);
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

        private sealed class AlwaysSatisfiedRequirements : IRequirementEvaluator
        {
            public RequirementEvaluation Evaluate(Requirement requirement, StepContext context) =>
                RequirementEvaluation.Satisfied();
        }

        private sealed class FakeUndo : IDisposable
        {
            internal int Closed;

            public void Dispose() => Closed++;
        }

        /// <summary>
        /// Records one event delivery: the payload summary, the journal
        /// record types already written at that moment and the delivering
        /// thread — the SPEC §E8 ordering evidence (after the journal
        /// write, on the main thread).
        /// </summary>
        private sealed class EventCapture
        {
            private readonly FakeRunStore _store;

            internal readonly List<string> Entries = new List<string>();
            internal readonly List<JournalRecordType[]> JournalAtEvent =
                new List<JournalRecordType[]>();
            internal readonly List<int> ThreadIds = new List<int>();

            internal EventCapture(FakeRunStore store) => _store = store;

            internal void Record(string entry)
            {
                Entries.Add(entry);
                var types = new JournalRecordType[_store.Appended.Count];
                for (var i = 0; i < _store.Appended.Count; i++)
                {
                    types[i] = _store.Appended[i].Type;
                }
                JournalAtEvent.Add(types);
                ThreadIds.Add(System.Threading.Thread.CurrentThread.ManagedThreadId);
            }
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
            internal readonly FakeUndo Undo = new FakeUndo();
            internal Func<bool>? DebugLogging;
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
                Session,
                debugLogging: DebugLogging);

            internal RunLoop Loop => _loop ??= new RunLoop(
                Coordinator,
                Clock,
                Editor,
                DescriptorTestFixtures.CreateRegistry(),
                Snapshot(),
                Resolve,
                new AlwaysSatisfiedRequirements(),
                _ => Undo,
                () => false,
                monotonicTimerFactory: () => new FakeMonotonicTimer(0));

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

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        // ── Dummy operations ────────────────────────────────────────────

        /// <summary>An Immediate marker: sets its <c>mark</c> output.</summary>
        private sealed class MarkerOperation : IOperation
        {
            internal MarkerOperation(string id) =>
                Descriptor = Op.Define(id, 1)
                    .Summary("Marks execution.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("mark", "done:" + Descriptor.Id);
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

        /// <summary>
        /// An Immediate that writes one <see cref="IRunLogSink"/> entry at
        /// the configured level and — through the internal Runner debug
        /// channel — one debug entry, then succeeds.
        /// </summary>
        private sealed class LogWritingOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.logwriter", 1)
                .Summary("Writes log entries.")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                ctx.Log.Write(
                    Severity.Warning, "a warning from the operation",
                    code: "OW-RUN-004", data: Json("{\"k\":1}"));
                ctx.Log.Write(Severity.Info, "an info line from the operation");
                ((StepLogSink)ctx.Log).WriteDebug("debug detail");
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

        /// <summary>A Waitable that waits once, then succeeds on Poll.</summary>
        private sealed class WaitOnceOperation : IOperation
        {
            private bool _polled;

            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.wait_once", 1)
                .Summary("Waits once.")
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
                if (_polled)
                {
                    return StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_RUN_006, "Poll must run once."));
                }
                _polled = true;
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// An Immediate that fails its first attempt with a retryable
        /// diagnostic, then succeeds — the step_failed{retry} event case.
        /// </summary>
        private sealed class FlakyOperation : IOperation
        {
            private int _executes;

            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.dummy.flaky", 1)
                .Summary("Fails once, then succeeds.")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                _executes++;
                return _executes == 1
                    ? StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true))
                    : StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // ── Plan/doc builders ───────────────────────────────────────────

        private static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            IReadOnlyDictionary<string, int>? transitions = null,
            int timeoutSeconds = 30,
            RetryPolicy? retry = null,
            Idempotency idempotency = Idempotency.Idempotent) =>
            new PlanStep(
                index, nodeId, operationId, 1,
                new string(hashSeed, 64), new string((char)(hashSeed + 1), 64),
                inputs ?? new Dictionary<string, InputSource>(),
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

        private static string AcceptRun(Fixture fixture, CompiledPlan plan, string[] nodeIds)
        {
            var accepted = fixture.Coordinator.Accept(
                plan, Doc(nodeIds), ActorKind.Agent, Options());
            Assert.IsTrue(accepted.Ok, "acceptance failed");
            return accepted.Value!.RunId;
        }

        private static List<JournalRecord> RecordsOfType(
            Fixture fixture, JournalRecordType type) =>
            fixture.Store.Appended.FindAll(record => record.Type == type);

        private static JObject DataOf(JournalRecord record) =>
            (JObject)record.Data.ToToken();

        private static string LogLevel(JournalRecord record) =>
            (string?)DataOf(record)["level"] ?? string.Empty;

        private static List<JournalRecord> LogRecords(Fixture fixture, string level) =>
            RecordsOfType(fixture, JournalRecordType.Log)
                .FindAll(record => LogLevel(record) == level);

        private static EventCapture Subscribe(Fixture fixture)
        {
            var capture = new EventCapture(fixture.Store);
            var events = fixture.Coordinator.Events;
            events.RunStateChanged += change =>
                capture.Record("run:" + change.State);
            events.StepStateChanged += change =>
                capture.Record(
                    $"step:{change.Step}:{change.State}:{change.Attempt?.ToString() ?? "-"}");
            events.LogAppended += entry =>
                capture.Record($"log:{entry.Level}:{entry.Step?.ToString() ?? "-"}");
            events.RegistryChanged += changes =>
                capture.Record("registry:" + changes.Count);
            return capture;
        }

        // ── SPEC §E8: events arrive after their journal records ─────────

        [Test]
        public void StateEventsArriveAfterTheirJournalRecords()
        {
            var fixture = NewFixture();
            var capture = Subscribe(fixture);
            var m1 = new MarkerOperation("test.dummy.m1");
            var m2 = new MarkerOperation("test.dummy.m2");
            fixture.Register(m1, 'd');
            fixture.Register(m2, 'j');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.dummy.m2", 'j',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps), new[] { "n1", "n2" });

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            // One event per journaled state change, in journal order:
            // queued on accept, running on start, per step running then
            // succeeded, then the terminal state.
            var expected = new[]
            {
                "run:Queued",
                "run:Running",
                "step:0:Running:1", "step:0:Succeeded:1",
                "step:1:Running:1", "step:1:Succeeded:1",
                "run:Succeeded",
            };
            CollectionAssert.AreEqual(expected, capture.Entries);
            // The corresponding journal record was already durable at each
            // delivery (SPEC §E8): the capture records the journal view at
            // event time.
            var expectedRecords = new[]
            {
                JournalRecordType.RunAccepted,
                JournalRecordType.RunStarted,
                JournalRecordType.StepStarted, JournalRecordType.StepSucceeded,
                JournalRecordType.StepStarted, JournalRecordType.StepSucceeded,
                JournalRecordType.RunFinished,
            };
            for (var i = 0; i < capture.JournalAtEvent.Count; i++)
            {
                CollectionAssert.Contains(
                    capture.JournalAtEvent[i], expectedRecords[i],
                    $"event {i} ({capture.Entries[i]}) arrived before its journal record");
            }
            // Every delivery ran on the calling (main) thread — SPEC §E8's
            // synchronous delivery.
            var mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            for (var i = 0; i < capture.ThreadIds.Count; i++)
            {
                Assert.AreEqual(mainThread, capture.ThreadIds[i]);
            }
        }

        [Test]
        public void WaitingStepReportsWaitingAfterStepWaitingRecord()
        {
            var fixture = NewFixture();
            var capture = Subscribe(fixture);
            var wait = new WaitOnceOperation();
            fixture.Register(wait, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.wait_once", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(fixture, Plan(steps), new[] { "n1" });

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));
            CollectionAssert.Contains(capture.Entries, "step:0:Waiting:1");
            var waitingIndex = capture.Entries.IndexOf("step:0:Waiting:1");
            CollectionAssert.Contains(
                capture.JournalAtEvent[waitingIndex], JournalRecordType.StepWaiting,
                "the Waiting event arrived before its step_waiting record");

            fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            CollectionAssert.Contains(capture.Entries, "step:0:Succeeded:1");
        }

        [Test]
        public void RetryingStepReportsRunningWithPendingAttempt()
        {
            var fixture = NewFixture();
            var capture = Subscribe(fixture);
            var flaky = new FlakyOperation();
            fixture.Register(flaky, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.flaky", 'd',
                    retry: new RetryPolicy(MaxAttempts: 2, InitialBackoffMs: 10)),
            };
            var runId = AcceptRun(fixture, Plan(steps), new[] { "n1" });

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            // The failed first attempt is journaled step_failed{retry:
            // {nextAttempt: 2}} and the event reports the §F2 fold's state:
            // still Running, at the pending attempt — never Failed.
            CollectionAssert.Contains(capture.Entries, "step:0:Running:2");
            var retryIndex = capture.Entries.IndexOf("step:0:Running:2");
            CollectionAssert.Contains(
                capture.JournalAtEvent[retryIndex], JournalRecordType.StepFailed,
                "the retry event arrived before its step_failed record");
            CollectionAssert.DoesNotContain(capture.Entries, "step:0:Failed:1");

            fixture.Clock.Advance(TimeSpan.FromMilliseconds(20));
            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            CollectionAssert.Contains(capture.Entries, "step:0:Succeeded:2");
        }

        // ── SPEC §E8: the event arrives after the in-memory transition ──

        [Test]
        public void RunStateChangedSeesTheNewInMemoryState()
        {
            var fixture = NewFixture();
            var coordinator = fixture.Coordinator;
            var seen = new List<(RunState Published, RunState? InMemory)>();
            coordinator.Events.RunStateChanged += change =>
                seen.Add((change.State, coordinator.StateOf(change.RunId)));
            var marker = new MarkerOperation("test.dummy.m1");
            fixture.Register(marker, 'd');
            var runId = AcceptRun(
                fixture, Plan(new[] { Step(0, "n1", "test.dummy.m1", 'd') }),
                new[] { "n1" });

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Succeeded, coordinator.StateOf(runId));
            var published = new List<RunState>();
            foreach (var entry in seen)
            {
                published.Add(entry.Published);
                Assert.AreEqual(entry.Published, entry.InMemory,
                    $"StateOf inside the {entry.Published} callback still showed the previous state");
            }
            CollectionAssert.AreEqual(
                new[] { RunState.Queued, RunState.Running, RunState.Succeeded },
                published);
        }

        [Test]
        public void RunStateChangedSeesFailedAndCancelledInMemory()
        {
            // Terminal Failed: the step's terminal failure ends the Run —
            // StateOf inside the run_finished dispatch already reads Failed.
            var failing = NewFixture();
            var failingCoordinator = failing.Coordinator;
            var failingSeen = new List<(RunState Published, RunState? InMemory)>();
            failingCoordinator.Events.RunStateChanged += change =>
                failingSeen.Add((change.State, failingCoordinator.StateOf(change.RunId)));
            var flaky = new FlakyOperation();
            failing.Register(flaky, 'd');
            var failedRun = AcceptRun(
                failing,
                Plan(new[] { Step(0, "n1", "test.dummy.flaky", 'd',
                    retry: new RetryPolicy(MaxAttempts: 1)) }),
                new[] { "n1" });

            Assert.IsTrue(failing.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Failed, failingCoordinator.StateOf(failedRun));
            var failingPublished = new List<RunState>();
            foreach (var entry in failingSeen)
            {
                failingPublished.Add(entry.Published);
                Assert.AreEqual(entry.Published, entry.InMemory,
                    $"StateOf inside the {entry.Published} callback still showed the previous state");
            }
            CollectionAssert.Contains(failingPublished, RunState.Failed);

            // Terminal Cancelled: a Queued Run's pending cancel is
            // processed on the Tick — the run_finished{cancelled} dispatch
            // already reads Cancelled.
            var cancelling = NewFixture();
            var cancelCoordinator = cancelling.Coordinator;
            var cancelSeen = new List<(RunState Published, RunState? InMemory)>();
            cancelCoordinator.Events.RunStateChanged += change =>
                cancelSeen.Add((change.State, cancelCoordinator.StateOf(change.RunId)));
            var cancelledRun = AcceptRun(
                cancelling,
                Plan(new[] { Step(0, "n1", "test.dummy.m1", 'd') }),
                new[] { "n1" });

            Assert.IsTrue(cancelCoordinator.Cancel(cancelledRun, "stop requested").Ok);
            Assert.IsTrue(cancelling.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Cancelled, cancelCoordinator.StateOf(cancelledRun));
            var cancelPublished = new List<RunState>();
            foreach (var entry in cancelSeen)
            {
                cancelPublished.Add(entry.Published);
                Assert.AreEqual(entry.Published, entry.InMemory,
                    $"StateOf inside the {entry.Published} callback still showed the previous state");
            }
            CollectionAssert.Contains(cancelPublished, RunState.Cancelled);
        }

        // ── SPEC §E8 test: a throwing subscriber never stops the Run ────

        [Test]
        public void ThrowingSubscriberIsLoggedAndRunCompletes()
        {
            var fixture = NewFixture();
            var events = fixture.Coordinator.Events;
            // A subscriber that throws on EVERY event — including the fault
            // record's own LogAppended: delivery must terminate anyway.
            events.RunStateChanged += _ => throw new InvalidOperationException("run fault");
            events.StepStateChanged += _ => throw new InvalidOperationException("step fault");
            events.LogAppended += _ => throw new InvalidOperationException("log fault");
            var stillReceives = Subscribe(fixture);
            var m1 = new MarkerOperation("test.dummy.m1");
            fixture.Register(m1, 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.dummy.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(fixture, Plan(steps), new[] { "n1" });

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            // SPEC §E8 test: the Run completed despite the throwing
            // subscriber — and a later subscriber still received every
            // event (per-handler isolation).
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            CollectionAssert.Contains(stillReceives.Entries, "run:Succeeded");
            // Each throwing dispatch produced one warning fault record —
            // and each fault record's own LogAppended (also thrown on,
            // suppressed) reached the quiet subscriber.
            var stateEntries = stillReceives.Entries.FindAll(
                e => !e.StartsWith("log:", StringComparison.Ordinal));
            CollectionAssert.AreEqual(
                new[]
                {
                    "run:Queued", "run:Running",
                    "step:0:Running:1", "step:0:Succeeded:1",
                    "run:Succeeded",
                },
                stateEntries);
            Assert.AreEqual(
                5, stillReceives.Entries.FindAll(e => e == "log:Warning:-").Count,
                "one fault record per throwing dispatch");
            // The exceptions were journaled as warning log records on the
            // Run — logged and ignored (SPEC §E8). A subscriber that also
            // throws on the fault record produces no unbounded recursion:
            // the Run ended.
            var faults = LogRecords(fixture, "warning").FindAll(record =>
                (string?)DataOf(record)["message"] ==
                "A run event subscriber threw an exception.");
            Assert.AreEqual(5, faults.Count, "one fault record per throwing dispatch");
            Assert.AreEqual(
                "System.InvalidOperationException",
                (string?)DataOf(faults[0])["data"]?["exception"]);
            // Fault records are ordinary log records: required:false,
            // Buffered per the SPEC §F2 table.
            foreach (var fault in faults)
            {
                Assert.IsFalse(fault.Required);
                Assert.AreEqual(Durability.Buffered, fault.Durability);
            }
        }

        // ── SPEC §F6: the single RunLogEntry / IRunLogSink contract ─────

        [Test]
        public void LogRecordAndLogAppendedShareTheSingleRunLogEntryModel()
        {
            // The one SPEC §F6 contract: ctx.Log speaks IRunLogSink and the
            // event carries the one Core RunLogEntry — declared once by
            // T-W1.5-03, reused here, never redeclared.
            Assert.AreEqual(
                typeof(Action<RunLogEntry>),
                typeof(IRunEvents).GetEvent(nameof(IRunEvents.LogAppended))!.EventHandlerType);
            Assert.AreEqual(
                typeof(IRunLogSink),
                typeof(StepContext).GetProperty(nameof(StepContext.Log))!.PropertyType);

            var fixture = NewFixture();
            var capture = Subscribe(fixture);
            var writer = new LogWritingOperation();
            fixture.Register(writer, 'd');
            var steps = new[] { Step(0, "n1", "test.dummy.logwriter", 'd') };
            var runId = AcceptRun(fixture, Plan(steps), new[] { "n1" });
            var appendedBeforeTick = fixture.Store.Appended.Count;

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            // The ctx.Log writes became journal log records (required:false,
            // Buffered): warning with code+data and info — the debug write
            // was dropped by the omitted debug policy (see the SG-08 tests).
            var logs = RecordsOfType(fixture, JournalRecordType.Log).FindAll(
                record => record.Step == 0);
            Assert.AreEqual(2, logs.Count, "info+ only — debug is gated by the default policy");
            var warning = logs[0];
            Assert.AreEqual("warning", LogLevel(warning));
            Assert.AreEqual("a warning from the operation", (string?)DataOf(warning)["message"]);
            Assert.AreEqual("OW-RUN-004", (string?)DataOf(warning)["code"]);
            Assert.AreEqual(1, (int?)DataOf(warning)["data"]?["k"]);
            Assert.AreEqual(Durability.Buffered, warning.Durability);
            Assert.IsFalse(warning.Required);
            // …and the same entry reached LogAppended as a RunLogEntry,
            // after the record existed (event i is the LogAppended event).
            var logEvents = capture.Entries.FindAll(e => e.StartsWith("log:", StringComparison.Ordinal));
            Assert.AreEqual(2, logEvents.Count);
            CollectionAssert.Contains(capture.Entries, "log:Warning:0");
            CollectionAssert.Contains(capture.Entries, "log:Info:0");
            for (var i = 0; i < capture.Entries.Count; i++)
            {
                if (capture.Entries[i].StartsWith("log:", StringComparison.Ordinal))
                {
                    CollectionAssert.Contains(
                        capture.JournalAtEvent[i], JournalRecordType.Log,
                        "LogAppended arrived before its log record");
                }
            }
            Assert.AreEqual(appendedBeforeTick + 6, fixture.Store.Appended.Count,
                "step_started + 2 logs + step_succeeded + run_started + run_finished after accept");
        }

        // ── SPEC §F6 SG-08: the debugLogging write-path gate ────────────

        [Test]
        public void DebugPolicyFalseOrOmittedDropsDebugRecords()
        {
            foreach (var policy in new Func<bool>?[] { null, () => false })
            {
                var fixture = NewFixture();
                fixture.DebugLogging = policy;
                var writer = new LogWritingOperation();
                fixture.Register(writer, 'd');
                var runId = AcceptRun(
                    fixture, Plan(new[] { Step(0, "n1", "test.dummy.logwriter", 'd') }),
                    new[] { "n1" });

                Assert.IsTrue(fixture.Loop.Tick().Ok);

                // No debug-level record reached the Core log store; info
                // and warning are journaled unchanged (SPEC §F6 SG-08).
                Assert.AreEqual(0, LogRecords(fixture, "debug").Count,
                    $"policy {(policy is null ? "omitted" : "false")} must drop debug");
                Assert.AreEqual(1, LogRecords(fixture, "info").Count);
                Assert.AreEqual(1, LogRecords(fixture, "warning").Count);

                // The funnel-level gate drops a direct debug append the
                // same way — success without a record or an event.
                var before = fixture.Store.Appended.Count;
                var appended = fixture.Coordinator.AppendRecord(
                    runId, JournalRecordType.Log, 0, null,
                    JsonValue.From(new JObject
                    {
                        ["level"] = "debug",
                        ["message"] = "direct debug",
                    }));
                Assert.IsTrue(appended.Ok);
                Assert.AreEqual(before, fixture.Store.Appended.Count,
                    "a gated debug append spends no journal line");
            }
        }

        [Test]
        public void DebugPolicyTrueJournalsDebugAndLeavesInfoUnchanged()
        {
            var fixture = NewFixture();
            fixture.DebugLogging = () => true;
            var capture = Subscribe(fixture);
            var writer = new LogWritingOperation();
            fixture.Register(writer, 'd');
            var runId = AcceptRun(
                fixture, Plan(new[] { Step(0, "n1", "test.dummy.logwriter", 'd') }),
                new[] { "n1" });

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            // true permits debug — journaled required:false/Buffered like
            // every log record — while info+ are exactly as before.
            var debug = LogRecords(fixture, "debug");
            Assert.AreEqual(1, debug.Count);
            Assert.AreEqual("debug detail", (string?)DataOf(debug[0])["message"]);
            Assert.AreEqual(0, debug[0].Step);
            Assert.IsFalse(debug[0].Required);
            Assert.AreEqual(Durability.Buffered, debug[0].Durability);
            Assert.AreEqual(1, LogRecords(fixture, "info").Count);
            Assert.AreEqual(1, LogRecords(fixture, "warning").Count);
            // The §0.6 Severity set cannot carry debug — the journaled
            // record raises no LogAppended (RunLogEntry stays the single
            // model), while info/warning entries fire normally.
            CollectionAssert.DoesNotContain(capture.Entries, "log:Debug:0");
            CollectionAssert.Contains(capture.Entries, "log:Warning:0");
            CollectionAssert.Contains(capture.Entries, "log:Info:0");
        }

        // ── SPEC §E8: RegistryChanged rides the same mechanism ──────────

        [Test]
        public void RegistryChangedDispatchesAndFaultsAreIgnored()
        {
            var fixture = NewFixture();
            var events = fixture.Coordinator.Events;
            var received = new List<IReadOnlyList<RegistryChange>>();
            var faultEntries = new List<RunLogEntry>();
            events.RegistryChanged += _ => throw new InvalidOperationException("registry fault");
            events.RegistryChanged += changes => received.Add(changes);
            events.LogAppended += entry => faultEntries.Add(entry);
            var changes = new List<RegistryChange>
            {
                new RegistryChange(
                    RegistryChangeKinds.Added, "example.op", 1, null,
                    Array.Empty<Diagnostic>()),
            };

            // No Run exists — a runless subscriber fault has no journal to
            // record into, so SPEC §E8's "logged" half is the §F6 warning
            // entry itself, delivered through LogAppended (the runless
            // fallback); it is never thrown to the publisher and never
            // blocks the remaining subscribers.
            Assert.DoesNotThrow(() => events.PublishRegistryChanges(changes));

            Assert.AreEqual(1, received.Count);
            Assert.AreSame(changes, received[0]);
            Assert.AreEqual(0, fixture.Store.Appended.Count,
                "a runless fault journals nothing — there is no Run journal");
            Assert.AreEqual(1, faultEntries.Count,
                "the runless subscriber fault is logged through the fallback");
            var entry = faultEntries[0];
            Assert.AreEqual(Severity.Warning, entry.Level);
            Assert.AreEqual("A run event subscriber threw an exception.", entry.Message);
            Assert.IsNull(entry.Step);
            Assert.AreEqual(
                "System.InvalidOperationException",
                (string?)entry.Data!.ToToken()["exception"]);
        }

        [Test]
        public void RunlessSubscriberFaultIsRecordedWithoutLogAppendedListener()
        {
            var fixture = NewFixture();
            var events = fixture.Coordinator.Events;
            var received = new List<IReadOnlyList<RegistryChange>>();
            events.RegistryChanged += _ => throw new InvalidOperationException("registry fault");
            events.RegistryChanged += changes => received.Add(changes);
            // No LogAppended listener at all — the runless fault must still
            // be logged (SPEC §E8): the coordinator's bounded internal
            // record holds it before the publish.
            var changes = new List<RegistryChange>
            {
                new RegistryChange(
                    RegistryChangeKinds.Added, "example.op", 1, null,
                    Array.Empty<Diagnostic>()),
            };

            Assert.DoesNotThrow(() => events.PublishRegistryChanges(changes));

            Assert.AreEqual(1, received.Count, "the fault never blocks later subscribers");
            Assert.AreEqual(0, fixture.Store.Appended.Count,
                "a runless fault journals nothing — there is no Run journal");
            var faults = fixture.Coordinator.RunlessSubscriberFaults;
            Assert.AreEqual(1, faults.Count);
            Assert.AreEqual(Severity.Warning, faults[0].Level);
            Assert.AreEqual("A run event subscriber threw an exception.", faults[0].Message);
            Assert.IsNull(faults[0].Step);
            Assert.AreEqual(
                "System.InvalidOperationException",
                (string?)faults[0].Data!.ToToken()["exception"]);
        }

        [Test]
        public void ThrowingLogAppendedListenerOnRunlessFaultIsRecordedNotRepublished()
        {
            var fixture = NewFixture();
            var events = fixture.Coordinator.Events;
            var deliveries = 0;
            events.RegistryChanged += _ => throw new InvalidOperationException("registry fault");
            events.LogAppended += _ =>
            {
                deliveries++;
                throw new ApplicationException("listener fault");
            };
            var changes = new List<RegistryChange>
            {
                new RegistryChange(
                    RegistryChangeKinds.Added, "example.op", 1, null,
                    Array.Empty<Diagnostic>()),
            };

            // Neither the subscriber fault nor the listener fault escapes;
            // the listener fault lands in the same bounded record and is
            // never republished — the delivery count proves no recursion.
            Assert.DoesNotThrow(() => events.PublishRegistryChanges(changes));

            Assert.AreEqual(1, deliveries, "the fallback entry publishes exactly once");
            Assert.AreEqual(0, fixture.Store.Appended.Count,
                "a runless fault journals nothing — there is no Run journal");
            var faults = fixture.Coordinator.RunlessSubscriberFaults;
            Assert.AreEqual(2, faults.Count,
                "the subscriber fault and the suppressed listener fault");
            Assert.AreEqual(
                "System.InvalidOperationException",
                (string?)faults[0].Data!.ToToken()["exception"]);
            Assert.AreEqual(
                "System.ApplicationException",
                (string?)faults[1].Data!.ToToken()["exception"]);
        }

        [Test]
        public void ThrowingLogAppendedListenerOnJournaledFaultLandsInTheBoundedLog()
        {
            var fixture = NewFixture();
            var events = fixture.Coordinator.Events;
            // During an active Run the first-level fault keeps the journal
            // path, but its warning record's own LogAppended publish runs
            // under the dispatcher's fault-logging guard — a listener that
            // throws there is the nested fault: no journal line (the append
            // would publish again), so §E8's "logged" half is the bounded
            // record, never republished.
            var first = true;
            events.RunStateChanged += _ =>
            {
                if (first)
                {
                    first = false;
                    throw new InvalidOperationException("run fault");
                }
            };
            var deliveries = 0;
            events.LogAppended += _ =>
            {
                deliveries++;
                throw new ApplicationException("listener fault");
            };
            var marker = new MarkerOperation("test.dummy.m1");
            fixture.Register(marker, 'd');
            var runId = AcceptRun(
                fixture, Plan(new[] { Step(0, "n1", "test.dummy.m1", 'd') }),
                new[] { "n1" });

            Assert.IsTrue(fixture.Loop.Tick().Ok);

            // The throwing subscribers never stopped the Run (SPEC §E8).
            Assert.AreEqual(RunState.Succeeded, fixture.Coordinator.StateOf(runId));
            // The first-level subscriber fault is journaled exactly as
            // before: one warning log record on the Run.
            var journaled = LogRecords(fixture, "warning").FindAll(record =>
                (string?)DataOf(record)["message"] ==
                "A run event subscriber threw an exception.");
            Assert.AreEqual(1, journaled.Count,
                "the first-level fault keeps its journal record");
            Assert.AreEqual(
                "System.InvalidOperationException",
                (string?)DataOf(journaled[0])["data"]?["exception"]);
            // The listener saw the fault record exactly once; its throw —
            // the nested fault — was recorded once in the bounded log and
            // never republished (a republish would add deliveries).
            Assert.AreEqual(1, deliveries);
            var faults = fixture.Coordinator.RunlessSubscriberFaults;
            Assert.AreEqual(1, faults.Count,
                "the nested LogAppended fault is recorded exactly once");
            Assert.AreEqual(Severity.Warning, faults[0].Level);
            Assert.AreEqual(
                "A run event subscriber threw an exception.", faults[0].Message);
            Assert.IsNull(faults[0].Step);
            Assert.AreEqual(
                "System.ApplicationException",
                (string?)faults[0].Data!.ToToken()["exception"]);
        }

        [Test]
        public void RunlessSubscriberFaultLogKeepsTheNewestHundred()
        {
            var fixture = NewFixture();
            var events = fixture.Coordinator.Events;
            var published = new List<RunLogEntry>();
            events.LogAppended += entry => published.Add(entry);
            events.RegistryChanged += _ => throw new InvalidOperationException("registry fault");
            var changes = new List<RegistryChange>
            {
                new RegistryChange(
                    RegistryChangeKinds.Added, "example.op", 1, null,
                    Array.Empty<Diagnostic>()),
            };

            const int total = 105;
            for (var i = 0; i < total; i++)
            {
                events.PublishRegistryChanges(changes);
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.AreEqual(total, published.Count);
            var faults = fixture.Coordinator.RunlessSubscriberFaults;
            Assert.AreEqual(100, faults.Count, "the bounded record keeps the newest 100");
            // Distinct timestamps make the retained window observable:
            // exactly the newest 100 published entries, oldest dropped.
            CollectionAssert.AreEqual(published.GetRange(total - 100, 100), faults);
        }
    }
}
