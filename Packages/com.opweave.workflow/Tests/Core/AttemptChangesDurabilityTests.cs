#nullable enable

// T-W2.4-02 [Core] tests: SPEC §F2 attempt change durability (2026-11-08 owner
// decision) — StepWaiting, StepFailed and StepCancelled carry the attempt's
// cumulative §F7 `changes` whenever the list is non-empty, a same-attempt
// re-record replaces (never duplicates), a re-recorded StepWaiting keeps the
// original deadline, a journal without the optional member restores exactly as
// before, and a resumed StepWaiting seeds the recorder so the record ending the
// attempt carries the whole attempt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal sealed class AttemptChangesDurabilityTests
    {
        private const string Session = "session-acd-1";
        private const string Session2 = "session-acd-2";
        private const string WorkflowId = "example.acd";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.Acd." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Coordinator.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private readonly List<Fixture> _fixtures = new List<Fixture>();
        private readonly FakeClock _clock = new FakeClock();
        private readonly FakeEditorState _editor = new FakeEditorState();

        private Fixture NewFixture(string session = Session)
        {
            var fixture = new Fixture(_root, session, _clock, _editor);
            _fixtures.Add(fixture);
            return fixture;
        }

        private sealed class FakeEditorState : IEditorState
        {
            public bool IsEditMode { get; set; } = true;
            public bool IsCompiling { get; set; }
            public bool IsUpdating { get; set; }
        }

        private sealed class FakeClock : IClock
        {
            private DateTimeOffset _now =
                new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
            public DateTimeOffset UtcNow => _now;
            public void Advance(TimeSpan delta) => _now += delta;
        }

        private sealed class FakeRequirements : IRequirementEvaluator
        {
            public RequirementEvaluation Evaluate(Requirement requirement, StepContext context) =>
                RequirementEvaluation.Satisfied();
        }

        private sealed class UndoScope : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private sealed class Fixture
        {
            private readonly string _root;
            internal readonly FileRunStore Store;
            internal readonly FakeClock Clock;
            internal readonly List<RegisteredOperation> Operations =
                new List<RegisteredOperation>();
            internal readonly Dictionary<string, Func<IOperation>> OpFactories =
                new Dictionary<string, Func<IOperation>>(StringComparer.Ordinal);
            internal readonly RunCoordinator Coordinator;
            private RunLoop? _loop;

            private readonly Func<string, IReadOnlyList<Diagnostic>?>? _finalizeRunArtifacts;

            internal RunLoop Loop => _loop ??= new RunLoop(
                Coordinator,
                Clock,
                new FakeEditorState(),
                DescriptorTestFixtures.CreateRegistry(),
                Snapshot(),
                Resolve,
                new FakeRequirements(),
                _ => new UndoScope(),
                () => false);

            internal Fixture(
                string root, string session, FakeClock clock, FakeEditorState editor,
                Func<string, IReadOnlyList<Diagnostic>?>? finalizeRunArtifacts = null)
            {
                _root = root;
                Clock = clock;
                _finalizeRunArtifacts = finalizeRunArtifacts;
                Store = new FileRunStore(root, session);
                Coordinator = new RunCoordinator(
                    Store,
                    (runId, plan, document, actor) =>
                        Store.CreateRun(runId, plan, document, actor),
                    editor,
                    clock,
                    () => Snapshot(),
                    () => CoordinatorRunLock.TryAcquire(_root),
                    session);
                if (_finalizeRunArtifacts is not null)
                {
                    Coordinator.BindRunEndArtifactFinalizer(_finalizeRunArtifacts);
                }
            }

            internal string StoreRoot => _root;

            internal RegistrySnapshot Snapshot() =>
                new RegistrySnapshot(
                    "reg_01J00000000000000000000000",
                    new string('f', 64),
                    Operations,
                    DescriptorTestFixtures.CreateRegistry(),
                    Array.Empty<RegistryChange>(),
                    Array.Empty<TypeRegistryFailure>());

            private Result<IOperation> Resolve(PlanStep step) =>
                OpFactories.TryGetValue(step.OperationId, out var factory)
                    ? Result<IOperation>.Success(factory())
                    : Result<IOperation>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_RUN_011,
                        "The step's Operation cannot be resolved.",
                        nodeId: step.NodeId));

            internal void Register(Func<IOperation> factory, char hashSeed)
            {
                var op = factory();
                // One instance per domain: the loop re-resolves the operation
                // for every Poll, and a fresh instance each time would restart
                // the scripted waits from the beginning.
                OpFactories[op.Descriptor.Id] = () => op;
                Operations.Add(new RegisteredOperation(
                    op.Descriptor, OperationOrigin.Custom, "Example.Operations",
                    null, new string(hashSeed, 64), new string((char)(hashSeed + 1), 64))
                {
                    Implementation = op,
                });
            }
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        private static ChangeTarget Target(string path) =>
            new ChangeTarget("asset", path, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        // ── Dummy operations ────────────────────────────────────────────

        private abstract class OpBase : IOperation
        {
            public abstract OperationDescriptor Descriptor { get; }
            public virtual IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();
            public virtual PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));
            public abstract StepResult Execute(StepContext ctx);
            public virtual StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");
            public virtual ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");
            public virtual void Cancel(StepContext ctx)
            {
            }
        }

        // A Waitable that records `changes` per scripted Poll returns:
        // each queue entry is the target path it records before the result,
        // or "-" for none; the last returns Succeeded.
        private sealed class RecordingWaitableOperation : OpBase
        {
            private readonly string _executeRecord;
            private readonly Queue<(string record, bool succeed)> _polls;
            private readonly List<string> _seen;

            internal RecordingWaitableOperation(
                string id, string executeRecord,
                IEnumerable<(string record, bool succeed)> polls, List<string> seen)
            {
                _executeRecord = executeRecord;
                _polls = new Queue<(string, bool)>(polls);
                _seen = seen;
                Descriptor = Op.Define(id, 1)
                    .Summary("Records changes while waiting.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public override OperationDescriptor Descriptor { get; }
            internal int PollCalls { get; private set; }

            public override StepResult Execute(StepContext ctx)
            {
                if (_executeRecord.Length != 0)
                {
                    ctx.Changes.Created(Target(_executeRecord), undoable: false);
                }
                return StepResult.Waiting(Json("{\"n\":0}"), "waiting", 1);
            }

            public override StepResult Poll(StepContext ctx)
            {
                PollCalls++;
                _seen.Add(ctx.ResumeToken?.CanonicalText ?? "<null>");
                var (record, succeed) = _polls.Dequeue();
                if (record != "-")
                {
                    ctx.Changes.Created(Target(record), undoable: false);
                }
                if (!succeed)
                {
                    return StepResult.Waiting(
                        Json("{\"n\":" + PollCalls + "}"), "waiting", 1);
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }
        }

        // A Reconcilable Immediate that fails retryably and never decides —
        // the §E7/§F5 NeedsReview fixture a review abort ends through the
        // coordinator, outside the Tick.
        private sealed class ReviewBoundOperation : OpBase
        {
            internal ReviewBoundOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Fails retryably; never decides.")
                    .Output("out", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Reconcilable)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx) =>
                StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true));

            public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never decides.");
        }

        // An Immediate that records a change then fails retryable once,
        // recording a different change when it succeeds on the retry.
        private sealed class FlakyRecordingOperation : OpBase
        {
            private readonly int[] _calls;

            internal FlakyRecordingOperation(string id, int[] calls)
            {
                _calls = calls;
                Descriptor = Op.Define(id, 1)
                    .Summary("Records, fails once, records again and succeeds.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _calls[0]++;
                ctx.Changes.Created(Target("Assets/try" + _calls[0] + ".mat"), undoable: false);
                if (_calls[0] == 1)
                {
                    return StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true));
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }
        }

        // ── Plan/document builders ──────────────────────────────────────

        private static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            int timeoutSeconds = 30,
            RetryPolicy? retry = null,
            Idempotency idempotency = Idempotency.Idempotent) =>
            new PlanStep(
                index, nodeId, operationId, 1,
                new string(hashSeed, 64), new string((char)(hashSeed + 1), 64),
                new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                timeoutSeconds, retry ?? new RetryPolicy(),
                idempotency);

        private static CompiledPlan Plan(params PlanStep[] steps)
        {
            var stepList = new List<PlanStep>(steps);
            var hashInput = JsonValue.From(new JObject
            {
                ["canonicalJsonVersion"] = 1,
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = WorkflowId,
                    ["inputs"] = new JArray(),
                    ["outputs"] = new JArray(),
                },
                ["executionInputs"] = PlanHashInputParts.ExecutionInputs(
                    Array.Empty<string>(), null),
                ["steps"] = PlanHashInputParts.Steps(stepList),
                ["transitions"] = PlanHashInputParts.Transitions(stepList),
                ["retry"] = PlanHashInputParts.Retry(stepList),
                ["timeouts"] = PlanHashInputParts.Timeouts(stepList),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(
                    stepList, Array.Empty<ApprovalRequirement>()),
                ["usedOperationFingerprints"] =
                    PlanHashInputParts.OperationFingerprints(stepList),
                ["usedTypeFingerprints"] = new JArray(),
                ["usedCodecFingerprints"] = new JArray(),
                ["environmentIdentifiers"] = new JObject
                {
                    ["unityVersion"] = "6000.0.0f1",
                    ["editorOS"] = "windows",
                    ["activeBuildTarget"] = "windows",
                    ["scriptingBackend"] = "mono",
                    ["apiCompatibilityLevel"] = "netstandard2.1",
                    ["scriptingDefineSymbols"] = new JArray(),
                },
            });
            return new CompiledPlan(
                "plan_01J00000000000000000000000",
                CanonicalJson.Sha256(hashInput.ToToken()),
                WorkflowId, new string('b', 64), new string('c', 64),
                Json("{}"), stepList,
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

        private static string AcceptRun(Fixture fixture, CompiledPlan plan, params string[] nodeIds)
        {
            var accepted = fixture.Coordinator.Accept(
                plan, Doc(nodeIds), ActorKind.Agent, new AcceptOptions(null, false));
            Assert.IsTrue(accepted.Ok, "acceptance failed");
            return accepted.Value!.RunId;
        }

        private static void Ticks(Fixture fixture, int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.IsTrue(fixture.Loop.Tick().Ok, $"Tick {i} failed.");
            }
        }

        private RunSnapshot Load(string runId)
        {
            var loaded = new FileRunStore(_root, "reload-check").Load(runId);
            Assert.IsTrue(loaded.Ok, "Load failed.");
            return loaded.Value!;
        }

        private static string JournalPath(Fixture fixture, string runId) =>
            Path.Combine(fixture.StoreRoot, "Runs", runId, "journal.ndjson");

        private static List<JournalRecord> JournalRecords(Fixture fixture, string runId)
        {
            var records = new List<JournalRecord>();
            var lines = File.ReadAllLines(JournalPath(fixture, runId));
            for (var i = 1; i < lines.Length; i++)
            {
                var read = JournalCodec.ReadRecord(lines[i], i);
                if (read.Status == JournalLineStatus.Decoded)
                {
                    records.Add(read.Record!);
                }
            }
            return records;
        }

        private static List<JObject> RecordsOfType(
            List<JournalRecord> records, JournalRecordType type) =>
            records.Where(r => r.Type == type)
                .Select(r => (JObject)r.Data.ToToken())
                .ToList();

        private static JArray? ChangesOf(JObject data) =>
            data["changes"] as JArray;

        private static string[] PathsOf(JObject data) =>
            (data["changes"] as JArray)!
                .OfType<JObject>()
                .Select(e => (string)e["target"]!["path"]!)
                .ToArray()!;

        // ── SPEC §F2 writer: StepWaiting carries the cumulative list ─────

        [Test]
        public void StepWaitingCarriesAndAccumulatesTheAttemptsChanges()
        {
            var seen = new List<string>();
            var live = NewFixture();
            live.Register(
                () => new RecordingWaitableOperation(
                    "test.acd.wait", "Assets/a.mat",
                    new[] { ("Assets/b.mat", false), ("-", true) }, seen), 'd');
            var runId = AcceptRun(live,
                Plan(Step(0, "n1", "test.acd.wait", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" })),
                "n1");

            Ticks(live, 1); // Execute: record a.mat -> StepWaiting{changes:[a]}
            var journal = JournalRecords(live, runId);
            var waiting = RecordsOfType(journal, JournalRecordType.StepWaiting);
            Assert.AreEqual(1, waiting.Count);
            var first = ChangesOf(waiting[0]);
            Assert.IsNotNull(first, "a non-empty cumulative list must be journaled");
            Assert.AreEqual(1, first!.Count);
            Assert.AreEqual("Assets/a.mat", (string)first[0]!["target"]!["path"]!);
            Assert.AreEqual(1, (int)first[0]!["attempt"]!);
            Assert.AreEqual("created", (string)first[0]!["action"]!);
            Assert.AreEqual("n1", (string)first[0]!["nodeId"]!);
            var deadline = (string)waiting[0]["deadline"]!;

            _clock.Advance(TimeSpan.FromSeconds(1));
            Ticks(live, 1); // Poll: record b.mat -> StepWaiting{changes:[a,b]}
            journal = JournalRecords(live, runId);
            waiting = RecordsOfType(journal, JournalRecordType.StepWaiting);
            Assert.AreEqual(2, waiting.Count, "a changed re-Wait is re-recorded");
            var second = ChangesOf(waiting[1]);
            Assert.IsNotNull(second);
            Assert.AreEqual(2, second!.Count,
                "the re-recorded StepWaiting carries the whole cumulative list");
            Assert.AreEqual(
                deadline, (string)waiting[1]["deadline"]!,
                "a re-recorded StepWaiting keeps the original deadline");
            Assert.AreEqual(
                "{\"n\":1}", CanonicalJson.Serialize(waiting[1]["resumeToken"]!),
                "the re-record carries the latest resumeToken");

            _clock.Advance(TimeSpan.FromSeconds(1));
            Ticks(live, 1); // Poll: succeed -> StepSucceeded{changes:[a,b]}
            journal = JournalRecords(live, runId);
            var succeeded = RecordsOfType(journal, JournalRecordType.StepSucceeded);
            Assert.AreEqual(1, succeeded.Count,
                "state=" + Load(runId).State + " journal: " +
                string.Join(" | ", File.ReadAllLines(JournalPath(live, runId))));
            Assert.AreEqual(
                new[] { "Assets/a.mat", "Assets/b.mat" }, PathsOf(succeeded[0]));
            Assert.AreEqual(RunState.Succeeded, Load(runId).State);
        }

        // ── SPEC §F2 writer: a non-empty list is never omitted ───────────

        [Test]
        public void FailedAndCancelledRecordsCarryTheAttemptsChanges()
        {
            // Retryable failure: the failed attempt's list rides on StepFailed.
            var calls = new int[1];
            var live = NewFixture();
            live.Register(() => new FlakyRecordingOperation("test.acd.flaky", calls), 'd');
            var runId = AcceptRun(live,
                Plan(Step(0, "n1", "test.acd.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(3, 60_000, 2.0, 60_000))),
                "n1");
            Ticks(live, 1); // attempt 1: record try1 -> Failed(retryable)
            var failed = RecordsOfType(JournalRecords(live, runId), JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            var failedChanges = ChangesOf(failed[0]);
            Assert.IsNotNull(failedChanges, "the failed attempt's changes must be journaled");
            Assert.AreEqual("Assets/try1.mat", (string)failedChanges![0]!["target"]!["path"]!);
            Assert.AreEqual(1, (int)failedChanges[0]!["attempt"]!);

            _clock.Advance(TimeSpan.FromMinutes(2));
            Ticks(live, 3); // attempt 2: record try2 -> Succeeded
            var succeeded = RecordsOfType(JournalRecords(live, runId), JournalRecordType.StepSucceeded);
            Assert.AreEqual(1, succeeded.Count);
            var succeededChanges = ChangesOf(succeeded[0]);
            Assert.AreEqual(1, succeededChanges!.Count,
                "the retried attempt's list is its own — the failed attempt's " +
                "elements stay on the StepFailed record, never mixed in");
            Assert.AreEqual("Assets/try2.mat", (string)succeededChanges[0]!["target"]!["path"]!);
            Assert.AreEqual(2, (int)succeededChanges[0]!["attempt"]!);
            Assert.AreEqual(RunState.Succeeded, Load(runId).State);

            // Cancelled Waiting: the cancelled attempt's list rides on StepCancelled.
            var cancelledSeen = new List<string>();
            var run2 = NewFixture(Session2);
            run2.Register(
                () => new RecordingWaitableOperation(
                    "test.acd.hold", "Assets/held.mat",
                    new[] { ("-", true) }, cancelledSeen), 'd');
            var runId2 = AcceptRun(run2,
                Plan(Step(0, "n1", "test.acd.hold", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" })),
                "n1");
            Ticks(run2, 1); // Execute: record held -> StepWaiting{changes:[held]}
            Assert.IsTrue(run2.Coordinator.Cancel(runId2, "stop").Ok);
            Ticks(run2, 2);
            var cancelled =
                RecordsOfType(JournalRecords(run2, runId2), JournalRecordType.StepCancelled);
            Assert.AreEqual(1, cancelled.Count);
            var cancelledChanges = ChangesOf(cancelled[0]);
            Assert.IsNotNull(cancelledChanges,
                "the cancelled attempt's changes must be journaled");
            Assert.AreEqual("Assets/held.mat", (string)cancelledChanges![0]!["target"]!["path"]!);
            Assert.AreEqual(RunState.Cancelled, Load(runId2).State);
        }

        // ── SPEC §F4 item 5: a resumed StepWaiting restores the recorder ──

        [Test]
        public void ResumedWaitingStepRestoresTheAttemptsChanges()
        {
            var live = NewFixture();
            live.Register(
                () => new RecordingWaitableOperation(
                    "test.acd.wait", "Assets/before.mat",
                    new[] { ("Assets/after.mat", true) }, new List<string>()), 'd');
            var runId = AcceptRun(live,
                Plan(Step(0, "n1", "test.acd.wait", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" })),
                "n1");
            Ticks(live, 1); // Execute: record before -> StepWaiting{changes:[before]}
            var before = Load(runId);
            Assert.AreEqual(StepState.Waiting, before.Steps[0].State);
            Assert.IsNotNull(before.Steps[0].Changes,
                "the snapshot carries the last StepWaiting's durable changes");
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(
                () => new RecordingWaitableOperation(
                    "test.acd.wait", "Assets/before.mat",
                    new[] { ("Assets/after.mat", true) }, new List<string>()), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok, "resume failed");
            _clock.Advance(TimeSpan.FromSeconds(1));
            Ticks(fresh, 3);

            var succeeded = RecordsOfType(JournalRecords(fresh, runId), JournalRecordType.StepSucceeded);
            Assert.AreEqual(1, succeeded.Count);
            Assert.AreEqual(
                new[] { "Assets/before.mat", "Assets/after.mat" }, PathsOf(succeeded[0]),
                "the restored recorder makes the ending record carry the WHOLE attempt");
            Assert.AreEqual(RunState.Succeeded, Load(runId).State);
        }

        // ── SPEC §F2 backward compatibility: no `changes` member ────────

        [Test]
        public void AJournalWithoutChangesRestoresTheSameStateAsBefore()
        {
            var live = NewFixture();
            live.Register(
                () => new RecordingWaitableOperation(
                    "test.acd.wait", "",
                    new[] { ("-", true) }, new List<string>()), 'd');
            var runId = AcceptRun(live,
                Plan(Step(0, "n1", "test.acd.wait", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" })),
                "n1");
            Ticks(live, 1); // Execute records nothing -> StepWaiting has no `changes`
            var waiting = RecordsOfType(JournalRecords(live, runId), JournalRecordType.StepWaiting);
            Assert.AreEqual(1, waiting.Count);
            Assert.IsNull(waiting[0]["changes"],
                "an empty cumulative list is omitted, the pre-decision form");
            var before = Load(runId);
            Assert.AreEqual(StepState.Waiting, before.Steps[0].State);
            Assert.IsNull(before.Steps[0].Changes);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(
                () => new RecordingWaitableOperation(
                    "test.acd.wait", "",
                    new[] { ("-", true) }, new List<string>()), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok, "a journal without `changes` must resume as before");
            _clock.Advance(TimeSpan.FromSeconds(1));
            Ticks(fresh, 3);
            Assert.AreEqual(RunState.Succeeded, Load(runId).State);
            var succeeded = RecordsOfType(JournalRecords(fresh, runId), JournalRecordType.StepSucceeded);
            Assert.AreEqual(0, ChangesOf(succeeded[0])!.Count);
        }

        // ── SPEC §F1: a refused pre-terminal artifact write fails the Run ──

        [Test]
        public void ARefusedPreTerminalArtifactWriteEndsTheRunFailed()
        {
            // The §F1 durable completion path: the injected artifact writer
            // (the §F7 changes.json finalize in the UnityAdapter) runs BEFORE
            // the terminal record, so its refusal is a Run Store write failure
            // like every refused required write — the Run ends Failed with the
            // store diagnostic on its terminal record, never a Succeeded Run
            // whose change list silently went missing.
            var calls = new List<string>();
            var terminalSeen = false;
            var fixture = new Fixture(
                _root, Session, _clock, _editor,
                runId =>
                {
                    calls.Add(runId);
                    terminalSeen = File.ReadAllLines(
                            Path.Combine(_root, "Runs", runId, "journal.ndjson"))
                        .Any(line => line.Contains("\"type\":\"run_finished\""));
                    return new[]
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_STORE_001,
                            "The Run artifacts could not be written."),
                    };
                });
            _fixtures.Add(fixture);
            fixture.Register(
                () => new RecordingWaitableOperation(
                    "test.acd.wait", "Assets/a.mat",
                    new[] { ("-", true) }, new List<string>()), 'd');
            var runId = AcceptRun(fixture,
                Plan(Step(0, "n1", "test.acd.wait", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" })),
                "n1");

            Ticks(fixture, 1); // Execute: Waiting
            _clock.Advance(TimeSpan.FromSeconds(1));
            Ticks(fixture, 3); // Poll: Succeeded -> the refused finalize ends Failed

            Assert.AreEqual(RunState.Failed, Load(runId).State);
            Assert.AreEqual(
                new[] { runId }, calls,
                "the artifact writer runs exactly once in the end path");
            Assert.IsFalse(
                terminalSeen,
                "the artifact writer runs BEFORE the terminal record");
            var finished = RecordsOfType(
                JournalRecords(fixture, runId), JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string)finished[0]["state"]!);
            Assert.AreEqual(
                ErrorCodes.OW_STORE_001, (string)finished[0]["summary"]!["code"]!);
            Assert.AreEqual(
                1, RecordsOfType(
                    JournalRecords(fixture, runId), JournalRecordType.StepSucceeded).Count,
                "the step's own record must survive the refused finalize");
        }

        // ── SPEC §F1: the single writer runs the finalizer on every end ──

        // The loop's Failed end is still one writer: the finalizer runs once,
        // before the terminal record, and its refusal rides on the durable
        // run_finished{failed} the end already is — the step's decided cause
        // keeps the summary while the refused write joins its diagnostics.
        [Test]
        public void ARefusedFinalizerInALoopFailedEndKeepsTheDecidedCause()
        {
            var calls = new List<string>();
            var terminalSeen = false;
            var fixture = new Fixture(
                _root, Session, _clock, _editor,
                runId =>
                {
                    calls.Add(runId);
                    terminalSeen = File.ReadAllLines(
                            Path.Combine(_root, "Runs", runId, "journal.ndjson"))
                        .Any(line => line.Contains("\"type\":\"run_finished\""));
                    return new[]
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_STORE_001,
                            "The Run artifacts could not be written."),
                    };
                });
            _fixtures.Add(fixture);
            fixture.Register(() => new FlakyRecordingOperation("test.acd.fail", new int[1]), 'd');
            var runId = AcceptRun(fixture,
                Plan(Step(0, "n1", "test.acd.fail", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(MaxAttempts: 1))),
                "n1");

            Ticks(fixture, 3);

            Assert.AreEqual(RunState.Failed, Load(runId).State);
            Assert.AreEqual(
                new[] { runId }, calls,
                "the finalizer runs exactly once in the Failed end path");
            Assert.IsFalse(
                terminalSeen,
                "the finalizer runs BEFORE the terminal record");
            var finished = RecordsOfType(
                JournalRecords(fixture, runId), JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string)finished[0]["state"]!);
            var summary = (JObject)finished[0]["summary"]!;
            Assert.AreEqual(
                ErrorCodes.OW_UNITY_008, (string)summary["code"]!,
                "the step's decided cause keeps the run_finished summary");
            var extras = (JArray)summary["diagnostics"]!;
            Assert.IsTrue(
                extras.OfType<JObject>().Any(
                    entry => (string?)entry["code"] == ErrorCodes.OW_STORE_001 &&
                        (string?)entry["message"] == "The Run artifacts could not be written."),
                "the refused artifact write rides on the summary diagnostics");
        }

        // The coordinator-driven review abort is the same single writer: a
        // refused changes.json inside it ends the Run Failed with the store
        // diagnostic — never the durable Cancelled the decision asked for
        // while its change list silently went missing (SPEC §F1/§F7).
        [Test]
        public void ARefusedFinalizerTurnsAReviewAbortedRunFailed()
        {
            var calls = new List<string>();
            var terminalSeen = false;
            var fixture = new Fixture(
                _root, Session, _clock, _editor,
                runId =>
                {
                    calls.Add(runId);
                    terminalSeen = File.ReadAllLines(
                            Path.Combine(_root, "Runs", runId, "journal.ndjson"))
                        .Any(line => line.Contains("\"type\":\"run_finished\""));
                    return new[]
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_STORE_001,
                            "The Run artifacts could not be written."),
                    };
                });
            _fixtures.Add(fixture);
            fixture.Register(() => new ReviewBoundOperation("test.acd.review"), 'd');
            var runId = AcceptRun(fixture,
                Plan(Step(0, "n1", "test.acd.review", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable)),
                "n1");
            Ticks(fixture, 1); // retryable failure -> Reconcile Unknown -> NeedsReview
            Assert.AreEqual(RunState.NeedsReview, fixture.Coordinator.StateOf(runId));

            var resolved = fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort);

            Assert.IsTrue(resolved.Ok, "the abort's records must be durably written");
            Assert.AreEqual(RunState.Failed, resolved.Value!.State);
            Assert.AreEqual(RunState.Failed, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(RunState.Failed, Load(runId).State);
            Assert.AreEqual(
                new[] { runId }, calls,
                "the finalizer runs exactly once in the review-abort end");
            Assert.IsFalse(
                terminalSeen,
                "the finalizer runs BEFORE the terminal record");
            var finished = RecordsOfType(
                JournalRecords(fixture, runId), JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("failed", (string)finished[0]["state"]!);
            Assert.AreEqual(
                ErrorCodes.OW_STORE_001, (string)finished[0]["summary"]!["code"]!,
                "the refused artifact write is the durable cause (SPEC §F1)");
            Assert.That(
                (string?)finished[0]["summary"]!["diagnostics"]![0]!["code"],
                Is.EqualTo(ErrorCodes.OW_RUN_005),
                "the replaced cancelled decision stays durable on the summary");
            Assert.AreEqual(
                1, RecordsOfType(
                    JournalRecords(fixture, runId), JournalRecordType.ReviewResolved).Count,
                "the abort's own record stays durable");
        }

        // The successful review abort is the same seam without the refusal:
        // the finalizer runs once, before run_finished, and the Cancelled the
        // decision asked for is what the journal holds.
        [Test]
        public void AReviewAbortedRunFinalizesItsArtifactsOnceBeforeTheEnd()
        {
            var calls = new List<string>();
            var terminalSeen = false;
            var fixture = new Fixture(
                _root, Session, _clock, _editor,
                runId =>
                {
                    calls.Add(runId);
                    terminalSeen = File.ReadAllLines(
                            Path.Combine(_root, "Runs", runId, "journal.ndjson"))
                        .Any(line => line.Contains("\"type\":\"run_finished\""));
                    return null;
                });
            _fixtures.Add(fixture);
            fixture.Register(() => new ReviewBoundOperation("test.acd.review"), 'd');
            var runId = AcceptRun(fixture,
                Plan(Step(0, "n1", "test.acd.review", 'd',
                    outputTypes: new Dictionary<string, string> { ["out"] = "core.String" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable)),
                "n1");
            Ticks(fixture, 1);
            Assert.AreEqual(RunState.NeedsReview, fixture.Coordinator.StateOf(runId));

            var resolved = fixture.Coordinator.ResolveReview(runId, ReviewDecision.Abort);

            Assert.IsTrue(resolved.Ok);
            Assert.AreEqual(RunState.Cancelled, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(RunState.Cancelled, Load(runId).State);
            Assert.AreEqual(
                new[] { runId }, calls,
                "the finalizer runs exactly once in the review-abort end");
            Assert.IsFalse(
                terminalSeen,
                "the finalizer runs BEFORE the terminal record");
            var finished = RecordsOfType(
                JournalRecords(fixture, runId), JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual("cancelled", (string)finished[0]["state"]!);
        }
    }
}
