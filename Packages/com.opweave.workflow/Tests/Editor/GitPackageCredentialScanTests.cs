#nullable enable

// T-W4.6-03 [Edit] tests: local:T-W4.6-03:git-package-interruption —
// the SPEC §G8.1 / §F1 認証情報 half on a dedicated surface: every
// credential-bearing Git URL form offered to the real Agent API
// (validate_workflow and execute_workflow on the production
// AgentApiService/UnityAgentWorkflowBackend over the real
// unity.package.add Operation and a real FileRunStore) answers
// OW-OP-006 before a Plan exists, and the recursive scan of the
// whole Run Store tree, the production Run Store root, the response
// envelopes and the Unity Console finds zero credential strings —
// while a credential-free URL on the same source persists its Run
// Store artifacts verbatim (SPEC §G8.1 tests 5-8, §F1).
//
// AgentCredentialUrlTests (T-W4.2-07) proved the rejection and the
// no-echo contract per command on throwaway stores; this suite adds
// the ticket's own persistence evidence: the Run Store is scanned
// RECURSIVELY — every file, not only the named artifacts — after the
// attempts, on a store the attempts share with a real accepted Run,
// and the production Library/OpWeave root is scanned alongside it.
// The credential forms ride the same admissible source the
// interruption suite installs, so the only reason for rejection is
// the embedded credential — never the host grammar or the allowlist.

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
    /// <summary>
    /// <c>local:T-W4.6-03:git-package-interruption</c> credential
    /// non-persistence: the recursive whole-store scan after
    /// credential-URL attempts (SPEC §G8.1, §F1).
    /// </summary>
    internal sealed class GitPackageCredentialScanTests
    {
        private const string WorkflowId = "example.gitpkg.interrupt.credscan";

        // The admissible Git source the interruption suite installs —
        // embedding credentials in it keeps the only failure reason
        // the credential check.
        private const string AdmissibleSource =
            "https://fixture.opweave.invalid/gitresume/repo.git";

        // Every credential-bearing form SPEC §G8.1 names: userinfo
        // password, userinfo token and a credential query parameter.
        private static readonly string[] CredentialIds =
        {
            "https://user:w46-pw@fixture.opweave.invalid/gitresume/repo.git?path=/pkgs/w46pkg#HEAD",
            "https://w46-tok@fixture.opweave.invalid/gitresume/repo.git?path=/pkgs/w46pkg#HEAD",
            "https://fixture.opweave.invalid/gitresume/repo.git?access_token=w46-qsec",
        };

        // Every substring a persisted artifact, log or response must
        // never carry — the credential material plus the query key.
        private static readonly string[] CredentialSecrets =
        {
            "w46-pw", "w46-tok", "w46-qsec",
            "user:w46-pw@fixture.opweave.invalid",
            "w46-tok@fixture.opweave.invalid", "access_token",
        };

        // The credential-free forms SG-19 admits — plain HTTPS and
        // SCP-style SSH without a password.
        private static readonly string[] AcceptedIds =
        {
            "https://example.com/org/repo.git",
            "git@example.com:org/repo.git",
        };

        // The credential-free form of the interruption suite's source —
        // the accepted Run that shares the store with the attempts.
        private const string AcceptedFixtureId =
            "https://fixture.opweave.invalid/gitresume/repo.git" +
            "?path=/pkgs/w46pkg#" +
            "abcdef0123456789abcdef0123456789abcdef01";

        // The six §F1 Run Store artifacts plus the run index — a
        // rejected workflow may create none of them.
        private static readonly string[] RunStoreFileNames =
        {
            "plan.json", "workflow.json", "journal.ndjson",
            "state.json", "changes.json", "index.json",
        };

        private static string ProductionStoreRoot =>
            Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeave");

        private string _root = null!;
        private string _projectRoot = null!;
        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library",
                "OpWeaveGitInterruptCredScan",
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

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

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

        // The SG-19 allowlist admits every source these tests use —
        // the admissible fixture origin and the two credential-free
        // examples — so the credential check is the only reason the
        // credential forms can fail.
        private static void ArmAllowlist(RunnerHostFixture fixture) =>
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"allowedPackageSources\":[" +
                "\"git:" + AdmissibleSource + "\"," +
                "\"git:https://example.com/org/repo.git\"," +
                "\"git:git@example.com:org/repo.git\"]}");

        private static void RegisterOps(RunnerHostFixture fixture)
        {
            fixture.Register(
                new RunnerHostFixture.MarkerOperation(
                    "core.start", fixture.Events),
                'a');
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

        // The wait channel the Operation never reaches: a rejected
        // workflow fails before a Plan, and the accepted forms never
        // run a step — a live Check would be a wiring bug.
        private sealed class NeverPolled : IWaitCondition
        {
            public WaitStatus Check(JsonValue token) =>
                WaitStatus.Pending(token);
        }

        // The producer whose packageId output feeds the ConstantOnly
        // 'id' port on the edge-fed shape.
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
                "\"controlEdges\":[" +
                "{\"from\":\"start\",\"to\":\"add\"}]," +
                "\"inputBindings\":[{" +
                "\"fromInput\":\"pkgSource\",\"to\":{\"node\":\"add\",\"port\":\"id\"}}]}");

        private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));

        // SPEC §G8.1: no Run Store file exists under the root for a
        // rejected workflow — the named artifacts, the run index and
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
                    "a rejected workflow wrote " + artifact +
                    " to the Run Store");
            }
        }

        // SPEC §G8.1 "出力しない": neither the response envelope nor
        // the Unity Console may carry the credential text.
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

        // The whole-store scan: EVERY file under a root, recursive —
        // plan/workflow/journal/state/changes, the run index, lock and
        // hand-off files — must carry zero credential strings.
        private static void AssertNoCredentialsInTree(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (var file in Directory.GetFiles(
                root, "*", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                foreach (var secret in CredentialSecrets)
                {
                    Assert.IsFalse(
                        content.Contains(secret, StringComparison.Ordinal),
                        Path.GetFileName(file) + " leaked '" + secret + "'");
                }
            }
        }

        private void AssertStoreScansClean()
        {
            AssertNoCredentialsInTree(_root);
            AssertNoCredentialsInTree(ProductionStoreRoot);
        }

        // ── every credential form: OW-OP-006 on both commands, no Plan,
        //    no Run Store file, no echo, zero secrets under either root ──

        [TestCaseSource(nameof(CredentialIds))]
        public void ConstantCredentialId_Rejected_NoStoreEchoOrSecrets(
            string packageId)
        {
            var fixture = NewFixture();
            ArmAllowlist(fixture);
            RegisterOps(fixture);
            var api = Api(fixture);
            var tap = new ConsoleTap();
            AgentApiResponse validated;
            AgentApiResponse executed;
            using (tap)
            {
                validated = api.ValidateWorkflow(
                    new ValidateWorkflowRequest(ConstantIdBody(packageId), null));
                executed = api.ExecuteWorkflow(
                    new ExecuteWorkflowRequest(
                        ConstantIdBody(packageId), null, null));
            }

            Assert.IsFalse(validated.Ok,
                "a credential-bearing id must fail validate_workflow");
            Assert.IsNull(validated.Data, "a rejected validate carries no summary");
            Assert.IsTrue(
                validated.Errors.Any(d => d.Code == ErrorCodes.OW_OP_006),
                "expected OW-OP-006 on validate; got: " +
                Describe(validated.Errors));

            Assert.IsFalse(executed.Ok,
                "a credential-bearing id must fail execute_workflow");
            Assert.IsNull(executed.Data, "a rejected execute carries no runId");
            Assert.IsTrue(
                executed.Errors.Any(d => d.Code == ErrorCodes.OW_OP_006),
                "expected OW-OP-006 on execute; got: " +
                Describe(executed.Errors));

            AssertRunStoreEmpty();
            AssertNoEcho(validated, tap.Joined(), packageId);
            AssertNoEcho(executed, tap.Joined(), packageId);
            AssertNoEcho(executed, tap.Joined(), CredentialSecrets);
            AssertStoreScansClean();
        }

        // ── fed 'id': OW-OP-006 ConstantOnly regardless of the value,
        //    no echo, no Run Store write, zero secrets ──

        [Test]
        public void EdgeFedId_Rejected_NoStoreEchoOrSecrets()
        {
            var fixture = NewFixture();
            ArmAllowlist(fixture);
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
            Assert.IsTrue(
                response.Errors.Any(d => d.Code == ErrorCodes.OW_OP_006),
                "expected OW-OP-006; got: " + Describe(response.Errors));

            AssertRunStoreEmpty();
            AssertNoEcho(response, tap.Joined(), "packageId");
            AssertStoreScansClean();
        }

        [Test]
        public void BindingFedCredentialId_Rejected_NoStoreEchoOrSecrets()
        {
            var fixture = NewFixture();
            ArmAllowlist(fixture);
            RegisterOps(fixture);
            var api = Api(fixture);
            var inputs = JObject.Parse(
                "{\"pkgSource\":\"https://user:w46-pw@fixture.opweave.invalid" +
                "/gitresume/repo.git\"}");
            var tap = new ConsoleTap();
            AgentApiResponse response;
            using (tap)
            {
                response = api.ExecuteWorkflow(new ExecuteWorkflowRequest(
                    BindingFedBody(), null, inputs));
            }

            Assert.IsFalse(response.Ok);
            Assert.IsNull(response.Data);
            Assert.IsTrue(
                response.Errors.Any(d => d.Code == ErrorCodes.OW_OP_006),
                "expected OW-OP-006; got: " + Describe(response.Errors));

            AssertRunStoreEmpty();
            AssertNoEcho(response, tap.Joined(),
                "w46-pw", "user:w46-pw@fixture.opweave.invalid");
            AssertStoreScansClean();
        }

        // ── the ticket's whole-store evidence: credential attempts and
        //    an accepted Run share one store — the recursive scan finds
        //    zero credential strings while the admissible source is
        //    persisted verbatim ──

        [Test]
        public void AttemptsThenAcceptedRun_SharedStore_NoSecretsPersisted()
        {
            var fixture = NewFixture();
            ArmAllowlist(fixture);
            RegisterOps(fixture);
            var api = Api(fixture);
            var tap = new ConsoleTap();
            AgentApiResponse accepted;
            using (tap)
            {
                foreach (var credId in CredentialIds)
                {
                    var rejected = api.ExecuteWorkflow(
                        new ExecuteWorkflowRequest(
                            ConstantIdBody(credId), null, null));
                    Assert.IsFalse(
                        rejected.Ok, credId + " must fail execute_workflow");
                    Assert.IsNull(rejected.Data);
                    Assert.IsTrue(
                        rejected.Errors.Any(
                            d => d.Code == ErrorCodes.OW_OP_006),
                        "expected OW-OP-006; got: " +
                        Describe(rejected.Errors));
                    AssertNoEcho(rejected, tap.Joined(), credId);
                }

                var validated = api.ValidateWorkflow(
                    new ValidateWorkflowRequest(
                        ConstantIdBody(AcceptedFixtureId), null));
                Assert.IsTrue(
                    validated.Ok,
                    "the credential-free form must validate: " +
                    Describe(validated.Errors));
                var planHash = (string?)((JObject)validated.Data!)["planHash"];
                Assert.IsNotNull(planHash, "the Plan must carry a planHash");

                accepted = api.ExecuteWorkflow(
                    new ExecuteWorkflowRequest(
                        ConstantIdBody(AcceptedFixtureId), null, null,
                        ExpectedPlanHash: planHash));
            }

            Assert.IsTrue(
                accepted.Ok,
                "the credential-free form must execute: " +
                Describe(accepted.Errors));
            var runId = (string?)((JObject)accepted.Data!)["runId"];
            Assert.IsNotNull(runId, "the accepted Run carries a runId");

            var runDir = Path.Combine(_root, "Runs", runId!);
            Assert.IsTrue(
                File.Exists(Path.Combine(runDir, "plan.json")),
                "plan.json is written for the accepted Run");
            Assert.IsTrue(
                File.Exists(Path.Combine(runDir, "workflow.json")),
                "workflow.json is written for the accepted Run");
            Assert.IsTrue(
                File.Exists(Path.Combine(runDir, "journal.ndjson")),
                "journal.ndjson is written for the accepted Run");
            Assert.IsTrue(
                File.ReadAllText(Path.Combine(runDir, "workflow.json"))
                    .Contains(AcceptedFixtureId, StringComparison.Ordinal),
                "the audit copy keeps the credential-free source — " +
                "the scan is not vacuous");

            AssertNoEcho(accepted, tap.Joined(), CredentialSecrets);
            AssertStoreScansClean();
        }

        // ── credential-free Git URLs pass both commands end to end and
        //    leave a clean store ──

        [TestCaseSource(nameof(AcceptedIds))]
        public void CredentialFreeId_Accepted_StoreScanClean(string packageId)
        {
            var fixture = NewFixture();
            ArmAllowlist(fixture);
            RegisterOps(fixture);
            var api = Api(fixture);
            var tap = new ConsoleTap();
            AgentApiResponse validated;
            AgentApiResponse executed;
            using (tap)
            {
                validated = api.ValidateWorkflow(
                    new ValidateWorkflowRequest(ConstantIdBody(packageId), null));
                executed = api.ExecuteWorkflow(
                    new ExecuteWorkflowRequest(
                        ConstantIdBody(packageId), null, null,
                        ExpectedPlanHash:
                            validated.Ok && validated.Data is JObject data
                                ? (string?)data["planHash"]
                                : null));
            }

            Assert.IsTrue(
                validated.Ok,
                packageId + " should validate: " + Describe(validated.Errors));
            Assert.IsTrue(
                executed.Ok,
                packageId + " should execute: " + Describe(executed.Errors));
            var runId = (string?)((JObject)executed.Data!)["runId"];
            Assert.IsNotNull(runId, "the accepted Run carries a runId");

            var runDir = Path.Combine(_root, "Runs", runId!);
            Assert.IsTrue(
                File.Exists(Path.Combine(runDir, "plan.json")),
                "plan.json is written for the accepted Run");
            Assert.IsTrue(
                File.Exists(Path.Combine(runDir, "workflow.json")),
                "workflow.json is written for the accepted Run");
            Assert.IsTrue(
                File.ReadAllText(Path.Combine(runDir, "workflow.json"))
                    .Contains(packageId, StringComparison.Ordinal),
                "the audit copy keeps the credential-free source");

            AssertNoEcho(executed, tap.Joined(), CredentialSecrets);
            AssertStoreScansClean();
        }
    }
}
