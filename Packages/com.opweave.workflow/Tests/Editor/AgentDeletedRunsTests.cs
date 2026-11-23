#nullable enable

// T-W4.4-02 [Edit] tests: local:T-W4.4-02:deleted-runid — SPEC §0.6
// SG-16 through the §H1 Agent surface: get_run_status, get_run_logs and
// list_runs answer the Core-owned deletion verdict — OW-STORE-003 for a
// tombstoned runId, OW-RUN-010 for a never-created one — through the
// Run Store's own tombstone file, never a second tombstone store on the
// AgentApi side.

using System;
using System.Collections.Generic;
using System.IO;
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
    internal sealed class AgentDeletedRunsTests
    {
        private const string WorkflowId = "example.agent-deleted";
        private const string OpEmit = "test.deleted.emit";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<RunStatusTracker> _trackers = new List<RunStatusTracker>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveAgentDeletedTests",
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
        // same shape AgentRunObservationTests builds — with the SG-16
        // tombstone verdict bound to the Run Store's own lookup, the
        // production AgentApiInstaller binding.
        private AgentApiService Api(RunnerHostFixture fixture)
        {
            var tracker = new RunStatusTracker(
                fixture.Host.Events,
                runId => fixture.Host.Store.Load(runId),
                () => fixture.Host.Store.List(new RunQuery()));
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

        private static string Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        private static void Drive(OpWeaveRunnerHost host, string runId, int maxTicks = 50)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    return;
                }
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not reach a terminal state within " + maxTicks + " ticks.");
        }

        // A one-step Run driven to Succeeded — a terminal §F8 deletion
        // candidate the tombstone transaction accepts.
        private static string FinishedRun(RunnerHostFixture fixture)
        {
            var events = new List<string>();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(OpEmit, events), 'a');
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
            var runId = Accept(fixture, plan, doc);
            Drive(fixture.Host, runId);
            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            return runId;
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

        // The §H1 read path on a deleted Run: the Core tombstone makes
        // every reader answer OW-STORE-003 where OW-RUN-010 used to
        // stand — status, logs and list — while a never-created id keeps
        // OW-RUN-010: the deleted-vs-never-created distinction of SPEC
        // §0.6 SG-16, unmodified through AgentApi.
        [Test]
        public void DeletedRunAnswersOwStore003AcrossEveryReader()
        {
            var fixture = NewFixture();
            var runId = FinishedRun(fixture);
            var api = Api(fixture);
            Assert.IsTrue(
                fixture.Host.Store.Delete(runId).Ok,
                "the Core deletion failed");
            Assert.IsTrue(
                File.Exists(Path.Combine(_root, "deleted-runs.ndjson")),
                "the Core tombstone file exists — the only tombstone store");

            var status = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsFalse(status.Ok, "get_run_status of a deleted Run must fail");
            Assert.AreEqual("OW-STORE-003", ErrorCode(status));
            var logs = api.GetRunLogs(new GetRunLogsRequest(runId));
            Assert.IsFalse(logs.Ok, "get_run_logs of a deleted Run must fail");
            Assert.AreEqual("OW-STORE-003", ErrorCode(logs));

            var unknown = Identifiers.NewRunId();
            var missingStatus = api.GetRunStatus(new GetRunStatusRequest(unknown));
            Assert.IsFalse(missingStatus.Ok);
            Assert.AreEqual(
                "OW-RUN-010", ErrorCode(missingStatus),
                "a never-created runId keeps the unknown verdict");
            var missingLogs = api.GetRunLogs(new GetRunLogsRequest(unknown));
            Assert.IsFalse(missingLogs.Ok);
            Assert.AreEqual("OW-RUN-010", ErrorCode(missingLogs));
        }

        // The stale-snapshot case: the tracker still holds the finished
        // Run's last status when the store deletes it — list_runs must
        // hide it and get_run_status must answer deleted, because the
        // verdict is the tombstone file's, not the snapshot's.
        [Test]
        public void DeletedRunIsHiddenFromListRuns()
        {
            var fixture = NewFixture();
            var runId = FinishedRun(fixture);
            var api = Api(fixture);
            // Seed the snapshot first, then delete underneath it.
            Assert.IsTrue(api.GetRunStatus(new GetRunStatusRequest(runId)).Ok);
            Assert.IsTrue(fixture.Host.Store.Delete(runId).Ok);

            var listed = api.ListRuns(new ListRunsRequest(Limit: 10));
            Assert.IsTrue(listed.Ok, "list_runs failed: " + Describe(listed.Errors));
            var runs = (JArray)listed.Data!["runs"]!;
            foreach (var entry in runs)
            {
                Assert.AreNotEqual(
                    runId, (string?)entry["runId"],
                    "a tombstoned Run is never listed");
            }
        }

        // The single-store proof of SPEC §0.6 SG-16: AgentApi keeps no
        // tombstone copy — a tombstone record written straight into the
        // Core file answers the very next get_run_status as deleted,
        // without any event reaching the service first.
        [Test]
        public void TombstoneVerdictIsTheCoreStoreOwn()
        {
            var fixture = NewFixture();
            var runId = FinishedRun(fixture);
            var api = Api(fixture);
            Assert.IsTrue(
                api.GetRunStatus(new GetRunStatusRequest(runId)).Ok,
                "the finished Run answers before the tombstone exists");
            File.WriteAllText(
                Path.Combine(_root, "deleted-runs.ndjson"),
                DeletedRuns.Line(
                    runId, UtcTimestamp.Now(), DeletedRuns.ReasonRetention) + "\n");

            var status = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.IsFalse(status.Ok);
            Assert.AreEqual(
                "OW-STORE-003", ErrorCode(status),
                "the verdict is read from the Core tombstone store itself");
            var logs = api.GetRunLogs(new GetRunLogsRequest(runId));
            Assert.IsFalse(logs.Ok);
            Assert.AreEqual("OW-STORE-003", ErrorCode(logs));
        }
    }
}
