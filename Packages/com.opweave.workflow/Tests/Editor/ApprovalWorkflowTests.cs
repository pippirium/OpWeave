#nullable enable

// T-W3.4-02 [Edit] tests: local:T-W3.4-02:approval-flow — an Agent Run with
// ask effects enters AwaitingApproval and raises the Run Viewer notification
// hook with the per-node detail (an input-bound port's detail carries the
// resolved Plan value it executes with beside the binding marker, and the
// detail names the custom and external Operations every Plan step uses with
// their sources, SPEC §H4 item 3); the approval service is the only decision
// surface and writes
// exactly the Core T-W1.8-09 approval/decision journal records with the OS
// user name as approver; the decision is bound to the presented planHash (a
// stale or already-rejected hash cannot be approved and an edited workflow
// value — both revisions compiled through the production
// WorkflowCompiler/PlanBuilder path — produces a new planHash that asks
// again, SPEC §H4 test 2); an approval past approvalTimeoutMinutes ends the
// Run Cancelled with OW-PERM-003 and batchmode refuses with OW-PERM-004. A
// decision on a Run this workflow already ended by rejection or expiry
// answers OW-PERM-003 (its recorded outcome, SPEC §J); a decision on a Run
// in any other state is an untabled SPEC §E1 transition and throws like the
// Core decision API.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class ApprovalWorkflowTests
    {
        private const string WorkflowId = "example.approvalflow";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveApprovalTests",
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

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // ── Plan builders ───────────────────────────────────────────────

        private static PlanStep Step(
            int index,
            string nodeId,
            string operationId,
            char hashSeed,
            IReadOnlyList<string>? effects = null,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            int next = -1,
            Idempotency idempotency = Idempotency.Idempotent) =>
            new PlanStep(
                index,
                nodeId,
                operationId,
                1,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64),
                inputs ?? new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = next },
                effects ?? (IReadOnlyList<string>)Array.Empty<string>(),
                30,
                new RetryPolicy(),
                idempotency);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"fixture JSON must parse: {text}");
            return parsed.Value!;
        }

        // The approvals-carrying variant of RunnerHostFixture.Plan: the same
        // SG-12 hash input, with the asks flowing into requiredEffects so the
        // planHash covers them (SPEC §D7).
        private static CompiledPlan Plan(
            string workflowId,
            IReadOnlyList<PlanStep> steps,
            IReadOnlyList<ApprovalRequirement> approvals)
        {
            var hashInput = new JObject
            {
                ["canonicalJsonVersion"] = 1,
                ["environmentIdentifiers"] = new JObject
                {
                    ["unityVersion"] = "6000.0.85f1",
                    ["editorOS"] = "windows",
                    ["activeBuildTarget"] = "windows",
                    ["scriptingBackend"] = "mono",
                    ["apiCompatibilityLevel"] = "netstandard2.1",
                    ["scriptingDefineSymbols"] = new JArray(),
                },
                ["executionInputs"] = PlanHashInputParts.ExecutionInputs(
                    Array.Empty<string>(), null),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(steps, approvals),
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = workflowId,
                    ["inputs"] = new JArray(),
                    ["outputs"] = new JArray(),
                },
                ["retry"] = PlanHashInputParts.Retry(steps),
                ["steps"] = PlanHashInputParts.Steps(steps),
                ["timeouts"] = PlanHashInputParts.Timeouts(steps),
                ["transitions"] = PlanHashInputParts.Transitions(steps),
                ["usedCodecFingerprints"] = new JArray(),
                ["usedOperationFingerprints"] =
                    PlanHashInputParts.OperationFingerprints(steps),
                ["usedTypeFingerprints"] = new JArray(),
            };
            var input = JsonValue.From(hashInput);
            return new CompiledPlan(
                "plan_" + Identifiers.NewUlid(),
                CanonicalJson.Sha256(input.ToToken()),
                workflowId,
                new string('b', 64),
                new string('c', 64),
                Json("{}"),
                steps,
                Array.Empty<PlanOutput>(),
                PlanBuilder.UnionEffects(steps),
                approvals,
                Array.Empty<PlanSourceMapEntry>(),
                input);
        }

        // A single-step ask Plan: the step's op must be registered first so
        // the §E2 item 5 fingerprint check leaves it alone.
        private static CompiledPlan AskPlan(
            string workflowId,
            char hashSeed,
            string operationId = "test.approval.marker",
            string effect = "asset.delete",
            IReadOnlyDictionary<string, InputSource>? inputs = null)
        {
            var steps = new[]
            {
                Step(0, "ask", operationId, hashSeed,
                    effects: new[] { effect },
                    inputs: inputs,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var approvals = new[]
            {
                new ApprovalRequirement("ask", effect, operationId + " v1"),
            };
            return Plan(workflowId, steps, approvals);
        }

        private static WorkflowDocument AskDoc(string workflowId, string operationId = "test.approval.marker") =>
            RunnerHostFixture.Doc(
                workflowId,
                new[] { new NodeData { Id = "ask", Operation = operationId, Version = 1 } },
                Array.Empty<ControlEdge>());

        private static Result<RunHandle> Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc) =>
            fixture.Host.AcceptRun(plan, doc, ActorKind.Agent, RunnerHostFixture.Options());

        // ── Compiled-path fixtures: production compiler → Plan → AcceptRun ──

        // Registers the compiled path's two Operations with distinct
        // synthetic fingerprint pairs: a start marker and the approval-gated
        // writer whose declared asset.delete lands on the Plan's approvals
        // under the §C4 default Agent policy.
        private static void RegisterCompiledOps(RunnerHostFixture fixture)
        {
            fixture.Register(
                new RunnerHostFixture.MarkerOperation("core.start", fixture.Events), 'a');
            fixture.Register(
                new AskOperation("test.approval.writer", fixture.Events), 'c');
        }

        // A start → ask document the production pipeline accepts; the ask
        // node's ports take their suppliers from the parameters and input
        // bindings the case declares.
        private static WorkflowDocument CompiledAskDoc(
            string workflowId,
            IDictionary<string, JToken>? askParameters = null,
            IList<WorkflowInput>? inputs = null,
            IList<InputBinding>? bindings = null)
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = workflowId,
                Inputs = inputs,
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData
                    {
                        Id = "ask",
                        Operation = "test.approval.writer",
                        Version = 1,
                        Parameters = askParameters,
                    },
                },
                ControlEdges = new List<ControlEdge> { new ControlEdge("start", "ask") },
                InputBindings = bindings,
            });
            Assert.IsTrue(created.Ok, "fixture workflow document must be valid");
            return created.Value!;
        }

        // The production compile the runner consumes (SPEC §D1 → §D7): the
        // snapshot-backed WorkflowCompiler over the fixture Registry — real
        // Resolve, D2–D6 and PlanBuilder stages — with the fixture type
        // probe and a deterministic provenance so the hash is reproducible.
        private static CompiledPlan CompilePlan(
            RunnerHostFixture fixture, WorkflowDocument doc, JsonValue? inputs = null)
        {
            var report = new WorkflowCompiler(
                    fixture.Snapshot(),
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    fixture.Probe,
                    new StubPlanProvenance())
                .Validate(new ValidationRequest(
                    doc, inputs, ValidationMode.Static, ActorKind.Agent));
            Assert.IsTrue(report.Ok, "compile failed: " + Describe(report.Errors));
            Assert.IsNotNull(report.Plan, "an error-free compile must produce the Plan");
            return report.Plan!;
        }

        // A minimal StaticContext (Core hosts no implementation, SPEC §0.6
        // SG-06); the fixture Operations' ValidateStatic reports nothing.
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
                throw new NotSupportedException("The fixture Operations read no static port.");
        }

        // A deterministic IPlanProvenance — the [Core]
        // PlanProvenanceFixtures.Stub answers atom for atom — so a compiled
        // Plan's hash is reproducible without Editor environment reads.
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

        // An Immediate marker whose declared asset.delete effect asks under
        // the default Agent policy — the compiled path's approval-requiring
        // node. Its input ports take the constant, bound and missing
        // suppliers the detail tests need.
        private sealed class AskOperation : IOperation
        {
            private readonly List<string> _events;

            internal AskOperation(string id, List<string> events)
            {
                _events = events;
                Descriptor = Op.Define(id, 1)
                    .Summary("Writes one asset after human approval.")
                    .Input("path", "core.String", required: true)
                    .Input("content", "core.Json", required: false)
                    .Input("extra", "core.String", required: false)
                    .Output("mark", "core.String")
                    .Effects(EffectNames.AssetDelete)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                _events.Add("execute:" + Descriptor.Id);
                ctx.SetOutput("mark", "done:" + Descriptor.Id);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // ── AwaitingApproval + the Run Viewer notification hook ─────────

        [Test]
        public void AskRunAwaitsApprovalAndRaisesTheNotificationHook()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var notified = new List<RunApprovalDetail>();
            fixture.Host.Approvals.ApprovalPending += detail => notified.Add(detail);

            var accepted = Accept(fixture, plan, doc);

            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Assert.AreEqual(RunState.AwaitingApproval, accepted.Value.State);
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Host.Coordinator.StateOf(runId));

            // SPEC §H5 item 3: the notification hook fired exactly once with
            // the detail the approval screen shows.
            Assert.AreEqual(1, notified.Count);
            var detail = notified[0];
            Assert.AreEqual(runId, detail.RunId);
            Assert.AreEqual(plan.PlanHash, detail.PlanHash);
            Assert.AreEqual(WorkflowId, detail.WorkflowId);
            Assert.AreEqual(
                fixture.Clock.UtcNow + TimeSpan.FromMinutes(1440), detail.DeadlineUtc,
                "the default approvalTimeoutMinutes is 1440 (SPEC §H4.1)");

            // SPEC §H4 item 3: per node — effects, the asked effect, and the
            // custom Operation's origin and source.
            Assert.AreEqual(1, detail.Nodes.Count);
            var node = detail.Nodes[0];
            Assert.AreEqual("ask", node.NodeId);
            Assert.AreEqual("test.approval.marker", node.OperationId);
            Assert.AreEqual(1, node.Version);
            Assert.That(node.Effects, Is.EqualTo(new[] { "asset.delete" }));
            Assert.That(node.ApprovalEffects, Is.EqualTo(new[] { "asset.delete" }));
            Assert.AreEqual(OperationOrigin.Custom, node.Origin);
            Assert.AreEqual("Example.Operations", node.AssemblyName);

            // The same detail is what the pending list and DetailOf serve.
            Assert.AreEqual(1, fixture.Host.Approvals.PendingApprovals.Count);
            Assert.IsNotNull(fixture.Host.Approvals.DetailOf(runId));
            Assert.IsNull(fixture.Host.Approvals.DetailOf("run_01J00000000000000000000000"));
        }

        // The detail's constant inputs carry the paths and the full code the
        // human reads; bound ports show their binding, never a value.
        [Test]
        public void DetailCarriesConstantInputsAndBoundInputMarkers()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var inputs = new Dictionary<string, InputSource>
            {
                ["path"] = new Constant(Json("\"Assets/Scripts/Generated.cs\"")),
                ["content"] = new Constant(Json("\"class Generated {}\"")),
                ["target"] = new FromInput("root"),
            };
            var plan = AskPlan(WorkflowId, 'a', inputs: inputs);
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));

            var detail = fixture.Host.Approvals.DetailOf(accepted.Value!.RunId)!;
            var node = detail.Nodes[0];
            Assert.AreEqual(
                "Assets/Scripts/Generated.cs",
                (string?)node.ConstantInputs["path"].ToToken());
            Assert.AreEqual(
                "class Generated {}",
                (string?)node.ConstantInputs["content"].ToToken());
            Assert.IsFalse(
                node.ConstantInputs.ContainsKey("target"),
                "a bound port is not a fixed value");
            Assert.AreEqual("input:root", node.BoundInputs["target"]);
        }

        // SPEC §H4 item 3: a workflow-input-bound port's detail carries the
        // resolved Plan value the port executes with — the human reads the
        // actual path, not only the "input:<id>" marker. The three bindings
        // cover every resolution: a supplied value, an explicit JSON null
        // (present, never Missing — §A0.1) and an unsupplied declared input
        // (Missing, never null).
        [Test]
        public void DetailCarriesResolvedValuesForInputBoundPorts()
        {
            var fixture = NewFixture();
            RegisterCompiledOps(fixture);
            var doc = CompiledAskDoc(
                WorkflowId,
                inputs: new List<WorkflowInput>
                {
                    new WorkflowInput("target", "core.String", Required: true),
                    new WorkflowInput("note", "core.Json", Required: false),
                    new WorkflowInput("ghost", "core.String", Required: false),
                },
                bindings: new List<InputBinding>
                {
                    new InputBinding("target", new PortRef("ask", "path")),
                    new InputBinding("note", new PortRef("ask", "content")),
                    new InputBinding("ghost", new PortRef("ask", "extra")),
                });
            var plan = CompilePlan(fixture, doc,
                Json("{\"target\":\"Assets/Scripts/Generated.cs\",\"note\":null}"));
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));

            var detail = fixture.Host.Approvals.DetailOf(accepted.Value!.RunId)!;
            var node = detail.Nodes[0];
            Assert.AreEqual("ask", node.NodeId);
            Assert.IsEmpty(node.ConstantInputs, "every port is bound — no written literal");

            // The supplied value resolves verbatim — the approver reads the
            // exact path that will execute.
            Assert.AreEqual("input:target", node.BoundInputs["path"]);
            Assert.IsTrue(node.ResolvedInputs["path"].IsPresent);
            Assert.AreEqual(
                "Assets/Scripts/Generated.cs",
                (string?)node.ResolvedInputs["path"].Value!.ToToken());

            // An explicit JSON null is a supplied value — present, never Missing.
            Assert.AreEqual("input:note", node.BoundInputs["content"]);
            Assert.IsTrue(node.ResolvedInputs["content"].IsPresent);
            Assert.AreEqual(
                JTokenType.Null,
                node.ResolvedInputs["content"].Value!.ToToken().Type);

            // An unsupplied declared input resolves Missing — never a null.
            Assert.AreEqual("input:ghost", node.BoundInputs["extra"]);
            Assert.IsFalse(node.ResolvedInputs["extra"].IsPresent);
            Assert.IsNull(node.ResolvedInputs["extra"].Value);
        }

        // Registers the op in the fixture snapshot with an explicit origin
        // and provenance — the custom/external source variants the fixed
        // project-assembly form of fixture.Register cannot express.
        private static void RegisterOp(
            RunnerHostFixture fixture,
            IOperation op,
            char hashSeed,
            OperationOrigin origin = OperationOrigin.Custom,
            string assemblyName = "Example.Operations",
            string? sourceFile = null,
            string? packageId = null,
            string? resolvedCommit = null)
        {
            fixture.Operations.Add(new RegisteredOperation(
                op.Descriptor,
                origin,
                assemblyName,
                sourceFile,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64))
            {
                PackageId = packageId,
                ResolvedCommit = resolvedCommit,
                Implementation = op,
            });
        }

        // SPEC §H4 item 3: the custom and external Operations the Run uses
        // are named for the whole Plan — the approved Run executes every
        // step, not only the ask nodes. A Plan with one ask node plus a
        // separate all-allowed custom Operation and an external UPM-package
        // Operation lists both — and the ask node's own custom Operation —
        // with their sources, in Plan order; a Standard Operation is never
        // listed. The per-node identification of the effects that asked is
        // unchanged.
        [Test]
        public void DetailListsTheUsedCustomAndExternalOperationsForTheWholePlan()
        {
            var fixture = NewFixture();
            RegisterOp(
                fixture,
                new RunnerHostFixture.MarkerOperation("test.custom.fetch", fixture.Events),
                'c',
                sourceFile: "Assets/OpWeave/Editor/Operations/Fetch.cs");
            RegisterOp(
                fixture,
                new RunnerHostFixture.MarkerOperation("test.external.ship", fixture.Events),
                'e',
                assemblyName: "External.Ops",
                packageId: "com.example.ops",
                resolvedCommit: new string('5', 40));
            RegisterOp(
                fixture,
                new RunnerHostFixture.MarkerOperation("test.standard.echo", fixture.Events),
                '7',
                origin: OperationOrigin.Standard,
                assemblyName: "OpWeave.Workflow");
            RegisterOp(
                fixture,
                new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events),
                'a');
            var steps = new[]
            {
                Step(0, "fetch", "test.custom.fetch", 'c',
                    effects: new[] { "read" },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: 1),
                Step(1, "ship", "test.external.ship", 'e',
                    effects: new[] { "read" },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: 2),
                Step(2, "echo", "test.standard.echo", '7',
                    effects: new[] { "read" },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: 3),
                Step(3, "ask", "test.approval.marker", 'a',
                    effects: new[] { "asset.delete" },
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var plan = Plan(WorkflowId, steps, new[]
            {
                new ApprovalRequirement("ask", "asset.delete", "test.approval.marker v1"),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "fetch", Operation = "test.custom.fetch", Version = 1 },
                    new NodeData { Id = "ship", Operation = "test.external.ship", Version = 1 },
                    new NodeData { Id = "echo", Operation = "test.standard.echo", Version = 1 },
                    new NodeData { Id = "ask", Operation = "test.approval.marker", Version = 1 },
                },
                new[]
                {
                    new ControlEdge("fetch", "ship"),
                    new ControlEdge("ship", "echo"),
                    new ControlEdge("echo", "ask"),
                });
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));

            var detail = fixture.Host.Approvals.DetailOf(accepted.Value!.RunId)!;

            // The per-node identification of the effects that asked is
            // unchanged: only the ask node carries a node detail.
            Assert.AreEqual(1, detail.Nodes.Count);
            var node = detail.Nodes[0];
            Assert.AreEqual("ask", node.NodeId);
            Assert.That(node.Effects, Is.EqualTo(new[] { "asset.delete" }));
            Assert.That(node.ApprovalEffects, Is.EqualTo(new[] { "asset.delete" }));
            Assert.AreEqual(OperationOrigin.Custom, node.Origin);
            Assert.IsNull(node.ResolvedCommit);

            // Every step's custom or external Operation, in Plan order,
            // with its source — including the all-allowed steps the detail
            // once omitted.
            Assert.AreEqual(3, detail.UsedOperations.Count);

            var fetch = detail.UsedOperations[0];
            Assert.AreEqual("fetch", fetch.NodeId);
            Assert.AreEqual("test.custom.fetch", fetch.OperationId);
            Assert.AreEqual(1, fetch.Version);
            Assert.AreEqual(OperationOrigin.Custom, fetch.Origin);
            Assert.AreEqual("Example.Operations", fetch.AssemblyName);
            Assert.AreEqual("Assets/OpWeave/Editor/Operations/Fetch.cs", fetch.SourceFile);
            Assert.IsNull(fetch.PackageId);
            Assert.IsNull(fetch.ResolvedCommit);

            var ship = detail.UsedOperations[1];
            Assert.AreEqual("ship", ship.NodeId);
            Assert.AreEqual("test.external.ship", ship.OperationId);
            Assert.AreEqual(OperationOrigin.Custom, ship.Origin);
            Assert.AreEqual("External.Ops", ship.AssemblyName);
            Assert.AreEqual("com.example.ops", ship.PackageId);
            Assert.AreEqual(new string('5', 40), ship.ResolvedCommit);

            var ask = detail.UsedOperations[2];
            Assert.AreEqual("ask", ask.NodeId);
            Assert.AreEqual("test.approval.marker", ask.OperationId);
            Assert.AreEqual(OperationOrigin.Custom, ask.Origin);
            Assert.AreEqual("Example.Operations", ask.AssemblyName);

            // The Standard Operation is the trusted set — never listed.
            foreach (var used in detail.UsedOperations)
            {
                Assert.AreNotEqual("echo", used.NodeId);
            }
        }

        // ── Approve: the single Core decision record, OS-user approver ───

        [Test]
        public void ApprovedRunExecutesTheApprovedPlanHash()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var approved = fixture.Host.Approvals.Approve(runId, plan.PlanHash);

            Assert.IsTrue(approved.Ok, "approve failed: " + Describe(approved.Diagnostics));
            Assert.AreEqual(RunState.Queued, approved.Value!.State);

            Drive(fixture.Host, runId);

            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Assert.That(fixture.Events, Is.EqualTo(new[] { "execute:test.approval.marker" }),
                "the approved Plan's steps ran");
            // Exactly one decision record, written by the Core API: the
            // journal's approval_granted is the coordinator's own seq-2
            // write — the service has no journal-write path of its own.
            var lines = RunnerHostFixture.JournalLines(_root, runId);
            var granted = 0;
            foreach (var line in lines)
            {
                if (line.Contains("\"type\":\"approval_granted\""))
                {
                    granted++;
                    Assert.IsTrue(line.Contains("\"seq\":2"),
                        "the decision record is the Run's first post-accept record");
                    Assert.IsTrue(
                        line.Contains("\"approver\":\"" + Environment.UserName + "\""),
                        "the approver is the OS user name (SPEC §T.4 L-09)");
                    Assert.IsTrue(line.Contains("\"required\":true"));
                }
            }
            Assert.AreEqual(1, granted, "exactly one approval_granted record");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"approval_rejected\""));
            // The journal header binds the approved Plan's hash — the Run
            // ran exactly the Plan the human approved.
            Assert.IsTrue(lines[0].Contains(plan.PlanHash));
            Assert.AreEqual(0, fixture.Host.Approvals.PendingApprovals.Count);
        }

        // ── Reject: Cancelled with OW-PERM-003 ───────────────────────────

        [Test]
        public void RejectedRunIsCancelledWithOwPerm003()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var rejected = fixture.Host.Approvals.Reject(runId, plan.PlanHash, "not today");

            Assert.IsTrue(rejected.Ok, "reject failed: " + Describe(rejected.Diagnostics));
            Assert.AreEqual(RunState.Cancelled, rejected.Value!.State);
            Assert.AreEqual(ErrorCodes.OW_PERM_003, rejected.Diagnostics[0].Code);
            Assert.AreEqual(RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsEmpty(fixture.Events, "a rejected Plan never executes");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"approval_rejected\"",
                    "\"approver\":\"" + Environment.UserName + "\"",
                    "\"reason\":\"not today\""));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"run_finished\"", "\"state\":\"cancelled\""));
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"approval_"),
                "exactly one approval decision record");
            Assert.AreEqual(0, fixture.Host.Approvals.PendingApprovals.Count);
        }

        // A rejected planHash cannot be approved afterwards — the decision
        // is terminal, no further record is journaled.
        [Test]
        public void RejectedRunCannotBeApproved()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Assert.IsTrue(
                fixture.Host.Approvals.Reject(runId, plan.PlanHash, "no").Ok);

            var approved = fixture.Host.Approvals.Approve(runId, plan.PlanHash);

            Assert.IsFalse(approved.Ok, "a rejected Run must not be approvable");
            Assert.AreEqual(ErrorCodes.OW_PERM_003, approved.Diagnostics[0].Code);
            Assert.AreEqual(RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"approval_granted\""),
                "no grant may be journaled for a rejected Run");

            // A second rejection attempt answers with the same recorded
            // outcome — OW-PERM-003 — and journals nothing either.
            var rejectedAgain = fixture.Host.Approvals.Reject(runId, plan.PlanHash, "again");
            Assert.IsFalse(rejectedAgain.Ok);
            Assert.AreEqual(ErrorCodes.OW_PERM_003, rejectedAgain.Diagnostics[0].Code);
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"approval_"),
                "the ended Run takes no further decision records");
        }

        // SPEC §E1/§J: a decision on a Run in a state the transition table
        // does not cover — Queued, Running or Succeeded — asks for an
        // untabled transition: an implementation-bug InvalidOperationException,
        // the same detection the Core decision API of T-W1.8-09 applies,
        // never OW-PERM-003 (whose published meaning is rejection/expiry).
        // The Run and its journal change nothing.
        [Test]
        public void DecisionOnANonAwaitingRunThrowsAnUntabledTransition()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.LabelWaitableOperation("test.approval.waitable");
            fixture.Register(op, 'a');
            var steps = new[]
            {
                Step(0, "ask", "test.approval.waitable", 'a',
                    effects: new[] { "asset.delete" },
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" }),
            };
            var plan = Plan(WorkflowId, steps, new[]
            {
                new ApprovalRequirement("ask", "asset.delete", "test.approval.waitable v1"),
            });
            var doc = AskDoc(WorkflowId, "test.approval.waitable");
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var granted = fixture.Host.Approvals.Approve(runId, plan.PlanHash);
            Assert.IsTrue(granted.Ok, "approve failed: " + Describe(granted.Diagnostics));
            Assert.AreEqual(RunState.Queued, fixture.Host.Coordinator.StateOf(runId));

            // Queued: the grant already decided the Run — a further decision
            // is an untabled transition.
            Assert.Throws<InvalidOperationException>(
                () => fixture.Host.Approvals.Approve(runId, plan.PlanHash));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Host.Approvals.Reject(runId, plan.PlanHash, "no"));
            Assert.AreEqual(RunState.Queued, fixture.Host.Coordinator.StateOf(runId));

            // Running: one Tick starts the waitable step and the Run is
            // mid-execution — still no approval transition exists.
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Host.Approvals.Approve(runId, plan.PlanHash));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Host.Approvals.Reject(runId, plan.PlanHash, "no"));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));

            // Succeeded: the terminal state admits no decision either.
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            Assert.IsTrue(fixture.Host.Tick().Ok, "Tick failed");
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            Assert.IsTrue(fixture.Host.Tick().Ok, "Tick failed");
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Host.Approvals.Approve(runId, plan.PlanHash));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Host.Approvals.Reject(runId, plan.PlanHash, "no"));
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));

            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"approval_"),
                "the thrown decisions journaled nothing — the grant is the only record");
        }

        // ── planHash binding ────────────────────────────────────────────

        [Test]
        public void StalePlanHashCannotBeApproved()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var stale = fixture.Host.Approvals.Approve(runId, new string('9', 64));

            Assert.IsFalse(stale.Ok);
            Assert.AreEqual(ErrorCodes.OW_RUN_003, stale.Diagnostics[0].Code);
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"approval_granted\""),
                "a stale hash journals nothing");

            // The presented hash still decides.
            var approved = fixture.Host.Approvals.Approve(runId, plan.PlanHash);
            Assert.IsTrue(approved.Ok, "approve failed: " + Describe(approved.Diagnostics));
            Assert.AreEqual(RunState.Queued, fixture.Host.Coordinator.StateOf(runId));
        }

        [Test]
        public void StalePlanHashCannotBeRejected()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var rejected = fixture.Host.Approvals.Reject(runId, new string('9', 64), "no");

            Assert.IsFalse(rejected.Ok);
            Assert.AreEqual(ErrorCodes.OW_RUN_003, rejected.Diagnostics[0].Code);
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"approval_rejected\""));
        }

        // SPEC §H4 test 2: an edited workflow value re-run after the
        // approval produces a different planHash — a fresh AwaitingApproval
        // Run the earlier approval cannot decide. Both revisions compile
        // through the production WorkflowCompiler → PlanBuilder path the
        // runner consumes; only one constant parameter changes between them.
        [Test]
        public void EditedWorkflowRequiresANewApproval()
        {
            var fixture = NewFixture();
            RegisterCompiledOps(fixture);
            var doc1 = CompiledAskDoc(
                WorkflowId,
                new Dictionary<string, JToken>
                {
                    ["path"] = JToken.Parse("\"Assets/First.cs\""),
                });
            var plan1 = CompilePlan(fixture, doc1);
            var accepted1 = Accept(fixture, plan1, doc1);
            Assert.IsTrue(accepted1.Ok, "accept failed: " + Describe(accepted1.Diagnostics));
            var runId1 = accepted1.Value!.RunId;
            Assert.IsTrue(fixture.Host.Approvals.Approve(runId1, plan1.PlanHash).Ok);
            Drive(fixture.Host, runId1);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId1));

            // The edited revision — the workflow value changed — through
            // the same production compile.
            var doc2 = CompiledAskDoc(
                WorkflowId,
                new Dictionary<string, JToken>
                {
                    ["path"] = JToken.Parse("\"Assets/Second.cs\""),
                });
            var plan2 = CompilePlan(fixture, doc2);
            Assert.AreNotEqual(plan1.PlanHash, plan2.PlanHash,
                "the edit must produce a different planHash");
            var accepted2 = Accept(fixture, plan2, doc2);
            Assert.IsTrue(accepted2.Ok, "accept failed: " + Describe(accepted2.Diagnostics));
            var runId2 = accepted2.Value!.RunId;
            Assert.AreNotEqual(runId1, runId2, "the re-run is a fresh Run");
            Assert.AreEqual(RunState.AwaitingApproval,
                fixture.Host.Coordinator.StateOf(runId2),
                "the edited workflow's new planHash asks again (SPEC §H4 test 2)");

            // The earlier approval is bound to plan1's hash — it cannot be
            // applied to the edited workflow's Run.
            var stale = fixture.Host.Approvals.Approve(runId2, plan1.PlanHash);
            Assert.IsFalse(stale.Ok);
            Assert.AreEqual(ErrorCodes.OW_RUN_003, stale.Diagnostics[0].Code);
            Assert.AreEqual(RunState.AwaitingApproval,
                fixture.Host.Coordinator.StateOf(runId2),
                "the refused attempt decided nothing");

            var approved2 = fixture.Host.Approvals.Approve(runId2, plan2.PlanHash);
            Assert.IsTrue(approved2.Ok, "approve failed: " + Describe(approved2.Diagnostics));
            Drive(fixture.Host, runId2);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId2));
            Assert.That(fixture.Events, Is.EqualTo(new[]
            {
                "execute:core.start",
                "execute:test.approval.writer",
                "execute:core.start",
                "execute:test.approval.writer",
            }));
        }

        // ── Expiry: approvalTimeoutMinutes → Cancelled OW-PERM-003 ───────

        [Test]
        public void ExpiredApprovalIsCancelledWithOwPerm003()
        {
            var fixture = NewFixture();
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"approvalTimeoutMinutes\":1}");
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            // Still inside the timeout — the Run waits.
            Assert.IsTrue(fixture.Host.Tick().Ok);
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Host.Coordinator.StateOf(runId));

            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
            var tick = fixture.Host.Tick();

            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            var sawPerm003 = false;
            foreach (var diagnostic in tick.Diagnostics)
            {
                sawPerm003 |= diagnostic.Code == ErrorCodes.OW_PERM_003;
            }
            Assert.IsTrue(sawPerm003, "the expiry reports OW-PERM-003");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"approval_rejected\"",
                    "\"approver\":\"" + Environment.UserName + "\"",
                    "\"reason\":\"" + OpWeaveApprovalService.ExpiredReason + "\""));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"run_finished\"",
                    "\"state\":\"cancelled\"", "OW-PERM-003"));
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"step_skipped\""));
            Assert.IsEmpty(fixture.Events);
        }

        // A decision attempted at the deadline applies the expiry rejection
        // first — the attempt itself fails with OW-PERM-003.
        [Test]
        public void ApproveAfterTheDeadlineExpiresTheRun()
        {
            var fixture = NewFixture();
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"approvalTimeoutMinutes\":1}");
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            var approved = fixture.Host.Approvals.Approve(runId, plan.PlanHash);

            Assert.IsFalse(approved.Ok);
            Assert.AreEqual(ErrorCodes.OW_PERM_003, approved.Diagnostics[0].Code);
            Assert.AreEqual(RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"approval_rejected\"",
                    "\"reason\":\"" + OpWeaveApprovalService.ExpiredReason + "\""));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"approval_granted\""));
        }

        // A decision after the sweep already expired the Run answers with
        // the Run's recorded outcome — OW-PERM-003 (SPEC §J: approval not
        // obtained) — and journals nothing.
        [Test]
        public void DecisionAfterExpiryReturnsOwPerm003()
        {
            var fixture = NewFixture();
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"approvalTimeoutMinutes\":1}");
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));

            var approved = fixture.Host.Approvals.Approve(runId, plan.PlanHash);
            var rejected = fixture.Host.Approvals.Reject(runId, plan.PlanHash, "too late");

            Assert.IsFalse(approved.Ok);
            Assert.AreEqual(ErrorCodes.OW_PERM_003, approved.Diagnostics[0].Code);
            Assert.IsFalse(rejected.Ok);
            Assert.AreEqual(ErrorCodes.OW_PERM_003, rejected.Diagnostics[0].Code);
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"approval_"),
                "the expired Run takes no further decision records");
        }

        // ── Non-interactive refusal: batchmode → OW-PERM-004 ─────────────

        [Test]
        public void BatchModeRefusesAnApprovalRunWithOwPerm004()
        {
            var fixture = NewFixture();
            fixture.IsBatchMode = true;
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);

            var accepted = Accept(fixture, plan, doc);

            Assert.IsFalse(accepted.Ok, "a batchmode Editor must refuse an approval Run");
            Assert.AreEqual(ErrorCodes.OW_PERM_004, accepted.Diagnostics[0].Code);
            Assert.IsFalse(
                Directory.Exists(Path.Combine(_root, "Runs")),
                "the refusal happened before any Run side effect");
        }

        // Runs without asks still accept in batchmode — the refusal covers
        // only what no human can decide.
        [Test]
        public void BatchModeStillAcceptsRunsWithoutApprovals()
        {
            var fixture = NewFixture();
            fixture.IsBatchMode = true;
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = Plan(WorkflowId, new[]
            {
                Step(0, "ask", "test.approval.marker", 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            }, Array.Empty<ApprovalRequirement>());
            var doc = AskDoc(WorkflowId);

            var accepted = Accept(fixture, plan, doc);

            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            Assert.AreEqual(RunState.Queued, accepted.Value!.State);
        }

        // A decision call into a non-interactive Editor fails OW-PERM-004 —
        // no journal record is written.
        [Test]
        public void BatchModeDecisionCallFailsOwPerm004()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation("test.approval.marker", fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            fixture.IsBatchMode = true;
            var approved = fixture.Host.Approvals.Approve(runId, plan.PlanHash);
            var rejected = fixture.Host.Approvals.Reject(runId, plan.PlanHash, "no");

            Assert.IsFalse(approved.Ok);
            Assert.AreEqual(ErrorCodes.OW_PERM_004, approved.Diagnostics[0].Code);
            Assert.IsFalse(rejected.Ok);
            Assert.AreEqual(ErrorCodes.OW_PERM_004, rejected.Diagnostics[0].Code);
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Host.Coordinator.StateOf(runId));
            Assert.AreEqual(
                0, RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"approval_"),
                "a batchmode refusal journals nothing");
        }
    }
}
