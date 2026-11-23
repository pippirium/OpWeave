#nullable enable

// T-W2.8-01 [Edit] tests (local:T-W2.8-01:dry-run, SPEC §D8 test): a Dry
// Run validation calls each step's Preflight in execution order with
// pending values (ctx.IsPending(port) with the producing nodeId) for the
// outputs of unexecuted steps, reports the PlannedChange{nodeId, kind,
// target, effect} list with guarantee "none", leaves the project's assets
// and open scenes untouched, reports OW-UNITY-005 for a Prefab creation
// onto an existing path, and refuses with OW-RUN-001 in Play Mode. The
// production entry point — the public WorkflowCompiler constructor with
// no internal seam — performs the same Dry Run on the Edit Mode main
// thread through the bootstrap-registered environment.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.8-01:dry-run — the production compiler's Dry Run mode over
    /// a real <see cref="UnityStepContext"/> factory.
    /// </summary>
    internal sealed class DryRunPreflightTests
    {
        private const string ProbeFolder = "Assets/OpWeaveDryRunProbe";
        private const string OccupiedPath = "Assets/OpWeaveDryRunProbe/Occupied.prefab";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<GameObject> _scratch = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            // One clean untitled scene so leftover unsaved changes can
            // neither enter nor leave this fixture; the probe folder is
            // created per run and deleted in TearDown.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveDryRunTests",
                "t" + Guid.NewGuid().ToString("N"));
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveDryRunProbe");
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            foreach (var go in _scratch)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            _scratch.Clear();
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        private GameObject Scratch(string name)
        {
            var go = new GameObject(name);
            _scratch.Add(go);
            return go;
        }

        // The production compile in DryRun mode: the adapter's
        // UnityWorkflowCompiler.Create — the internal production wiring —
        // wires the snapshot-backed WorkflowCompiler's D8 phase to each
        // step's real UnityStepContext through DryRunStepContextFactory,
        // gated on the Editor state and main-thread probes (SPEC §D8
        // item 3).
        private static ValidationReport DryRun(
            RunnerHostFixture fixture,
            WorkflowDocument doc,
            JsonValue? inputs = null,
            IEditorState? editorState = null)
        {
            var snapshot = fixture.Snapshot();
            var report = UnityWorkflowCompiler.Create(
                    snapshot,
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    fixture.Probe,
                    new StubPlanProvenance(),
                    editorState)
                .Validate(new ValidationRequest(
                    doc, inputs, ValidationMode.DryRun, ActorKind.Agent));
            return report;
        }

        private static WorkflowDocument Doc(
            string id,
            IReadOnlyList<NodeData> nodes,
            IReadOnlyList<ControlEdge> controlEdges,
            IReadOnlyList<DataEdge>? dataEdges = null)
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = id,
                Nodes = new List<NodeData>(nodes),
                ControlEdges = new List<ControlEdge>(controlEdges),
                DataEdges = dataEdges is null ? null : new List<DataEdge>(dataEdges),
            });
            Assert.IsTrue(created.Ok, "fixture workflow document must be valid");
            return created.Value!;
        }

        private static NodeData Node(
            string id, string operation, int version = 1,
            IDictionary<string, JToken>? parameters = null) =>
            new NodeData { Id = id, Operation = operation, Version = version, Parameters = parameters };

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));

        // The "project untouched" evidence: every file under the probe
        // folder (paths + bytes) plus every loaded scene's path/dirty state.
        private static string ProjectState()
        {
            var parts = new List<string>();
            if (Directory.Exists(ProbeFolder))
            {
                foreach (var file in Directory
                             .GetFiles(ProbeFolder, "*", SearchOption.AllDirectories)
                             .OrderBy(p => p, StringComparer.Ordinal))
                {
                    var hash = System.Security.Cryptography.SHA256.Create()
                        .ComputeHash(File.ReadAllBytes(file));
                    parts.Add(file + ":" + BitConverter.ToString(hash));
                }
            }
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                parts.Add($"scene[{i}]={scene.path}|{scene.isDirty}|{scene.rootCount}");
                foreach (var root in scene.GetRootGameObjects())
                {
                    parts.Add("root:" + root.name);
                }
            }
            return string.Join("\n", parts);
        }

        // An Immediate that reports a planned change and records what its
        // Preflight saw — the pending view of SPEC §D8 item 1.
        private sealed class PlanRecordingOperation : IOperation
        {
            private readonly List<string> _calls;
            private readonly string _target;

            internal PlanRecordingOperation(
                string id, List<string> calls, string target,
                IReadOnlyList<PortDescriptor>? inputs = null,
                IReadOnlyList<PortDescriptor>? outputs = null)
            {
                _calls = calls;
                _target = target;
                var descriptor = Op.Define(id, 1)
                    .Summary("Records a planned change.")
                    .Effects(EffectNames.AssetCreate)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
                Descriptor = descriptor with
                {
                    Inputs = inputs ?? descriptor.Inputs,
                    Outputs = outputs ?? descriptor.Outputs,
                };
            }

            public OperationDescriptor Descriptor { get; }
            internal List<bool> PendingSeen { get; } = new List<bool>();
            internal List<string> PendingGetMessages { get; } = new List<string>();
            internal List<string?> ValuesSeen { get; } = new List<string?>();
            internal List<Type> ContextTypes { get; } = new List<Type>();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx)
            {
                _calls.Add(Descriptor.Id);
                ContextTypes.Add(ctx.GetType());
                foreach (var port in Descriptor.Inputs)
                {
                    PendingSeen.Add(ctx.IsPending(port.Name));
                    if (ctx.IsPending(port.Name))
                    {
                        try
                        {
                            ctx.Get<JsonValue>(port.Name);
                            PendingGetMessages.Add("<no exception>");
                        }
                        catch (InvalidOperationException e)
                        {
                            PendingGetMessages.Add(e.Message);
                        }
                    }
                    else
                    {
                        ValuesSeen.Add(ctx.IsPresent(port.Name)
                            ? ctx.Get<JsonValue>(port.Name).CanonicalText
                            : null);
                    }
                }
                return new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    new[]
                    {
                        new PlannedChange(
                            string.Empty, "create", _target, EffectNames.AssetCreate),
                    },
                    JsonValue.Parse("{}").Value!);
            }

            public StepResult Execute(StepContext ctx) => throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) => throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        private static IReadOnlyList<PortDescriptor> Ports(
            params (string name, string type, bool required)[] ports)
        {
            var list = new List<PortDescriptor>();
            foreach (var port in ports)
            {
                list.Add(new PortDescriptor(
                    port.name, port.type, port.required, null, string.Empty, null));
            }
            return list;
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

        // A deterministic IPlanProvenance (the [Core]
        // PlanProvenanceFixtures.Stub answers atom for atom).
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

        // ── SPEC §D8 ────────────────────────────────────────────────────

        // A valid workflow returns each step's planned changes with
        // guarantee "none" — Preflight ran in execution order, the edge-fed
        // port was a pending value naming its producer — and the project is
        // byte-for-byte untouched.
        [Test]
        public void DryRunListsPlannedChangesWithGuaranteeNoneAndTouchesNothing()
        {
            var fixture = NewFixture();
            var calls = new List<string>();
            // Every node — the start node included — carries a recording
            // double so the execution-order assertion observes all three
            // Preflight calls, not only the two PlanRecordingOperations.
            var start = new PlanRecordingOperation(
                "core.start", calls, "Assets/dryrun-start.out");
            fixture.Register(start, 'a');
            var source = new PlanRecordingOperation(
                "test.dryrun.source", calls, "Assets/dryrun-source.out",
                outputs: Ports(("out", "core.String", false)));
            fixture.Register(source, 'c');
            var sink = new PlanRecordingOperation(
                "test.dryrun.sink", calls, "Assets/dryrun-sink.out",
                inputs: Ports(("fed", "core.String", true), ("name", "core.String", true)));
            fixture.Register(sink, 'e');
            var doc = Doc(
                "example.dryrun",
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "test.dryrun.source"),
                    Node("b", "test.dryrun.sink",
                        parameters: new Dictionary<string, JToken>
                        {
                            ["name"] = new JValue("artifact"),
                        }),
                },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[] { new DataEdge(new PortRef("a", "out"), new PortRef("b", "fed")) });
            var before = ProjectState();

            var report = DryRun(fixture, doc);

            Assert.IsTrue(report.Ok, "dry run failed: " + Describe(report.Errors));
            Assert.That(calls, Is.EqualTo(
                new[] { "core.start", "test.dryrun.source", "test.dryrun.sink" }),
                "Preflight must run in execution order (SPEC §D8)");

            // §D8 item 1: the edge-fed port is pending with its producing
            // nodeId; the constant arrived as a present value.
            Assert.That(sink.PendingSeen, Is.EqualTo(new[] { true, false }));
            Assert.That(sink.PendingGetMessages, Has.Count.EqualTo(1));
            Assert.That(sink.PendingGetMessages[0], Does.Contain("'a'"));
            Assert.That(sink.ValuesSeen, Is.EqualTo(new string?[] { "\"artifact\"" }));

            // §D8 items 2-3: every planned change names its step's kind,
            // target and effect, stamped with the step's node id.
            Assert.IsNotNull(report.PlannedChanges);
            Assert.That(report.PlannedChanges!, Has.Count.EqualTo(3));
            Assert.That(report.PlannedChanges![0].NodeId, Is.EqualTo("start"));
            Assert.That(report.PlannedChanges![1].NodeId, Is.EqualTo("a"));
            Assert.That(report.PlannedChanges![2].NodeId, Is.EqualTo("b"));
            foreach (var change in report.PlannedChanges!)
            {
                Assert.That(change.Kind, Is.EqualTo("create"));
                Assert.That(change.Target, Does.StartWith("Assets/dryrun-"));
                Assert.That(change.Effect, Is.EqualTo("asset.create"));
            }
            Assert.That(report.Guarantee, Is.EqualTo("none"));
            Assert.That(ProjectState(), Is.EqualTo(before),
                "a Dry Run must not modify the project");
        }

        // SPEC §D8 test: Prefab creation onto an existing path reports
        // OW-UNITY-005 and changes nothing in the project.
        [Test]
        public void DryRunPrefabCreateOnExistingPathReportsOwUnity005AndNoProjectChange()
        {
            var fixture = NewFixture();
            var calls = new List<string>();
            var start = new RunnerHostFixture.MarkerOperation("core.start", fixture.Events);
            fixture.Register(start, 'a');
            fixture.Register(new PrefabCreateFromGameObjectOperation(), 'c');
            var source = Scratch("DryRunSource");
            // The occupant must be a valid imported Prefab: a foreign
            // file at the .prefab path fails the importer itself, which
            // never reaches the create contract's occupied-path check.
            PrefabUtility.SaveAsPrefabAsset(Scratch("Occupant"), OccupiedPath);
            var doc = Doc(
                "example.dryrun-occupied",
                new[]
                {
                    Node("start", "core.start"),
                    Node("create", "unity.prefab.create_from_gameobject",
                        parameters: new Dictionary<string, JToken>
                        {
                            ["source"] = new RefResolver().ToRef(
                                source, "unity.GameObjectRef").ToToken(),
                            ["path"] = new JValue(OccupiedPath),
                        }),
                },
                new[] { new ControlEdge("start", "create") });
            var before = ProjectState();

            var report = DryRun(fixture, doc);

            Assert.IsFalse(report.Ok);
            var error = report.Errors.SingleOrDefault(
                d => d.Code == ErrorCodes.OW_UNITY_005);
            Assert.IsNotNull(error,
                "expected OW-UNITY-005, got: " + Describe(report.Errors));
            Assert.That(error!.NodeId, Is.EqualTo("create"));
            Assert.That(report.Guarantee, Is.EqualTo("none"));
            Assert.That(ProjectState(), Is.EqualTo(before),
                "the preflight violation must not modify the project");
        }

        // §D8 item 3: in Play Mode the Dry Run answers OW-RUN-001 and no
        // Preflight runs at all.
        [Test]
        public void DryRunInPlayModeReportsOwRun001AndRunsNoPreflight()
        {
            var fixture = NewFixture();
            var calls = new List<string>();
            var start = new RunnerHostFixture.MarkerOperation("core.start", fixture.Events);
            fixture.Register(start, 'a');
            var source = new PlanRecordingOperation(
                "test.dryrun.source", calls, "Assets/dryrun-source.out");
            fixture.Register(source, 'c');
            var playMode = new EditorStateProbe(
                isPlayingOrWillChangePlaymode: () => true,
                isCompiling: () => false,
                isUpdating: () => false,
                isBuildingPlayer: () => false,
                isBatchMode: () => false);
            var doc = Doc(
                "example.dryrun-playmode",
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "test.dryrun.source"),
                },
                new[] { new ControlEdge("start", "a") });
            var before = ProjectState();

            var report = DryRun(fixture, doc, editorState: playMode);

            Assert.IsFalse(report.Ok);
            Assert.IsNotNull(
                report.Errors.SingleOrDefault(d => d.Code == ErrorCodes.OW_RUN_001),
                "expected OW-RUN-001, got: " + Describe(report.Errors));
            Assert.IsEmpty(calls, "no Preflight may run in Play Mode");
            Assert.AreEqual(0, start.PreflightCalls);
            Assert.That(report.Guarantee, Is.EqualTo("none"));
            Assert.That(ProjectState(), Is.EqualTo(before));
        }

        // §D8 item 3: the Unity-facing Dry Run invoked on a background
        // thread answers OW-RUN-001 — the production main-thread gate
        // refuses before any Preflight or Editor flag read can run off the
        // main thread.
        [Test]
        public void DryRunOffMainThreadReportsOwRun001AndRunsNoPreflight()
        {
            var fixture = NewFixture();
            var calls = new List<string>();
            var start = new RunnerHostFixture.MarkerOperation("core.start", fixture.Events);
            fixture.Register(start, 'a');
            var source = new PlanRecordingOperation(
                "test.dryrun.source", calls, "Assets/dryrun-source.out");
            fixture.Register(source, 'c');
            var doc = Doc(
                "example.dryrun-offmain",
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "test.dryrun.source"),
                },
                new[] { new ControlEdge("start", "a") });
            var snapshot = fixture.Snapshot();
            var compiler = UnityWorkflowCompiler.Create(
                snapshot,
                (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                fixture.Probe,
                new StubPlanProvenance());
            var before = ProjectState();

            var report = Task.Run(() => compiler.Validate(new ValidationRequest(
                    doc, null, ValidationMode.DryRun, ActorKind.Agent)))
                .GetAwaiter().GetResult();

            Assert.IsFalse(report.Ok);
            Assert.IsNotNull(
                report.Errors.SingleOrDefault(d => d.Code == ErrorCodes.OW_RUN_001),
                "expected OW-RUN-001, got: " + Describe(report.Errors));
            Assert.IsEmpty(calls, "no Preflight may run off the main thread");
            Assert.AreEqual(0, start.PreflightCalls);
            Assert.That(report.Guarantee, Is.EqualTo("none"));
            Assert.That(ProjectState(), Is.EqualTo(before));
        }

        // §D8 item 3 production-path regression: a caller that constructs
        // the PUBLIC snapshot-backed WorkflowCompiler — no internal seam —
        // on the Edit Mode main thread binds the DryRunEnvironment the
        // adapter bootstrap installed at domain load, so the Dry Run runs
        // each step's Preflight through the real UnityStepContext and
        // reports the planned changes. Before that registration existed
        // this entry point answered OW-RUN-001 and ran no Preflight.
        [Test]
        public void PublicCompilerOnEditModeMainThreadRunsDryRunWithPreflightAndChanges()
        {
            var fixture = NewFixture();
            var calls = new List<string>();
            var start = new PlanRecordingOperation(
                "core.start", calls, "Assets/dryrun-start.out");
            fixture.Register(start, 'a');
            var source = new PlanRecordingOperation(
                "test.dryrun.source", calls, "Assets/dryrun-source.out",
                outputs: Ports(("out", "core.String", false)));
            fixture.Register(source, 'c');
            var sink = new PlanRecordingOperation(
                "test.dryrun.sink", calls, "Assets/dryrun-sink.out",
                inputs: Ports(("fed", "core.String", true), ("name", "core.String", true)));
            fixture.Register(sink, 'e');
            var doc = Doc(
                "example.dryrun-public",
                new[]
                {
                    Node("start", "core.start"),
                    Node("a", "test.dryrun.source"),
                    Node("b", "test.dryrun.sink",
                        parameters: new Dictionary<string, JToken>
                        {
                            ["name"] = new JValue("artifact"),
                        }),
                },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[] { new DataEdge(new PortRef("a", "out"), new PortRef("b", "fed")) });
            var before = ProjectState();

            var report = new WorkflowCompiler(
                    fixture.Snapshot(),
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    fixture.Probe,
                    new StubPlanProvenance())
                .Validate(new ValidationRequest(
                    doc, null, ValidationMode.DryRun, ActorKind.Agent));

            Assert.IsTrue(report.Ok, "dry run failed: " + Describe(report.Errors));
            Assert.That(calls, Is.EqualTo(
                new[] { "core.start", "test.dryrun.source", "test.dryrun.sink" }),
                "Preflight must run in execution order (SPEC §D8)");
            // The bootstrap-registered environment — the adapter's real
            // UnityStepContext, not the Core JSON fallback — built every
            // step's context.
            Assert.That(start.ContextTypes, Is.EqualTo(new[] { typeof(UnityStepContext) }));
            Assert.That(source.ContextTypes, Is.EqualTo(new[] { typeof(UnityStepContext) }));
            Assert.That(sink.ContextTypes, Is.EqualTo(new[] { typeof(UnityStepContext) }));
            // §D8 item 1: the edge-fed port is pending with its producing
            // nodeId; the constant arrived as a present value.
            Assert.That(sink.PendingSeen, Is.EqualTo(new[] { true, false }));
            Assert.That(sink.PendingGetMessages, Has.Count.EqualTo(1));
            Assert.That(sink.PendingGetMessages[0], Does.Contain("'a'"));
            Assert.That(sink.ValuesSeen, Is.EqualTo(new string?[] { "\"artifact\"" }));
            Assert.IsNotNull(report.PlannedChanges);
            Assert.That(report.PlannedChanges!, Has.Count.EqualTo(3));
            Assert.That(report.PlannedChanges![0].NodeId, Is.EqualTo("start"));
            Assert.That(report.PlannedChanges![1].NodeId, Is.EqualTo("a"));
            Assert.That(report.PlannedChanges![2].NodeId, Is.EqualTo("b"));
            Assert.That(report.Guarantee, Is.EqualTo("none"));
            Assert.That(ProjectState(), Is.EqualTo(before),
                "a Dry Run must not modify the project");
        }
    }
}
