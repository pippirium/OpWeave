#nullable enable

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
    /// <summary>
    /// [Core] tests for the SPEC §F4 resume driver of T-W1.8-08
    /// (local:T-W1.8-08:core-resume): the Runner state and the §E5
    /// ValueStore are rebuilt purely from the journal — the last
    /// state-changing step record decides (StepSucceeded → Transitions,
    /// StepWaiting → Poll on a fresh Operation instance from the
    /// resumeToken alone, a decided retry → Running at the recorded
    /// attempt after not-before, a bare StepStarted → the single §F5
    /// table) — while every uncertain journal ends NeedsReview and no
    /// completed step ever re-runs. The suite drives the SPEC §F2 test 2
    /// replay check, the §C2 test 2 resumeToken-only Poll, the §N.4
    /// test 2 reordered-Transitions Plan and the fresh-loop reload
    /// fixtures: an interrupted finish releases a Run resource exactly
    /// once (a), a retry backoff resumes at the recorded attempt after
    /// not-before (b), a resumed step uses the restored Run resource and
    /// it is released once (c), and a resumed Waiting keeps its
    /// journaled deadline (d, SPEC §E6 item 1). Fixtures use a real
    /// <see cref="FileRunStore"/> over a temp root, a real
    /// <see cref="CoordinatorRunLock"/> and dummy Operations; journal
    /// truncation simulates the interruption. The same sources run under
    /// dotnet and Unity Mono.
    /// </summary>
    internal sealed class RunResumeTests
    {
        private const string Session = "session-resume-test";
        private const string Session2 = "session-resume-test-2";
        private const string WorkflowId = "example.resume";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.Resume." + Guid.NewGuid().ToString("N"));
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
                new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

            public DateTimeOffset UtcNow => _now;

            public void Advance(TimeSpan delta) => _now += delta;
        }

        /// <summary>
        /// The deterministic per-Tick monotonic timer (T-W1.8-10): every
        /// Elapsed read advances by the fixture-set amount — drives the
        /// §E3 tick budget independently of real time, so a JIT, GC or
        /// Editor stall can no longer leave a Tick's steps unstarted.
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

        /// <summary>A real store/coordinator/loop stack over the shared
        /// root — one "Editor session": a fresh Fixture on the same root
        /// is the post-reload world of the interruption tests.</summary>
        private sealed class Fixture
        {
            private readonly string _root;
            internal readonly FileRunStore Store;
            internal readonly FakeClock Clock;
            internal readonly FakeEditorState Editor;
            internal readonly List<RegisteredOperation> Operations =
                new List<RegisteredOperation>();
            internal readonly Dictionary<string, Func<IOperation>> OpFactories =
                new Dictionary<string, Func<IOperation>>(StringComparer.Ordinal);
            internal readonly List<RunResource> Released = new List<RunResource>();
            internal readonly List<string> Events = new List<string>();
            internal readonly RunCoordinator Coordinator;
            private RunLoop? _loop;

            /// <summary>
            /// Added to every Elapsed read of the fake monotonic timer the
            /// RunLoop creates for a Tick — 0 keeps the §E3 tick budget
            /// deterministic (T-W1.8-10).
            /// </summary>
            internal int MonotonicAdvancePerReadMs;

            // The Loop binds its operation snapshot at construction — lazily,
            // after the fixture's Operations are registered (a Reload-style
            // rebuild resolves from the registry then in force).
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
                monotonicTimerFactory: () =>
                    new FakeMonotonicTimer(MonotonicAdvancePerReadMs),
                releaseResource: (_, resource) =>
                {
                    Released.Add(resource);
                    return Result<Unit>.Success(Unit.Value);
                });

            internal Fixture(string root, string session, FakeClock clock, FakeEditorState editor)
            {
                _root = root;
                Clock = clock;
                Editor = editor;
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

            // Every resolve mints a NEW instance — the SPEC §C2 items 3–4
            // contract: no step state survives outside the resumeToken.
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
                OpFactories[op.Descriptor.Id] = factory;
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

        /// <summary>Immediate marker: logs <c>execute:&lt;id&gt;</c> and
        /// sets its <c>mark</c> output; Idempotency is parameterized.</summary>
        private sealed class MarkerOperation : OpBase
        {
            private readonly List<string> _events;
            private readonly string _id;
            private readonly string _mark;

            internal MarkerOperation(
                string id, List<string> events, Idempotency? idempotency = null,
                string? mark = null)
            {
                _id = id;
                _events = events;
                _mark = mark ?? "done:" + id;
                Descriptor = Op.Define(id, 1)
                    .Summary("Marks execution order.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(idempotency ?? Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _events.Add("execute:" + _id);
                ctx.SetOutput("mark", _mark);
                return StepResult.Succeeded();
            }
        }

        /// <summary>A Reconcilable whose Reconcile verdict is scripted —
        /// counts the calls and captures the snapshot it was given.</summary>
        private sealed class ReconcilingOperation : OpBase
        {
            private readonly ReconcileResult _verdict;
            private readonly string _id;
            private readonly List<string> _events;

            internal ReconcilingOperation(
                string id, List<string> events, ReconcileResult verdict)
            {
                _id = id;
                _events = events;
                _verdict = verdict;
                Descriptor = Op.Define(id, 1)
                    .Summary("Reconciles with a scripted verdict.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Reconcilable)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }
            internal int ReconcileCalls;
            internal JsonValue? SeenSnapshot;

            public override StepResult Execute(StepContext ctx)
            {
                _events.Add("execute:" + _id);
                ctx.SetOutput("mark", "done:" + _id);
                return StepResult.Succeeded();
            }

            public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                ReconcileCalls++;
                SeenSnapshot = snapshot;
                return _verdict;
            }
        }

        /// <summary>A Waitable that completes after a fixed number of
        /// polls: Execute and every poll but the last return Waiting with
        /// a counter resumeToken; the last sets its output. Each Poll
        /// records the resumeToken it saw — the SPEC §C2 test 2 evidence
        /// that a fresh instance resumes from the token alone.</summary>
        private sealed class WaitOnceOperation : OpBase
        {
            private readonly List<JsonValue?> _seenTokens;

            internal WaitOnceOperation(string id, List<JsonValue?> seenTokens, int polls = 2)
            {
                _seenTokens = seenTokens;
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits, then completes from the resumeToken.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(Json("{\"tick\":0}"), "waiting", 10);

            public override StepResult Poll(StepContext ctx)
            {
                _seenTokens.Add(ctx.ResumeToken);
                var tick = (int?)ctx.ResumeToken?.ToToken()["tick"] ?? 0;
                if (tick < 1)
                {
                    return StepResult.Waiting(
                        Json("{\"tick\":" + (tick + 1) + "}"), "waiting", 10);
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }
        }

        /// <summary>A Waitable that never completes on its own — the
        /// journaled-deadline expiry fixture; counts Cancel calls.</summary>
        private sealed class ForeverWaitableOperation : OpBase
        {
            internal ForeverWaitableOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits forever.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public override OperationDescriptor Descriptor { get; }
            internal int CancelCalls;

            public override StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(Json("\"t0\""), "waiting", 10);

            public override StepResult Poll(StepContext ctx) =>
                StepResult.Waiting(Json("\"t1\""), "waiting", 10);

            public override void Cancel(StepContext ctx) => CancelCalls++;
        }

        /// <summary>An Immediate that fails retryable a fixed number of
        /// times before succeeding — the retry-backoff resume fixture.</summary>
        private sealed class FlakyOperation : OpBase
        {
            private readonly int _failures;
            private readonly List<string> _events;
            private readonly string _id;

            internal FlakyOperation(string id, List<string> events, int failures)
            {
                _id = id;
                _events = events;
                _failures = failures;
                Descriptor = Op.Define(id, 1)
                    .Summary("Fails, then succeeds.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }
            internal int ExecuteCalls;

            public override StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                if (ExecuteCalls <= _failures)
                {
                    return StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true));
                }
                _events.Add("execute:" + _id);
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }
        }

        /// <summary>Immediate that acquires one Run resource then
        /// succeeds — the interrupted-finish release fixture (a).</summary>
        private sealed class ResourceMarkerOperation : OpBase
        {
            private readonly string _id;

            internal ResourceMarkerOperation(string id)
            {
                _id = id;
                Descriptor = Op.Define(id, 1)
                    .Summary("Acquires a Run resource.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                ((IRunResourceRegistry)ctx).Acquire(
                    "editing_contents", Json("{\"guid\":\"" + _id + "\"}"));
                ctx.SetOutput("mark", "done:" + _id);
                return StepResult.Succeeded();
            }
        }

        /// <summary>A Waitable that acquires a Run resource in Execute
        /// then waits; the resumed Poll RELEASES it through the restored
        /// resource registry — the outstanding-resource resume fixture (c).</summary>
        private sealed class ResourceWaitableOperation : OpBase
        {
            private readonly string _id;
            private readonly List<string> _events;

            internal ResourceWaitableOperation(string id, List<string> events)
            {
                _id = id;
                _events = events;
                Descriptor = Op.Define(id, 1)
                    .Summary("Holds a Run resource while waiting.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                ((IRunResourceRegistry)ctx).Acquire(
                    "editing_contents", Json("{\"guid\":\"" + _id + "\"}"));
                return StepResult.Waiting(Json("\"t0\""), "holding", 10);
            }

            public override StepResult Poll(StepContext ctx)
            {
                // The restored entry proves usable: releasing it through
                // the registry journals the single resource_released.
                ((IRunResourceRegistry)ctx).Release(
                    "editing_contents", Json("{\"guid\":\"" + _id + "\"}"));
                _events.Add("released:" + _id);
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }
        }

        /// <summary>A Reconcilable Immediate whose first
        /// <paramref name="failures"/> Execute calls — counted across
        /// instances, since the factory mints a new one per resolve —
        /// fail retryable and whose Reconcile dequeues a shared scripted
        /// verdict: the review-retry recovery fixture, where durable
        /// failure and verdict state outlive a session.</summary>
        private sealed class ScriptFailReconcilingOperation : OpBase
        {
            private readonly int[] _calls;
            private readonly int _failures;
            private readonly Queue<ReconcileResult> _verdicts;

            internal ScriptFailReconcilingOperation(
                string id, int[] calls, int failures, Queue<ReconcileResult> verdicts)
            {
                _calls = calls;
                _failures = failures;
                _verdicts = verdicts;
                Descriptor = Op.Define(id, 1)
                    .Summary("Fails retryable, then succeeds.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Reconcilable)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _calls[0]++;
                if (_calls[0] <= _failures)
                {
                    return StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_008, "A transient failure.", retryable: true));
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                _verdicts.Count == 0
                    ? new ReconcileResult.Unknown("No scripted verdict left.")
                    : _verdicts.Dequeue();
        }

        // ── Plan builders ───────────────────────────────────────────────

        private static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            IReadOnlyDictionary<string, int>? transitions = null,
            int timeoutSeconds = 30,
            RetryPolicy? retry = null,
            Idempotency? idempotency = null) =>
            new PlanStep(
                index, nodeId, operationId, 1,
                new string(hashSeed, 64), new string((char)(hashSeed + 1), 64),
                inputs ?? new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                transitions ?? new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                timeoutSeconds, retry ?? new RetryPolicy(),
                idempotency ?? OpWeave.Core.Operations.Idempotency.Idempotent);

        // A fully revalidatable Plan for the real FileRunStore: the §D0.1
        // planHashInput is built by the same PlanHashInputParts the
        // compiler uses and planHash is its real SHA-256.
        private static CompiledPlan Plan(
            PlanStep[] steps,
            PlanOutput[]? outputs = null,
            JsonValue? inputs = null)
        {
            var stepList = new List<PlanStep>(steps);
            var inputsValue = inputs ?? Json("{}");
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
                    Array.Empty<string>(), inputsValue.ToToken()),
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
                inputsValue,
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

        private static string AcceptRun(Fixture fixture, CompiledPlan plan)
        {
            var accepted = fixture.Coordinator.Accept(
                plan, Doc("n1", "n2", "n3"), ActorKind.Agent, new AcceptOptions(null, false));
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

        private static string JournalPath(Fixture fixture, string runId) =>
            Path.Combine(fixture.StoreRoot, "Runs", runId, "journal.ndjson");

        /// <summary>The interruption point: the journal is truncated back
        /// to the header plus <paramref name="records"/> record lines —
        /// every later record never reached disk.</summary>
        private static void TruncateJournal(Fixture fixture, string runId, int records)
        {
            fixture.Store.FlushPending();
            var journal = JournalPath(fixture, runId);
            var lines = File.ReadAllLines(journal);
            File.WriteAllLines(journal, lines.Take(1 + records));
        }

        private static List<JournalRecord> RecordsOfType(
            List<JournalRecord> records, JournalRecordType type) =>
            records.FindAll(record => record.Type == type);

        // Reads the journal back into decoded records for assertions.
        private static List<JournalRecord> JournalRecords(Fixture fixture, string runId) =>
            DecodeRecords(File.ReadAllLines(JournalPath(fixture, runId)));

        // Decodes the record lines of a journal image (header at index 0) —
        // used both for the on-disk journal and for an in-memory prefix a
        // boundary sweep is about to interrupt at.
        private static List<JournalRecord> DecodeRecords(IReadOnlyList<string> lines)
        {
            var records = new List<JournalRecord>();
            for (var i = 1; i < lines.Count; i++)
            {
                var read = JournalCodec.ReadRecord(lines[i], i);
                if (read.Status == JournalLineStatus.Decoded)
                {
                    records.Add(read.Record!);
                }
            }
            return records;
        }

        // The snapshot a RELOAD sees — always a fresh FileRunStore over the
        // on-disk journal; a store instance that appended the records keeps
        // its own fold cache and would hide a truncation.
        private RunSnapshot Load(string runId)
        {
            var loaded = new FileRunStore(_root, "reload-check").Load(runId);
            Assert.IsTrue(loaded.Ok, "Load failed.");
            return loaded.Value!;
        }

        // ── T-W1.8-10: the fixture's Tick budget follows the injected timer ──

        // The Unity 6000.0 intermittent failure this remediation removes:
        // with the production StopwatchMonotonicTimer a >50 ms real-time
        // stall inside the first Tick ended it after only part of the
        // reordered §N.4 chain ran, and the "execute:" order assertion
        // saw a partial sequence. The fake timer makes the same budget
        // states deterministic — an exhausted Tick starts nothing, an
        // unspent Tick drives the whole chain — so per-Tick progress no
        // longer depends on wall-clock time.
        [Test]
        public void TickProgressFollowsTheInjectedMonotonicTimer()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.tk.s0", live.Events), 'd');
            live.Register(() => new MarkerOperation("test.tk.s1", live.Events), 'e');
            live.Register(() => new MarkerOperation("test.tk.s2", live.Events), 'c');
            var steps = new[]
            {
                Step(0, "n1", "test.tk.s0", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(1, "n2", "test.tk.s1", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
                Step(2, "n3", "test.tk.s2", 'c',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
            };
            var runId = AcceptRun(live, Plan(steps));

            // An exhausted budget defers every pending step — the failure
            // mode the real timer produced intermittently.
            live.MonotonicAdvancePerReadMs = RunLoop.DefaultTickBudgetMs;
            Ticks(live, 1);
            var stalled = Load(runId);
            Assert.AreEqual(RunState.Running, stalled.State);
            Assert.AreEqual(
                0, stalled.Steps.Count,
                "an exhausted Tick leaves every step without a journal record");
            Assert.AreEqual(0, live.Events.Count, "no Operation ran in the Tick");

            // A fresh Tick whose timer stays under the budget drives the
            // whole §N.4 test 2 chain, whatever real time passed while
            // the Run was alive.
            live.MonotonicAdvancePerReadMs = 0;
            Ticks(live, 1);
            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(
                new[] { "execute:test.tk.s0", "execute:test.tk.s2", "execute:test.tk.s1" },
                live.Events.Where(e => e.StartsWith("execute:")).ToArray());
        }

        // ── SPEC §F2 test 2 + §F4 item 5: replay equals the live state ──

        [Test]
        public void ReplayEqualsLiveStateAndResumeContinuesAfterSuccess()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rs.m1", live.Events), 'd');
            live.Register(() => new MarkerOperation("test.rs.m2", live.Events), 'e');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.rs.m2", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            // Interrupt after step_succeeded(0): accepted, started,
            // started(0), succeeded(0) — 4 records.
            Ticks(live, 1);
            TruncateJournal(live, runId, 4);
            var before = Load(runId);
            Assert.AreEqual(RunState.Running, before.State);
            Assert.AreEqual(1, before.Steps.Count);
            Assert.AreEqual(StepState.Succeeded, before.Steps[0].State);
            Assert.AreEqual(
                "done:test.rs.m1",
                before.Steps[0].Outputs!.ToToken()["mark"]!.Value<string>());
            Assert.AreEqual(0, before.LastStepIndex);
            Assert.IsFalse(before.Ended);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.rs.m1", fresh.Events), 'd');
            fresh.Register(() => new MarkerOperation("test.rs.m2", fresh.Events), 'e');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok, "resume failed");
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 3);

            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.IsTrue(after.Ended);
            // The succeeded step was never re-run; step 1 ran exactly once.
            Assert.IsFalse(
                fresh.Events.Contains("execute:test.rs.m1"),
                "the completed step re-ran after resume");
            Assert.AreEqual(1, fresh.Events.Count(e => e == "execute:test.rs.m2"));
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.RunResumed).Count);
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.StepStarted)
                    .Count(r => r.Step == 0),
                "only the pre-interruption step_started exists — no re-execution");
        }

        // ── §C2 test 2: Poll resumes on a fresh instance from the token ──

        [Test]
        public void ResumeWaitingPollsFreshInstanceWithResumeTokenOnly()
        {
            var seen = new List<JsonValue?>();
            var live = NewFixture();
            live.Register(() => new WaitOnceOperation("test.rs.wait", seen), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.wait", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    timeoutSeconds: 600),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1); // Execute -> StepWaiting{token {"tick":0}}
            var before = Load(runId);
            Assert.AreEqual(StepState.Waiting, before.Steps[0].State);
            Assert.AreEqual(
                "{\"tick\":0}", before.Steps[0].ResumeToken!.CanonicalText);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new WaitOnceOperation("test.rs.wait", seen), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 1);
            _clock.Advance(TimeSpan.FromSeconds(1));
            Ticks(fresh, 3);

            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            // A fresh instance saw exactly the journaled token — and only
            // the token (never the dropped step state).
            Assert.GreaterOrEqual(seen.Count, 1);
            Assert.AreEqual("{\"tick\":0}", seen[0]!.CanonicalText);
        }

        // ── §E6 item 1 (fixture d): the journaled deadline is kept ──────

        [Test]
        public void ResumeWaitingKeepsJournaledDeadline()
        {
            var live = NewFixture();
            live.Register(() => new ForeverWaitableOperation("test.rs.forever"), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.forever", 'd', timeoutSeconds: 30),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            var before = Load(runId);
            var deadline = before.Steps[0].Deadline;
            Assert.IsNotNull(deadline, "the deadline is journaled");
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            var op = new ForeverWaitableOperation("test.rs.forever");
            fresh.Register(() => op, 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Ticks(fresh, 2);
            Assert.AreEqual(RunState.Running, Load(runId).State);
            Assert.AreEqual(0, op.CancelCalls, "Cancel before the journaled deadline");

            // The deadline was step-start + 30 s, journaled — advancing
            // PAST it expires the wait; a reload must never reset it.
            _clock.Advance(TimeSpan.FromSeconds(31));
            Ticks(fresh, 3);
            var after = Load(runId);
            Assert.AreEqual(RunState.Failed, after.State);
            Assert.AreEqual(1, op.CancelCalls, "Cancel exactly once on expiry");
            var journal = JournalRecords(fresh, runId);
            var failed = RecordsOfType(journal, JournalRecordType.StepFailed);
            Assert.IsTrue(failed.Count > 0);
            Assert.IsTrue(
                ((JObject)failed.Last().Data.ToToken())["error"]!
                    .Value<string>() == "OW-RUN-004");
        }

        // ── Fixture (b): a decided retry resumes after not-before ───────

        [Test]
        public void ResumeRetryBackoffPreservesAttemptAndNotBefore()
        {
            var live = NewFixture();
            live.Register(() => new FlakyOperation("test.rs.flaky", live.Events, 1), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(3, 60_000, 2.0, 60_000)),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1); // attempt 1 fails retryable -> step_failed{retry{2, notBefore}}
            var before = Load(runId);
            Assert.AreEqual(StepState.Running, before.Steps[0].State);
            Assert.AreEqual(2, before.Steps[0].Attempt);
            Assert.IsNotNull(before.Steps[0].RetryNotBefore);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            var op = new FlakyOperation("test.rs.flaky", fresh.Events, 0);
            fresh.Register(() => op, 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 2);
            Assert.AreEqual(0, op.ExecuteCalls, "the retry fired before not-before");

            _clock.Advance(TimeSpan.FromSeconds(61));
            Ticks(fresh, 3);
            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(1, op.ExecuteCalls);
            var journal = JournalRecords(fresh, runId);
            // The resumed attempt ran at attempt 2 — the journaled value.
            var restarted = RecordsOfType(journal, JournalRecordType.StepStarted);
            Assert.AreEqual(2, restarted.Last().Attempt);
        }

        // ── Fixture (a): interrupted finish releases the resource once ──

        [Test]
        public void ResumeInterruptedFinishReleasesResourceOnce()
        {
            var live = NewFixture();
            live.Register(() => new ResourceMarkerOperation("test.rs.res"), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.res", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            // Interrupt between resource_acquired and Run end: drop the
            // release and the terminal record — 5 records remain
            // (accepted, started, started, acquired, succeeded).
            TruncateJournal(live, runId, 5);
            var before = Load(runId);
            Assert.AreEqual(1, before.OpenResources.Count);
            Assert.IsFalse(before.Ended);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new ResourceMarkerOperation("test.rs.res"), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Ticks(fresh, 3);
            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(0, after.OpenResources.Count);
            Assert.AreEqual(1, fresh.Released.Count, "released exactly once");
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.ResourceReleased).Count);
        }

        // ── Fixture (c): the resumed step uses the restored resource ────

        [Test]
        public void ResumeWaitingStepUsesRestoredResourceReleasedOnce()
        {
            var live = NewFixture();
            live.Register(() => new ResourceWaitableOperation("test.rs.resw", live.Events), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.resw", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1); // Execute: acquire + StepWaiting — resource outstanding
            var before = Load(runId);
            Assert.AreEqual(1, before.OpenResources.Count);
            Assert.AreEqual(StepState.Waiting, before.Steps[0].State);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new ResourceWaitableOperation("test.rs.resw", fresh.Events), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Ticks(fresh, 3);
            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(0, after.OpenResources.Count);
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.ResourceReleased).Count,
                "the restored resource was released exactly once");
            // The resumed step released it through the registry itself —
            // the release seam never double-released.
            Assert.AreEqual(0, fresh.Released.Count);
            Assert.IsTrue(fresh.Events.Contains("released:test.rs.resw"));
        }

        // ── §F5: StepStarted-only branches ──────────────────────────────

        [Test]
        public void ResumeStartedOnlyIdempotentReexecutes()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rs.m1", live.Events), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            TruncateJournal(live, runId, 3); // step_started(0) is last
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.rs.m1", fresh.Events), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 3);
            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(1, fresh.Events.Count(e => e == "execute:test.rs.m1"));
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(2, RecordsOfType(journal, JournalRecordType.StepStarted).Last().Attempt);
        }

        [Test]
        public void ResumeStartedOnlyUnsafeIsNeedsReview()
        {
            var live = NewFixture();
            live.Register(
                () => new MarkerOperation(
                    "test.rs.unsafe", live.Events, Idempotency.Unsafe),
                'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.unsafe", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Unsafe),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            TruncateJournal(live, runId, 3); // StepStarted only
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(
                () => new MarkerOperation(
                    "test.rs.unsafe", fresh.Events, Idempotency.Unsafe),
                'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);
            Ticks(fresh, 2);
            var after = Load(runId);
            Assert.AreEqual(RunState.NeedsReview, after.State);
            Assert.AreEqual(StepState.NeedsReview, after.Steps[0].State);
            Assert.AreEqual(
                0, fresh.Events.Count(e => e == "execute:test.rs.unsafe"),
                "an Unsafe step is never re-executed");
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                0, RecordsOfType(journal, JournalRecordType.RunResumed).Count);
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.StepNeedsReview).Count);
        }

        [Test]
        public void ResumeStartedOnlyReconcileUnknownIsNeedsReview()
        {
            var live = NewFixture();
            live.Register(
                () => new ReconcilingOperation(
                    "test.rs.rec", live.Events,
                    new ReconcileResult.Unknown("cannot prove")),
                'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.rec", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            TruncateJournal(live, runId, 3);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(
                () => new ReconcilingOperation(
                    "test.rs.rec", fresh.Events,
                    new ReconcileResult.Unknown("cannot prove")),
                'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);
            var after = Load(runId);
            Assert.AreEqual(RunState.NeedsReview, after.State);
            Assert.AreEqual(0, fresh.Events.Count(e => e == "execute:test.rs.rec"));
        }

        [Test]
        public void ResumeStartedOnlyReconcileCompletedAdoptsOutputs()
        {
            var live = NewFixture();
            live.Register(
                () => new ReconcilingOperation(
                    "test.rs.rec", live.Events, new ReconcileResult.NotApplied()),
                'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.rec", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Reconcilable),
                Step(1, "n2", "test.rs.tail", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            live.Register(() => new MarkerOperation("test.rs.tail", live.Events), 'e');
            Ticks(live, 1);
            TruncateJournal(live, runId, 3); // step_started(0) only
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(
                () => new ReconcilingOperation(
                    "test.rs.rec", fresh.Events,
                    new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue> { ["mark"] = Json("\"adopted\"") })),
                'd');
            fresh.Register(() => new MarkerOperation("test.rs.tail", fresh.Events), 'e');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 3);
            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            // Reconcile's proven outputs were adopted — never re-executed.
            Assert.AreEqual(
                0, fresh.Events.Count(e => e == "execute:test.rs.rec"));
            Assert.AreEqual(
                "adopted",
                after.Steps[0].Outputs!.ToToken()["mark"]!.Value<string>());
            var journal = JournalRecords(fresh, runId);
            var succeeded = RecordsOfType(journal, JournalRecordType.StepSucceeded);
            Assert.IsTrue(
                ((JObject)succeeded.First().Data.ToToken())["reconciled"]!
                    .Value<bool>());
        }

        [Test]
        public void ResumeStartedOnlyReconcileNotAppliedReexecutes()
        {
            var live = NewFixture();
            live.Register(
                () => new ReconcilingOperation(
                    "test.rs.rec", live.Events, new ReconcileResult.NotApplied()),
                'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.rec", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            TruncateJournal(live, runId, 3); // step_started(0) only
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            var op = new ReconcilingOperation(
                "test.rs.rec", fresh.Events, new ReconcileResult.NotApplied());
            fresh.Register(() => op, 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 3);
            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            // NotApplied is not adoption: the §F5 consult ran once with the
            // journaled Preflight snapshot and the step re-executed
            // exactly once.
            Assert.AreEqual(1, op.ReconcileCalls);
            Assert.AreEqual("{}", op.SeenSnapshot!.CanonicalText);
            Assert.AreEqual(
                1, fresh.Events.Count(e => e == "execute:test.rs.rec"));
            Assert.AreEqual(
                "done:test.rs.rec",
                after.Steps[0].Outputs!.ToToken()["mark"]!.Value<string>());
            var journal = JournalRecords(fresh, runId);
            // A fresh step_started{attempt:2} is the write-ahead record of
            // the re-execution — journaled before the attempt's first side
            // effect (the outputs its step_succeeded persists).
            var restartedAt = journal.FindIndex(
                r => r.Type == JournalRecordType.StepStarted && r.Attempt == 2);
            var succeededAt = journal.FindIndex(
                r => r.Type == JournalRecordType.StepSucceeded);
            Assert.GreaterOrEqual(
                restartedAt, 0, "the re-execution journaled a fresh step_started");
            Assert.GreaterOrEqual(succeededAt, 0);
            Assert.Less(
                restartedAt, succeededAt,
                "the fresh step_started precedes the re-execution's side effect");
        }

        // ── §N.4 test 2: Transitions drive, never index + 1 ─────────────

        [Test]
        public void ResumeFollowsReorderedTransitionsTable()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rs.s0", live.Events), 'd');
            live.Register(() => new MarkerOperation("test.rs.s1", live.Events), 'e');
            live.Register(() => new MarkerOperation("test.rs.s2", live.Events), 'c');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.s0", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(1, "n2", "test.rs.s1", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
                Step(2, "n3", "test.rs.s2", 'c',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1); // s0 -> s2 -> s1 per the table, never index + 1
            Assert.AreEqual(
                new[] { "execute:test.rs.s0", "execute:test.rs.s2", "execute:test.rs.s1" },
                live.Events.Where(e => e.StartsWith("execute:")).ToArray());
            // Interrupt after step_succeeded(2): records accepted, started,
            // started(0), succeeded(0), started(2), succeeded(2).
            TruncateJournal(live, runId, 6);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.rs.s0", fresh.Events), 'd');
            fresh.Register(() => new MarkerOperation("test.rs.s1", fresh.Events), 'e');
            fresh.Register(() => new MarkerOperation("test.rs.s2", fresh.Events), 'c');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Ticks(fresh, 3);
            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            // The table sent step 2 -> step 1; nothing else ran.
            Assert.AreEqual(
                new[] { "execute:test.rs.s1" },
                fresh.Events.Where(e => e.StartsWith("execute:")).ToArray());
        }

        // ── Store-surfaced NeedsReview cases (§F0/§F2) ─────────────────

        [Test]
        public void ResumeMiddleCorruptionIsNeedsReview()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rs.m1", live.Events), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            live.Coordinator.Dispose();

            // Corrupt a MIDDLE journal line — not the droppable tail.
            var journal = JournalPath(live, runId);
            var lines = File.ReadAllLines(journal);
            lines[2] = "{\"definitely\":\"not a record\"}";
            File.WriteAllLines(journal, lines);

            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.rs.m1", fresh.Events), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);
            Assert.AreEqual(
                0, fresh.Events.Count(e => e == "execute:test.rs.m1"),
                "no automatic re-execution on corruption");
        }

        [Test]
        public void ResumeUnknownFormatVersionIsNeedsReview()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rs.m1", live.Events), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            live.Coordinator.Dispose();

            var journal = JournalPath(live, runId);
            var lines = File.ReadAllLines(journal);
            var header = (JObject)JObject.Parse(lines[0]);
            header["formatVersion"] = 99;
            lines[0] = header.ToString(Newtonsoft.Json.Formatting.None);
            File.WriteAllLines(journal, lines);

            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.rs.m1", fresh.Events), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);
        }

        [Test]
        public void ResumeUninterpretableRequiredRecordIsNeedsReview()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rs.m1", live.Events), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            TruncateJournal(live, runId, 3);
            // A required record the fold cannot interpret (malformed
            // retry member) — journaled with a valid crc so the store
            // decodes it and the fold must refuse it (SPEC §F0).
            var line = JournalCodec.WriteRecord(
                new JournalRecord(
                    4, "2026-11-08T00:00:00.000Z", Session,
                    JournalRecordType.StepFailed, 0, null,
                    JsonValue.From(
                        JObject.Parse("{\"error\":\"OW-UNITY-008\",\"retry\":{\"nextAttempt\":\"x\"}}"))));
            File.AppendAllLines(JournalPath(live, runId), new[] { line });
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.rs.m1", fresh.Events), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);
            Assert.AreEqual(0, fresh.Events.Count(e => e == "execute:test.rs.m1"));
        }

        // ── Fresh-loop reload: pending cancel processed after resume ────

        [Test]
        public void ResumeProcessesJournaledCancelNextTick()
        {
            var live = NewFixture();
            live.Register(() => new ForeverWaitableOperation("test.rs.forever"), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.forever", 'd'),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            var cancelled = live.Coordinator.Cancel(runId, "stop it");
            Assert.IsTrue(cancelled.Ok);
            var before = Load(runId);
            Assert.IsTrue(before.CancelPending);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            var op = new ForeverWaitableOperation("test.rs.forever");
            fresh.Register(() => op, 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 2);
            var after = Load(runId);
            Assert.AreEqual(RunState.Cancelled, after.State);
            Assert.AreEqual(1, op.CancelCalls, "the restored Waiting step was cancelled once");
        }

        // ── Fresh-loop reload: interrupted failure finish completes ─────

        [Test]
        public void ResumeInterruptedFailureFinishCompletesFailed()
        {
            var live = NewFixture();
            live.Register(() => new FlakyOperation("test.rs.flaky", live.Events, 9), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.flaky", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1); // terminal step_failed (MaxAttempts=1), crash before finish
            TruncateJournal(live, runId, 4);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new FlakyOperation("test.rs.flaky", fresh.Events, 9), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            var after = Load(runId);
            Assert.AreEqual(RunState.Failed, after.State);
            Assert.IsTrue(after.Ended);
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                0, RecordsOfType(journal, JournalRecordType.RunResumed).Count,
                "an interrupted finish is completed, not resumed");
            Assert.AreEqual(
                0, fresh.Events.Count(e => e == "execute:test.rs.flaky"),
                "the failed step never re-ran");
        }

        // ── Fresh-loop reload: a suspended Run behind the Editor gate ──

        // SPEC §F4/§E3 item 4: a suspended Run adopted while the gate is
        // still blocked has no rebuilt projection; the resume that clears
        // the gate must rebuild and evaluate the journal BEFORE the Run
        // may transition to Running — never drive an empty projection
        // that re-executes completed steps and loses the step position.
        [Test]
        public void ResumeSuspendedBehindGateRebuildsBeforeRunning()
        {
            var seen = new List<JsonValue?>();
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.sg.m1", live.Events), 'd');
            live.Register(() => new WaitOnceOperation("test.sg.wait", seen), 'e');
            var steps = new[]
            {
                Step(0, "n1", "test.sg.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.sg.wait", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    timeoutSeconds: 600),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1); // step 0 succeeds; step 1 goes Waiting
            _editor.IsEditMode = false;
            Ticks(live, 1); // the head-of-Tick gate suspends the Run
            var before = Load(runId);
            Assert.AreEqual(RunState.Suspended, before.State);
            Assert.AreEqual(StepState.Waiting, before.Steps[1].State);
            live.Coordinator.Dispose();

            // The next session resumes with the gate still blocked: the
            // Run is adopted Suspended — nothing is journaled, rebuilt or
            // driven while the gate holds.
            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.sg.m1", fresh.Events), 'd');
            fresh.Register(() => new WaitOnceOperation("test.sg.wait", seen), 'e');
            var blocked = fresh.Loop.Resume(runId);
            Assert.IsTrue(blocked.Ok);
            Assert.AreEqual(RunState.Suspended, blocked.Value!.State);
            Ticks(fresh, 1);
            Assert.AreEqual(RunState.Suspended, Load(runId).State);
            Assert.AreEqual(
                0,
                RecordsOfType(JournalRecords(fresh, runId), JournalRecordType.RunResumed)
                    .Count,
                "a blocked resume journals nothing");

            _editor.IsEditMode = true;
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 1);
            _clock.Advance(TimeSpan.FromSeconds(1));
            Ticks(fresh, 3);

            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(
                0, fresh.Events.Count(e => e == "execute:test.sg.m1"),
                "the completed step re-ran after the gate cleared");
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                1,
                RecordsOfType(journal, JournalRecordType.StepStarted).Count(r => r.Step == 0),
                "step 0's journaled success stands — the position comes from the journal");
            Assert.AreEqual(
                1,
                RecordsOfType(journal, JournalRecordType.StepStarted).Count(r => r.Step == 1),
                "the Waiting step resumed through Poll — never re-executed");
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.RunResumed).Count);
            Assert.AreEqual("{\"tick\":0}", seen[0]!.CanonicalText);
            Assert.AreEqual(StepState.Succeeded, after.Steps[1].State);
        }

        // A suspended Run whose last step record is a bare StepStarted —
        // the crash window between the write-ahead record and the
        // suspended attempt's outcome: the §F5 consult still decides at
        // the delayed resume, so an Unsafe step ends NeedsReview and is
        // never executed.
        [Test]
        public void ResumeSuspendedStartedOnlyUnsafeDecidesNeedsReview()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.su.m1", live.Events), 'd');
            live.Register(
                () => new MarkerOperation(
                    "test.su.unsafe", live.Events, Idempotency.Unsafe), 'e');
            var steps = new[]
            {
                Step(0, "n1", "test.su.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.su.unsafe", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Unsafe),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1);
            // Interruption shape: step 1's StepStarted is its last record
            // and the Run is Suspended — keep the prefix through
            // step_started(1) (accepted, run_started, started(0),
            // succeeded(0), started(1)) and append the run_suspended the
            // event-time path (SPEC §1.4) would journal in the window.
            TruncateJournal(live, runId, 5);
            File.AppendAllLines(
                JournalPath(live, runId),
                new[]
                {
                    JournalCodec.WriteRecord(new JournalRecord(
                        6, "2026-11-08T00:00:01.000Z", Session,
                        JournalRecordType.RunSuspended, null, null,
                        JsonValue.From(JObject.Parse("{\"reason\":\"play_mode\"}")))),
                });
            live.Coordinator.Dispose();

            _editor.IsEditMode = false;
            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.su.m1", fresh.Events), 'd');
            fresh.Register(
                () => new MarkerOperation(
                    "test.su.unsafe", fresh.Events, Idempotency.Unsafe), 'e');
            var blocked = fresh.Loop.Resume(runId);
            Assert.IsTrue(blocked.Ok);
            Assert.AreEqual(RunState.Suspended, blocked.Value!.State);

            _editor.IsEditMode = true;
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);
            Ticks(fresh, 2);

            var after = Load(runId);
            Assert.AreEqual(RunState.NeedsReview, after.State);
            Assert.AreEqual(StepState.Succeeded, after.Steps[0].State);
            Assert.AreEqual(StepState.NeedsReview, after.Steps[1].State);
            Assert.AreEqual(
                0,
                fresh.Events.Count(e => e.StartsWith("execute:", StringComparison.Ordinal)),
                "no step re-executed — the Unsafe consult decided first");
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.StepNeedsReview).Count);
            Assert.AreEqual(
                0, RecordsOfType(journal, JournalRecordType.RunResumed).Count,
                "the §F5 verdict precedes run_resumed — the Run never resumed");
        }

        // The same suspended started-only shape with a Reconcilable
        // step: the §F5 consult decides uniformly for every outcome —
        // a Completed verdict adopts the proven outputs while the Run
        // still stands Suspended, and that successful adoption is a
        // successful recovery decision: run_resumed is journaled once,
        // the Run transitions to Running once and continues to the next
        // step in the same flow — never parked for a second Resume.
        [Test]
        public void ResumeSuspendedStartedOnlyReconcileCompletedAdoptsAndContinues()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.sa.m1", live.Events), 'd');
            live.Register(
                () => new ReconcilingOperation(
                    "test.sa.rec", live.Events, new ReconcileResult.NotApplied()), 'e');
            live.Register(() => new MarkerOperation("test.sa.tail", live.Events), 'a');
            var steps = new[]
            {
                Step(0, "n1", "test.sa.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.sa.rec", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 },
                    idempotency: Idempotency.Reconcilable),
                Step(2, "n3", "test.sa.tail", 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 3);
            // The same interruption shape as the Unsafe case: the
            // started-only step 1 is last and the Run is Suspended.
            TruncateJournal(live, runId, 5);
            File.AppendAllLines(
                JournalPath(live, runId),
                new[]
                {
                    JournalCodec.WriteRecord(new JournalRecord(
                        6, "2026-11-08T00:00:01.000Z", Session,
                        JournalRecordType.RunSuspended, null, null,
                        JsonValue.From(JObject.Parse("{\"reason\":\"play_mode\"}")))),
                });
            live.Coordinator.Dispose();

            _editor.IsEditMode = false;
            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.sa.m1", fresh.Events), 'd');
            var rec = new ReconcilingOperation(
                "test.sa.rec", fresh.Events,
                new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue> { ["mark"] = Json("\"adopted\"") }));
            fresh.Register(() => rec, 'e');
            fresh.Register(() => new MarkerOperation("test.sa.tail", fresh.Events), 'a');
            var blocked = fresh.Loop.Resume(runId);
            Assert.IsTrue(blocked.Ok);
            Assert.AreEqual(RunState.Suspended, blocked.Value!.State);
            Assert.AreEqual(
                6, JournalRecords(fresh, runId).Count,
                "a blocked resume journals nothing");

            _editor.IsEditMode = true;
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(
                RunState.Running, resumed.Value!.State,
                "the successful adoption resumed the Run — no second Resume");
            Ticks(fresh, 3);

            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(1, rec.ReconcileCalls, "the §F5 consult ran once");
            Assert.AreEqual(
                0, fresh.Events.Count(e => e == "execute:test.sa.rec"),
                "the adopted step never re-executed");
            Assert.AreEqual(
                "adopted",
                after.Steps[1].Outputs!.ToToken()["mark"]!.Value<string>());
            Assert.AreEqual(
                1, fresh.Events.Count(e => e == "execute:test.sa.tail"),
                "the Run continued to the next step in the same flow");
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.RunResumed).Count,
                "run_resumed journaled exactly once");
            var adoptedSuccess = RecordsOfType(journal, JournalRecordType.StepSucceeded)
                .First(r => r.Step == 1);
            Assert.IsTrue(
                ((JObject)adoptedSuccess.Data.ToToken())["reconciled"]!.Value<bool>());
        }

        // The Reconcile -> NotApplied row of the same shape: the §F5
        // consult allows re-execution, the cleared resume journals
        // run_resumed and transitions to Running, and the step
        // re-executes exactly once — at the journaled attempt + 1 —
        // after the transition.
        [Test]
        public void ResumeSuspendedStartedOnlyReconcileNotAppliedReexecutesOnce()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.sb.m1", live.Events), 'd');
            live.Register(
                () => new ReconcilingOperation(
                    "test.sb.rec", live.Events, new ReconcileResult.NotApplied()), 'e');
            live.Register(() => new MarkerOperation("test.sb.tail", live.Events), 'a');
            var steps = new[]
            {
                Step(0, "n1", "test.sb.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.sb.rec", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 },
                    idempotency: Idempotency.Reconcilable),
                Step(2, "n3", "test.sb.tail", 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 3);
            TruncateJournal(live, runId, 5);
            File.AppendAllLines(
                JournalPath(live, runId),
                new[]
                {
                    JournalCodec.WriteRecord(new JournalRecord(
                        6, "2026-11-08T00:00:01.000Z", Session,
                        JournalRecordType.RunSuspended, null, null,
                        JsonValue.From(JObject.Parse("{\"reason\":\"play_mode\"}")))),
                });
            live.Coordinator.Dispose();

            _editor.IsEditMode = false;
            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.sb.m1", fresh.Events), 'd');
            var rec = new ReconcilingOperation(
                "test.sb.rec", fresh.Events, new ReconcileResult.NotApplied());
            fresh.Register(() => rec, 'e');
            fresh.Register(() => new MarkerOperation("test.sb.tail", fresh.Events), 'a');
            var blocked = fresh.Loop.Resume(runId);
            Assert.IsTrue(blocked.Ok);
            Assert.AreEqual(RunState.Suspended, blocked.Value!.State);
            Assert.AreEqual(
                6, JournalRecords(fresh, runId).Count,
                "a blocked resume journals nothing");

            _editor.IsEditMode = true;
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 3);

            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(1, rec.ReconcileCalls, "the §F5 consult ran once");
            Assert.AreEqual(
                1, fresh.Events.Count(e => e == "execute:test.sb.rec"),
                "the step re-executed exactly once after the transition");
            Assert.AreEqual(
                1, fresh.Events.Count(e => e == "execute:test.sb.tail"));
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.RunResumed).Count);
            var resumedAt = journal.FindIndex(
                r => r.Type == JournalRecordType.RunResumed);
            var restartedAt = journal.FindIndex(
                r => r.Type == JournalRecordType.StepStarted && r.Attempt == 2);
            Assert.GreaterOrEqual(restartedAt, 0);
            Assert.Less(
                resumedAt, restartedAt,
                "the re-execution followed the journaled run_resumed — attempt + 1");
        }

        // The Reconcile -> Unknown row: uncertain evidence is never an
        // automatic outcome — the suspended Run goes NeedsReview on the
        // cleared resume, no run_resumed is journaled and nothing
        // executes.
        [Test]
        public void ResumeSuspendedStartedOnlyReconcileUnknownIsNeedsReview()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.sc.m1", live.Events), 'd');
            live.Register(
                () => new ReconcilingOperation(
                    "test.sc.rec", live.Events, new ReconcileResult.NotApplied()), 'e');
            var steps = new[]
            {
                Step(0, "n1", "test.sc.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.sc.rec", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 2);
            TruncateJournal(live, runId, 5);
            File.AppendAllLines(
                JournalPath(live, runId),
                new[]
                {
                    JournalCodec.WriteRecord(new JournalRecord(
                        6, "2026-11-08T00:00:01.000Z", Session,
                        JournalRecordType.RunSuspended, null, null,
                        JsonValue.From(JObject.Parse("{\"reason\":\"play_mode\"}")))),
                });
            live.Coordinator.Dispose();

            _editor.IsEditMode = false;
            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.sc.m1", fresh.Events), 'd');
            var rec = new ReconcilingOperation(
                "test.sc.rec", fresh.Events,
                new ReconcileResult.Unknown("cannot prove"));
            fresh.Register(() => rec, 'e');
            var blocked = fresh.Loop.Resume(runId);
            Assert.IsTrue(blocked.Ok);
            Assert.AreEqual(RunState.Suspended, blocked.Value!.State);

            _editor.IsEditMode = true;
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);
            Ticks(fresh, 2);

            var after = Load(runId);
            Assert.AreEqual(RunState.NeedsReview, after.State);
            Assert.AreEqual(StepState.NeedsReview, after.Steps[1].State);
            Assert.AreEqual(1, rec.ReconcileCalls, "the §F5 consult ran once");
            Assert.AreEqual(
                0, fresh.Events.Count(e => e.StartsWith("execute:", StringComparison.Ordinal)),
                "uncertain evidence never re-executes");
            var journal = JournalRecords(fresh, runId);
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.StepNeedsReview).Count);
            Assert.AreEqual(
                0, RecordsOfType(journal, JournalRecordType.RunResumed).Count,
                "a NeedsReview verdict means the Run never resumed");
        }

        // The Reconcile -> Completed row whose adopted outputs fail
        // validation: the recovery decision is still a resume, so
        // run_resumed and the Suspended -> Running transition are
        // journaled BEFORE the adopted success is applied — the failed
        // validation then ends the Run through the normal Running ->
        // Failed finish (SPEC §E1 has no Suspended -> Failed pair):
        // step_failed carries the step's OW-RUN-007, every still-Pending
        // step is journaled step_skipped, the held Run resource is
        // released exactly once and the exclusion is freed.
        [Test]
        public void ResumeSuspendedStartedOnlyReconcileCompletedInvalidOutputsFailsRun()
        {
            var live = NewFixture();
            live.Register(() => new ResourceMarkerOperation("test.sd.res"), 'd');
            live.Register(
                () => new ReconcilingOperation(
                    "test.sd.rec", live.Events, new ReconcileResult.NotApplied()), 'e');
            live.Register(() => new MarkerOperation("test.sd.tail", live.Events), 'a');
            var steps = new[]
            {
                Step(0, "n1", "test.sd.res", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.sd.rec", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 },
                    idempotency: Idempotency.Reconcilable),
                Step(2, "n3", "test.sd.tail", 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 3);
            // The suspended started-only shape, plus step 0's still-open
            // Run resource: accepted, run_started, started(0),
            // resource_acquired, succeeded(0), started(1).
            TruncateJournal(live, runId, 6);
            File.AppendAllLines(
                JournalPath(live, runId),
                new[]
                {
                    JournalCodec.WriteRecord(new JournalRecord(
                        7, "2026-11-08T00:00:01.000Z", Session,
                        JournalRecordType.RunSuspended, null, null,
                        JsonValue.From(JObject.Parse("{\"reason\":\"play_mode\"}")))),
                });
            live.Coordinator.Dispose();

            _editor.IsEditMode = false;
            var fresh = NewFixture(Session2);
            fresh.Register(() => new ResourceMarkerOperation("test.sd.res"), 'd');
            var rec = new ReconcilingOperation(
                "test.sd.rec", fresh.Events,
                // The verdict proves completion but restores no value
                // for the declared "mark" port — Collect fails
                // OW-RUN-007.
                new ReconcileResult.Completed(new Dictionary<string, JsonValue>()));
            fresh.Register(() => rec, 'e');
            fresh.Register(() => new MarkerOperation("test.sd.tail", fresh.Events), 'a');
            var blocked = fresh.Loop.Resume(runId);
            Assert.IsTrue(blocked.Ok);
            Assert.AreEqual(RunState.Suspended, blocked.Value!.State);

            _editor.IsEditMode = true;
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(
                RunState.Failed, resumed.Value!.State,
                "the resume journaled run_resumed, then the failed adoption ended the Run");

            var after = Load(runId);
            Assert.AreEqual(RunState.Failed, after.State);
            Assert.IsTrue(after.Ended);
            Assert.AreEqual(1, rec.ReconcileCalls, "the §F5 consult ran once");
            Assert.AreEqual(
                0, fresh.Events.Count(e => e.StartsWith("execute:", StringComparison.Ordinal)),
                "no step re-executed");
            var journal = JournalRecords(fresh, runId);
            var resumedAt = journal.FindIndex(
                r => r.Type == JournalRecordType.RunResumed);
            var failedAt = journal.FindIndex(
                r => r.Type == JournalRecordType.StepFailed && r.Step == 1);
            Assert.GreaterOrEqual(resumedAt, 0);
            Assert.GreaterOrEqual(failedAt, 0);
            Assert.Less(
                resumedAt, failedAt,
                "run_resumed is journaled before the failed adoption's step_failed");
            Assert.AreEqual(
                "OW-RUN-007",
                ((JObject)journal[failedAt].Data.ToToken())["error"]!.Value<string>(),
                "step_failed carries the output-validation code");
            Assert.AreEqual(
                1,
                RecordsOfType(journal, JournalRecordType.StepSkipped).Count(r => r.Step == 2),
                "the still-Pending tail step is journaled step_skipped");
            var finished = RecordsOfType(journal, JournalRecordType.RunFinished);
            Assert.AreEqual(1, finished.Count);
            var finishedData = (JObject)finished[0].Data.ToToken();
            Assert.AreEqual("failed", finishedData["state"]!.Value<string>());
            Assert.AreEqual(
                "OW-RUN-007",
                ((JObject)finishedData["summary"]!)["code"]!.Value<string>(),
                "run_finished{failed} carries the step's error code");
            Assert.AreEqual(
                1, fresh.Released.Count,
                "the open Run resource was released exactly once");
            Assert.AreEqual(
                1, RecordsOfType(journal, JournalRecordType.ResourceReleased).Count);
            var reacquired = CoordinatorRunLock.TryAcquire(_root);
            Assert.IsTrue(
                reacquired.Ok && reacquired.Value is not null,
                "the exclusion is released");
            reacquired.Value!.Dispose();
        }

        // ── Fresh-loop reload: a pending review retry survives the crash ──

        // SPEC §E1/§F4: review_resolved{retry_step} is durable — a crash
        // before the next Tick restores the parked NeedsReview projection
        // so the retried step re-enters E4 step 1 through
        // ResumeReviewedStep at attempt + 1 (the pending decision
        // consumed), exactly like the uninterrupted path — never a bare
        // ExecuteStep at the journaled attempt.
        [Test]
        public void ResumeAfterReviewRetryDecisionRearmsAtNextAttempt()
        {
            var calls = new[] { 0 };
            var verdicts = new Queue<ReconcileResult>(new ReconcileResult[]
            {
                new ReconcileResult.Unknown("State cannot be proven."),
                new ReconcileResult.NotApplied(),
            });
            var live = NewFixture();
            live.Register(
                () => new ScriptFailReconcilingOperation(
                    "test.rr.review", calls, 2, verdicts), 'd');
            var steps = new[]
            {
                Step(0, "n1", "test.rr.review", 'd',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    retry: new RetryPolicy(MaxAttempts: 3, InitialBackoffMs: 500),
                    idempotency: Idempotency.Reconcilable),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 1); // attempt 1 fails retryable; Reconcile Unknown -> needs_review
            var needsReview = Load(runId);
            Assert.AreEqual(RunState.NeedsReview, needsReview.State);
            Assert.AreEqual(StepState.NeedsReview, needsReview.Steps[0].State);
            Assert.AreEqual(1, needsReview.Steps[0].Attempt);

            var resolved = live.Coordinator.ResolveReview(runId, ReviewDecision.RetryStep);
            Assert.IsTrue(resolved.Ok);
            Assert.AreEqual(RunState.Running, resolved.Value!.State);
            // The interruption boundary: review_resolved{retry_step} is
            // journaled but the re-arm's own step_started never ran.
            live.Coordinator.Dispose();

            var boundary = Load(runId);
            Assert.AreEqual(RunState.Running, boundary.State);
            Assert.AreEqual(0, boundary.ReviewRetryStep);
            Assert.AreEqual(StepState.NeedsReview, boundary.Steps[0].State);

            var fresh = NewFixture(Session2);
            fresh.Register(
                () => new ScriptFailReconcilingOperation(
                    "test.rr.review", calls, 2, verdicts), 'd');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 1); // re-arms attempt 2 -> fails -> scheduled retry
            _clock.Advance(TimeSpan.FromSeconds(2));
            Ticks(fresh, 3);

            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(
                3, calls[0], "one Execute per attempt — the reviewed step ran once");
            var journal = JournalRecords(fresh, runId);
            var started = RecordsOfType(journal, JournalRecordType.StepStarted);
            Assert.AreEqual(
                new int?[] { 1, 2, 3 },
                started.Select(r => r.Attempt).ToArray(),
                "the retried attempt runs at attempt + 1 — never the journaled attempt");
            var failed = RecordsOfType(journal, JournalRecordType.StepFailed);
            Assert.AreEqual(1, failed.Count);
            Assert.AreEqual(
                3,
                ((JObject)((JObject)failed[0].Data.ToToken())["retry"]!)["nextAttempt"]!
                    .Value<int>(),
                "retry accounting continues from the re-armed attempt");
            var succeeded = RecordsOfType(journal, JournalRecordType.StepSucceeded);
            Assert.AreEqual(3, succeeded.Last().Attempt);
        }

        // ── Interruption sweep: resume at every journal boundary ────────

        [Test]
        public void ResumeAtEveryJournalBoundary()
        {
            // One live run to completion captures the journal shape:
            // accepted, started, started(0), succeeded(0), started(1),
            // succeeded(1), finished — every prefix is a valid
            // interruption point.
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rs.m1", live.Events), 'd');
            live.Register(() => new MarkerOperation("test.rs.m2", live.Events), 'e');
            var steps = new[]
            {
                Step(0, "n1", "test.rs.m1", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "n2", "test.rs.m2", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps));
            Ticks(live, 3);
            // The live replay SPEC §F4 item 5 requires every resumed run
            // to reproduce — captured before any cut.
            var liveState = Load(runId);
            Assert.AreEqual(RunState.Succeeded, liveState.State);
            var journal = JournalPath(live, runId);
            var original = File.ReadAllLines(journal);
            var total = original.Length - 1; // minus header
            live.Coordinator.Dispose();

            for (var cut = 1; cut <= total; cut++)
            {
                var prefix = original.Take(1 + cut).ToArray();
                File.WriteAllLines(journal, prefix);
                var prefixRecords = DecodeRecords(prefix);
                var fresh = NewFixture(Session2 + cut);
                fresh.Register(() => new MarkerOperation("test.rs.m1", fresh.Events), 'd');
                fresh.Register(() => new MarkerOperation("test.rs.m2", fresh.Events), 'e');
                var resumed = fresh.Loop.Resume(runId);
                Assert.IsTrue(resumed.Ok, $"resume at record {cut} failed");
                Ticks(fresh, 5);
                var after = Load(runId);
                // The resumed run ends in the live run's outcome...
                Assert.AreEqual(
                    liveState.State, after.State, $"run outcome at record {cut}");
                Assert.AreEqual(
                    liveState.Ended, after.Ended, $"run end at record {cut}");
                Assert.AreEqual(
                    liveState.Steps.Count, after.Steps.Count,
                    $"journaled steps at record {cut}");
                foreach (var liveStep in liveState.Steps)
                {
                    var i = liveStep.Index;
                    // ...and the journal prefix alone fixes each step's
                    // §F5 outcome: a step whose step_succeeded the prefix
                    // already carries never re-runs; every other step the
                    // live run executed runs exactly once — a
                    // bare-StepStarted cut included, whose single
                    // re-execution is journaled at attempt + 1.
                    var stepRecords = prefixRecords.FindAll(r => r.Step == i);
                    var succeededInPrefix = stepRecords.Exists(
                        r => r.Type == JournalRecordType.StepSucceeded);
                    var bareStarted = !succeededInPrefix
                        && stepRecords.Count > 0
                        && stepRecords[stepRecords.Count - 1].Type
                            == JournalRecordType.StepStarted;
                    Assert.AreEqual(
                        succeededInPrefix ? 0 : 1,
                        fresh.Events.Count(
                            e => e == "execute:" + steps[i].OperationId),
                        $"step {i} executions at record {cut}");
                    var afterStep = after.Steps.First(s => s.Index == i);
                    Assert.AreEqual(
                        liveStep.State, afterStep.State,
                        $"step {i} state at record {cut}");
                    Assert.AreEqual(
                        liveStep.Attempt + (bareStarted ? 1 : 0),
                        afterStep.Attempt,
                        $"step {i} attempt at record {cut}");
                    Assert.AreEqual(
                        liveStep.Outputs?.CanonicalText,
                        afterStep.Outputs?.CanonicalText,
                        $"step {i} outputs at record {cut}");
                }
                fresh.Coordinator.Dispose();
            }
        }
    }
}
