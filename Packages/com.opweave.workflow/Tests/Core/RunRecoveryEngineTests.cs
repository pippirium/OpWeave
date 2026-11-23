#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Recovery;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the recovery start-up decision of T-W4.3-01
    /// (<c>local:T-W4.3-01:recovery-startup</c>, SPEC §F4 items 1/2/6 with the
    /// SPEC §E2 item 3 exclusion and takeover rules): the pass takes or confirms
    /// the <c>coordinator.lock</c> exclusion and only inside it reads
    /// <c>lock.json</c> and the journal-derived unfinished set, re-attaches every
    /// unfinished Run, and resumes exactly the one Run the exclusion passes to
    /// after its journal verified. Everything it cannot verify ends NeedsReview —
    /// reported, never journaled — with the <c>lock.json</c> residue left in place
    /// so new mutating Runs keep failing closed (W4.3-R01/R02/R03/R04/R23).
    /// Fixtures use the real <see cref="FileRunStore"/>, the real
    /// <see cref="CoordinatorRunLock"/> and dummy Operations over one temp root,
    /// so the exclusion is the OS's own: a live owner genuinely denies it and a
    /// dropped handle genuinely frees it. The same sources run under dotnet and
    /// Unity Mono.
    /// </summary>
    internal sealed class RunRecoveryEngineTests
    {
        private const string Session = "session-recovery-test";
        private const string Session2 = "session-recovery-test-2";
        private const string WorkflowId = "example.recovery";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private string _root = null!;
        private readonly List<Fixture> _fixtures = new List<Fixture>();
        private readonly FakeClock _clock = new FakeClock();
        private readonly FakeEditorState _editor = new FakeEditorState();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.Recovery." + Guid.NewGuid().ToString("N"));
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

        /// <summary>One Editor session: the real store/coordinator/loop stack
        /// over the shared root, plus the recovery engine over its injected
        /// seams. A fresh Fixture on the same root is the world after the
        /// previous session died; <see cref="LockSource"/> and
        /// <see cref="TerminationProven"/> are that world's lock method.</summary>
        private sealed class Fixture
        {
            private readonly string _root;
            private RunLoop? _loop;
            private RunRecoveryEngine? _engine;
            internal readonly FileRunStore Store;
            internal readonly RunCoordinator Coordinator;
            internal readonly FakeClock Clock;
            internal readonly FakeEditorState Editor;
            internal readonly List<RegisteredOperation> Operations =
                new List<RegisteredOperation>();
            internal readonly Dictionary<string, Func<IOperation>> OpFactories =
                new Dictionary<string, Func<IOperation>>(StringComparer.Ordinal);
            internal readonly List<string> Events = new List<string>();

            /// <summary>How this session obtains the exclusion. The default is
            /// the real OS acquisition, so a live owner's denial is real.</summary>
            internal Func<Result<CoordinatorRunLock>> LockSource;

            /// <summary>Whether this session's lock method can prove the process
            /// a recorded owner belongs to has ended (SPEC §E2 item 3 (b)).</summary>
            internal Func<bool> TerminationProven;

            internal Fixture(
                string root, string session, FakeClock clock, FakeEditorState editor)
            {
                _root = root;
                Clock = clock;
                Editor = editor;
                LockSource = () => CoordinatorRunLock.TryAcquire(root);
                TerminationProven = () => true;
                Store = new FileRunStore(root, session);
                Coordinator = new RunCoordinator(
                    Store,
                    (runId, plan, document, actor) =>
                        Store.CreateRun(runId, plan, document, actor),
                    editor,
                    clock,
                    () => Snapshot(),
                    () => CoordinatorRunLock.TryAcquire(root),
                    session);
            }

            // The Loop binds its Operation snapshot at construction — lazily,
            // after this fixture's Operations are registered.
            internal RunLoop Loop => _loop ??= new RunLoop(
                Coordinator,
                Clock,
                Editor,
                DescriptorTestFixtures.CreateRegistry(),
                Snapshot(),
                Resolve,
                new FakeRequirements(),
                _ => new UndoScope(),
                () => false);

            internal RunRecoveryEngine Engine => _engine ??= new RunRecoveryEngine(
                Coordinator,
                Loop,
                Store,
                () => LockSource(),
                () => TerminationProven());

            internal RegistrySnapshot Snapshot() =>
                new RegistrySnapshot(
                    "reg_01J00000000000000000000000",
                    new string('f', 64),
                    Operations,
                    DescriptorTestFixtures.CreateRegistry(),
                    Array.Empty<RegistryChange>(),
                    Array.Empty<TypeRegistryFailure>());

            // Every resolve mints a NEW instance: no step state survives outside
            // the resumeToken (SPEC §C2), which is what makes a takeover run on
            // the journal alone.
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

        private static void RegisterOperations(Fixture fixture)
        {
            fixture.Register(() => new MarkerOperation("test.rx.mark", fixture.Events), 'd');
            fixture.Register(() => new MarkerOperation("test.rx.mark2", fixture.Events), 'a');
            fixture.Register(() => new WaitOperation("test.rx.wait", fixture.Events), 'e');
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

        /// <summary>Immediate marker: logs <c>execute:&lt;id&gt;</c> so a
        /// re-execution after recovery would be visible.</summary>
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

        /// <summary>A Waitable that stays Waiting through Execute and finishes on
        /// the first <see cref="Poll"/>: a session that dies while it waits
        /// leaves a real unfinished journal and a real held exclusion, and its
        /// successor must continue by polling — never by executing again.</summary>
        private sealed class WaitOperation : OpBase
        {
            private readonly List<string> _events;
            private readonly string _id;

            internal WaitOperation(string id, List<string> events)
            {
                _id = id;
                _events = events;
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits, then finishes on the first poll.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _events.Add("execute:" + _id);
                return StepResult.Waiting(Json("{\"tick\":0}"), "waiting", 1);
            }

            public override StepResult Poll(StepContext ctx)
            {
                _events.Add("poll:" + _id);
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }
        }

        // ── Plan builders ───────────────────────────────────────────────

        private static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed,
            string outputPort, string outputType,
            IReadOnlyDictionary<string, int>? transitions = null) =>
            new PlanStep(
                index, nodeId, operationId, 1,
                new string(hashSeed, 64), new string((char)(hashSeed + 1), 64),
                new Dictionary<string, InputSource>(),
                new Dictionary<string, string> { [outputPort] = outputType },
                transitions ?? new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                30, new RetryPolicy(), Idempotency.Idempotent);

        // A fully revalidatable Plan for the real FileRunStore: the §D0.1
        // planHashInput is built by the same PlanHashInputParts the compiler
        // uses (approvals included, so a re-load re-derives the identical
        // requiredEffects member) and planHash is its real SHA-256.
        private static CompiledPlan Plan(
            IReadOnlyList<PlanStep> steps, int approvalCount = 0)
        {
            var approvals = new List<ApprovalRequirement>();
            for (var i = 0; i < approvalCount; i++)
            {
                approvals.Add(new ApprovalRequirement(
                    "n" + i, EffectNames.AssetModify, "example.op@1"));
            }
            var inputsValue = Json("{}");
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
                ["steps"] = PlanHashInputParts.Steps(steps),
                ["transitions"] = PlanHashInputParts.Transitions(steps),
                ["retry"] = PlanHashInputParts.Retry(steps),
                ["timeouts"] = PlanHashInputParts.Timeouts(steps),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(steps, approvals),
                ["usedOperationFingerprints"] =
                    PlanHashInputParts.OperationFingerprints(steps),
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
                Array.Empty<PlanOutput>(),
                Array.Empty<string>(), approvals,
                Array.Empty<PlanSourceMapEntry>(), hashInput);
        }

        // mark → wait: the second step leaves the Run genuinely Running when the
        // session dies, with its exclusion held and the wait journaled.
        private static CompiledPlan MarkWaitPlan() => Plan(new[]
        {
            Step(0, "n1", "test.rx.mark", 'd', "mark", "core.String",
                new Dictionary<string, int> { ["next"] = 1 }),
            Step(1, "n2", "test.rx.wait", 'e', "done", "core.Bool"),
        });

        // mark → mark2: an immediate Plan that reaches Succeeded in one tick.
        private static CompiledPlan MarkMarkPlan() => Plan(new[]
        {
            Step(0, "n1", "test.rx.mark", 'd', "mark", "core.String",
                new Dictionary<string, int> { ["next"] = 1 }),
            Step(1, "n2", "test.rx.mark2", 'a', "mark", "core.String"),
        });

        // A single immediate step: the Run a recovery pass must not resume but
        // only re-attach (Queued), or start/approve afterwards.
        private static CompiledPlan MarkPlan(int approvalCount = 0) => Plan(new[]
        {
            Step(0, "n1", "test.rx.mark", 'd', "mark", "core.String"),
        }, approvalCount);

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

        private static Result<RunHandle> AcceptResult(
            Fixture fixture, CompiledPlan plan, bool queue = false) =>
            fixture.Coordinator.Accept(
                plan, Doc("n1", "n2"), ActorKind.Agent, new AcceptOptions(null, queue));

        private static string AcceptRun(
            Fixture fixture, CompiledPlan plan, bool queue = false)
        {
            var accepted = AcceptResult(fixture, plan, queue);
            Assert.IsTrue(accepted.Ok, "acceptance failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        // ── Driving and reading back ────────────────────────────────────

        // Every tick advances the fake clock: a journaled wait's poll interval is
        // real time the loop honours.
        private static void Tick(Fixture fixture)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var tick = fixture.Loop.Tick();
            Assert.IsTrue(tick.Ok, "the tick failed: " + Describe(tick.Diagnostics));
        }

        private static RunState RunUntilTerminal(Fixture fixture, string runId)
        {
            for (var i = 0; i < 6; i++)
            {
                Tick(fixture);
                if (fixture.Coordinator.StateOf(runId) is { } state &&
                    (state == RunState.Succeeded || state == RunState.Failed ||
                     state == RunState.Cancelled))
                {
                    return state;
                }
            }
            Assert.Fail(
                "The Run did not end within 6 ticks; state: " +
                fixture.Coordinator.StateOf(runId));
            return RunState.Failed;
        }

        private static string JournalPath(string root, string runId) =>
            Path.Combine(root, "Runs", runId, "journal.ndjson");

        private List<JournalRecord> JournalRecords(string runId)
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Store.FlushPending();
            }
            var lines = File.ReadAllLines(JournalPath(_root, runId));
            var records = new List<JournalRecord>();
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

        private static int CountOf(
            List<JournalRecord> records, JournalRecordType type) =>
            records.Count(record => record.Type == type);

        private RunSnapshot JournalStateOf(string runId)
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Store.FlushPending();
            }
            // A store instance that appended the records keeps its own fold
            // cache, so the picture is always read through a fresh one.
            var loaded = new FileRunStore(_root, "recovery-probe").Load(runId);
            Assert.IsTrue(loaded.Ok, "the journal did not reconstruct: " +
                Describe(loaded.Diagnostics));
            return loaded.Value!;
        }

        // ── lock.json / coordinator.lock probes ─────────────────────────

        // Records a lock.json owner exactly as an abnormal end leaves it: the
        // handle is dropped (the exclusion frees), the record survives.
        private static void WriteResidue(string root, string runId, string session)
        {
            var acquired = CoordinatorRunLock.TryAcquire(root);
            Assert.IsTrue(acquired.Ok,
                "the probe could not hold the exclusion: " + Describe(acquired.Diagnostics));
            var held = acquired.Value!;
            var written = held.WriteState(RunLockState.AcquiredNow(runId, session));
            Assert.IsTrue(written.Ok,
                "the probe could not record lock.json: " + Describe(written.Diagnostics));
            held.Dispose();
        }

        // Empties lock.json the way an orderly Run end does (SPEC §E2 item 3): the
        // record an abnormal end left behind is gone while the journal it named
        // stays unfinished — the torn hand-over that lets another mutating Run
        // start, which is how two unfinished mutating journals come to exist.
        private static void EmptyRecordedLockState(string root)
        {
            var acquired = CoordinatorRunLock.TryAcquire(root);
            Assert.IsTrue(acquired.Ok,
                "the probe could not hold the exclusion: " + Describe(acquired.Diagnostics));
            Assert.IsTrue(acquired.Value!.Release().Ok,
                "the probe could not empty lock.json");
        }

        private static void AssertLockJsonNames(string root, string runId, string session)
        {
            var path = Path.Combine(root, CoordinatorRunLock.StateFileName);
            Assert.IsTrue(File.Exists(path), "lock.json was never written");
            var parsed = CanonicalJson.Parse(File.ReadAllText(path, Utf8));
            Assert.IsTrue(parsed.Ok, "lock.json is not readable JSON");
            var document = (JObject)parsed.Value!;
            Assert.AreEqual(runId, (string?)document["runId"]);
            Assert.AreEqual(session, (string?)document["editorSessionId"]);
        }

        private static bool LockJsonRecordsNothing(string root)
        {
            var path = Path.Combine(root, CoordinatorRunLock.StateFileName);
            return !File.Exists(path) || new FileInfo(path).Length == 0;
        }

        // Whether anyone could take the exclusion right now — the OS's own answer
        // to "is a Run holding it". The probe drops its handle without touching
        // lock.json (Dispose is the abandoned-state end).
        private static bool ExclusionFree(string root)
        {
            var probe = CoordinatorRunLock.TryAcquire(root);
            if (!probe.Ok)
            {
                return false;
            }
            probe.Value!.Dispose();
            return true;
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            return string.Join(
                "; ", diagnostics.Select(d => d is null ? "<null>" : d.Code + " " + d.Message));
        }

        private static string[] CodesOf(IReadOnlyList<Diagnostic> diagnostics) =>
            diagnostics.Select(d => d?.Code ?? "<null>").ToArray();

        // The pass ran, reported, and did not fail on the store.
        private static RunRecoveryReport Recover(Fixture fixture, string? previousOwnedRunId)
        {
            var recovered = fixture.Engine.RecoverAtStartup(previousOwnedRunId);
            Assert.IsTrue(recovered.Ok, "the recovery pass failed: " +
                Describe(recovered.Diagnostics));
            return recovered.Value!;
        }

        private static RunRecoveryReport Reconcile(Fixture fixture)
        {
            var reconciled = fixture.Engine.ReconcileSuspendedRun();
            Assert.IsTrue(reconciled.Ok, "the Edit Mode reconciliation failed: " +
                Describe(reconciled.Diagnostics));
            return reconciled.Value!;
        }

        private static RunRecoveryResult ResultOf(RunRecoveryReport report, string runId)
        {
            var found = report.Runs.FirstOrDefault(result => result.RunId == runId);
            Assert.IsNotNull(found, "the report does not mention " + runId);
            return found!;
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        // ── SPEC §E2 test 2 (takeover part) + §F4 item 6 ────────────────

        /// <summary>W4.3-R03/R04: a lock.json record carrying a different
        /// <c>editorSessionId</c> is taken over only by the session that actually
        /// obtained the exclusion, and only on the strength of its verified
        /// journal — the resume is journaled, <c>lock.json</c> re-records this
        /// session's id while the same exclusion stays held, and the journal's
        /// last step record decides the continuation (the finished step never
        /// re-executes, the waiting step is polled).</summary>
        [Test]
        public void OtherSessionsUnfinishedRunIsTakenOverOnlyWithTheLockAndVerifiedJournal()
        {
            var crashed = NewFixture(Session2);
            RegisterOperations(crashed);
            var runId = AcceptRun(crashed, MarkWaitPlan());
            Tick(crashed);
            Assert.AreEqual(RunState.Running, crashed.Coordinator.StateOf(runId));
            // The wait's Execute already ran — the interruption is after it, so
            // only a Poll may follow (SPEC §F4 item 5).
            Assert.AreEqual(
                new[] { "execute:test.rx.mark", "execute:test.rx.wait" },
                crashed.Events.ToArray());
            AssertLockJsonNames(_root, runId, Session2);
            crashed.Coordinator.Dispose();

            var next = NewFixture(Session);
            RegisterOperations(next);
            var report = Recover(next, previousOwnedRunId: null);

            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(1, report.Runs.Count);
            Assert.AreEqual(runId, report.Runs[0].RunId);
            Assert.AreEqual(RunRecoveryOutcome.Resumed, report.Runs[0].Outcome);
            Assert.AreEqual(RunState.Running, report.Runs[0].State);
            Assert.AreEqual(0, report.Diagnostics.Count);

            // SPEC §F4 item 6: RunResumed journaled, lock.json re-recorded for
            // this session, and coordinator.lock still held by the taken-over Run.
            Assert.AreEqual(1, CountOf(JournalRecords(runId), JournalRecordType.RunResumed));
            AssertLockJsonNames(_root, runId, Session);
            Assert.IsFalse(
                ExclusionFree(_root),
                "the hand-over keeps the exclusion held (SPEC §F4 item 6)");

            // The continuation comes from the journal alone: m1 is not executed
            // again and the journaled wait resumes by polling.
            Assert.AreEqual(RunState.Succeeded, RunUntilTerminal(next, runId));
            Assert.AreEqual(new[] { "poll:test.rx.wait" }, next.Events.ToArray());
            Assert.IsTrue(
                LockJsonRecordsNothing(_root),
                "the orderly end emptied lock.json before releasing");
        }

        // ── lock.json-only residue (SPEC §F4 item 1, §E2 "Editor 再起動後") ──

        /// <summary>W4.3-R02/R03: a <c>lock.json</c> residue whose Run has no
        /// journal to verify is the "lock.json だけが残っている場合も同じ"
        /// shape — NeedsReview, nothing resumed or journaled, the record left in
        /// place, and because it stands, new mutating Runs keep refusing with
        /// OW-RUN-002 instead of double-executing.</summary>
        [Test]
        public void LockJsonOnlyResidueEndsNeedsReviewAndIsLeftInPlace()
        {
            var ghostId = Identifiers.NewRunId();
            WriteResidue(_root, ghostId, Session2);

            var next = NewFixture(Session);
            RegisterOperations(next);
            var report = Recover(next, previousOwnedRunId: null);

            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(1, report.Runs.Count);
            Assert.AreEqual(ghostId, report.Runs[0].RunId);
            Assert.AreEqual(RunRecoveryOutcome.NeedsReview, report.Runs[0].Outcome);
            Assert.IsNull(report.Runs[0].State);
            Assert.Contains(ErrorCodes.OW_STORE_002, CodesOf(report.Diagnostics));

            Assert.IsFalse(
                File.Exists(JournalPath(_root, ghostId)),
                "the recovery invented no journal for a Run the store does not have");
            AssertLockJsonNames(_root, ghostId, Session2);
            Assert.IsFalse(next.Coordinator.HoldsAnyRunLock());
            Assert.IsTrue(
                ExclusionFree(_root),
                "a pass that resumes nothing never keeps the exclusion");

            var queuedId = AcceptRun(next, MarkPlan());
            var started = next.Coordinator.TryStartNext();
            Assert.IsTrue(started.Ok, Describe(started.Diagnostics));
            Assert.IsNull(started.Value);
            Assert.Contains(ErrorCodes.OW_RUN_002, CodesOf(started.Diagnostics));
            Assert.AreEqual(RunState.Queued, next.Coordinator.StateOf(queuedId));
        }

        // ── SPEC §E2 item 3: crash during a lock.json update ────────────

        /// <summary>W4.3-R02: an atomic replace leaves lock.json either before or
        /// after the update, so recovery reconciles it against the journals and
        /// index.json — a record naming a Run whose journal proves it finished
        /// while another journal is still unfinished is a contradiction:
        /// NeedsReview, no resume, and the mismatched record is never overwritten.</summary>
        [Test]
        public void CrashDuringLockJsonUpdateReconcilesAgainstTheJournalAndEndsNeedsReview()
        {
            var first = NewFixture(Session);
            RegisterOperations(first);
            var endedId = AcceptRun(first, MarkMarkPlan());
            Assert.AreEqual(RunState.Succeeded, RunUntilTerminal(first, endedId));
            first.Coordinator.Dispose();

            var crashed = NewFixture(Session2);
            RegisterOperations(crashed);
            var mutatingId = AcceptRun(crashed, MarkWaitPlan());
            Tick(crashed);
            Assert.AreEqual(RunState.Running, JournalStateOf(mutatingId).State);
            crashed.Coordinator.Dispose();

            // The torn update: the record names the Run the journal proves ended.
            WriteResidue(_root, endedId, Session2);

            var next = NewFixture(Session);
            RegisterOperations(next);
            var report = Recover(next, previousOwnedRunId: null);

            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(1, report.Runs.Count);
            Assert.AreEqual(mutatingId, report.Runs[0].RunId);
            Assert.AreEqual(RunRecoveryOutcome.NeedsReview, report.Runs[0].Outcome);
            Assert.AreEqual(RunState.Running, report.Runs[0].State);
            Assert.Contains(ErrorCodes.OW_STORE_002, CodesOf(report.Diagnostics));

            Assert.AreEqual(0, CountOf(JournalRecords(mutatingId), JournalRecordType.RunResumed));
            Assert.AreEqual(
                new[] { "execute:test.rx.mark", "execute:test.rx.wait" },
                crashed.Events.ToArray(),
                "the contradictory Run was never driven (二重実行はしない)");
            AssertLockJsonNames(_root, endedId, Session2);
            Assert.IsTrue(ExclusionFree(_root));
        }

        /// <summary>W4.3-R02: a record naming one unfinished mutating Run does not
        /// arbitrate a second unfinished journal that also claims a mutating state —
        /// the record is one of the claimants, so it settles nothing (SPEC §E2
        /// item 2). Neither Run may take the exclusion this pass holds: both end
        /// NeedsReview (OW-STORE-002, never the live-owner OW-RUN-002), neither is
        /// resumed nor taken over, the record stays, and new mutating Runs keep
        /// failing closed.</summary>
        [Test]
        public void ARecordNamingOneOfTwoUnfinishedMutatingRunsEndsBothNeedsReview()
        {
            // Two unfinished mutating journals is a state only a crash while the
            // exclusion moved between sessions can leave: each session dies with its
            // Run Running and its exclusion dropped, and the second session could
            // start only because the record the first one abandoned was emptied
            // while its journal stayed unfinished — the torn hand-over of SPEC §E2
            // item 3, which is exactly the update a crash can leave half-done.
            var first = NewFixture(Session2);
            RegisterOperations(first);
            var firstId = AcceptRun(first, MarkWaitPlan());
            Tick(first);
            Assert.AreEqual(RunState.Running, JournalStateOf(firstId).State);
            first.Coordinator.Dispose();
            EmptyRecordedLockState(_root);

            var second = NewFixture(Session);
            RegisterOperations(second);
            var secondId = AcceptRun(second, MarkWaitPlan());
            Tick(second);
            Assert.AreEqual(RunState.Running, JournalStateOf(secondId).State);
            second.Coordinator.Dispose();

            // The contradictory record: lock.json names the first of them, which
            // arbitrates nothing while a second journal claims the same state.
            WriteResidue(_root, firstId, Session2);

            var next = NewFixture(Session2);
            RegisterOperations(next);
            var report = Recover(next, previousOwnedRunId: null);

            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(2, report.Runs.Count);
            Assert.AreEqual(RunRecoveryOutcome.NeedsReview, ResultOf(report, firstId).Outcome);
            Assert.AreEqual(RunState.Running, ResultOf(report, firstId).State);
            Assert.AreEqual(RunRecoveryOutcome.NeedsReview, ResultOf(report, secondId).Outcome);
            Assert.AreEqual(RunState.Running, ResultOf(report, secondId).State);
            Assert.Contains(ErrorCodes.OW_STORE_002, CodesOf(report.Diagnostics));
            Assert.IsFalse(
                CodesOf(report.Diagnostics).Contains(ErrorCodes.OW_RUN_002),
                "a contradictory record is not the live-owner answer of §E2 item 3 (a)");

            // No resume for either: no record, no driver, no re-execution.
            Assert.AreEqual(0, CountOf(JournalRecords(firstId), JournalRecordType.RunResumed));
            Assert.AreEqual(0, CountOf(JournalRecords(secondId), JournalRecordType.RunResumed));
            Assert.AreEqual(0, next.Events.Count);
            Assert.IsFalse(next.Coordinator.HoldsAnyRunLock());

            // The record is neither taken over nor overwritten and the pass that
            // resumes nothing keeps no exclusion.
            AssertLockJsonNames(_root, firstId, Session2);
            Assert.IsTrue(ExclusionFree(_root));

            // Because the record stands, new mutating Runs keep failing closed.
            var queuedId = AcceptRun(next, MarkPlan());
            var started = next.Coordinator.TryStartNext();
            Assert.IsTrue(started.Ok, Describe(started.Diagnostics));
            Assert.IsNull(started.Value);
            Assert.Contains(ErrorCodes.OW_RUN_002, CodesOf(started.Diagnostics));
            Assert.AreEqual(RunState.Queued, next.Coordinator.StateOf(queuedId));
        }

        // ── SPEC §E2 item 3 (a): the exclusion could not be kept ────────

        /// <summary>W4.3-R02: an Editor that cannot re-acquire coordinator.lock
        /// after the reload resumes neither its own Run (NeedsReview, reported —
        /// §F2 has no run-level record to write) nor a new one (OW-RUN-002), and
        /// it never steals the live owner's lock: the owner keeps driving its Run
        /// and keeps its own record.</summary>
        [Test]
        public void AnEditorThatCannotReAcquireTheLockResumesNothingAndReportsItsOwnRunNeedsReview()
        {
            var owner = NewFixture(Session);
            RegisterOperations(owner);
            var runId = AcceptRun(owner, MarkWaitPlan());
            Tick(owner);
            Assert.IsFalse(
                ExclusionFree(_root),
                "the live owner holds the exclusion — a live owner's lock is never taken");

            var reloaded = NewFixture(Session2);
            RegisterOperations(reloaded);
            var report = Recover(reloaded, previousOwnedRunId: runId);

            Assert.IsFalse(report.ExclusionHeld);
            Assert.Contains(ErrorCodes.OW_RUN_002, CodesOf(report.Diagnostics));
            Assert.AreEqual(1, report.Runs.Count);
            Assert.AreEqual(runId, report.Runs[0].RunId);
            Assert.AreEqual(RunRecoveryOutcome.NeedsReview, report.Runs[0].Outcome);

            Assert.AreEqual(0, CountOf(JournalRecords(runId), JournalRecordType.RunResumed));
            Assert.IsFalse(reloaded.Coordinator.HoldsAnyRunLock());
            Assert.AreEqual(RunState.Running, owner.Coordinator.StateOf(runId));
            AssertLockJsonNames(_root, runId, Session);

            // No new Run either (the ticket's "lock unavailable → no resume and no
            // new Run"): the mutating Plan is refused at acceptance, and one the
            // caller explicitly queues still cannot pass the start gate.
            var refused = AcceptResult(reloaded, MarkPlan());
            Assert.IsFalse(refused.Ok);
            Assert.Contains(ErrorCodes.OW_RUN_002, CodesOf(refused.Diagnostics));
            var queuedId = AcceptRun(reloaded, MarkPlan(), queue: true);
            var started = reloaded.Coordinator.TryStartNext();
            Assert.IsTrue(started.Ok, Describe(started.Diagnostics));
            Assert.IsNull(started.Value);
            Assert.Contains(ErrorCodes.OW_RUN_002, CodesOf(started.Diagnostics));
            Assert.AreEqual(RunState.Queued, reloaded.Coordinator.StateOf(queuedId));

            Assert.AreEqual(RunState.Succeeded, RunUntilTerminal(owner, runId));
        }

        // ── SPEC §E2 item 3 (b): the owner's end cannot be proven ───────

        /// <summary>W4.3-R02: obtaining the exclusion is not proof about the
        /// process a record names. Where the lock method cannot prove that
        /// process ended, a recorded unfinished Run is not auto-resumed and no new
        /// Run starts (OW-RUN-002) — the record stays for the human decision.</summary>
        [Test]
        public void ARecordedOwnerWhoseProcessCannotBeProvenEndedIsNotTakenOver()
        {
            var crashed = NewFixture(Session2);
            RegisterOperations(crashed);
            var runId = AcceptRun(crashed, MarkWaitPlan());
            Tick(crashed);
            crashed.Coordinator.Dispose();

            var next = NewFixture(Session);
            RegisterOperations(next);
            next.TerminationProven = () => false;
            var report = Recover(next, previousOwnedRunId: null);

            Assert.IsTrue(
                report.ExclusionHeld,
                "item 3 (b) is the case where the exclusion WAS obtained");
            Assert.AreEqual(1, report.Runs.Count);
            Assert.AreEqual(runId, report.Runs[0].RunId);
            Assert.AreEqual(RunRecoveryOutcome.NeedsReview, report.Runs[0].Outcome);
            Assert.Contains(ErrorCodes.OW_RUN_002, CodesOf(report.Diagnostics));

            Assert.AreEqual(0, CountOf(JournalRecords(runId), JournalRecordType.RunResumed));
            Assert.IsFalse(next.Coordinator.HoldsAnyRunLock());
            AssertLockJsonNames(_root, runId, Session2);

            var queuedId = AcceptRun(next, MarkPlan());
            var started = next.Coordinator.TryStartNext();
            Assert.IsNull(started.Value);
            Assert.Contains(ErrorCodes.OW_RUN_002, CodesOf(started.Diagnostics));
            Assert.AreEqual(RunState.Queued, next.Coordinator.StateOf(queuedId));
        }

        // ── SPEC §F4 item 2: re-attachment of the Runs that hold no lock ──

        /// <summary>W4.3-R23: Queued and AwaitingApproval Runs hold no exclusion,
        /// so the pass re-attaches them without driving anything — and after the
        /// reload they are still themselves: the human approval decision still
        /// lands and the start gate still starts one.</summary>
        [Test]
        public void QueuedAndAwaitingApprovalRunsAreReattachedAndStayStartableAndApprovable()
        {
            var before = NewFixture(Session);
            RegisterOperations(before);
            var queuedId = AcceptRun(before, MarkPlan());
            var awaitingId = AcceptRun(before, MarkPlan(approvalCount: 1));
            Assert.AreEqual(RunState.Queued, before.Coordinator.StateOf(queuedId));
            Assert.AreEqual(RunState.AwaitingApproval, before.Coordinator.StateOf(awaitingId));
            before.Coordinator.Dispose();

            var after = NewFixture(Session2);
            RegisterOperations(after);
            var report = Recover(after, previousOwnedRunId: null);

            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(2, report.Runs.Count);
            Assert.AreEqual(RunRecoveryOutcome.Reattached, ResultOf(report, queuedId).Outcome);
            Assert.AreEqual(
                RunState.Queued, ResultOf(report, queuedId).State,
                "a re-attached Run is reported as the journal has it");
            Assert.AreEqual(
                RunRecoveryOutcome.Reattached, ResultOf(report, awaitingId).Outcome);
            Assert.AreEqual(RunState.AwaitingApproval, ResultOf(report, awaitingId).State);
            Assert.AreEqual(RunState.AwaitingApproval, after.Coordinator.StateOf(awaitingId));
            Assert.AreEqual(0, report.Diagnostics.Count);

            // Re-attach only: no resume record, and no exclusion was taken by a Run.
            Assert.AreEqual(0, CountOf(JournalRecords(queuedId), JournalRecordType.RunResumed));
            Assert.AreEqual(0, CountOf(JournalRecords(awaitingId), JournalRecordType.RunResumed));
            Assert.IsTrue(
                ExclusionFree(_root),
                "a pass that attaches only inactive Runs drops the exclusion it took");

            var approved = after.Coordinator.Approve(awaitingId, "human");
            Assert.IsTrue(approved.Ok,
                "the re-attached Run refused the human approval: " + Describe(approved.Diagnostics));
            Assert.AreEqual(RunState.Queued, after.Coordinator.StateOf(awaitingId));

            var started = after.Coordinator.TryStartNext();
            Assert.IsTrue(started.Ok, Describe(started.Diagnostics));
            Assert.IsNotNull(started.Value, "the re-attached Run was not startable");
            var startedId = started.Value!.RunId;
            Assert.Contains(startedId, new[] { queuedId, awaitingId });
            Assert.AreEqual(RunState.Running, after.Coordinator.StateOf(startedId));
            AssertLockJsonNames(_root, startedId, Session2);
        }

        // ── SPEC §1.4 / §E1: Edit Mode re-entry without a domain reload ──

        /// <summary>W4.3-R17: a Run suspended by Play Mode keeps its exclusion and
        /// its journaled Suspended state. The Edit-Mode re-entry reconciliation
        /// re-applies the §E3 item 4 Editor-state gate — while the Editor is busy
        /// the Run stays Suspended and nothing is journaled; once the gate clears
        /// the same call resumes it (the §E1 table's only way back to Running).</summary>
        [Test]
        public void EditModeReEntryReconcilesTheSuspendedRunOnceTheEditorGateClears()
        {
            var fixture = NewFixture();
            RegisterOperations(fixture);
            var runId = AcceptRun(fixture, MarkWaitPlan());
            Tick(fixture);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));

            Assert.IsTrue(fixture.Loop.SuspendActive("play_mode").Ok);
            Assert.AreEqual(RunState.Suspended, fixture.Coordinator.StateOf(runId));

            _editor.IsUpdating = true;
            var held = Reconcile(fixture);
            Assert.IsTrue(held.ExclusionHeld, "the Suspended Run still holds coordinator.lock");
            Assert.AreEqual(1, held.Runs.Count);
            Assert.AreEqual(runId, held.Runs[0].RunId);
            Assert.AreEqual(RunRecoveryOutcome.Suspended, held.Runs[0].Outcome);
            Assert.AreEqual(RunState.Suspended, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(0, CountOf(JournalRecords(runId), JournalRecordType.RunResumed));

            _editor.IsUpdating = false;
            var resumed = Reconcile(fixture);
            Assert.AreEqual(RunRecoveryOutcome.Resumed, ResultOf(resumed, runId).Outcome);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(1, CountOf(JournalRecords(runId), JournalRecordType.RunResumed));

            Assert.AreEqual(RunState.Succeeded, RunUntilTerminal(fixture, runId));
        }

        // ── The exclusion is handed over, never duplicated ──────────────

        /// <summary>A pass re-entered while this domain already drives a Run does
        /// nothing: the Run's own handle is the exclusion, and a second managed
        /// owner could close it through the wrong one.</summary>
        [Test]
        public void ThePassIsANoOpWhileThisDomainAlreadyDrivesARun()
        {
            var fixture = NewFixture();
            RegisterOperations(fixture);
            var runId = AcceptRun(fixture, MarkWaitPlan());
            Tick(fixture);
            var recordsBefore = JournalRecords(runId).Count;

            var report = Recover(fixture, previousOwnedRunId: runId);

            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(0, report.Runs.Count);
            Assert.AreEqual(0, report.Diagnostics.Count);
            Assert.AreEqual(RunState.Running, fixture.Coordinator.StateOf(runId));
            Assert.AreEqual(recordsBefore, JournalRecords(runId).Count);
            Assert.IsFalse(ExclusionFree(_root), "the live Run's exclusion is untouched");
        }

        /// <summary>SPEC §F4 item 1: the pass that finds no unfinished Run still
        /// releases the exclusion it took — keeping it would block every other
        /// Editor for nothing — and records no owner.</summary>
        [Test]
        public void AStartupPassWithNoUnfinishedRunDropsTheExclusionItTook()
        {
            var fixture = NewFixture();
            RegisterOperations(fixture);

            var report = Recover(fixture, previousOwnedRunId: null);

            Assert.IsTrue(report.ExclusionHeld);
            Assert.AreEqual(0, report.Runs.Count);
            Assert.AreEqual(0, report.Diagnostics.Count);
            Assert.IsTrue(LockJsonRecordsNothing(_root), "no Run took the exclusion to record");
            Assert.IsTrue(ExclusionFree(_root));

            var runId = AcceptRun(fixture, MarkPlan());
            Assert.AreEqual(RunState.Succeeded, RunUntilTerminal(fixture, runId));
        }
    }
}
