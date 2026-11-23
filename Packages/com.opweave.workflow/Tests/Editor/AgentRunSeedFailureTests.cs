#nullable enable

// T-W3.2-08 [Edit] tests: local:T-W3.2-08:observation-store-failure — SPEC
// §H1 get_run_status / list_runs over the §H2 item 3 snapshot while the
// Run Store cannot serve: a failed enumeration and a listed Run's failed
// load keep the store's own diagnostic instead of an empty list or an
// "unknown run" verdict, and the tracker reseeds on the main thread —
// triggered by a later observation request (queued through the injected
// dispatch seam the production binding gives MainThreadDispatcher.Post)
// or by a §E8 update — so recovered Runs become visible with no domain
// reload. The instrumented store seams prove the retries still touch
// mutable Core state on the main thread only (SPEC §0.4 / §H2 item 3).

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class AgentRunSeedFailureTests
    {
        private const string WorkflowId = "example.agent-seed-failure";
        private const string OpEmit = "test.seed.emit";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<RunStatusTracker> _trackers = new List<RunStatusTracker>();
        private readonly List<Action> _posted = new List<Action>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveAgentSeedFailureTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var tracker in _trackers)
            {
                tracker.Dispose();
            }
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        // The service under test over the fixture's real seams — the
        // same shape AgentRunObservationTests builds — with the tracker's
        // load/list pair replaced by the test's instrumented delegates
        // and the main-thread dispatch seam captured so the test drains
        // the queued reseeds itself, exactly where the production
        // MainThreadDispatcher drain would run them.
        private AgentApiService Api(
            RunnerHostFixture fixture,
            Func<string, Result<RunSnapshot>> load,
            Func<Result<IReadOnlyList<RunIndexEntry>>> list)
        {
            var tracker = new RunStatusTracker(
                fixture.Host.Events,
                load,
                list,
                dispatchToMainThread: action =>
                {
                    lock (_posted)
                    {
                        _posted.Add(action);
                    }
                });
            _trackers.Add(tracker);
            var root = fixture.Host.RunStoreRoot;
            return new AgentApiService(
                fixture.Host.Store,
                new AgentApiCursorCodec(new byte[32]),
                fixture.Host.Coordinator.Cancel,
                runStatus: tracker,
                runStoreRoot: root,
                debugLogging: () => false,
                readChanges: runId =>
                {
                    var records = JournaledChanges.Read(
                        JournaledChanges.PathOf(root, runId), out var failure);
                    return failure is null
                        ? Result<IReadOnlyList<ChangeRecord>>.Success(records)
                        : Result<IReadOnlyList<ChangeRecord>>.Failure(
                            ErrorCodes.Create(ErrorCodes.OW_STORE_002, failure));
                },
                isDeletedRun: fixture.Host.Store.IsDeletedRun);
        }

        // Runs the reseed callbacks the observation requests queued —
        // on the test's main thread, matching the production drain.
        private void DrainPosted()
        {
            List<Action> pending;
            lock (_posted)
            {
                pending = new List<Action>(_posted);
                _posted.Clear();
            }
            foreach (var action in pending)
            {
                action();
            }
        }

        private int PostedCount()
        {
            lock (_posted)
            {
                return _posted.Count;
            }
        }

        // A Queued Run journaled into the store before the tracker
        // exists — the "existing inactive Run" of the ticket's defect.
        private static string QueuedRun(RunnerHostFixture fixture)
        {
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "s", OpEmit, 'a',
                    outputTypes: new Dictionary<string, string>
                    {
                        ["mark"] = "core.String",
                    }),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "s", Operation = OpEmit, Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        private static string ErrorCode(AgentApiResponse response) =>
            (string?)((JObject)response.ToWire()["errors"]![0]!)["code"] ?? "";

        private static List<string> RunIds(AgentApiResponse response)
        {
            var ids = new List<string>();
            foreach (var run in (JArray)response.Data!["runs"]!)
            {
                ids.Add((string?)run!["runId"] ?? "");
            }
            return ids;
        }

        // The first enumeration fails (OW-STORE-001 — the store's own
        // enumeration-failure code): list_runs and get_run_status both
        // report that diagnostic — never a successful empty list or an
        // OW-RUN-010 — the observation requests queue the retry onto the
        // main thread, and the retried enumeration makes the Runs
        // visible with no domain reload.
        [Test]
        public void FailedEnumeration_ReportsFailureAndReseeds()
        {
            var fixture = NewFixture();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(OpEmit, fixture.Events), 'a');
            var runId = QueuedRun(fixture);
            var mainThread = Environment.CurrentManagedThreadId;
            var storeThreads = new List<int>();
            var enumerations = 0;
            void NoteThread()
            {
                lock (storeThreads)
                {
                    storeThreads.Add(Environment.CurrentManagedThreadId);
                }
            }
            var api = Api(
                fixture,
                load: id =>
                {
                    NoteThread();
                    return fixture.Host.Store.Load(id);
                },
                list: () =>
                {
                    NoteThread();
                    return Interlocked.Increment(ref enumerations) == 1
                        ? Result<IReadOnlyList<RunIndexEntry>>.Failure(
                            ErrorCodes.Create(
                                ErrorCodes.OW_STORE_001,
                                "The Run Store could not enumerate the Runs directory."))
                        : fixture.Host.Store.List(new RunQuery());
                });

            var listed = api.ListRuns(new ListRunsRequest(Limit: 10));
            Assert.IsFalse(listed.Ok,
                "list_runs must fail closed while the enumeration is unproven");
            Assert.AreEqual("OW-STORE-001", ErrorCode(listed),
                "the retained store diagnostic is reported, not a synthetic code");

            var status = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsFalse(status.Ok,
                "an existing Run is never masked as unknown or absent");
            Assert.AreEqual("OW-STORE-001", ErrorCode(status));

            var unproven = api.GetRunStatus(
                new GetRunStatusRequest(Identifiers.NewRunId()));
            Assert.AreEqual("OW-STORE-001", ErrorCode(unproven),
                "an unproven runId is never answered OW-RUN-010");

            Assert.GreaterOrEqual(PostedCount(), 1,
                "the observation requests queued the main-thread retry");
            DrainPosted();

            var recovered = api.ListRuns(new ListRunsRequest(Limit: 10));
            Assert.IsTrue(recovered.Ok,
                "list_runs after the retried enumeration: " + Describe(recovered.Errors));
            CollectionAssert.Contains(RunIds(recovered), runId,
                "the previously invisible Run lists after recovery");

            var recoveredStatus = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsTrue(recoveredStatus.Ok,
                "get_run_status after recovery: " + Describe(recoveredStatus.Errors));
            Assert.AreEqual("queued", (string?)recoveredStatus.Data!["state"]);

            var missing = api.GetRunStatus(
                new GetRunStatusRequest(Identifiers.NewRunId()));
            Assert.AreEqual("OW-RUN-010", ErrorCode(missing),
                "a proven membership makes a genuinely absent runId unknown again");

            foreach (var thread in storeThreads)
            {
                Assert.AreEqual(mainThread, thread,
                    "the Run Store seams only ever run on the main thread");
            }
        }

        // The enumeration succeeds but the listed Run's first load fails
        // (OW-STORE-002): get_run_status reports that failure for the
        // Run — never OW-RUN-010 — and list_runs serves no silently
        // incomplete result; the retried load makes the Run visible.
        [Test]
        public void FailedRunLoad_ReportsFailureAndReseeds()
        {
            var fixture = NewFixture();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(OpEmit, fixture.Events), 'a');
            var runId = QueuedRun(fixture);
            var firstLoad = true;
            var api = Api(
                fixture,
                load: id =>
                {
                    if (firstLoad)
                    {
                        firstLoad = false;
                        return Result<RunSnapshot>.Failure(
                            ErrorCodes.Create(
                                ErrorCodes.OW_STORE_002,
                                "The Run's journal could not be read."));
                    }
                    return fixture.Host.Store.Load(id);
                },
                list: () => fixture.Host.Store.List(new RunQuery()));

            var listed = api.ListRuns(new ListRunsRequest(Limit: 10));
            Assert.IsFalse(listed.Ok,
                "a Run that cannot be served is never a silently partial list");
            Assert.AreEqual("OW-STORE-002", ErrorCode(listed));

            var status = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsFalse(status.Ok);
            Assert.AreEqual("OW-STORE-002", ErrorCode(status),
                "the Run reports its retained load failure, not OW-RUN-010");

            var unknown = api.GetRunStatus(
                new GetRunStatusRequest(Identifiers.NewRunId()));
            Assert.AreEqual("OW-RUN-010", ErrorCode(unknown),
                "the successful enumeration still proves a genuinely absent runId");

            DrainPosted();

            var recovered = api.ListRuns(new ListRunsRequest(Limit: 10));
            Assert.IsTrue(recovered.Ok,
                "list_runs after the retried load: " + Describe(recovered.Errors));
            CollectionAssert.Contains(RunIds(recovered), runId);

            var recoveredStatus = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsTrue(recoveredStatus.Ok,
                "get_run_status after recovery: " + Describe(recoveredStatus.Errors));
            Assert.AreEqual("queued", (string?)recoveredStatus.Data!["state"]);
        }

        // The retry also runs on a later update: while the seed stands
        // failed, the §E8 dispatch of a newly accepted Run drives the
        // reseed on the main thread — no queued callback is needed for
        // the recovered state to become visible.
        [Test]
        public void FailedEnumeration_ReseedsOnRunStateUpdate()
        {
            var fixture = NewFixture();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(OpEmit, fixture.Events), 'a');
            var runId = QueuedRun(fixture);
            var enumerations = 0;
            var api = Api(
                fixture,
                load: id => fixture.Host.Store.Load(id),
                list: () =>
                    Interlocked.Increment(ref enumerations) == 1
                        ? Result<IReadOnlyList<RunIndexEntry>>.Failure(
                            ErrorCodes.Create(
                                ErrorCodes.OW_STORE_001,
                                "The Run Store could not enumerate the Runs directory."))
                        : fixture.Host.Store.List(new RunQuery()));

            var status = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.AreEqual("OW-STORE-001", ErrorCode(status));

            // Accepting another Run publishes RunStateChanged — the
            // update path itself retries the failed enumeration on the
            // main thread before the Run's own reload.
            var second = QueuedRun(fixture);

            var recovered = api.ListRuns(new ListRunsRequest(Limit: 10));
            Assert.IsTrue(recovered.Ok,
                "list_runs after the update-driven reseed: " + Describe(recovered.Errors));
            var ids = RunIds(recovered);
            CollectionAssert.Contains(ids, runId,
                "the Run invisible at seed lists after the retried enumeration");
            CollectionAssert.Contains(ids, second);
            var recoveredStatus = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsTrue(recoveredStatus.Ok,
                "get_run_status after recovery: " + Describe(recoveredStatus.Errors));
            Assert.AreEqual("queued", (string?)recoveredStatus.Data!["state"]);
        }

        // The retry an observation request triggers never runs on the
        // requesting thread: a background get_run_status only queues the
        // reseed — the store seams stay on the main thread where the
        // production dispatcher would drain them (SPEC §0.4 / §H2 item 3).
        [Test]
        public void BackgroundObservationRequest_OnlyQueuesTheReseed()
        {
            var fixture = NewFixture();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(OpEmit, fixture.Events), 'a');
            var runId = QueuedRun(fixture);
            var mainThread = Environment.CurrentManagedThreadId;
            var storeThreads = new List<int>();
            void NoteThread()
            {
                lock (storeThreads)
                {
                    storeThreads.Add(Environment.CurrentManagedThreadId);
                }
            }
            var enumerations = 0;
            var api = Api(
                fixture,
                load: id =>
                {
                    NoteThread();
                    return fixture.Host.Store.Load(id);
                },
                list: () =>
                {
                    NoteThread();
                    return Interlocked.Increment(ref enumerations) == 1
                        ? Result<IReadOnlyList<RunIndexEntry>>.Failure(
                            ErrorCodes.Create(
                                ErrorCodes.OW_STORE_001,
                                "The Run Store could not enumerate the Runs directory."))
                        : fixture.Host.Store.List(new RunQuery());
                });

            var status = Task.Run(
                () => api.GetRunStatus(new GetRunStatusRequest(runId)));
            Assert.IsTrue(
                status.Wait(TimeSpan.FromSeconds(30)),
                "the background request must answer with the retained failure");
            Assert.IsFalse(status.Result.Ok);
            Assert.AreEqual("OW-STORE-001", ErrorCode(status.Result));
            var storeCallsAfterRequest = storeThreads.Count;

            Assert.GreaterOrEqual(PostedCount(), 1,
                "the background request queued the main-thread retry");
            DrainPosted();
            Assert.Greater(storeThreads.Count, storeCallsAfterRequest,
                "draining the queued reseed is what reaches the store");

            var recovered = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsTrue(recovered.Ok,
                "get_run_status after recovery: " + Describe(recovered.Errors));
            Assert.AreEqual("queued", (string?)recovered.Data!["state"]);
            foreach (var thread in storeThreads)
            {
                Assert.AreEqual(mainThread, thread,
                    "the Run Store seams only ever run on the main thread");
            }
        }

        // The mid-reseed window is fail-closed end to end (PR #410
        // correction): the retried enumeration already succeeded and
        // published its listed Run, but that Run's load is still in
        // flight on the main thread — paused on a ManualResetEventSlim
        // inside the injected load seam, never a sleep. While the load
        // stands pending a list_runs and get_run_status issued from
        // another thread keep reporting the retained enumeration
        // failure: never a silently partial list and never OW-RUN-010 —
        // not for the listed Run and not for a runId whose absence the
        // unfinished pass cannot prove yet. Releasing the load
        // publishes the complete state.
        [Test]
        public void PendingSeedLoad_FailsClosedUntilEveryLoadAnswers()
        {
            var fixture = NewFixture();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(OpEmit, fixture.Events), 'a');
            var runId = QueuedRun(fixture);
            var loadEntered = new ManualResetEventSlim();
            var releaseLoad = new ManualResetEventSlim();
            var enumerations = 0;
            var api = Api(
                fixture,
                load: id =>
                {
                    loadEntered.Set();
                    releaseLoad.Wait(TimeSpan.FromSeconds(60));
                    return fixture.Host.Store.Load(id);
                },
                list: () =>
                    Interlocked.Increment(ref enumerations) == 1
                        ? Result<IReadOnlyList<RunIndexEntry>>.Failure(
                            ErrorCodes.Create(
                                ErrorCodes.OW_STORE_001,
                                "The Run Store could not enumerate the Runs directory."))
                        : fixture.Host.Store.List(new RunQuery()));

            // The construction enumeration fails: the retained
            // OW-STORE-001 is the tracker's answer and the request
            // queues the main-thread retry.
            var failed = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.AreEqual("OW-STORE-001", ErrorCode(failed));
            Assert.GreaterOrEqual(PostedCount(), 1,
                "the observation request queued the main-thread retry");

            // The background observer: while the listed Run's load
            // stands paused on the main thread it asks for the list,
            // the listed Run's status and an unproven runId — then
            // releases the load.
            var observed = new List<AgentApiResponse>();
            var entered = false;
            var observer = new Thread(() =>
            {
                try
                {
                    entered = loadEntered.Wait(TimeSpan.FromSeconds(30));
                    observed.Add(api.ListRuns(new ListRunsRequest(Limit: 10)));
                    observed.Add(api.GetRunStatus(new GetRunStatusRequest(runId)));
                    observed.Add(api.GetRunStatus(
                        new GetRunStatusRequest(Identifiers.NewRunId())));
                }
                finally
                {
                    releaseLoad.Set();
                }
            });
            observer.Start();
            DrainPosted();
            observer.Join();

            Assert.IsTrue(entered,
                "the reseed's load of the listed Run never started");
            Assert.AreEqual(3, observed.Count);
            Assert.IsFalse(observed[0].Ok,
                "list_runs mid-load must fail closed, never a partial list");
            Assert.AreEqual("OW-STORE-001", ErrorCode(observed[0]),
                "the retained enumeration failure answers for the whole membership");
            Assert.IsFalse(observed[1].Ok);
            Assert.AreEqual("OW-STORE-001", ErrorCode(observed[1]),
                "a listed Run still being loaded keeps the seed failure, never OW-RUN-010");
            Assert.IsFalse(observed[2].Ok);
            Assert.AreEqual("OW-STORE-001", ErrorCode(observed[2]),
                "an unproven runId is never OW-RUN-010 while the pass cannot prove absence");

            var recovered = api.ListRuns(new ListRunsRequest(Limit: 10));
            Assert.IsTrue(recovered.Ok,
                "list_runs after the released load: " + Describe(recovered.Errors));
            CollectionAssert.Contains(RunIds(recovered), runId);
            var recoveredStatus = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsTrue(recoveredStatus.Ok,
                "get_run_status after recovery: " + Describe(recoveredStatus.Errors));
            Assert.AreEqual("queued", (string?)recoveredStatus.Data!["state"]);
            var missing = api.GetRunStatus(
                new GetRunStatusRequest(Identifiers.NewRunId()));
            Assert.AreEqual("OW-RUN-010", ErrorCode(missing),
                "a completed enumeration proves an absent runId unknown again");
        }
    }
}
