#nullable enable

// T-W3.2-03 [Edit] tests: local:T-W3.2-03:workflow-commands — SPEC §H1
// (validate_workflow, execute_workflow, migrate_workflow), §H3 item 2
// (compact Plan summary), §D0.1/§D7 item 7 (expectedPlanHash → OW-RUN-003),
// §H4.1/§0.1 F-05 (IntegrityLevel=full → OW-OP-011, never downgraded),
// §A7 item 3 (migrated JSON + change list, never a file write) and the
// §H4 permission-stage fixtures moved from T-W3.4-01: Agent code.write is
// never below ask, code.template follows its own setting, a destructive
// Operation without Undo=Full is at least ask, a whitelisted Operation
// with an ask effect still appears in approvals and deny yields
// OW-PERM-001. AgentApi is called directly (no Pipeline, SPEC §H1 test 1)
// against the production backend bound to a real RunnerHost/FileRunStore.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Migration;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class AgentWorkflowCommandsTests
    {
        private const string WorkflowId = "example.agent-commands";
        private const string MarkerOpId = "test.agent.marker";
        private const string MigrateOpId = "test.agent.migrate";

        private const string FullSettings =
            "{\"version\":2,\"extensions\":{\"integrityLevel\":\"full\"}}";

        private string _root = null!;
        private string _projectRoot = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveAgentWorkflowTests",
                "t" + Guid.NewGuid().ToString("N"));
            // The 'path' argument anchor of the fixture backend: files the
            // command reads live under this probe project, never under
            // Assets/ — the teardown removes the whole probe root.
            _projectRoot = Path.Combine(_root, "project");
        }

        [TearDown]
        public void TearDown()
        {
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

        // The service under test: the fixture's real Run Store and cancel
        // seam, plus the production UnityAgentWorkflowBackend bound to the
        // same seams the installed wiring uses — the host's settings-derived
        // run-policy environment, the fixture snapshot, the standard
        // TypeProvider pair and the host's §E2 AcceptRun gate chain.
        private AgentApiService Api(RunnerHostFixture fixture) =>
            new AgentApiService(
                fixture.Host.Store,
                new AgentApiCursorCodec(new byte[32]),
                fixture.Host.Coordinator.Cancel,
                workflows: new UnityAgentWorkflowBackend(
                    _projectRoot,
                    fixture.Host.CurrentRunPolicyEnvironment,
                    () => fixture.Snapshot(),
                    StandardProviders,
                    fixture.Host.AcceptRun,
                    typeProbe: fixture.Probe,
                    editorState: fixture.Editor));

        private static TypeProviderCandidate[] StandardProviders() =>
            new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(),
                    OperationOrigin.Standard),
                new TypeProviderCandidate(
                    UnityTypeProvider.ProviderId, new UnityTypeProvider(),
                    OperationOrigin.Standard),
            };

        // Registers the entry point plus one fixture Operation so the
        // synthetic snapshot can resolve the two-node fixture workflows.
        private static void RegisterOps(RunnerHostFixture fixture, IOperation op)
        {
            fixture.Register(
                new RunnerHostFixture.MarkerOperation("core.start", fixture.Events), 'a');
            fixture.Register(op, 'c');
        }

        // A two-node fixture document body: start → one node of the given
        // Operation version. core.start and the edge are always the same
        // so the compiled shape never depends on test-side choices.
        private static JObject WorkflowBody(string operationId, int version = 1) =>
            JObject.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"" + WorkflowId + "\"," +
                "\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"op\",\"operation\":\"" + operationId + "\",\"version\":" +
                    version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" +
                "],\"controlEdges\":[{\"from\":\"start\",\"to\":\"op\"}]}");

        private void WriteProjectFile(string relativePath, string text)
        {
            var full = Path.Combine(_projectRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        private string ProjectFile(string relativePath) =>
            Path.Combine(_projectRoot, relativePath);

        private static void AssertErrorCode(AgentApiResponse response, string code)
        {
            Assert.IsTrue(
                response.Errors.Any(d => d.Code == code),
                $"expected error {code}; got: {Describe(response.Errors)}");
        }

        private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));

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

        // ── validate_workflow (SPEC §H1, §H3 item 2) ─────────────────────

        // A valid inline document compiles through the shared compiler and
        // answers the compact summary: step count, the declared effects,
        // the approval list and the §D0.1 planHash the caller can bind.
        [Test]
        public void ValidateInlineWorkflow_ReturnsCompactSummaryWithPlanHash()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new RunnerHostFixture.MarkerOperation(
                MarkerOpId, fixture.Events));

            var response = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody(MarkerOpId), null));

            Assert.IsTrue(response.Ok, "validate failed: " + Describe(response.Errors));
            var data = (JObject)response.Data!;
            Assert.AreEqual(2, (int?)data["steps"], "start and the marker step");
            Assert.That(
                ((JArray)data["effects"]!).Select(e => (string?)e).ToArray(),
                Is.EqualTo(new[] { EffectNames.Read }));
            Assert.AreEqual(0, ((JArray)data["approvals"]!).Count,
                "a read effect needs no approval under the Agent default policy");
            var planHash = (string?)data["planHash"];
            Assert.IsNotNull(planHash);
            Assert.IsTrue(
                System.Text.RegularExpressions.Regex.IsMatch(planHash!, @"\A[0-9a-f]{64}\z"),
                "planHash is a 64-digit lowercase SHA-256 hex string");
            Assert.IsNull(data["plan"], "the compact summary carries no Plan by default");
            Assert.IsNull(data["plannedChanges"], "a static validate runs no preflight");
        }

        // The 'path' source reads the same document from inside the
        // project — same workflow, same planHash as the inline call.
        [Test]
        public void ValidateWorkflow_PathSource_ReadsTheProjectFile()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new RunnerHostFixture.MarkerOperation(
                MarkerOpId, fixture.Events));
            var api = Api(fixture);
            WriteProjectFile(
                "wf/valid.opweave.json",
                WorkflowBody(MarkerOpId).ToString(Newtonsoft.Json.Formatting.None));

            var inline = api.ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody(MarkerOpId), null));
            var fromPath = api.ValidateWorkflow(
                new ValidateWorkflowRequest(null, "wf/valid.opweave.json"));

            Assert.IsTrue(fromPath.Ok, "path validate failed: " + Describe(fromPath.Errors));
            Assert.AreEqual(
                (string?)((JObject)inline.Data!)["planHash"],
                (string?)((JObject)fromPath.Data!)["planHash"],
                "the same document produces the same planHash from either source");
        }

        // A '..' escape or a rooted path never reaches the file system —
        // OW-API-001 request-shape misuse.
        [Test]
        public void ValidateWorkflow_PathEscapes_ReturnOwApi001()
        {
            var fixture = NewFixture();
            var api = Api(fixture);

            foreach (var bad in new[] { "../outside.opweave.json", "wf/../../outside.json" })
            {
                var escape = api.ValidateWorkflow(new ValidateWorkflowRequest(null, bad));
                Assert.IsFalse(escape.Ok);
                AssertErrorCode(escape, "OW-API-001");
            }

            var rooted = api.ValidateWorkflow(
                new ValidateWorkflowRequest(null, Path.Combine(_projectRoot, "wf/x.opweave.json")));
            Assert.IsFalse(rooted.Ok);
            AssertErrorCode(rooted, "OW-API-001");
        }

        // Exactly one source: supplying both, neither or a non-object body
        // is OW-API-001 before any backend call.
        [Test]
        public void ValidateWorkflow_SourceShapeMisuse_ReturnsOwApi001()
        {
            var fixture = NewFixture();
            var api = Api(fixture);
            var body = WorkflowBody(MarkerOpId);

            var both = api.ValidateWorkflow(new ValidateWorkflowRequest(body, "wf/x.opweave.json"));
            Assert.IsFalse(both.Ok);
            AssertErrorCode(both, "OW-API-001");

            var neither = api.ValidateWorkflow(new ValidateWorkflowRequest(null, null));
            Assert.IsFalse(neither.Ok);
            AssertErrorCode(neither, "OW-API-001");

            var array = api.ValidateWorkflow(
                new ValidateWorkflowRequest(new JArray(body), null));
            Assert.IsFalse(array.Ok);
            AssertErrorCode(array, "OW-API-001");
        }

        // An invalid document reports the validator's own diagnostics —
        // never a fabricated success.
        [Test]
        public void ValidateWorkflow_InvalidDocument_ReturnsDiagnostics()
        {
            var fixture = NewFixture();
            var bad = JObject.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"example.no-start\"," +
                "\"nodes\":[],\"controlEdges\":[]}");

            var response = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(bad, null));

            Assert.IsFalse(response.Ok);
            AssertErrorCode(response, "OW-VAL-001");
            Assert.IsNull(response.Data, "a failed validate carries no summary");
        }

        // dryRun: true runs the §D8 preflight phase — the predicted changes
        // ride in data.plannedChanges with the fixed guarantee:"none"
        // marker.
        [Test]
        public void ValidateWorkflow_DryRun_ReportsPlannedChangesWithNoGuarantee()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new PlanningOperation(MarkerOpId));

            var response = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody(MarkerOpId), null, DryRun: true));

            Assert.IsTrue(response.Ok, "dry run failed: " + Describe(response.Errors));
            var data = (JObject)response.Data!;
            var changes = (JArray)data["plannedChanges"]!;
            Assert.AreEqual(1, changes.Count, "the fixture preflight predicts one change");
            var change = (JObject)changes[0]!;
            Assert.AreEqual("create", (string?)change["kind"]);
            Assert.AreEqual("Assets/agent-commands/probe.txt", (string?)change["target"]);
            Assert.AreEqual(EffectNames.AssetCreate, (string?)change["effect"]);
            Assert.AreEqual("none", (string?)data["guarantee"],
                "SPEC §D8 item 3: a Dry Run's predictions guarantee nothing");
        }

        // includePlan: true adds the serialized Plan (plan.json content)
        // alongside the compact summary.
        [Test]
        public void ValidateWorkflow_IncludePlan_SerializesThePlan()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new RunnerHostFixture.MarkerOperation(
                MarkerOpId, fixture.Events));

            var response = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody(MarkerOpId), null, IncludePlan: true));

            Assert.IsTrue(response.Ok, "validate failed: " + Describe(response.Errors));
            var data = (JObject)response.Data!;
            var plan = data["plan"] as JObject;
            Assert.IsNotNull(plan, "includePlan adds the serialized Plan to data");
            Assert.AreEqual(
                (string?)data["planHash"], (string?)plan!["planHash"],
                "the serialized Plan is the one the summary hashes");
            Assert.AreEqual(1, (int?)plan["formatVersion"], "plan.json formatVersion");
        }

        // ── execute_workflow (SPEC §H1, §E2, §D7 item 7) ────────────────

        // The §D7 item 7 round trip: validate yields the planHash, execute
        // binds it through expectedPlanHash, and the admitted Run carries
        // runId + its acceptance state — then really runs to Succeeded.
        [Test]
        public void ExecuteWorkflow_ValidWorkflow_AdmitsAndRuns()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new RunnerHostFixture.MarkerOperation(
                MarkerOpId, fixture.Events));
            var api = Api(fixture);
            var body = WorkflowBody(MarkerOpId);

            var validated = api.ValidateWorkflow(new ValidateWorkflowRequest(body, null));
            Assert.IsTrue(validated.Ok, "validate failed: " + Describe(validated.Errors));
            var planHash = (string?)((JObject)validated.Data!)["planHash"];
            Assert.IsNotNull(planHash);

            var executed = api.ExecuteWorkflow(new ExecuteWorkflowRequest(
                body, null, null, ExpectedPlanHash: planHash));

            Assert.IsTrue(executed.Ok, "execute failed: " + Describe(executed.Errors));
            var data = (JObject)executed.Data!;
            var runId = (string?)data["runId"];
            Assert.IsNotNull(runId);
            Assert.IsTrue(runId!.StartsWith("run_", StringComparison.Ordinal),
                "runId carries the §F1 prefix");
            Assert.AreEqual("queued", (string?)data["state"],
                "a no-approval Run is admitted Queued (SPEC §E2)");

            Drive(fixture.Host, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId),
                "the admitted Plan is the one the Runner executes");
        }

        // A mismatched expectedPlanHash is refused before anything is
        // persisted — OW-RUN-003 (SPEC §D7 item 7, §E2).
        [Test]
        public void ExecuteWorkflow_ExpectedPlanHashMismatch_ReturnsOwRun003()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new RunnerHostFixture.MarkerOperation(
                MarkerOpId, fixture.Events));
            var api = Api(fixture);

            var response = api.ExecuteWorkflow(new ExecuteWorkflowRequest(
                WorkflowBody(MarkerOpId), null, null,
                ExpectedPlanHash: new string('0', 64)));

            Assert.IsFalse(response.Ok);
            AssertErrorCode(response, "OW-RUN-003");
            Assert.IsNull(response.Data, "a refused execution reports no runId");
            var runs = fixture.Host.Store.List(new RunQuery());
            Assert.IsTrue(runs.Ok);
            Assert.AreEqual(0, runs.Value!.Count, "no Run directory was created");
        }

        // An invalid document never reaches acceptance — the same failure
        // validate reports, and the Run Store stays empty.
        [Test]
        public void ExecuteWorkflow_InvalidWorkflow_NeverProducesRunId()
        {
            var fixture = NewFixture();
            var bad = JObject.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"example.no-start\"," +
                "\"nodes\":[],\"controlEdges\":[]}");

            var response = Api(fixture).ExecuteWorkflow(
                new ExecuteWorkflowRequest(bad, null, null));

            Assert.IsFalse(response.Ok);
            AssertErrorCode(response, "OW-VAL-001");
            Assert.IsNull(response.Data);
            var runs = fixture.Host.Store.List(new RunQuery());
            Assert.IsTrue(runs.Ok);
            Assert.AreEqual(0, runs.Value!.Count,
                "an invalid workflow creates no Run side effects");
        }

        // SPEC §H4.1 / D-R6: a settings file in the error state fails the
        // validate command closed with the loader's own OW-CFG-001 — never
        // a compile under defaults that were never approved. Covers the
        // adapter-side policy-environment seam the backend binds: the
        // Agent-facing assembly keeps no settings-loading member.
        [Test]
        public void ValidateWorkflow_LoadFailure_FailsClosedWithOwCfg001()
        {
            var fixture = NewFixture();
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse("{ not json");

            var response = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody(MarkerOpId), null));

            Assert.IsFalse(response.Ok);
            AssertErrorCode(response, "OW-CFG-001");
            Assert.IsNull(response.Data, "a failed validate carries no summary");
        }

        // SPEC §0.1 F-05 / §H4.1: a configured integrityLevel=full is a
        // valid setting that refuses the workflow with OW-OP-011 — never
        // downgraded to weak.
        [Test]
        public void ExecuteWorkflow_FullIntegrityLevel_ReturnsOwOp011()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new RunnerHostFixture.MarkerOperation(
                MarkerOpId, fixture.Events));
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(FullSettings);

            var response = Api(fixture).ExecuteWorkflow(new ExecuteWorkflowRequest(
                WorkflowBody(MarkerOpId), null, null));

            Assert.IsFalse(response.Ok);
            AssertErrorCode(response, "OW-OP-011");
            Assert.IsNull(response.Data);
        }

        // ── migrate_workflow (SPEC §H1, §A7) ────────────────────────────

        // A node written for version 1 migrates to the latest registered
        // version through the registered IOperationMigration: the returned
        // JSON carries the migration's rewrite and the bumped version, the
        // change list records the step, and the caller's input JSON is
        // untouched.
        [Test]
        public void MigrateWorkflow_OperationVersion_UpgradesAndReportsChanges()
        {
            var fixture = NewFixture();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(MigrateOpId, fixture.Events), 'a');
            fixture.Register(new MigratingMarkerOperation(MigrateOpId), 'c');
            var body = JObject.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"example.agent-migrate\"," +
                "\"nodes\":[{\"id\":\"op\",\"operation\":\"" + MigrateOpId + "\"," +
                "\"version\":1,\"parameters\":{\"legacy\":\"v\"}}]," +
                "\"controlEdges\":[]}");

            var response = Api(fixture).MigrateWorkflow(new MigrateWorkflowRequest(body, null));

            Assert.IsTrue(response.Ok, "migrate failed: " + Describe(response.Errors));
            var data = (JObject)response.Data!;
            var migrated = (JObject)data["workflow"]!;
            var node = (JObject)((JArray)migrated["nodes"]!)[0]!;
            Assert.AreEqual(2, (int?)node["version"], "the node lands on the latest version");
            Assert.AreEqual("v", (string?)((JObject)node["parameters"]!)["current"],
                "the migration's own rewrite is applied");
            Assert.IsNull(((JObject)node["parameters"]!)["legacy"]);
            var changes = (JArray)data["changes"]!;
            Assert.AreEqual(1, changes.Count);
            var change = (JObject)changes[0]!;
            Assert.AreEqual("operation_version", (string?)change["kind"]);
            Assert.AreEqual("op", (string?)change["nodeId"]);
            Assert.AreEqual(MigrateOpId, (string?)change["operationId"]);
            Assert.AreEqual("1", (string?)change["fromVersion"]);
            Assert.AreEqual("2", (string?)change["toVersion"]);

            Assert.AreEqual(1, (int?)((JObject)((JArray)body["nodes"]!)[0]!)["version"],
                "SPEC §A7 item 3: the caller's document is never mutated");
        }

        // The 'path' source: the migrated JSON comes back, the file on
        // disk is byte-identical — saving stays with the caller (SPEC §A7
        // item 3).
        [Test]
        public void MigrateWorkflow_PathSource_ReturnsJsonWithoutWritingTheFile()
        {
            var fixture = NewFixture();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(MigrateOpId, fixture.Events), 'a');
            fixture.Register(new MigratingMarkerOperation(MigrateOpId), 'c');
            var original =
                "{\"schemaVersion\":\"1.0\",\"id\":\"example.agent-migrate\"," +
                "\"nodes\":[{\"id\":\"op\",\"operation\":\"" + MigrateOpId + "\"," +
                "\"version\":1,\"parameters\":{\"legacy\":\"v\"}}]," +
                "\"controlEdges\":[]}";
            WriteProjectFile("wf/migrate.opweave.json", original);

            var response = Api(fixture).MigrateWorkflow(
                new MigrateWorkflowRequest(null, "wf/migrate.opweave.json"));

            Assert.IsTrue(response.Ok, "migrate failed: " + Describe(response.Errors));
            var data = (JObject)response.Data!;
            var migrated = (JObject)data["workflow"]!;
            Assert.AreEqual(2, (int?)((JObject)((JArray)migrated["nodes"]!)[0]!)["version"]);
            Assert.AreEqual(
                original, File.ReadAllText(ProjectFile("wf/migrate.opweave.json")),
                "migrate_workflow never writes the file back");
        }

        // A document whose major schemaVersion no registered migration
        // reaches is OW-WF-002 (SPEC §A7 phase 1, §J).
        [Test]
        public void MigrateWorkflow_UnsupportedSchema_ReturnsOwWf002()
        {
            var fixture = NewFixture();
            var body = JObject.Parse(
                "{\"schemaVersion\":\"0.9\",\"id\":\"example.ancient\"," +
                "\"nodes\":[],\"controlEdges\":[]}");

            var response = Api(fixture).MigrateWorkflow(new MigrateWorkflowRequest(body, null));

            Assert.IsFalse(response.Ok);
            AssertErrorCode(response, "OW-WF-002");
        }

        // ── §H4 permission stages through the commands (from T-W3.4-01) ─

        // Stage 3 floor: an Agent settings file may not relax code.write —
        // "allow" is judged at least ask, so the approval stays on the
        // Plan the summary reports.
        [Test]
        public void ValidateWorkflow_AgentCodeWriteAllow_StillRequiresAsk()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new EffectOperation(
                "test.agent.codewrite", new[] { EffectNames.CodeWrite }));
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"policy\":{\"agent\":{\"code.write\":\"allow\"}}}");

            var response = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody("test.agent.codewrite"), null));

            Assert.IsTrue(response.Ok, "validate failed: " + Describe(response.Errors));
            AssertApproval(response, "op", EffectNames.CodeWrite);
        }

        // Stage 2 follows its own setting: ask produces the approval, the
        // Agent-default allow produces none.
        [Test]
        public void ValidateWorkflow_CodeTemplate_FollowsItsOwnSetting()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new EffectOperation(
                "test.agent.template", new[] { EffectNames.CodeTemplate }));
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"policy\":{\"agent\":{\"code.template\":\"ask\"}}}");

            var ask = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody("test.agent.template"), null));
            Assert.IsTrue(ask.Ok, "validate failed: " + Describe(ask.Errors));
            AssertApproval(ask, "op", EffectNames.CodeTemplate);

            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2}");
            var allowed = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody("test.agent.template"), null));
            Assert.IsTrue(allowed.Ok, "validate failed: " + Describe(allowed.Errors));
            Assert.AreEqual(
                0, ((JArray)((JObject)allowed.Data!)["approvals"]!).Count,
                "code.template's own allow setting waives the approval");
        }

        // SPEC §H4 item 2: destructive without Undo=Full is at least ask
        // regardless of the effect's own level (asset.modify allows for
        // the Agent); Undo=Full escapes the floor.
        [Test]
        public void ValidateWorkflow_DestructiveWithoutFullUndo_RequiresAsk()
        {
            var fixture = NewFixture();
            fixture.Register(
                new RunnerHostFixture.MarkerOperation("core.start", fixture.Events), 'a');
            fixture.Register(
                new EffectOperation(
                    "test.agent.destructive", new[] { EffectNames.AssetModify },
                    destructive: true, undo: UndoSupport.None), 'c');
            fixture.Register(
                new EffectOperation(
                    "test.agent.undone", new[] { EffectNames.AssetModify },
                    destructive: true, undo: UndoSupport.Full), 'e');
            var api = Api(fixture);

            var floored = api.ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody("test.agent.destructive"), null));
            Assert.IsTrue(floored.Ok, "validate failed: " + Describe(floored.Errors));
            AssertApproval(floored, "op", EffectNames.AssetModify);

            var undone = api.ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody("test.agent.undone"), null));
            Assert.IsTrue(undone.Ok, "validate failed: " + Describe(undone.Errors));
            Assert.AreEqual(
                0, ((JArray)((JObject)undone.Data!)["approvals"]!).Count,
                "Undo=Full releases the destructive floor");
        }

        // SPEC §H4 test 4 / §C0.2 / §X.9: whitelist membership is a
        // separate stage — the Operation's ask effect still lands in
        // approvals even when the settings whitelist names it exactly.
        [Test]
        public void ValidateWorkflow_WhitelistedOperation_AskEffectStaysInApprovals()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new EffectOperation(
                "test.agent.whitelisted", new[] { EffectNames.AssetDelete }));
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"extensions\":{\"discoveryMode\":\"whitelist\"," +
                "\"whitelist\":{\"operations\":[{" +
                "\"packageId\":\"@project\",\"assemblyName\":\"Example.Operations\"," +
                "\"operationId\":\"test.agent.whitelisted\",\"version\":1}]}}}");

            var response = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody("test.agent.whitelisted"), null));

            Assert.IsTrue(response.Ok, "validate failed: " + Describe(response.Errors));
            AssertApproval(response, "op", EffectNames.AssetDelete);
        }

        // deny produces OW-PERM-001 — the command surfaces the validator's
        // own permission diagnostic.
        [Test]
        public void ValidateWorkflow_DeniedEffect_ReturnsOwPerm001()
        {
            var fixture = NewFixture();
            RegisterOps(fixture, new EffectOperation(
                "test.agent.denied", new[] { EffectNames.AssetDelete }));
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"policy\":{\"agent\":{\"asset.delete\":\"deny\"}}}");

            var response = Api(fixture).ValidateWorkflow(
                new ValidateWorkflowRequest(WorkflowBody("test.agent.denied"), null));

            Assert.IsFalse(response.Ok);
            AssertErrorCode(response, "OW-PERM-001");
        }

        private static void AssertApproval(
            AgentApiResponse response, string nodeId, string effect)
        {
            var approvals = (JArray)((JObject)response.Data!)["approvals"]!;
            Assert.IsTrue(
                approvals.Any(a =>
                    (string?)a["nodeId"] == nodeId && (string?)a["effect"] == effect),
                $"expected an approval for {effect} on {nodeId}; got: {approvals}");
        }

        // ── Test operations ─────────────────────────────────────────────

        // An Immediate marker declaring arbitrary effects and the §C1
        // destructive/Undo pair — the §H4 stage fixtures never execute it.
        private sealed class EffectOperation : IOperation
        {
            internal EffectOperation(
                string id, string[] effects, bool destructive = false,
                UndoSupport undo = UndoSupport.None)
            {
                var built = Op.Define(id, 1)
                    .Summary("Declares the effects the policy test needs.")
                    .Effects(effects)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
                Descriptor = built with { Destructive = destructive, Undo = undo };
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // An Immediate whose Preflight predicts one planned change — the
        // §D8 dryRun fixture.
        private sealed class PlanningOperation : IOperation
        {
            internal PlanningOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Predicts one planned change.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    new[]
                    {
                        new PlannedChange(
                            "op", "create", "Assets/agent-commands/probe.txt",
                            EffectNames.AssetCreate),
                    },
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // The version-2 implementation of the migration fixture: its
        // IOperationMigration accepts node version 1 and rewrites the
        // 'legacy' parameter to 'current'; the port-rename metadata
        // declares explicitly that no external port is renamed (the §A7
        // fail-closed contract an undeclared list would trip).
        private sealed class MigratingMarkerOperation
            : IOperation, IOperationMigration, IOperationMigrationPortRenames
        {
            internal MigratingMarkerOperation(string id)
            {
                Descriptor = Op.Define(id, 2)
                    .Summary("Migrates version-1 nodes to version 2.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }
            public int FromVersion => 1;
            public IReadOnlyList<OperationPortRename> PortRenames { get; } =
                Array.AsReadOnly(Array.Empty<OperationPortRename>());

            public void Migrate(JObject node)
            {
                if (node["parameters"] is JObject parameters &&
                    parameters["legacy"] is { } value)
                {
                    parameters.Remove("legacy");
                    parameters["current"] = value;
                }
            }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
