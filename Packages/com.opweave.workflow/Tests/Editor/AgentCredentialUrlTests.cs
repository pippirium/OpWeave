#nullable enable

// T-W4.2-07 [Edit] tests: local:T-W4.2-07:agentapi-execute — SPEC §G8.1 /
// §D6 item 3 / §F1 through the real §H1 Agent API surface. Each SPEC §G8.1
// credential form, plus edge- and binding-fed 'id' suppliers, is offered to
// validate_workflow and execute_workflow on the production
// AgentApiService/UnityAgentWorkflowBackend wiring against the real
// unity.package.add Operation and a real FileRunStore: every credential
// form answers OW-OP-006 "Git URL contains credentials" without a Plan,
// without any Run Store file and without echoing the credential in the
// response or the Unity Console; the credential-free HTTPS and SCP forms
// pass the same commands end to end.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class AgentCredentialUrlTests
    {
        private const string WorkflowId = "example.agent-credential";
        private const string CredentialMessage = "Git URL contains credentials";

        // The five Run Store artifacts SPEC §G8.1 names — a rejected
        // workflow must leave none of them anywhere under the store root.
        private static readonly string[] RunStoreFileNames =
        {
            "plan.json", "workflow.json", "journal.ndjson",
            "state.json", "changes.json", "index.json",
        };

        private string _root = null!;
        private string _projectRoot = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveAgentCredentialTests",
                "t" + Guid.NewGuid().ToString("N"));
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

        // The real Unity Console channel (Application.logMessageReceived) —
        // "inspecting the Console records" of the verification method.
        private sealed class ConsoleTap : IDisposable
        {
            private readonly List<string> _received = new List<string>();

            internal ConsoleTap() =>
                Application.logMessageReceived += OnMessage;

            private void OnMessage(string condition, string stackTrace, LogType type) =>
                _received.Add(condition);

            internal string Joined() => string.Join("\n", _received);

            public void Dispose() =>
                Application.logMessageReceived -= OnMessage;
        }

        // The wait channel the Operation never reaches: a rejected workflow
        // fails before a Plan, and the accepted forms never run a step in
        // these tests — a live Check would be a wiring bug.
        private sealed class NeverPolled : IWaitCondition
        {
            public WaitStatus Check(JsonValue token) =>
                WaitStatus.Pending(token);
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        // The same service wiring AgentWorkflowCommandsTests uses: the
        // production backend over the fixture's real host seams.
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

        // The entry point, the data-edge producer and the real
        // unity.package.add Operation — its production settings/policy seams
        // bound to the fixture so the §G8.1 SG-19 allowlist is the test's
        // only variable.
        private static void RegisterOps(RunnerHostFixture fixture)
        {
            fixture.Register(
                new RunnerHostFixture.MarkerOperation("core.start", fixture.Events), 'a');
            fixture.Register(new IdSourceOperation(), 'b');
            fixture.Register(
                new UnityPackageAddOperation(
                    () => fixture.LoadSettings(),
                    id => PackageResolvedCondition.InitialToken(id, true),
                    new NeverPolled(),
                    () => new PackageInstallView(
                        null, null, Array.Empty<ResolvedPackageView>())),
                'c');
        }

        // The producer whose packageId output feeds the ConstantOnly 'id'
        // port on the edge-fed shape — the rejection never consults the
        // runtime value, so Execute is never reached and stays inert.
        private sealed class IdSourceOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } =
                Op.Define("example.id_source", 1)
                    .Summary("Produce a package identifier.")
                    .Output("packageId", "core.String")
                    .Build();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        private static JObject ConstantIdBody(string packageId) =>
            JObject.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"" + WorkflowId + "\"," +
                "\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"add\",\"operation\":\"unity.package.add\",\"version\":1," +
                "\"parameters\":{\"id\":" + JToken.FromObject(packageId).ToString(
                    Newtonsoft.Json.Formatting.None) + "}" +
                "}],\"controlEdges\":[{\"from\":\"start\",\"to\":\"add\"}]}");

        private static JObject EdgeFedBody() =>
            JObject.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"" + WorkflowId + "\"," +
                "\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"source\",\"operation\":\"example.id_source\",\"version\":1}," +
                "{\"id\":\"add\",\"operation\":\"unity.package.add\",\"version\":1}]," +
                "\"controlEdges\":[" +
                "{\"from\":\"start\",\"to\":\"source\"},{\"from\":\"source\",\"to\":\"add\"}]," +
                "\"dataEdges\":[{" +
                "\"from\":{\"node\":\"source\",\"port\":\"packageId\"}," +
                "\"to\":{\"node\":\"add\",\"port\":\"id\"}}]}");

        private static JObject BindingFedBody() =>
            JObject.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"" + WorkflowId + "\"," +
                "\"inputs\":[{\"id\":\"pkgSource\",\"type\":\"core.String\",\"required\":true}]," +
                "\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"add\",\"operation\":\"unity.package.add\",\"version\":1}]," +
                "\"controlEdges\":[{\"from\":\"start\",\"to\":\"add\"}]," +
                "\"inputBindings\":[{" +
                "\"fromInput\":\"pkgSource\",\"to\":{\"node\":\"add\",\"port\":\"id\"}}]}");

        private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));

        // SPEC §G8.1: no Run Store file exists under the root for a
        // rejected workflow — the five named artifacts, the run index and
        // the Runs/ directory itself.
        private void AssertRunStoreEmpty()
        {
            Assert.IsFalse(
                Directory.Exists(Path.Combine(_root, "Runs")),
                "a rejected workflow created the Runs/ directory");
            if (!Directory.Exists(_root))
            {
                return;
            }
            var persisted = Directory
                .GetFiles(_root, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();
            foreach (var artifact in RunStoreFileNames)
            {
                Assert.IsFalse(
                    persisted.Contains(artifact),
                    "a rejected workflow wrote " + artifact + " to the Run Store");
            }
        }

        // SPEC §G8.1 "出力しない": neither the response envelope nor the
        // Unity Console may carry the credential text.
        private static void AssertNoEcho(
            AgentApiResponse response, string console, params string[] secrets)
        {
            var envelope = response.ToJson();
            foreach (var diagnostic in response.Errors.Concat(response.Warnings))
            {
                envelope += "\n" + string.Join("\n",
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.NodeId ?? string.Empty,
                    diagnostic.Port ?? string.Empty,
                    diagnostic.JsonPath ?? string.Empty,
                    diagnostic.Hint ?? string.Empty);
            }
            foreach (var secret in secrets)
            {
                Assert.IsFalse(
                    envelope.Contains(secret, StringComparison.Ordinal),
                    $"the response leaked '{secret}': {envelope}");
                Assert.IsFalse(
                    console.Contains(secret, StringComparison.Ordinal),
                    $"the Unity Console leaked '{secret}': {console}");
            }
        }

        private void AssertNoFileContains(params string[] secrets)
        {
            if (!Directory.Exists(_root))
            {
                return;
            }
            foreach (var file in Directory.GetFiles(
                _root, "*", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                foreach (var secret in secrets)
                {
                    Assert.IsFalse(
                        content.Contains(secret, StringComparison.Ordinal),
                        $"{Path.GetFileName(file)} leaked '{secret}'");
                }
            }
        }

        // ── SPEC §G8.1 test 5 — every credential form: OW-OP-006 on both
        // commands, no Plan, no Run Store file, no echo ─────────────────────

        private static readonly string[] CredentialIds =
        {
            "https://user:s3cr3t-pw@example.com/org/repo.git",
            "https://tok-9f8e7d@example.com/org/repo.git",
            "https://example.com/org/repo.git?access_token=deadbeef42",
        };

        private static readonly string[] CredentialSecrets =
        {
            "s3cr3t-pw", "tok-9f8e7d", "deadbeef42",
            "user:s3cr3t-pw@example.com", "access_token",
        };

        [TestCaseSource(nameof(CredentialIds))]
        public void ValidateWorkflowCredentialIdIsRejectedWithoutEcho(string packageId)
        {
            var fixture = NewFixture();
            RegisterOps(fixture);
            var api = Api(fixture);
            var tap = new ConsoleTap();
            AgentApiResponse response;
            using (tap)
            {
                response = api.ValidateWorkflow(
                    new ValidateWorkflowRequest(ConstantIdBody(packageId), null));
            }

            Assert.IsFalse(response.Ok,
                "a credential-bearing id must fail validate_workflow");
            Assert.IsNull(response.Data, "a rejected validate carries no summary");
            var error = response.Errors
                .Where(d => d.Code == ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual(CredentialMessage, error.Message);
            Assert.AreEqual("add", error.NodeId);
            Assert.AreEqual("id", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.id", error.JsonPath);

            AssertRunStoreEmpty();
            AssertNoEcho(response, tap.Joined(), packageId);
            AssertNoEcho(response, tap.Joined(), CredentialSecrets);
            AssertNoFileContains(packageId);
            AssertNoFileContains(CredentialSecrets);
        }

        [TestCaseSource(nameof(CredentialIds))]
        public void ExecuteWorkflowCredentialIdIsRejectedWithoutEcho(string packageId)
        {
            var fixture = NewFixture();
            RegisterOps(fixture);
            var api = Api(fixture);
            var tap = new ConsoleTap();
            AgentApiResponse response;
            using (tap)
            {
                response = api.ExecuteWorkflow(new ExecuteWorkflowRequest(
                    ConstantIdBody(packageId), null, null));
            }

            Assert.IsFalse(response.Ok,
                "a credential-bearing id must fail execute_workflow");
            Assert.IsNull(response.Data, "a rejected execute carries no runId");
            Assert.IsTrue(
                response.Errors.Any(d => d.Code == ErrorCodes.OW_OP_006),
                "expected OW-OP-006; got: " + Describe(response.Errors));

            AssertRunStoreEmpty();
            AssertNoEcho(response, tap.Joined(), packageId);
            AssertNoEcho(response, tap.Joined(), CredentialSecrets);
            AssertNoFileContains(packageId);
            AssertNoFileContains(CredentialSecrets);
        }

        // ── SPEC §G8.1 test 7 — fed 'id': OW-OP-006 ConstantOnly regardless
        // of the value, no echo, no Run Store write ─────────────────────────

        [Test]
        public void ExecuteWorkflowEdgeFedIdIsRejectedWithoutEcho()
        {
            var fixture = NewFixture();
            RegisterOps(fixture);
            var api = Api(fixture);
            var tap = new ConsoleTap();
            AgentApiResponse response;
            using (tap)
            {
                response = api.ExecuteWorkflow(
                    new ExecuteWorkflowRequest(EdgeFedBody(), null, null));
            }

            Assert.IsFalse(response.Ok);
            Assert.IsNull(response.Data);
            var error = response.Errors
                .Where(d => d.Code == ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("add", error.NodeId);
            Assert.AreEqual("id", error.Port);
            Assert.AreEqual("$.dataEdges[0]", error.JsonPath);

            AssertRunStoreEmpty();
            AssertNoEcho(response, tap.Joined(), "packageId");
        }

        [Test]
        public void ExecuteWorkflowBindingFedCredentialIdIsRejectedWithoutEcho()
        {
            var secret = "s3cr3t-pw";
            var fixture = NewFixture();
            RegisterOps(fixture);
            var api = Api(fixture);
            var inputs = JObject.Parse(
                "{\"pkgSource\":\"https://user:" + secret + "@example.com/org/repo.git\"}");
            var tap = new ConsoleTap();
            AgentApiResponse response;
            using (tap)
            {
                response = api.ExecuteWorkflow(new ExecuteWorkflowRequest(
                    BindingFedBody(), null, inputs));
            }

            Assert.IsFalse(response.Ok);
            Assert.IsNull(response.Data);
            var error = response.Errors
                .Where(d => d.Code == ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("add", error.NodeId);
            Assert.AreEqual("$.inputBindings[0]", error.JsonPath);

            AssertRunStoreEmpty();
            AssertNoEcho(response, tap.Joined(), secret, "user:" + secret + "@example.com");
            AssertNoFileContains(secret);
        }

        // ── SPEC §G8.1 test 8 — credential-free Git URLs pass both commands
        // end to end against the real Operation and Run Store ───────────────

        private static readonly string[] AcceptedIds =
        {
            "https://example.com/org/repo.git",
            "git@example.com:org/repo.git",
        };

        [TestCaseSource(nameof(AcceptedIds))]
        public void CredentialFreeIdIsAcceptedThroughValidateAndExecute(
            string packageId)
        {
            var fixture = NewFixture();
            RegisterOps(fixture);
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"allowedPackageSources\":[" +
                "\"git:https://example.com/org/repo.git\"," +
                "\"git:git@example.com:org/repo.git\"]}");
            var api = Api(fixture);
            var body = ConstantIdBody(packageId);

            var validated = api.ValidateWorkflow(
                new ValidateWorkflowRequest(body, null));
            Assert.IsTrue(validated.Ok,
                packageId + " should validate: " + Describe(validated.Errors));
            var planHash = (string?)((JObject)validated.Data!)["planHash"];
            Assert.IsNotNull(planHash, "the real Plan must carry a planHash");

            var executed = api.ExecuteWorkflow(new ExecuteWorkflowRequest(
                body, null, null, ExpectedPlanHash: planHash));
            Assert.IsTrue(executed.Ok,
                packageId + " should execute: " + Describe(executed.Errors));
            var data = (JObject)executed.Data!;
            var runId = (string?)data["runId"];
            Assert.IsNotNull(runId, "the accepted Run carries a runId");
            Assert.IsTrue(
                runId!.StartsWith("run_", StringComparison.Ordinal),
                "runId carries the §F1 prefix");

            // The accepted Run really persisted the §F1 layout — and the
            // only source text it holds is the credential-free one.
            var runDir = Path.Combine(_root, "Runs", runId!);
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "plan.json")),
                "plan.json is written for the accepted Run");
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "workflow.json")),
                "workflow.json is written for the accepted Run");
            Assert.IsTrue(File.Exists(Path.Combine(runDir, "journal.ndjson")),
                "journal.ndjson is written for the accepted Run");
            var audit = File.ReadAllText(Path.Combine(runDir, "workflow.json"));
            Assert.IsTrue(
                audit.Contains(packageId, StringComparison.Ordinal),
                "the audit copy keeps the credential-free source");
            AssertNoFileContains(CredentialSecrets);
        }
    }
}
