#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for SPEC §D6 item 3 — the §G8.1 credential-bearing Git URL
    /// rejection on the constant <c>id</c> of <c>unity.package.add</c>
    /// (local:T-W4.2-03:credential-url-rejection): each credential form is
    /// rejected with OW-OP-006 before a Plan exists (SPEC §G8.1 tests 5-8,
    /// §F1), the diagnostic carries the JsonPath and the fixed English message
    /// but never the value, edge- or binding-fed <c>id</c> is OW-OP-006
    /// regardless of value (SPEC §D4 item 4), and credential-free HTTPS/SCP/
    /// ssh:// and registry ids pass. The same sources run under dotnet and
    /// Unity Mono.
    /// </summary>
    /// <remarks>
    /// The Run Store and <c>execute_workflow</c> no-write evidence is
    /// T-W4.2-07's; the check sits inside the shared
    /// <see cref="IWorkflowCompiler.Validate"/> pipeline's Validate stage,
    /// so a credential rejection stops the pipeline before the real Build
    /// Plan stage — <see cref="ValidationReport.Plan"/> stays null and no
    /// Run Store writes can follow.
    /// </remarks>
    internal sealed class CredentialGitUrlTests
    {
        // The SPEC-pinned fixed English message (ticket T-W4.2-03).
        private const string CredentialMessage = "Git URL contains credentials";

        // ── Fixtures ────────────────────────────────────────────────────────────

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

        // ── Descriptors ─────────────────────────────────────────────────────────

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

        // A variant whose id default holds the given value, so the Descriptor
        // default arm of the constant-value rule is exercised.
        private static OperationDescriptor PackageAddDefaultedDescriptor(string idDefault) =>
            Op.Define("unity.package.add", 1)
                .Summary("Add a package.")
                .Input("id", "core.String", required: false, c => c.ConstantOnly(),
                    @default: Json("\"" + idDefault + "\""))
                .Output("package", "core.Json")
                .Effects(EffectNames.PackageModify, EffectNames.Compile)
                .Build();

        private static OperationDescriptor IdSourceDescriptor() =>
            Op.Define("example.id_source", 1)
                .Summary("Produce a package identifier.")
                .Output("packageId", "core.String")
                .Build();

        // ── Snapshot / document helpers ─────────────────────────────────────────

        private static JsonValue Json(string text) => JsonValue.Parse(text).Value!;

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
                Id = "example.credential-url",
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
        private static ValidationReport Compile(
            WorkflowDocumentData data,
            RegistrySnapshot? snapshot = null,
            JsonValue? inputs = null,
            ActorKind actor = ActorKind.Agent) =>
            new WorkflowCompiler(
                    snapshot ?? Snapshot(StartDescriptor(), PackageAddDescriptor()),
                    StaticFactory,
                    new NoTypeProbe(),
                    new PlanProvenanceFixtures.Stub())
                .Validate(new ValidationRequest(
                    MustCreate(data), inputs, ValidationMode.Static, actor));

        private static ValidationReport CompileWithAddId(
            string packageId,
            RegistrySnapshot? snapshot = null,
            JsonValue? inputs = null) =>
            Compile(
                LinearData(
                    Node("start", "core.start"),
                    Node("add", "unity.package.add", parameters: new Dictionary<string, JToken>
                    {
                        ["id"] = JToken.Parse("\"" + packageId + "\""),
                    })),
                snapshot,
                inputs);

        private static Diagnostic[] Errors(ValidationReport report, string code) =>
            report.Errors.Where(d => d.Code == code).ToArray();

        private static Diagnostic[] All(ValidationReport report) =>
            report.Errors.Concat(report.Warnings).ToArray();

        // Every text field of every emitted diagnostic must stay free of the
        // rejected value (SPEC §G8.1 "出力しない" — message, hint, logs,
        // Console and responses never carry it).
        private static void AssertNoDiagnosticContains(ValidationReport report, params string[] secrets)
        {
            foreach (var diagnostic in All(report))
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

        // ── SPEC §G8.1 test 5 — credential forms rejected before the Plan ───────

        private static readonly string[] CredentialIds =
        {
            "https://user:s3cr3t-pw@example.com/org/repo.git",
            "https://user@example.com/org/repo.git",
            "https://tok-9f8e7d@example.com/org/repo.git",
            "http://user:s3cr3t-pw@example.com/org/repo.git",
            "https://user:s3cr3t-pw@example.com/org/repo.git?path=/sub",
            "https://example.com/org/repo.git?access_token=deadbeef42",
            "https://example.com/org/repo.git?token=deadbeef42",
            "https://example.com/org/repo.git?path=/sub&access_token=deadbeef42",
            "ssh://user:s3cr3t-pw@example.com/org/repo.git",
            "ssh://user@example.com@evil.example/org/repo.git",
            "user:s3cr3t-pw@example.com:org/repo.git",
            "git:s3cr3t-pw@example.com:org/repo.git",
        };

        [TestCaseSource(nameof(CredentialIds))]
        public void CredentialBearingIdIsOwOp006BeforePlan(string packageId)
        {
            var report = CompileWithAddId(packageId);

            Assert.IsFalse(report.Ok, "a credential-bearing id must fail validation");
            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual(CredentialMessage, error.Message);
            Assert.AreEqual("add", error.NodeId);
            Assert.AreEqual("id", error.Port);
            Assert.AreEqual("$.nodes[1].parameters.id", error.JsonPath);
            AssertNoDiagnosticContains(report, packageId, "s3cr3t-pw", "tok-9f8e7d", "deadbeef42");
        }

        // SPEC §G8.1 test 6: the rejection diagnostic carries only the JsonPath
        // and the fixed English message — the credential value never appears.
        [Test]
        public void RejectionEmitsOnlyJsonPathAndFixedMessage()
        {
            var report = CompileWithAddId("https://user:s3cr3t-pw@example.com/org/repo.git");

            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual(CredentialMessage, error.Message);
            Assert.AreEqual("$.nodes[1].parameters.id", error.JsonPath);
            Assert.IsNotNull(error.Hint, "the hint stays fixed English guidance");
            AssertNoDiagnosticContains(
                report,
                "https://user:s3cr3t-pw@example.com/org/repo.git",
                "s3cr3t-pw",
                "user:s3cr3t-pw@example.com");
        }

        // SPEC §G8.1 test 8: credential-free Git URLs and registry ids pass the
        // D6 item-3 check exactly as before.
        private static readonly string[] AcceptedIds =
        {
            "https://example.com/org/repo.git",
            "https://example.com/org/repo.git#v1.2.3",
            "https://example.com/org/repo.git?path=/sub",
            "https://example.com/org/repo.git?path=/sub#v1.2.3",
            "https://EXAMPLE.com/org/repo.git",
            "git@example.com:org/repo.git",
            "git@example.com:org/repo.git#main",
            "git@example.com:org/repo.git?path=/sub",
            "ssh://git@example.com/org/repo.git",
            "ssh://git@EXAMPLE.com/org/repo.git",
            "com.unity.textmeshpro@3.0.6",
            "com.unity.xr.management",
        };

        [TestCaseSource(nameof(AcceptedIds))]
        public void CredentialFreeIdIsAccepted(string packageId)
        {
            var report = CompileWithAddId(packageId);

            Assert.IsEmpty(Errors(report, ErrorCodes.OW_OP_006), packageId);
            Assert.IsTrue(report.Ok,
                packageId + " should validate: " + string.Join(
                    "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.IsNotNull(report.Plan,
                "the real Build Plan stage must produce a CompiledPlan for " + packageId);
        }

        // An unresolvable or unsafe-shaped id is not a credential: the
        // allowlist (OW-PERM-001) and codec checks own those verdicts, not
        // D6 item 3.
        private static readonly string[] NonCredentialIds =
        {
            "http://example.com/org/repo.git",
            "file:///opt/repo.git",
            "user@example.com:org/repo.git",
            "ftp://example.com/org/repo.git",
        };

        [TestCaseSource(nameof(NonCredentialIds))]
        public void NonCredentialIdIsNotReportedByItem3(string packageId)
        {
            var report = CompileWithAddId(packageId);

            Assert.IsEmpty(Errors(report, ErrorCodes.OW_OP_006).Where(
                d => d.Message == CredentialMessage), packageId);
        }

        // The Descriptor-default arm: a credential embedded in the port
        // default is still a constant id and rejects at the node JsonPath.
        [Test]
        public void DefaultedCredentialIdIsRejectedAtTheNode()
        {
            var snapshot = Snapshot(
                StartDescriptor(),
                PackageAddDefaultedDescriptor("https://user:s3cr3t-pw@example.com/org/repo.git"));
            var document = LinearData(
                Node("start", "core.start"),
                Node("add", "unity.package.add"));

            var report = Compile(document, snapshot);

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual(CredentialMessage, error.Message);
            Assert.AreEqual("$.nodes[1]", error.JsonPath);
            AssertNoDiagnosticContains(report, "s3cr3t-pw");
        }

        // A missing required id is OW-VAL-005 — never a credential finding.
        [Test]
        public void MissingIdIsOwVal005NotOwOp006()
        {
            var document = LinearData(
                Node("start", "core.start"),
                Node("add", "unity.package.add"));

            var report = Compile(document);

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            Assert.IsEmpty(Errors(report, ErrorCodes.OW_OP_006));
            Errors(report, ErrorCodes.OW_VAL_005).Single();
        }

        // ── SPEC §G8.1 test 7 / §D4 item 4 — edge- or binding-fed id ───────────

        [Test]
        public void EdgeFedIdIsOwOp006WithoutEcho()
        {
            var snapshot = Snapshot(
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

            var report = Compile(data, snapshot);

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("add", error.NodeId);
            Assert.AreEqual("id", error.Port);
            Assert.AreEqual("$.dataEdges[0]", error.JsonPath);
            AssertNoDiagnosticContains(report, "packageId");
        }

        [Test]
        public void BindingFedIdIsOwOp006WithoutEcho()
        {
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

            var report = Compile(data);

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            var error = Errors(report, ErrorCodes.OW_OP_006).Single();
            Assert.AreEqual("add", error.NodeId);
            Assert.AreEqual("$.inputBindings[0]", error.JsonPath);
            AssertNoDiagnosticContains(report, "pkgSource");
        }

        // The binding rejection holds regardless of the runtime value fed to
        // the WF input — a credential value supplied at execution input is
        // still rejected at D4 and never echoed (SPEC §G8.1 test 7).
        [Test]
        public void BindingFedCredentialValueIsOwOp006WithoutEcho()
        {
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
            var inputs = Json("{\"pkgSource\":\"https://user:s3cr3t-pw@example.com/org/repo.git\"}");

            var report = Compile(data, inputs: inputs);

            Assert.IsFalse(report.Ok);
            Assert.IsNull(report.Plan, "no Plan may be built for a rejected workflow");
            Assert.IsNotEmpty(Errors(report, ErrorCodes.OW_OP_006));
            AssertNoDiagnosticContains(report, "s3cr3t-pw", "user:s3cr3t-pw@example.com");
        }

        // ── Detection unit coverage ─────────────────────────────────────────────
        // Direct form coverage of the internal detector (the D6 phase's rule):
        // undeterminable shapes that may carry a password stay fail-closed.
        private static readonly string[] CredentialForms =
        {
            "https://user@host/x", "https://user:pw@host/x", "https://token@host/x",
            "http://user:pw@host/x", "ftp://user:pw@host/x",
            "https://a@b@host/x", "ssh://user:pw@host/x",
            "ssh://user@h1@h2/x", "user:pw@host:org/repo.git",
            "git:secret@host:org/repo.git", "x:pw@h:p",
            "name:tag@version",
            "https://host/x?access_token=1", "https://host/x?path=/s&token=1",
            "https://host/x?",
        };

        [TestCaseSource(nameof(CredentialForms))]
        public void DetectorFlagsCredentialForms(string packageId)
        {
            Assert.IsTrue(
                GitUrlCredentialCheck.ContainsCredentials(packageId), packageId);
        }

        private static readonly string[] CleanForms =
        {
            "https://host/x", "https://host/x?path=/s", "https://host/x?path=/s#r",
            "https://host/x?path=/s#r?token=1",
            "https://host/x#rev", "ssh://git@host/x", "ssh://user@host/x",
            "git@host:org/repo.git", "user@host:org/repo.git",
            "git@host:org/repo://with://at@chars",
            "com.unity.foo@1.0.0", "com.unity.foo", "name@1:2:3/x",
        };

        [TestCaseSource(nameof(CleanForms))]
        public void DetectorAcceptsCredentialFreeForms(string packageId)
        {
            Assert.IsFalse(
                GitUrlCredentialCheck.ContainsCredentials(packageId), packageId);
        }

        [Test]
        public void DetectorTreatsNullAndEmptyAsCredentialFree()
        {
            Assert.IsFalse(GitUrlCredentialCheck.ContainsCredentials(null));
            Assert.IsFalse(GitUrlCredentialCheck.ContainsCredentials(""));
        }
    }
}
