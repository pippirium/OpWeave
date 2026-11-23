#nullable enable

// T-W4.2-02 [Edit] tests: local:T-W4.2-02:script-create-compile — the
// workflow-level halves (SPEC §G8 items 2–3, §D6, §H4):
//  - the template → compile → component.add chain validates warning-only:
//    the generated type's existence earns the §D6 OW-VAL-015 warning, and
//    the intervening unity.script.compile silences §D6 item 6's
//    OW-VAL-013 — which a workflow without the compile node does earn;
//  - the Agent approval: an Agent Run whose Plan contains
//    unity.script.create stops in AwaitingApproval under the default
//    (ask) level AND under a persisted allow level, which §H4/SG-18
//    never lets stage 3 fall below ask; the detail shows the full
//    constant content and is bound to the planHash covering it;
//  - the §G8 item 2 bound at workflow validation: a constant path
//    outside the configured codePaths fails the whole validation with
//    OW-PERM-002;
//  - the M.7 creation guide exists and names the creation path.
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
using OpWeave.Operations;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The workflow-level halves of
    /// local:T-W4.2-02:script-create-compile — warning-only generated-type
    /// validation, the stage-3 approval gate and the codePaths bound.
    /// </summary>
    internal sealed class ScriptCreateWorkflowTests
    {
        private const string WorkflowId = "example.script-create-flow";

        private const string ProbeSource =
            "using UnityEngine;\n" +
            "\n" +
            "namespace Game.Gen\n" +
            "{\n" +
            "    public sealed class ApprovedProbe : MonoBehaviour\n" +
            "    {\n" +
            "    }\n" +
            "}\n";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveScriptCreateFlow",
                "t" + Guid.NewGuid().ToString("N"));
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

        // ── fixtures: production-compile path (the ApprovalWorkflowTests
        // pattern) ────────────────────────────────────────────────────────

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
                throw new NotSupportedException("the fixture reads Parameters only");
        }

        private sealed class StubPlanProvenance : IPlanProvenance
        {
            private static readonly Result<PlanEnvironment> s_environment =
                Result<PlanEnvironment>.Success(new PlanEnvironment(
                    "6000.0.85f1",
                    "Windows",
                    "StandaloneWindows64",
                    "Mono2x",
                    "NET_Standard",
                    new[] { "DEBUG", "TRACE", "UNITY_EDITOR" }));

            public IntegrityLevel IntegrityLevel => IntegrityLevel.Weak;
            public Result<PlanEnvironment> Environment => s_environment;

            public Result<PlanTypeFingerprint> TypeFingerprintFor(TypeDefinition definition)
            {
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var digest = sha256.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(definition.Id));
                var hex = BitConverter.ToString(digest)
                    .Replace("-", string.Empty).ToLowerInvariant();
                return Result<PlanTypeFingerprint>.Success(new PlanTypeFingerprint(
                    "test.provider",
                    new string('1', 64),
                    "Test.Types",
                    hex));
            }

            public Result<PlanCodecIdentity> CodecIdentityFor(TypeDefinition definition) =>
                Result<PlanCodecIdentity>.Success(new PlanCodecIdentity(
                    "test.provider",
                    "Test.Codecs." + definition.Id.Replace("<", "Of").Replace(">", string.Empty),
                    "Test.Codecs",
                    new string('2', 64)));
        }

        private static void RegisterCompiledOps(RunnerHostFixture fixture)
        {
            fixture.Register(
                new RunnerHostFixture.MarkerOperation("core.start", fixture.Events), 'a');
            fixture.Register(new GameObjectCreateOperation(), 'b');
            fixture.Register(new CreateScriptFromTemplateOperation(), 'c');
            fixture.Register(new CreateScriptOperation(), 'd');
            fixture.Register(new CompileScriptsOperation(), 'e');
            fixture.Register(new ComponentAddOperation(), 'f');
        }

        private static ValidationReport Compile(
            RunnerHostFixture fixture,
            WorkflowDocument doc,
            Func<ActorKind, IPermissionPolicy>? policyFor = null)
        {
            return new WorkflowCompiler(
                    fixture.Snapshot(),
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    fixture.Probe,
                    new StubPlanProvenance(),
                    policyFor: policyFor)
                .Validate(new ValidationRequest(
                    doc, null, ValidationMode.Static, ActorKind.Agent));
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));

        // ── The generated-type validation chain (SPEC §G8 item 3, §D6) ──

        // create_from_template → unity.script.compile → component.add: the
        // compile node resets the §D6 item 6 ordering rule, so the
        // generated type's not-yet-existing componentType warns OW-VAL-015
        // only — validation is warning-only, never an error.
        [Test]
        public void TemplateThenCompileThenComponentAdd_ValidatesWarningOnly()
        {
            var fixture = NewFixture();
            RegisterCompiledOps(fixture);
            var nodes = new List<NodeData>
            {
                new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                new NodeData
                {
                    Id = "gen",
                    Operation = "unity.script.create_from_template",
                    Version = 1,
                    Parameters = new Dictionary<string, JToken>
                    {
                        ["template"] = "mono_behaviour",
                        ["className"] = "GeneratedProbe",
                        ["folder"] = "Assets/Scripts",
                    },
                },
                new NodeData
                {
                    Id = "compile",
                    Operation = "unity.script.compile",
                    Version = 1,
                },
                new NodeData
                {
                    Id = "go",
                    Operation = "unity.gameobject.create",
                    Version = 1,
                    Parameters = new Dictionary<string, JToken>
                    {
                        ["name"] = "Probe",
                    },
                },
                new NodeData
                {
                    Id = "add",
                    Operation = "unity.component.add",
                    Version = 1,
                    Parameters = new Dictionary<string, JToken>
                    {
                        ["componentType"] = "GeneratedProbe",
                    },
                },
            };
            var edges = new List<ControlEdge>
            {
                new ControlEdge("start", "gen"),
                new ControlEdge("gen", "compile"),
                new ControlEdge("compile", "go"),
                new ControlEdge("go", "add"),
            };
            var report = Compile(fixture, DocWithTarget(nodes, edges));

            Assert.That(report.Ok, Is.True,
                "validation must be error-free: " + Describe(report.Errors));
            Assert.That(report.Errors, Is.Empty);
            Assert.That(report.Warnings.Select(d => d.Code).ToArray(),
                Is.EqualTo(new[] { ErrorCodes.OW_VAL_015 }),
                "the generated type warns OW-VAL-015 only once the compile intervenes");
            Assert.That(report.Warnings.All(d => d.Severity == Severity.Warning), Is.True);
        }

        // The same chain without the compile node: the type_exists node
        // after code generation earns §D6 item 6's OW-VAL-013 — the warning
        // that tells the author to insert unity.script.compile — beside
        // the OW-VAL-015 the missing type itself earns. Still no error.
        [Test]
        public void CreateThenComponentAdd_WithoutCompile_WarnsOwVal013()
        {
            var fixture = NewFixture();
            RegisterCompiledOps(fixture);
            var nodes = new List<NodeData>
            {
                new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                new NodeData
                {
                    Id = "write",
                    Operation = "unity.script.create",
                    Version = 1,
                    Parameters = new Dictionary<string, JToken>
                    {
                        ["path"] = "Assets/Scripts/Gen/Probe.cs",
                        ["content"] = ProbeSource,
                    },
                },
                new NodeData
                {
                    Id = "go",
                    Operation = "unity.gameobject.create",
                    Version = 1,
                    Parameters = new Dictionary<string, JToken>
                    {
                        ["name"] = "Probe",
                    },
                },
                new NodeData
                {
                    Id = "add",
                    Operation = "unity.component.add",
                    Version = 1,
                    Parameters = new Dictionary<string, JToken>
                    {
                        ["componentType"] = "Game.Gen.ApprovedProbe",
                    },
                },
            };
            var edges = new List<ControlEdge>
            {
                new ControlEdge("start", "write"),
                new ControlEdge("write", "go"),
                new ControlEdge("go", "add"),
            };
            var report = Compile(fixture, DocWithTarget(nodes, edges));

            Assert.That(report.Ok, Is.True,
                "the warnings never make validation fail: " + Describe(report.Errors));
            var codes = report.Warnings.Select(d => d.Code).ToList();
            Assert.That(codes, Contains.Item(ErrorCodes.OW_VAL_013),
                "type use before compile warns §D6 item 6");
            Assert.That(codes, Contains.Item(ErrorCodes.OW_VAL_015));
            Assert.That(report.Errors, Is.Empty);
        }

        // The §G8 item 2 bound at workflow validation: a constant path
        // outside the settings codePaths is OW-PERM-002 before a Plan
        // exists — the same refusal Preflight/Execute give a non-constant
        // value.
        [Test]
        public void Create_OutsideCodePaths_FailsValidation_OwPerm002()
        {
            var fixture = NewFixture();
            RegisterCompiledOps(fixture);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData
                    {
                        Id = "write",
                        Operation = "unity.script.create",
                        Version = 1,
                        Parameters = new Dictionary<string, JToken>
                        {
                            ["path"] = "Assets/Elsewhere/Probe.cs",
                            ["content"] = ProbeSource,
                        },
                    },
                },
                new[] { new ControlEdge("start", "write") });

            var report = Compile(fixture, doc);

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Errors.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_PERM_002));
        }

        // ── The stage-3 Agent approval (SPEC §G8 item 2, §H4) ───────────

        private static WorkflowDocument CreateDoc()
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = WorkflowId,
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData
                    {
                        Id = "write",
                        Operation = CreateScriptOperation.OperationId,
                        Version = 1,
                        Parameters = new Dictionary<string, JToken>
                        {
                            ["path"] = "Assets/Scripts/ApprovedProbe.cs",
                            ["content"] = ProbeSource,
                        },
                    },
                },
                ControlEdges = new List<ControlEdge> { new ControlEdge("start", "write") },
            });
            Assert.IsTrue(created.Ok, "fixture workflow document must be valid");
            return created.Value!;
        }

        // An Agent code.write always asks — under the default ask level
        // AND under a persisted "allow", which the loader floors to ask
        // (SPEC §H4 stage 3 / SG-18): the Plan carries the approval, the
        // Run stops in AwaitingApproval, and the detail shows the full
        // source bound to the planHash that covers it.
        [Test]
        public void AgentCreateRun_AwaitsApproval_UnderDefaultAndAllowSettings()
        {
            // The default (ask) level.
            var fixture = NewFixture();
            RegisterCompiledOps(fixture);
            var doc = CreateDoc();
            var report = Compile(fixture, doc);
            Assert.That(report.Ok, Is.True, "compile failed: " + Describe(report.Errors));
            Assert.That(report.Plan!.Approvals.Select(a => a.NodeId),
                Contains.Item("write"), "the Plan asks for the code.write step");

            var accepted = fixture.Host.AcceptRun(
                report.Plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            Assert.AreEqual(RunState.AwaitingApproval, accepted.Value!.State);
            var detail = fixture.Host.Approvals.DetailOf(accepted.Value!.RunId)!;
            Assert.AreEqual(report.Plan.PlanHash, detail.PlanHash,
                "the approval is bound to the planHash covering the content");
            var node = detail.Nodes.Single(n => n.NodeId == "write");
            Assert.AreEqual(
                ProbeSource, (string?)node.ConstantInputs["content"].ToToken(),
                "the approval shows the full constant source verbatim");
            Assert.AreEqual(
                "Assets/Scripts/ApprovedProbe.cs",
                (string?)node.ConstantInputs["path"].ToToken());
            Assert.That(node.ApprovalEffects, Contains.Item(EffectNames.CodeWrite));

            // A persisted allow never drops stage 3 below ask: the same
            // Plan compile over the corrected settings still asks.
            var settingsPath = Path.Combine(_root, "allow-settings.json");
            Directory.CreateDirectory(_root);
            File.WriteAllText(settingsPath,
                "{\"version\":2,\"policy\":{\"agent\":{\"code.write\":\"allow\"}}}");
            var loaded = OpWeaveSettingsLoader.Load(settingsPath);
            Assert.IsTrue(loaded.Ok, "the allow fixture must load");
            var settings = loaded.Value!;
            Assert.That(settings.AgentPolicy["code.write"], Is.EqualTo(PolicyLevel.Ask),
                "SG-18 floors a persisted Agent code.write allow to ask");

            var fixture2 = NewFixture();
            RegisterCompiledOps(fixture2);
            fixture2.LoadSettings = () => loaded;
            var report2 = Compile(fixture2, doc,
                policyFor: actor => SettingsPermissionPolicy.For(
                    actor == ActorKind.Agent ? PolicyActor.Agent : PolicyActor.Human,
                    settings));
            Assert.That(report2.Ok, Is.True,
                "compile under allow settings failed: " + Describe(report2.Errors));
            Assert.That(report2.Plan!.Approvals.Select(a => a.NodeId),
                Contains.Item("write"),
                "the floored level still asks for the code.write step");

            var accepted2 = fixture2.Host.AcceptRun(
                report2.Plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted2.Ok,
                "accept under allow settings failed: " + Describe(accepted2.Diagnostics));
            Assert.AreEqual(RunState.AwaitingApproval, accepted2.Value!.State,
                "a persisted allow must still stop the Agent Run in AwaitingApproval");
            var detail2 = fixture2.Host.Approvals.DetailOf(accepted2.Value!.RunId)!;
            Assert.AreEqual(
                ProbeSource, (string?)detail2.Nodes.Single(n => n.NodeId == "write")
                    .ConstantInputs["content"].ToToken());
        }

        // The M.7 creation guide the ticket ships must exist and name the
        // creation path: the codePaths opt-in, unity.script.create, the
        // every-time Agent approval and unity.script.compile.
        [Test]
        public void CreationGuide_Exists_AndNamesTheCreationPath()
        {
            var repoRoot = Path.GetFullPath(
                Path.Combine(RunnerHostFixture.ProjectRoot, ".."));
            var guide = Path.Combine(repoRoot, "docs", "agent", "custom-operations.md");

            Assert.That(File.Exists(guide), Is.True,
                "docs/agent/custom-operations.md documents the creation path");
            var text = File.ReadAllText(guide);
            Assert.That(text, Does.Contain("unity.script.create"));
            Assert.That(text, Does.Contain("unity.script.compile"));
            Assert.That(text, Does.Contain("codePaths"));
            Assert.That(text, Does.Contain("approval"));
        }

        // The component.add node needs its 'target' port fed: build the
        // document with the data edge from the created GameObject.
        private static WorkflowDocument DocWithTarget(
            IReadOnlyList<NodeData> nodes, IReadOnlyList<ControlEdge> edges)
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = WorkflowId,
                Nodes = nodes.ToList(),
                ControlEdges = edges.ToList(),
                DataEdges = new List<DataEdge>
                {
                    new DataEdge(
                        new PortRef("go", "gameObject"), new PortRef("add", "target")),
                },
            });
            Assert.IsTrue(created.Ok, "the data-edged fixture document must be valid");
            return created.Value!;
        }
    }
}
