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
    /// [Core] tests for the resume-time Registry and Editor-session checks
    /// of SPEC §F4 items 3 and 4 (T-W4.3-02,
    /// local:T-W4.3-02:resume-registry-session):
    ///
    ///   * SPEC §F4 test 2 — a journal whose session differs from the
    ///     Editor's proves an Editor restart, and a <c>scene.modify</c>
    ///     step completed after the last <c>scene.save</c> ends the resume
    ///     NeedsReview with OW-RUN-012;
    ///   * SPEC §F4 item 3 (R4) — every remaining step's recorded
    ///     descriptorHash / implementationHash is compared with the current
    ///     Registry and a removed, changed or failed registration ends the
    ///     resume NeedsReview with OW-RUN-011;
    ///   * SPEC §F4 item 4 — every session key (<c>handle</c>) the journaled
    ///     values carry is discarded across the restart, so a Ref resolved
    ///     afterwards keeps only its persistent keys.
    ///
    /// The fixture pattern follows RunResumeTests: a real
    /// <see cref="FileRunStore"/> over a temp root, a real
    /// <see cref="CoordinatorRunLock"/> and dummy Operations; a fresh
    /// fixture on the same root is the post-reload world — a different
    /// session id simulates an Editor restart. The same sources run under
    /// dotnet and Unity Mono.
    /// </summary>
    internal sealed class ResumeRegistrySessionTests
    {
        private const string Session = "session-resume-checks-1";
        private const string Session2 = "session-resume-checks-2";
        private const string WorkflowId = "example.resume.checks";

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.ResumeChecks." + Guid.NewGuid().ToString("N"));
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
                new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

            public DateTimeOffset UtcNow => _now;

            public void Advance(TimeSpan delta) => _now += delta;
        }

        /// <summary>
        /// The deterministic per-Tick monotonic timer (T-W4.3-06): every
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
        /// with another session id is the post-restart world.</summary>
        private sealed class Fixture
        {
            private readonly string _root;
            internal readonly FileRunStore Store;
            internal readonly List<RegisteredOperation> Operations =
                new List<RegisteredOperation>();
            internal readonly Dictionary<string, Func<IOperation>> OpFactories =
                new Dictionary<string, Func<IOperation>>(StringComparer.Ordinal);
            internal readonly List<string> Events = new List<string>();
            internal readonly RunCoordinator Coordinator;
            private RunLoop? _loop;

            /// <summary>
            /// Added to every Elapsed read of the fake monotonic timer the
            /// RunLoop creates for a Tick — 0 keeps the §E3 tick budget
            /// deterministic (T-W4.3-06).
            /// </summary>
            internal int MonotonicAdvancePerReadMs;

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
                releaseResource: (_, resource) => Result<Unit>.Success(Unit.Value));

            internal FakeClock Clock { get; }
            internal FakeEditorState Editor { get; }

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
        /// sets its <c>mark</c> output.</summary>
        private sealed class MarkerOperation : OpBase
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

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _events.Add("execute:" + _id);
                ctx.SetOutput("mark", "done:" + _id);
                return StepResult.Succeeded();
            }
        }

        /// <summary>Immediate that writes a Ref JSON output carrying a
        /// session <c>handle</c> — the handle-discard fixture's producer.</summary>
        private sealed class RefOutputOperation : OpBase
        {
            internal RefOutputOperation(string id, List<string> events)
            {
                _events = events;
                _id = id;
                Descriptor = Op.Define(id, 1)
                    .Summary("Outputs a Ref with a session handle.")
                    .Output("target", "unity.GameObjectRef")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            private readonly List<string> _events;
            private readonly string _id;

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _events.Add("execute:" + _id);
                ctx.SetOutput("target", Json(
                    "{\"kind\":\"gameObject\",\"handle\":-1338," +
                    "\"globalId\":\"GlobalObjectId_V1-1-aaaa-bbbb-0-0\"," +
                    "\"path\":\"/Root\",\"prefabAsset\":\"cccc\"}"));
                return StepResult.Succeeded();
            }
        }

        /// <summary>Immediate that captures the raw Ref JSON its input port
        /// resolved to — the handle-discard fixture's consumer.</summary>
        private sealed class RefCaptureOperation : OpBase
        {
            private readonly List<JsonValue> _seen;

            internal RefCaptureOperation(string id, List<JsonValue> seen)
            {
                _seen = seen;
                Descriptor = Op.Define(id, 1)
                    .Summary("Captures the resolved Ref input.")
                    .Input("target", "unity.GameObjectRef", required: true)
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _seen.Add(ctx.Get<JsonValue>("target"));
                ctx.SetOutput("mark", "seen");
                return StepResult.Succeeded();
            }
        }

        /// <summary>Immediate that writes a declared Ref output plus a
        /// Ref-shaped <c>core.Json</c> output — the producer for the
        /// type-directed discard tests.</summary>
        private sealed class RefPayloadOperation : OpBase
        {
            private readonly List<string> _events;
            private readonly string _id;

            internal RefPayloadOperation(string id, List<string> events)
            {
                _events = events;
                _id = id;
                Descriptor = Op.Define(id, 1)
                    .Summary("Outputs a Ref plus a Ref-shaped core.Json payload.")
                    .Output("target", "unity.GameObjectRef")
                    .Output("payload", "core.Json")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _events.Add("execute:" + _id);
                ctx.SetOutput("target", Json(
                    "{\"kind\":\"gameObject\",\"handle\":-1338," +
                    "\"globalId\":\"GlobalObjectId_V1-1-aaaa-bbbb-0-0\",\"path\":\"/Root\"}"));
                ctx.SetOutput("payload", Json(
                    "{\"kind\":\"asset\",\"handle\":\"customer-id\",\"note\":\"data\"}"));
                return StepResult.Succeeded();
            }
        }

        /// <summary>Immediate that captures the raw JSON of a declared Ref
        /// input, a declared <c>core.Json</c> input and two constants — the
        /// consumer for the type-directed discard tests.</summary>
        private sealed class RefJsonCaptureOperation : OpBase
        {
            private readonly Dictionary<string, JsonValue> _seen;

            internal RefJsonCaptureOperation(string id, Dictionary<string, JsonValue> seen)
            {
                _seen = seen;
                Descriptor = Op.Define(id, 1)
                    .Summary("Captures the resolved Ref and core.Json inputs.")
                    .Input("target", "unity.GameObjectRef", required: true)
                    .Input("payload", "core.Json", required: true)
                    .Input("constRef", "unity.GameObjectRef", required: true)
                    .Input("constJson", "core.Json", required: true)
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _seen["target"] = ctx.Get<JsonValue>("target");
                _seen["payload"] = ctx.Get<JsonValue>("payload");
                _seen["constRef"] = ctx.Get<JsonValue>("constRef");
                _seen["constJson"] = ctx.Get<JsonValue>("constJson");
                ctx.SetOutput("mark", "seen");
                return StepResult.Succeeded();
            }
        }

        /// <summary>A Waitable that reports its resumeToken on Poll and
        /// succeeds after the first one.</summary>
        private sealed class WaitOnceOperation : OpBase
        {
            private readonly List<JsonValue?> _seenTokens;
            private readonly JsonValue _token;

            internal WaitOnceOperation(
                string id, List<JsonValue?> seenTokens, JsonValue? token = null)
            {
                _seenTokens = seenTokens;
                _token = token ?? Json("{\"tick\":0}");
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
                StepResult.Waiting(_token, "waiting", 10);

            public override StepResult Poll(StepContext ctx)
            {
                _seenTokens.Add(ctx.ResumeToken);
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }
        }

        // ── Plan builders ───────────────────────────────────────────────

        private static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            IReadOnlyDictionary<string, int>? transitions = null,
            IReadOnlyList<string>? effects = null) =>
            new PlanStep(
                index, nodeId, operationId, 1,
                new string(hashSeed, 64), new string((char)(hashSeed + 1), 64),
                inputs ?? new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                transitions ?? new Dictionary<string, int> { ["next"] = -1 },
                effects ?? Array.Empty<string>(),
                30, new RetryPolicy(), OpWeave.Core.Operations.Idempotency.Idempotent);

        // A fully revalidatable Plan for the real FileRunStore (the pattern
        // of RunResumeTests): real planHash material through the shared
        // PlanHashInputParts.
        private static CompiledPlan Plan(PlanStep[] steps)
        {
            var stepList = new List<PlanStep>(steps);
            var inputs = Json("{}");
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
                    Array.Empty<string>(), inputs.ToToken()),
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
                inputs,
                steps,
                Array.Empty<PlanOutput>(),
                PlanBuilder.UnionEffects(stepList), new List<ApprovalRequirement>(),
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

        private RunSnapshot Load(string runId)
        {
            var loaded = new FileRunStore(_root, "reload-check").Load(runId);
            Assert.IsTrue(loaded.Ok, "Load failed.");
            return loaded.Value!;
        }

        // The mid-Run interruption: step 0 done, step 1 Waiting, then the
        // "Editor" goes away — the fresh fixture resumes from the journal.
        private static string InterruptAfterFirstStep(
            Fixture live, PlanStep[] steps, string[] nodeIds)
        {
            var runId = AcceptRun(live, Plan(steps), nodeIds);
            Ticks(live, 1);
            var load = new FileRunStore(live.StoreRoot, "check").Load(runId);
            Assert.IsTrue(load.Ok);
            var before = new RunSnapshotAssert(live, runId);
            Assert.AreEqual(
                RunState.Running, before.State,
                "diags: " + string.Join(" | ", load.Diagnostics.Select(d => d.Code + " " + d.Message)) +
                " journal: " + string.Join(" | ", File.ReadAllLines(JournalPath(live, runId))));
            Assert.AreEqual(StepState.Succeeded, before.StepState(0));
            Assert.AreEqual(StepState.Waiting, before.StepState(1));
            live.Coordinator.Dispose();
            return runId;
        }

        // A tiny snapshot reader bound to one fixture root — keeps the
        // interruption assertion readable inside the static helper.
        private sealed class RunSnapshotAssert
        {
            private readonly RunSnapshot _snapshot;

            internal RunSnapshotAssert(Fixture fixture, string runId)
            {
                var loaded = new FileRunStore(fixture.StoreRoot, "check").Load(runId);
                Assert.IsTrue(loaded.Ok);
                _snapshot = loaded.Value!;
            }

            internal RunState State => _snapshot.State;

            internal StepState StepState(int index) =>
                _snapshot.Steps.Single(s => s.Index == index).State;

            /// <summary>True while no journal record mentions the step —
            /// the pending state an exhausted Tick leaves behind.</summary>
            internal bool StepIsPending(int index) =>
                _snapshot.Steps.All(s => s.Index != index);
        }

        // ── T-W4.3-06: the fixture's Tick budget follows the injected timer ──

        // The Unity 6000.5 intermittent failure this remediation removes:
        // with the production StopwatchMonotonicTimer a >50 ms real-time
        // stall inside the first Tick left step 0 (or step 1) unstarted,
        // and the reloaded snapshot had no record for it. The fake timer
        // makes the same budget states deterministic — an exhausted Tick
        // starts nothing, an unspent Tick runs step 0 to Succeeded and
        // step 1 to Waiting — so per-Tick progress no longer depends on
        // wall-clock time.
        [Test]
        public void TickProgressFollowsTheInjectedMonotonicTimer()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rc.tick", live.Events), 'd');
            var seen = new List<JsonValue?>();
            live.Register(() => new WaitOnceOperation("test.rc.wait", seen), 'e');
            var steps = new[]
            {
                Step(0, "tick", "test.rc.tick", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "wait", "test.rc.wait", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(live, Plan(steps), "tick", "wait");

            // An exhausted budget defers every pending step — the failure
            // mode the real timer produced intermittently.
            live.MonotonicAdvancePerReadMs = RunLoop.DefaultTickBudgetMs;
            Ticks(live, 1);
            var stalled = new RunSnapshotAssert(live, runId);
            Assert.AreEqual(RunState.Running, stalled.State);
            Assert.IsTrue(
                stalled.StepIsPending(0),
                "an exhausted Tick leaves step 0 without a journal record");
            Assert.AreEqual(0, live.Events.Count, "no Operation ran in the Tick");

            // A fresh Tick whose timer stays under the budget runs both
            // steps, whatever real time passed while the Run was alive.
            live.MonotonicAdvancePerReadMs = 0;
            Ticks(live, 1);
            var done = new RunSnapshotAssert(live, runId);
            Assert.AreEqual(RunState.Running, done.State);
            Assert.AreEqual(StepState.Succeeded, done.StepState(0));
            Assert.AreEqual(StepState.Waiting, done.StepState(1));
        }

        // ── SPEC §F4 test 2 ([Core]): restart + unsaved scene.modify ────

        [Test]
        public void RestartWithUnsavedSceneModify_EndsNeedsReview()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rc.mod", live.Events), 'd');
            var seen = new List<JsonValue?>();
            live.Register(() => new WaitOnceOperation("test.rc.next", seen), 'e');
            var steps = new[]
            {
                Step(0, "mod", "test.rc.mod", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 },
                    effects: new[] { "scene.modify" }),
                Step(1, "next", "test.rc.next", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = InterruptAfterFirstStep(live, steps, new[] { "mod", "next" });

            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.rc.mod", fresh.Events), 'd');
            fresh.Register(() => new WaitOnceOperation("test.rc.next", seen), 'e');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok, "resume failed");
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);
            Assert.AreEqual(RunState.NeedsReview, Load(runId).State);

            var journal = JournalRecords(fresh, runId);
            var reviews = journal.FindAll(r => r.Type == JournalRecordType.StepNeedsReview);
            Assert.AreEqual(1, reviews.Count, "exactly one step_needs_review is journaled");
            Assert.AreEqual(
                "OW-RUN-012", (string)((JObject)reviews[0].Data.ToToken())["error"]!);
            Assert.AreEqual(0, reviews[0].Step, "the unsaved scene.modify step is blamed");
            Assert.AreEqual(
                0, journal.Count(r => r.Type == JournalRecordType.RunResumed),
                "the resume was never journaled");
            Assert.AreEqual(
                0, seen.Count,
                "the interrupted step was never re-executed or polled");
        }

        [Test]
        public void RestartWithSavedSceneModify_Resumes()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rc.mod", live.Events), 'd');
            live.Register(() => new MarkerOperation("test.rc.save", live.Events), 'e');
            var seen = new List<JsonValue?>();
            live.Register(() => new WaitOnceOperation("test.rc.next", seen), 'a');
            var steps = new[]
            {
                Step(0, "mod", "test.rc.mod", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 },
                    effects: new[] { "scene.modify" }),
                Step(1, "save", "test.rc.save", 'e',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 },
                    effects: new[] { "scene.save" }),
                Step(2, "next", "test.rc.next", 'a',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = AcceptRun(live, Plan(steps), "mod", "save", "next");
            Ticks(live, 1);
            live.Coordinator.Dispose();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.rc.mod", fresh.Events), 'd');
            fresh.Register(() => new MarkerOperation("test.rc.save", fresh.Events), 'e');
            fresh.Register(() => new WaitOnceOperation("test.rc.next", seen), 'a');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok, "resume failed");
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 3);

            var after = Load(runId);
            Assert.AreEqual(RunState.Succeeded, after.State);
            Assert.AreEqual(1, seen.Count, "the waiting step polled once");
            Assert.IsFalse(
                fresh.Events.Contains("execute:test.rc.mod"),
                "a completed step re-ran after the restart");
        }

        [Test]
        public void SameSessionWithUnsavedSceneModify_Resumes()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rc.mod", live.Events), 'd');
            var seen = new List<JsonValue?>();
            live.Register(() => new WaitOnceOperation("test.rc.next", seen), 'e');
            var steps = new[]
            {
                Step(0, "mod", "test.rc.mod", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 },
                    effects: new[] { "scene.modify" }),
                Step(1, "next", "test.rc.next", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = InterruptAfterFirstStep(live, steps, new[] { "mod", "next" });

            // A domain reload keeps the Editor session id — this is a reload,
            // not a restart, so no unsaved-change verdict applies.
            var fresh = NewFixture(Session);
            fresh.Register(() => new MarkerOperation("test.rc.mod", fresh.Events), 'd');
            fresh.Register(() => new WaitOnceOperation("test.rc.next", seen), 'e');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 2);
            Assert.AreEqual(RunState.Succeeded, Load(runId).State);
        }

        [Test]
        public void RestartWithoutSceneModify_Resumes()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rc.plain", live.Events), 'd');
            var seen = new List<JsonValue?>();
            live.Register(() => new WaitOnceOperation("test.rc.next", seen), 'e');
            var steps = new[]
            {
                Step(0, "plain", "test.rc.plain", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "next", "test.rc.next", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = InterruptAfterFirstStep(live, steps, new[] { "plain", "next" });

            var fresh = NewFixture(Session2);
            fresh.Register(() => new MarkerOperation("test.rc.plain", fresh.Events), 'd');
            fresh.Register(() => new WaitOnceOperation("test.rc.next", seen), 'e');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 2);
            Assert.AreEqual(RunState.Succeeded, Load(runId).State);
        }

        // ── SPEC §F4 item 4: session keys (handle) are discarded ────────

        [Test]
        public void RestartDiscardsRefHandlesFromJournaledValues()
        {
            var live = NewFixture();
            live.Register(() => new RefOutputOperation("test.rc.refout", live.Events), 'd');
            var seen = new List<JsonValue>();
            live.Register(() => new RefCaptureOperation("test.rc.refin", seen), 'e');
            var steps = new[]
            {
                Step(0, "out", "test.rc.refout", 'd',
                    outputTypes: new Dictionary<string, string>
                        { ["target"] = "unity.GameObjectRef" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "in", "test.rc.refin", 'e',
                    inputs: new Dictionary<string, InputSource>
                        { ["target"] = new FromStep(0, "target") },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            // Run to completion, then truncate the journal back to step 0's
            // success — step 1's first execution never happened.
            var runId = AcceptRun(live, Plan(steps), "out", "in");
            Ticks(live, 1);
            live.Store.FlushPending();
            var journal = JournalPath(live, runId);
            File.WriteAllLines(journal, File.ReadAllLines(journal).Take(5));
            live.Coordinator.Dispose();
            seen.Clear();

            var fresh = NewFixture(Session2);
            fresh.Register(() => new RefOutputOperation("test.rc.refout", fresh.Events), 'd');
            fresh.Register(() => new RefCaptureOperation("test.rc.refin", seen), 'e');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Ticks(fresh, 2);

            Assert.AreEqual(1, seen.Count, "the pending step ran once after resume");
            var resolved = seen[0].ToToken();
            Assert.IsNull(
                resolved["handle"],
                "the dead session's handle key is discarded: " + resolved);
            Assert.AreEqual("/Root", (string)resolved["path"]!,
                "the persistent keys survive the discard");
        }

        [Test]
        public void SameSessionKeepsRefHandles()
        {
            var live = NewFixture();
            live.Register(() => new RefOutputOperation("test.rc.refout", live.Events), 'd');
            var seen = new List<JsonValue>();
            live.Register(() => new RefCaptureOperation("test.rc.refin", seen), 'e');
            var steps = new[]
            {
                Step(0, "out", "test.rc.refout", 'd',
                    outputTypes: new Dictionary<string, string>
                        { ["target"] = "unity.GameObjectRef" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "in", "test.rc.refin", 'e',
                    inputs: new Dictionary<string, InputSource>
                        { ["target"] = new FromStep(0, "target") },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = AcceptRun(live, Plan(steps), "out", "in");
            Ticks(live, 1);
            live.Store.FlushPending();
            var journal = JournalPath(live, runId);
            File.WriteAllLines(journal, File.ReadAllLines(journal).Take(5));
            live.Coordinator.Dispose();
            seen.Clear();

            // A domain reload keeps the session — the handle is still live.
            var fresh = NewFixture(Session);
            fresh.Register(() => new RefOutputOperation("test.rc.refout", fresh.Events), 'd');
            fresh.Register(() => new RefCaptureOperation("test.rc.refin", seen), 'e');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Ticks(fresh, 2);

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual(
                -1338L, (long)seen[0].ToToken()["handle"]!,
                "a same-session reload keeps the session key");
        }

        // F1 (T-W4.3-02): a handle discarded after an Editor restart must
        // stay discarded on every later rebuild. The successful resume
        // journals run_resumed under the new session, so the run-level
        // session of the last record matches at the next domain reload —
        // the discard decision must use the session each output was
        // produced in, never the last record's.
        [Test]
        public void RestartThenSameSessionReload_DiscardsHandles()
        {
            var live = NewFixture();
            live.Register(() => new RefOutputOperation("test.rc.refout", live.Events), 'd');
            var seenTokens = new List<JsonValue?>();
            live.Register(() => new WaitOnceOperation("test.rc.wait", seenTokens), 'e');
            var seen = new List<JsonValue>();
            live.Register(() => new RefCaptureOperation("test.rc.refin", seen), 'a');
            var steps = new[]
            {
                Step(0, "out", "test.rc.refout", 'd',
                    outputTypes: new Dictionary<string, string>
                        { ["target"] = "unity.GameObjectRef" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "wait", "test.rc.wait", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(2, "in", "test.rc.refin", 'a',
                    inputs: new Dictionary<string, InputSource>
                        { ["target"] = new FromStep(0, "target") },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = InterruptAfterFirstStep(live, steps, new[] { "out", "wait", "in" });

            // The Editor restart: the resume journals run_resumed under
            // Session2, then the "process" goes away before another Tick —
            // step 1 stays Waiting and step 2 has not consumed the output.
            var fresh = NewFixture(Session2);
            fresh.Register(() => new RefOutputOperation("test.rc.refout", fresh.Events), 'd');
            fresh.Register(() => new WaitOnceOperation("test.rc.wait", seenTokens), 'e');
            fresh.Register(() => new RefCaptureOperation("test.rc.refin", seen), 'a');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok, "resume after the restart failed");
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            fresh.Coordinator.Dispose();

            var journal = JournalRecords(fresh, runId);
            var runResumed = journal.FindAll(r => r.Type == JournalRecordType.RunResumed);
            Assert.IsTrue(
                runResumed.Exists(
                    r => string.Equals(r.Session, Session2, StringComparison.Ordinal)),
                "the first resume journaled run_resumed under the new session");

            // A domain reload inside that same session: the journal's last
            // record carries Session2 — only the producing record's own
            // session still proves the handle is dead.
            var reloaded = NewFixture(Session2);
            reloaded.Register(() => new RefOutputOperation("test.rc.refout", reloaded.Events), 'd');
            reloaded.Register(() => new WaitOnceOperation("test.rc.wait", seenTokens), 'e');
            reloaded.Register(() => new RefCaptureOperation("test.rc.refin", seen), 'a');
            var reResumed = reloaded.Loop.Resume(runId);
            Assert.IsTrue(reResumed.Ok, "resume after the reload failed");
            Ticks(reloaded, 2);

            Assert.AreEqual(1, seen.Count, "the pending step ran once after the reload");
            var resolved = seen[0].ToToken();
            Assert.IsNull(
                resolved["handle"],
                "the dead session's handle key stays discarded across the " +
                "resume's own run_resumed: " + resolved);
            Assert.AreEqual(
                "/Root", (string)resolved["path"]!,
                "the persistent keys survive the discard");
        }

        // F2 (T-W4.3-02): the discard is declared-type directed. A Ref
        // port's session key dies across the restart — at the store and at
        // a plan-side constant — while a Ref-shaped object inside a
        // declared core.Json value, a core.Json constant and the opaque
        // resumeToken survive verbatim: they are data, not Refs.
        [Test]
        public void RestartKeepsOpaqueJsonAndTokenHandles()
        {
            var live = NewFixture();
            live.Register(() => new RefPayloadOperation("test.rc.payload", live.Events), 'd');
            var seenTokens = new List<JsonValue?>();
            var token = Json("{\"kind\":\"asset\",\"handle\":\"resume-handle\",\"tick\":1}");
            live.Register(() => new WaitOnceOperation("test.rc.wait", seenTokens, token), 'e');
            var seen = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            live.Register(() => new RefJsonCaptureOperation("test.rc.capture", seen), 'a');
            var steps = new[]
            {
                Step(0, "out", "test.rc.payload", 'd',
                    outputTypes: new Dictionary<string, string>
                    {
                        ["target"] = "unity.GameObjectRef",
                        ["payload"] = "core.Json",
                    },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "wait", "test.rc.wait", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(2, "in", "test.rc.capture", 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["target"] = new FromStep(0, "target"),
                        ["payload"] = new FromStep(0, "payload"),
                        ["constRef"] = new Constant(Json(
                            "{\"kind\":\"gameObject\",\"handle\":-99,\"path\":\"/Const\"}")),
                        ["constJson"] = new Constant(Json(
                            "{\"kind\":\"asset\",\"handle\":\"const-data\"}")),
                    },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = InterruptAfterFirstStep(live, steps, new[] { "out", "wait", "in" });

            var fresh = NewFixture(Session2);
            fresh.Register(() => new RefPayloadOperation("test.rc.payload", fresh.Events), 'd');
            fresh.Register(() => new WaitOnceOperation("test.rc.wait", seenTokens, token), 'e');
            fresh.Register(() => new RefJsonCaptureOperation("test.rc.capture", seen), 'a');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok, "resume failed");
            Ticks(fresh, 2);

            Assert.AreEqual(RunState.Succeeded, Load(runId).State);
            Assert.AreEqual(1, seenTokens.Count, "the waiting step polled once");
            // The declared Ref loses its dead session key; the persistent
            // keys survive — at the FromStep port…
            var target = seen["target"].ToToken();
            Assert.IsNull(target["handle"], "the Ref port's session key is discarded: " + target);
            Assert.AreEqual("/Root", (string)target["path"]!);
            // …and at the plan-side constant of declared Ref type…
            var constRef = seen["constRef"].ToToken();
            Assert.IsNull(constRef["handle"], "a stale constant Ref loses its key: " + constRef);
            Assert.AreEqual("/Const", (string)constRef["path"]!);
            // …while the Ref-shaped objects inside declared core.Json
            // values are data and reach the Operation verbatim.
            var payload = seen["payload"].ToToken();
            Assert.AreEqual(
                "customer-id", (string)payload["handle"]!,
                "a Ref-shaped object inside core.Json is data: " + payload);
            var constJson = seen["constJson"].ToToken();
            Assert.AreEqual(
                "const-data", (string)constJson["handle"]!,
                "a Ref-shaped core.Json constant is data: " + constJson);
            var seenToken = seenTokens[0]!.ToToken();
            Assert.AreEqual(
                "resume-handle", (string)seenToken["handle"]!,
                "the opaque resumeToken is never rewritten: " + seenToken);
        }

        // The plan-side counterpart of the same-session keep: constants
        // and execution inputs committed with the Run keep their session
        // keys on a domain reload, because the journal's creation session
        // is still the current one.
        [Test]
        public void SameSessionKeepsConstantRefHandle()
        {
            var live = NewFixture();
            live.Register(() => new RefPayloadOperation("test.rc.payload", live.Events), 'd');
            var seenTokens = new List<JsonValue?>();
            live.Register(() => new WaitOnceOperation("test.rc.wait", seenTokens), 'e');
            var seen = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            live.Register(() => new RefJsonCaptureOperation("test.rc.capture", seen), 'a');
            var steps = new[]
            {
                Step(0, "out", "test.rc.payload", 'd',
                    outputTypes: new Dictionary<string, string>
                    {
                        ["target"] = "unity.GameObjectRef",
                        ["payload"] = "core.Json",
                    },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "wait", "test.rc.wait", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    transitions: new Dictionary<string, int> { ["next"] = 2 }),
                Step(2, "in", "test.rc.capture", 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["target"] = new FromStep(0, "target"),
                        ["payload"] = new FromStep(0, "payload"),
                        ["constRef"] = new Constant(Json(
                            "{\"kind\":\"gameObject\",\"handle\":-99,\"path\":\"/Const\"}")),
                        ["constJson"] = new Constant(Json(
                            "{\"kind\":\"asset\",\"handle\":\"const-data\"}")),
                    },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var runId = InterruptAfterFirstStep(live, steps, new[] { "out", "wait", "in" });

            // A domain reload keeps the session — every key is still live.
            var fresh = NewFixture(Session);
            fresh.Register(() => new RefPayloadOperation("test.rc.payload", fresh.Events), 'd');
            fresh.Register(() => new WaitOnceOperation("test.rc.wait", seenTokens), 'e');
            fresh.Register(() => new RefJsonCaptureOperation("test.rc.capture", seen), 'a');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Ticks(fresh, 2);

            Assert.AreEqual(RunState.Succeeded, Load(runId).State);
            Assert.AreEqual(-99L, (long)seen["constRef"].ToToken()["handle"]!,
                "a same-session reload keeps the constant's session key");
            Assert.AreEqual(-1338L, (long)seen["target"].ToToken()["handle"]!,
                "a same-session reload keeps the output's session key");
        }

        // ── SPEC §F4 item 3: Registry fingerprints of remaining steps ───

        [Test]
        public void DeletedOperationForRemainingStep_EndsNeedsReview()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rc.done", live.Events), 'd');
            live.Register(() => new WaitOnceOperation("test.rc.gone", new List<JsonValue?>()), 'e');
            var steps = new[]
            {
                Step(0, "done", "test.rc.done", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "gone", "test.rc.gone", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = InterruptAfterFirstStep(live, steps, new[] { "done", "gone" });

            var fresh = NewFixture(Session);
            fresh.Register(() => new MarkerOperation("test.rc.done", fresh.Events), 'd');
            // The remaining step's Operation is gone from the new Registry.
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);
            Assert.AreEqual(RunState.NeedsReview, Load(runId).State);

            var journal = JournalRecords(fresh, runId);
            var reviews = journal.FindAll(r => r.Type == JournalRecordType.StepNeedsReview);
            Assert.AreEqual(1, reviews.Count);
            Assert.AreEqual(
                "OW-RUN-011", (string)((JObject)reviews[0].Data.ToToken())["error"]!);
            Assert.AreEqual(1, reviews[0].Step);
            Assert.AreEqual(
                0, journal.Count(r => r.Type == JournalRecordType.RunResumed),
                "the resume was never journaled");
        }

        [Test]
        public void ChangedOperationForRemainingStep_EndsNeedsReview()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rc.done", live.Events), 'd');
            live.Register(() => new WaitOnceOperation("test.rc.edit", new List<JsonValue?>()), 'e');
            var steps = new[]
            {
                Step(0, "done", "test.rc.done", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "edit", "test.rc.edit", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = InterruptAfterFirstStep(live, steps, new[] { "done", "edit" });

            var fresh = NewFixture(Session);
            fresh.Register(() => new MarkerOperation("test.rc.done", fresh.Events), 'd');
            // Registered under different fingerprints — the recorded pair
            // no longer matches (an edited implementation).
            fresh.Register(() => new WaitOnceOperation("test.rc.edit", new List<JsonValue?>()), 'g');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.NeedsReview, resumed.Value!.State);

            var journal = JournalRecords(fresh, runId);
            var reviews = journal.FindAll(r => r.Type == JournalRecordType.StepNeedsReview);
            Assert.AreEqual(1, reviews.Count);
            Assert.AreEqual(
                "OW-RUN-011", (string)((JObject)reviews[0].Data.ToToken())["error"]!);
        }

        [Test]
        public void DeletedOperationForCompletedStep_Resumes()
        {
            var live = NewFixture();
            live.Register(() => new MarkerOperation("test.rc.old", live.Events), 'd');
            var seen = new List<JsonValue?>();
            live.Register(() => new WaitOnceOperation("test.rc.next", seen), 'e');
            var steps = new[]
            {
                Step(0, "old", "test.rc.old", 'd',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    transitions: new Dictionary<string, int> { ["next"] = 1 }),
                Step(1, "next", "test.rc.next", 'e',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var runId = InterruptAfterFirstStep(live, steps, new[] { "old", "next" });

            // The COMPLETED step's Operation is gone — only the remaining
            // steps are compared (SPEC §F4 item 3), so the resume proceeds.
            var fresh = NewFixture(Session);
            fresh.Register(() => new WaitOnceOperation("test.rc.next", seen), 'e');
            var resumed = fresh.Loop.Resume(runId);
            Assert.IsTrue(resumed.Ok);
            Assert.AreEqual(RunState.Running, resumed.Value!.State);
            Ticks(fresh, 2);
            Assert.AreEqual(RunState.Succeeded, Load(runId).State);
        }
    }
}
