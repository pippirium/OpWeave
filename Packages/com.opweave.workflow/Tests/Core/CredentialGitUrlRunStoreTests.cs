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
    /// [Core] tests for SPEC §G8.1 / §D6 item 3 / §F1 — the end-to-end
    /// credential rejection of local:T-W4.2-07:execute-run-store: the real
    /// snapshot-backed <see cref="WorkflowCompiler"/> feeds the real
    /// <see cref="RunCoordinator"/> acceptance and start gates over a real
    /// <see cref="FileRunStore"/> in a temporary root. Each SPEC §G8.1
    /// credential form, plus edge- and binding-fed <c>id</c> suppliers, must
    /// fail with OW-OP-006 before a Plan exists and before any Run Store
    /// file (plan.json, workflow.json, journal.ndjson, state.json,
    /// changes.json) is written, and no diagnostic, captured §F6 log
    /// entry or persisted byte may carry the credential text; the
    /// credential-free HTTPS and SCP forms pass the same path end to end.
    /// The same sources run under dotnet and Unity Mono.
    /// </summary>
    /// <remarks>
    /// The compile-time arm of the check is T-W4.2-03's
    /// <c>CredentialGitUrlTests</c>; this fixture owns the execute side —
    /// acceptance, start and the Run Store tree — so every fixture op keeps
    /// a Descriptor with the SPEC §I.4 <c>unity.package.add</c> shape (a
    /// required ConstantOnly <c>core.String</c> <c>id</c>).
    /// </remarks>
    internal sealed class CredentialGitUrlRunStoreTests
    {
        private const string Session = "session-credential-run-store";
        private const string WorkflowId = "example.credential-run-store";

        // The SPEC-pinned fixed English message (ticket T-W4.2-03).
        private const string CredentialMessage = "Git URL contains credentials";

        private string _root = null!;
        private FileRunStore _store = null!;
        private RunCoordinator _coordinator = null!;
        private RegistrySnapshot _snapshot = null!;

        // Every RunLogEntry the coordinator published over the §E8
        // LogAppended channel — the Core's only log emission.
        private readonly List<RunLogEntry> _logEntries = new List<RunLogEntry>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "OpWeave.CredentialRunStore." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _store = new FileRunStore(_root, Session);
            _snapshot = Snapshot(StartDescriptor(), PackageAddDescriptor());
            _coordinator = NewCoordinator();
        }

        [TearDown]
        public void TearDown()
        {
            // Drop the OS exclusion a started Run holds before the root is
            // removed (SPEC §E2: Dispose leaves only the lock.json residue).
            _coordinator.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        // ── Fixtures ────────────────────────────────────────────────────────

        private sealed class FakeEditorState : IEditorState
        {
            public bool IsEditMode { get; set; } = true;
            public bool IsCompiling { get; set; }
            public bool IsUpdating { get; set; }
        }

        private sealed class FakeClock : IClock
        {
            public DateTimeOffset UtcNow { get; set; } =
                new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
        }

        private sealed class UnityShapesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => UnityShapeTypes.All;
        }

        private static readonly ITypeRegistry s_types = BuildTypes();

        private static ITypeRegistry BuildTypes()
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
            });
            Assert.AreEqual(0, build.Failures.Count,
                "registry build failed: " + (build.Failures.Count > 0 ? build.Failures[0].Detail : string.Empty));
            return build.Registry;
        }

        private sealed class NoTypeProbe : IStaticTypeProbe
        {
            public bool TypeExists(string typeName) => false;
        }

        private sealed class StubOperation : IOperation
        {
            internal StubOperation(OperationDescriptor descriptor) => Descriptor = descriptor;

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) => throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) => throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) => throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        private RunCoordinator NewCoordinator()
        {
            var store = _store;
            var coordinator = new RunCoordinator(
                store,
                (runId, plan, document, actor) =>
                    store.CreateRun(runId, plan, document, actor),
                new FakeEditorState(),
                new FakeClock(),
                () => _snapshot,
                () => CoordinatorRunLock.TryAcquire(_root),
                Session);
            // SPEC §F6/§E8: the Core's only logging channel — every
            // journaled log record surfaces through LogAppended as one
            // RunLogEntry, captured here so the no-echo assertions can
            // inspect every emitted log string.
            coordinator.Events.LogAppended += entry => _logEntries.Add(entry);
            return coordinator;
        }

        // ── Descriptors ─────────────────────────────────────────────────────

        private static OperationDescriptor StartDescriptor() =>
            Op.Define("core.start", 1).Summary("Workflow entry point.").Build();

        // The SPEC §I.4 unity.package.add row: a required ConstantOnly string
        // id (name@version or Git URL) with package.modify + compile effects.
        private static OperationDescriptor PackageAddDescriptor() =>
            Op.Define("unity.package.add", 1)
                .Summary("Add a package.")
                .Input("id", "core.String", required: true, c => c.ConstantOnly())
                .Output("package", "core.Json")
                .Effects(EffectNames.PackageModify, EffectNames.Compile)
                .Build();

        private static OperationDescriptor IdSourceDescriptor() =>
            Op.Define("example.id_source", 1)
                .Summary("Produce a package identifier.")
                .Output("packageId", "core.String")
                .Build();

        // ── Snapshot / document helpers ─────────────────────────────────────

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        private static OperationCandidate Candidate(IOperation operation) =>
            new OperationCandidate(
                operation.Descriptor,
                OperationOrigin.Standard,
                "Acme.Editor",
                "Assets/Acme/Operation.cs",
                new string('a', 64),
                HasPollImplementation: true,
                Implementation: operation);

        private static RegistrySnapshot Snapshot(params OperationDescriptor[] descriptors) =>
            RegistrySnapshotBuilder.Build(
                descriptors.Select(d => Candidate(new StubOperation(d))).ToArray(),
                s_types);

        private static NodeData Node(
            string id, string operation, int version = 1,
            IDictionary<string, JToken>? parameters = null) =>
            new NodeData { Id = id, Operation = operation, Version = version, Parameters = parameters };

        private static WorkflowDocumentData LinearData(params NodeData[] nodes)
        {
            var edges = new List<ControlEdge>();
            for (var i = 0; i + 1 < nodes.Length; i++)
            {
                edges.Add(new ControlEdge(nodes[i].Id!, nodes[i + 1].Id!));
            }
            return new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = WorkflowId,
                Nodes = new List<NodeData>(nodes),
                ControlEdges = edges,
            };
        }

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        private static StaticContext StaticFactory(
            string nodeId, IReadOnlyDictionary<string, InputValue> parameters) =>
            new OperationContractFixtures.Static(nodeId, parameters);

        // The production snapshot-backed pipeline — real Resolve, D2–D6 and
        // the real Build Plan stage fed by the stub SG-11 provenance.
        private ValidationReport Compile(
            WorkflowDocumentData data, JsonValue? inputs = null) =>
            new WorkflowCompiler(
                    _snapshot,
                    StaticFactory,
                    new NoTypeProbe(),
                    new PlanProvenanceFixtures.Stub())
                .Validate(new ValidationRequest(
                    MustCreate(data), inputs, ValidationMode.Static, ActorKind.Agent));

        private static WorkflowDocumentData ConstantIdData(string packageId) =>
            LinearData(
                Node("start", "core.start"),
                Node("add", "unity.package.add", parameters: new Dictionary<string, JToken>
                {
                    ["id"] = JValue.CreateString(packageId),
                }));

        private static Diagnostic[] Errors(ValidationReport report, string code) =>
            report.Errors.Where(d => d.Code == code).ToArray();

        // The execute path the verification names: compile, then the §E2
        // acceptance of the produced Plan — a rejected compile never reaches
        // it, exactly like the production execute workflow wiring.
        private Result<RunHandle> Execute(
            WorkflowDocument document, ValidationReport report)
        {
            if (!report.Ok || report.Plan is null)
            {
                return Result<RunHandle>.Failure(report.Errors);
            }
            return _coordinator.Accept(
                report.Plan, document, ActorKind.Agent, new AcceptOptions(null, false));
        }

        // ── Run Store / emission assertions ─────────────────────────────────

        // SPEC §G8.1: nothing persists for a rejected workflow — no Runs/
        // directory, none of the named Run Store artifacts, and in fact no
        // file at all under the store root: the temporary root is exclusive
        // to this fixture, so any byte a rejected workflow left behind is a
        // failure.
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
                .Select(path => Path.GetRelativePath(_root, path))
                .ToList();
            Assert.IsEmpty(persisted,
                "a rejected workflow left files in the Run Store: " +
                string.Join(", ", persisted));
        }

        // SPEC §G8.1 "出力しない": no diagnostic field may carry the
        // rejected text.
        private static void AssertNoDiagnosticContains(
            ValidationReport report, params string[] secrets)
        {
            foreach (var diagnostic in report.Errors.Concat(report.Warnings))
            {
                var emitted = string.Join("\n",
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.NodeId ?? string.Empty,
                    diagnostic.Port ?? string.Empty,
                    diagnostic.JsonPath ?? string.Empty,
                    diagnostic.Hint ?? string.Empty);
                foreach (var secret in secrets)
                {
                    Assert.IsFalse(
                        emitted.Contains(secret, StringComparison.Ordinal),
                        $"diagnostic {diagnostic.Code} leaked '{secret}': {emitted}");
                }
            }
        }

        // SPEC §G8.1 "出力しない": no field of any captured §F6 RunLogEntry
        // — message, code or data — may carry the rejected text. When the
        // rejection path emits nothing the caller asserts that emptiness
        // explicitly; this scan is what fails should a credential ever be
        // logged.
        private void AssertNoLogContains(params string[] secrets)
        {
            foreach (var entry in _logEntries)
            {
                var emitted = string.Join("\n",
                    entry.Level.ToString(),
                    entry.Step?.ToString() ?? string.Empty,
                    entry.Code ?? string.Empty,
                    entry.Message,
                    entry.Data?.ToToken().ToString(
                        Newtonsoft.Json.Formatting.None) ?? string.Empty);
                foreach (var secret in secrets)
                {
                    Assert.IsFalse(
                        emitted.Contains(secret, StringComparison.Ordinal),
                        $"a log entry leaked '{secret}': {emitted}");
                }
            }
        }

        // The full supplied URL plus every credential component the SPEC
        // §G8.1 forms carry: the userinfo text, its user and password/token
        // halves and the query key and token — the set asserted on every
        // emitted diagnostic, log and persisted byte.
        private static string[] SuppliedCredentialParts(string packageId)
        {
            var parts = new List<string> { packageId };
            parts.AddRange(CredentialSecrets);
            if (Uri.TryCreate(packageId, UriKind.Absolute, out var uri) &&
                uri.UserInfo.Length > 0)
            {
                parts.Add(uri.UserInfo);
                parts.AddRange(uri.UserInfo.Split(':'));
            }
            const string marker = "access_token=";
            var at = packageId.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                parts.Add("access_token");
                parts.Add(packageId.Substring(at + marker.Length));
                parts.Add(packageId.Substring(at));
            }
            return parts.ToArray();
        }

        // Every persisted byte under the Run Store root — journals and the
        // §E2 lock.json owner record included — stays free of the credential
        // text (SPEC §G8.1 "保存しない"). coordinator.lock alone may go
        // unread, and only while a live Run proves the skip by holding it
        // open exclusively; its content is asserted once the Run ends.
        private void AssertNoFileContains(params string[] secrets)
        {
            if (!Directory.Exists(_root))
            {
                return;
            }
            var skipped = 0;
            foreach (var file in Directory.GetFiles(
                _root, "*", SearchOption.AllDirectories))
            {
                string content;
                try
                {
                    content = File.ReadAllText(file);
                }
                catch (IOException e) when (IsSharingViolation(e))
                {
                    skipped++;
                    Assert.AreEqual(
                        CoordinatorRunLock.LockFileName, Path.GetFileName(file),
                        "only a live Run's coordinator.lock may deny the read");
                    continue;
                }
                foreach (var secret in secrets)
                {
                    Assert.IsFalse(
                        content.Contains(secret, StringComparison.Ordinal),
                        $"{Path.GetFileName(file)} leaked '{secret}'");
                }
            }
            Assert.LessOrEqual(skipped, 1,
                "a sharing violation is a skip proof only for coordinator.lock");
        }

        // A proven sharing violation in the HResult's low word (the
        // FileRunStore convention): Win32 ERROR_SHARING_VIOLATION (32) or
        // ERROR_LOCK_VIOLATION (33) from the share-mode-0 CreateFileW
        // handle, or the .NET Unix non-blocking flock denial surfaced as
        // EAGAIN/EWOULDBLOCK (11 on Linux, 35 on macOS). Any other open
        // failure fails the test outright.
        private static bool IsSharingViolation(IOException e)
        {
            var code = e.HResult & 0xFFFF;
            return code == 32 || code == 33 || code == 11 || code == 35;
        }

        // ── SPEC §G8.1 test 5 — credential forms: OW-OP-006, no Plan,
        // no Run Store write, no echo ───────────────────────────────────────

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
        public void CredentialConstantIdIsRejectedWithNoPlanAndNoRunStoreWrite(
            string packageId)
        {
            var data = ConstantIdData(packageId);
            var report = Compile(data);

            Assert.IsFalse(report.Ok, "a credential-bearing id must fail validation");
            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual(CredentialMessage, error.Message);
            Assert.AreEqual("add", error.NodeId);
            Assert.AreEqual("id", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.id", error.JsonPath);

            // The execute path stops at the failed compile: acceptance is
            // never reached, the Run Store root stays untouched and the
            // coordinator — never invoked — emits no log entry at all.
            var executed = Execute(MustCreate(data), report);
            Assert.IsFalse(executed.Ok);
            AssertRunStoreEmpty();
            Assert.IsEmpty(_logEntries,
                "a rejected workflow emitted a Core log record before any Run existed");
            AssertNoDiagnosticContains(report, SuppliedCredentialParts(packageId));
            AssertNoLogContains(SuppliedCredentialParts(packageId));
            AssertNoFileContains(SuppliedCredentialParts(packageId));
        }

        // ── SPEC §G8.1 test 7 — edge- or binding-fed id: OW-OP-006
        // regardless of value, no echo, no Run Store write ──────────────────

        [Test]
        public void EdgeFedIdIsRejectedWithNoPlanAndNoRunStoreWrite()
        {
            // The coordinator's start-gate registry seam reads the field —
            // no rebuild is needed when the compile snapshot changes.
            _snapshot = Snapshot(
                StartDescriptor(), PackageAddDescriptor(), IdSourceDescriptor());
            var data = LinearData(
                Node("start", "core.start"),
                Node("source", "example.id_source"),
                Node("add", "unity.package.add"));
            data.DataEdges = new List<DataEdge>
            {
                new DataEdge(
                    new PortRef("source", "packageId"), new PortRef("add", "id")),
            };

            var report = Compile(data);

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("add", error.NodeId);
            Assert.AreEqual("id", error.Port);
            Assert.AreEqual("$.dataEdges[0]", error.JsonPath);
            AssertRunStoreEmpty();
            Assert.IsEmpty(_logEntries,
                "a rejected workflow emitted a Core log record before any Run existed");
            AssertNoDiagnosticContains(report, "packageId");
            AssertNoLogContains("packageId");
        }

        [Test]
        public void BindingFedCredentialIdIsRejectedWithNoPlanAndNoRunStoreWrite()
        {
            var packageId = "https://user:s3cr3t-pw@example.com/org/repo.git";
            var data = LinearData(
                Node("start", "core.start"),
                Node("add", "unity.package.add"));
            data.Inputs = new List<WorkflowInput>
            {
                new WorkflowInput("pkgSource", "core.String", true),
            };
            data.InputBindings = new List<InputBinding>
            {
                new InputBinding("pkgSource", new PortRef("add", "id")),
            };
            // The ConstantOnly rejection holds regardless of the runtime
            // value — a credential-bearing WF input is still rejected at D4
            // and never echoed (SPEC §G8.1 test 7).
            var inputs = Json(
                "{\"pkgSource\":" +
                JToken.FromObject(packageId).ToString(
                    Newtonsoft.Json.Formatting.None) +
                "}");

            var report = Compile(data, inputs);

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("add", error.NodeId);
            Assert.AreEqual("$.inputBindings[0]", error.JsonPath);
            AssertRunStoreEmpty();
            Assert.IsEmpty(_logEntries,
                "a rejected workflow emitted a Core log record before any Run existed");
            AssertNoDiagnosticContains(report, SuppliedCredentialParts(packageId));
            AssertNoLogContains(SuppliedCredentialParts(packageId));
            AssertNoFileContains(SuppliedCredentialParts(packageId));
        }

        // ── SPEC §G8.1 test 8 — credential-free Git URLs pass acceptance
        // and start end to end ─────────────────────────────────────────────

        private static readonly string[] AcceptedIds =
        {
            "https://example.com/org/repo.git",
            "git@example.com:org/repo.git",
        };

        [TestCaseSource(nameof(AcceptedIds))]
        public void CredentialFreeIdIsAcceptedThroughExecute(string packageId)
        {
            var data = ConstantIdData(packageId);
            var document = MustCreate(data);
            var report = Compile(data);

            Assert.IsTrue(report.Ok,
                packageId + " should validate: " + string.Join(
                    "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.IsNotNull(report.Plan,
                "the real Build Plan stage must produce a CompiledPlan for " + packageId);

            var accepted = Execute(document, report);
            Assert.IsTrue(accepted.Ok,
                packageId + " should be accepted: " + string.Join(
                    "; ", accepted.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var runId = accepted.Value!.RunId;
            Assert.IsTrue(
                runId.StartsWith("run_", StringComparison.Ordinal),
                "runId carries the §F1 prefix");

            // package.modify is ask under the Agent default policy, so the
            // Run waits for the human decision; granting it queues the Run
            // and the start gate then starts it — the real §E2 path.
            if (accepted.Value.State == RunState.AwaitingApproval)
            {
                var approved = _coordinator.Approve(runId, "test.approver");
                Assert.IsTrue(approved.Ok, "Approve failed");
                Assert.AreEqual(RunState.Queued, approved.Value!.State);
            }
            var started = _coordinator.TryStartNext();
            Assert.IsTrue(started.Ok && started.Value is not null,
                "the accepted Run must pass the §E2 start gate");
            Assert.AreEqual(RunState.Running, started.Value!.State);

            // The accepted Run really persisted: the §F1 layout exists and
            // no credential text reached it (the clean source is the only
            // text the store may hold).
            var runDir = Path.Combine(_root, "Runs", runId);
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
            AssertNoLogContains(CredentialSecrets);
            AssertNoFileContains(CredentialSecrets);
        }
    }
}
