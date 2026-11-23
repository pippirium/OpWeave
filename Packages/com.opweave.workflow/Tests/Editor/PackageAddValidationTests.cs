#nullable enable

// T-W4.2-04 [Edit] tests: local:T-W4.2-04:package-add-reconcile — the
// real-Descriptor re-tests of SPEC §G8.1's credential and ConstantOnly
// rules, driven through the same production path the AgentApi's
// validate_workflow/execute_workflow commands run: the real
// OpWeave.Operations registry build, the real WorkflowCompiler phases
// (D4's ConstantOnly finding, D6's credential check, the Operation's
// own ValidateStatic over the real OpWeaveSettings load) and the real
// FileRunStore directory — a credential-bearing or fed id must produce
// OW-OP-006 before a Plan exists, must never echo the rejected text, and
// must leave no plan.json/workflow.json/journal/state/changes file
// behind. A source outside allowedPackageSources is OW-PERM-001.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W4.2-04:package-add-reconcile — the credential, fed-input
    /// and allowlist rejections of <c>unity.package.add</c>, re-tested
    /// through the real Descriptor on the real compile path.
    /// </summary>
    internal sealed class PackageAddValidationTests
    {
        private const string OperationsAssembly = "OpWeave.Operations";

        private static string RunStoreDir => Path.Combine(
            Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
            "Library", "OpWeavePackageAddValidation", "RunStore");

        // The five Run Store artifacts SPEC §G8.1 names: a rejected
        // credential workflow must leave none of them behind.
        private static readonly string[] RunStoreFiles =
        {
            "plan.json", "workflow.json", "journal.ndjson",
            "state.json", "changes.json",
        };

        [TearDown]
        public void TearDown()
        {
            var root = Path.GetDirectoryName(RunStoreDir);
            if (root is not null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static RegistryBuildResult Build(out RegistryPublication publication)
        {
            publication = new RegistryPublication();
            var seams = new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => TypeCache
                    .GetTypesWithAttribute<OpWeaveOperationAttribute>()
                    .Where(type => string.Equals(
                        type.Assembly.GetName().Name, OperationsAssembly,
                        StringComparison.Ordinal)),
                ProviderTypes: () =>
                    TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: type => EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());
            return RegistryBuildHost.Rebuild(
                seams, RegistryOperationStage.Production(), publication);
        }

        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;

            internal StubStaticContext(
                string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }

            public override string NodeId { get; }

            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException(
                    "unity.package.add's ValidateStatic reads Parameters only.");
        }

        private static ValidationReport Validate(string workflowJson)
        {
            var build = Build(out _);
            Assert.That(build.OperationFailures, Is.Empty);
            Assert.That(build.Snapshot, Is.Not.Null);
            var compiler = new WorkflowCompiler(
                build.Snapshot!,
                (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                new UnityTypeProbe(),
                UnityPlanProvenance.Capture(IntegrityLevel.Weak, build.Providers));
            var read = WorkflowSerializer.Read(workflowJson);
            Assert.That(read.Ok, Is.True, () => string.Join(
                "; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return compiler.Validate(new ValidationRequest(
                read.Value!, null, ValidationMode.Static, ActorKind.Agent));
        }

        private static string WorkflowWithConstantId(string id) =>
            "{\"schemaVersion\":\"1.0\",\"id\":\"test.package-add\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"add\",\"operation\":\"unity.package.add\",\"version\":1," +
            "\"parameters\":{\"id\":" + ToJsonString(id) + "}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"add\"}]}";

        private static string ToJsonString(string value) =>
            Newtonsoft.Json.Linq.JToken.FromObject(value).ToString(
                Newtonsoft.Json.Formatting.None);

        // Asserts every diagnostic field of the report: the rejected text
        // must not appear in message, hint, jsonPath, nodeId or port
        // (SPEC §G8.1 "出力しない").
        private static void AssertNoEcho(ValidationReport report, params string[] secrets)
        {
            foreach (var diagnostic in report.Errors.Concat(report.Warnings))
            {
                foreach (var secret in secrets)
                {
                    Assert.That(diagnostic.Message ?? "", Does.Not.Contain(secret),
                        "the rejection message echoes the candidate");
                    Assert.That(diagnostic.Hint ?? "", Does.Not.Contain(secret),
                        "the rejection hint echoes the candidate");
                    Assert.That(diagnostic.JsonPath ?? "", Does.Not.Contain(secret),
                        "the rejection jsonPath echoes the candidate");
                    Assert.That(diagnostic.NodeId ?? "", Does.Not.Contain(secret),
                        "the rejection nodeId echoes the candidate");
                    Assert.That(diagnostic.Port ?? "", Does.Not.Contain(secret),
                        "the rejection port echoes the candidate");
                }
            }
        }

        // Asserts the Run Store has none of the five persisted artifacts
        // (SPEC §G8.1: "Run Store（plan.json・workflow.json・
        // journal.ndjson・state.json・changes.json）に何も保存されない").
        private static void AssertRunStoreEmpty()
        {
            if (!Directory.Exists(RunStoreDir))
            {
                return;
            }
            var persisted = Directory
                .GetFiles(RunStoreDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();
            foreach (var artifact in RunStoreFiles)
            {
                Assert.That(persisted, Does.Not.Contain(artifact),
                    "a rejected workflow wrote " + artifact + " to the Run Store");
            }
        }

        // SPEC §G8.1's three credential forms, each rejected with
        // OW-OP-006 before a Plan exists, no echo, no Run Store write.
        [Test]
        public void ConstantId_CredentialUserInfoPassword_OwOp006_NoEcho_NoRunStore()
        {
            var id = "https://user:pass@github.com/org/repo.git";

            var report = Validate(WorkflowWithConstantId(id));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            Assert.That(
                report.Errors.Any(d => d.Code == "OW-OP-006"), Is.True,
                "credential userinfo with a password part is OW-OP-006");
            Assert.That(
                report.Errors.Any(d => d.Code == "OW-PERM-001"), Is.False,
                "the credential finding replaces the allowlist finding — " +
                "the URL never reaches the policy matcher");
            AssertNoEcho(report, "user:pass", "pass@");
            AssertRunStoreEmpty();
        }

        [Test]
        public void ConstantId_CredentialTokenUserinfo_OwOp006_NoEcho_NoRunStore()
        {
            var id = "https://secrettoken@github.com/org/repo.git";

            var report = Validate(WorkflowWithConstantId(id));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            Assert.That(
                report.Errors.Any(d => d.Code == "OW-OP-006"), Is.True,
                "a bare userinfo token is OW-OP-006");
            AssertNoEcho(report, "secrettoken");
            AssertRunStoreEmpty();
        }

        [Test]
        public void ConstantId_CredentialQueryParameter_OwOp006_NoEcho_NoRunStore()
        {
            var id = "https://github.com/org/repo.git?access_token=secretvalue";

            var report = Validate(WorkflowWithConstantId(id));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            Assert.That(
                report.Errors.Any(d => d.Code == "OW-OP-006"), Is.True,
                "a query parameter other than path= is OW-OP-006");
            AssertNoEcho(report, "secretvalue", "access_token=secretvalue");
            AssertRunStoreEmpty();
        }

        // The same-url-without-credentials control: disallowed under the
        // default allowlist but rejected as OW-PERM-001 — proving the
        // credential findings above are the OW-OP-006 path, not a blanket
        // rejection of the URL shape.
        [Test]
        public void ConstantId_GitUrlWithoutCredentials_OwPerm001()
        {
            var report = Validate(WorkflowWithConstantId("https://github.com/org/repo.git"));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            Assert.That(
                report.Errors.Any(d => d.Code == "OW-PERM-001"), Is.True,
                "the default allowlist admits no Git source");
            Assert.That(
                report.Errors.Any(d => d.Code == "OW-OP-006"), Is.False);
        }

        [Test]
        public void ConstantId_UnknownRegistryName_OwPerm001()
        {
            var report = Validate(WorkflowWithConstantId("com.example.pkg@1.0.0"));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            Assert.That(
                report.Errors.Any(d => d.Code == "OW-PERM-001"), Is.True,
                "com.example.* is outside the default registry:com.unity.* rule");
        }

        // SPEC §G8.1: a data-edge-fed id is OW-OP-006 regardless of the
        // value — the ConstantOnly rule (D4 item 4). The producer is
        // unity.gameobject.find, an unrelated output type on purpose: the
        // rejection must not depend on the value ever being evaluated.
        [Test]
        public void EdgeFedId_OwOp006_ConstantOnly()
        {
            var workflow =
                "{\"schemaVersion\":\"1.0\",\"id\":\"test.package-add-edge\"," +
                "\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"find\",\"operation\":\"unity.gameobject.find\",\"version\":1," +
                "\"parameters\":{\"name\":\"Probe\"}}," +
                "{\"id\":\"add\",\"operation\":\"unity.package.add\",\"version\":1}]," +
                "\"controlEdges\":[{\"from\":\"start\",\"to\":\"find\"}," +
                "{\"from\":\"find\",\"to\":\"add\"}]," +
                "\"dataEdges\":[{\"from\":{\"node\":\"find\",\"port\":\"gameObject\"}," +
                "\"to\":{\"node\":\"add\",\"port\":\"id\"}}]}";

            var report = Validate(workflow);

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            Assert.That(
                report.Errors.Any(d =>
                    d.Code == "OW-OP-006" &&
                    string.Equals(d.JsonPath, "$.dataEdges[0]", StringComparison.Ordinal)),
                Is.True, "the edge supplier is reported at $.dataEdges[0]");
        }

        // SPEC §G8.1: a workflow-input binding is the second ConstantOnly
        // violation — OW-OP-006 at $.inputBindings[0], no echo.
        [Test]
        public void BindingFedId_OwOp006_ConstantOnly()
        {
            var workflow =
                "{\"schemaVersion\":\"1.0\",\"id\":\"test.package-add-binding\"," +
                "\"inputs\":[{\"id\":\"packageId\",\"type\":\"core.String\",\"required\":true}]," +
                "\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"add\",\"operation\":\"unity.package.add\",\"version\":1}]," +
                "\"controlEdges\":[{\"from\":\"start\",\"to\":\"add\"}]," +
                "\"inputBindings\":[{\"fromInput\":\"packageId\"," +
                "\"to\":{\"node\":\"add\",\"port\":\"id\"}}]}";

            var report = Validate(workflow);

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Plan, Is.Null);
            Assert.That(
                report.Errors.Any(d =>
                    d.Code == "OW-OP-006" &&
                    string.Equals(d.JsonPath, "$.inputBindings[0]", StringComparison.Ordinal)),
                Is.True, "the binding supplier is reported at $.inputBindings[0]");
        }

        // The positive control on the real path: the default-allowlisted
        // registry id validates clean (the approval requirement for
        // package.modify lands on the Plan, not the diagnostics).
        [Test]
        public void ConstantId_AllowedRegistryId_ValidatesOk()
        {
            var report = Validate(
                WorkflowWithConstantId("com.unity.editorcoroutines@1.0.0"));

            Assert.That(report.Ok, Is.True, () => string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.That(report.Warnings, Is.Empty, () => string.Join(
                "; ", report.Warnings.Select(d => d.Code + " " + d.Message)));
            Assert.That(report.Plan, Is.Not.Null);
        }
    }
}
