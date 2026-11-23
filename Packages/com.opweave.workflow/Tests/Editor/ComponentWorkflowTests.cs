#nullable enable

// T-W2.5-03 [Edit] tests: local:T-W2.5-03:create-add-save-workflow — the
// start → unity.gameobject.create → unity.component.add → unity.scene.save
// workflow (P2 completion condition) validated by the production
// WorkflowCompiler and executed by the real RunnerHost: the Run must reach
// Succeeded and the saved scene must contain the GameObject with the
// added Component.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Operations;
using OpWeave.Testing;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.5-03:create-add-save-workflow — an end-to-end Run of the
    /// create-add-save chain over real production objects.
    /// </summary>
    internal sealed class ComponentWorkflowTests
    {
        private const string OperationsAssembly = "OpWeave.Operations";

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
                throw new NotSupportedException("No ValidateStatic reads a port.");
        }

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveComponentWorkflow",
                "t" + Guid.NewGuid().ToString("N"));
            // Every test starts from one clean untitled scene (the
            // OperationTestHarnessTests convention): a scene another test
            // left dirty or closed cannot cascade into this one.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
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
                try { Directory.Delete(_root, recursive: true); }
                catch (Exception) { }
            }
            // Leave the same clean untitled scene the next test's SetUp
            // expects — a dirty or closed scene must not cascade.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private static RegistryBuildResult Build()
        {
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
                seams, RegistryOperationStage.Production(), new RegistryPublication());
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
                Assert.IsTrue(tick.Ok, "Tick failed: " + string.Join("; ",
                    tick.Diagnostics.Select(d => d.Code + " " + d.Message)));
            }
            Assert.Fail("The Run did not reach a terminal state within " + maxTicks + " ticks.");
        }

        [Test]
        public void CreateAddSaveWorkflow_RunSucceeds_AndSavedSceneHoldsTheComponent()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            fixture.Register(new StartOperation(), 'a');
            fixture.Register(new GameObjectCreateOperation(), 'c');
            fixture.Register(new ComponentAddOperation(), 'e');
            fixture.Register(new SaveSceneOperation(), '5');
            fixture.Probe.Know("UnityEngine.Animator");

            using var harness = OperationTestHarness.Create();
            // A first harness object opens the harness-owned temporary
            // scene; the GameObject the chain creates must land in it so
            // the save step persists the same scene the test reopens.
            HarnessObjects.NewGameObject(harness, "SceneAnchor");
            var scene = SceneManager.GetSceneByPath(harness.TempScenePath);
            Assert.That(scene.isLoaded, Is.True, "the harness scene must be open");
            SceneManager.SetActiveScene(scene);

            var read = WorkflowSerializer.Read(
                "{\"schemaVersion\":\"1.0\",\"id\":\"wf.component-add-save\"," +
                "\"title\":\"Create a GameObject, add an Animator, save the scene\"," +
                "\"nodes\":[" +
                "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                "{\"id\":\"create\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
                "\"parameters\":{\"name\":\"FlowRig\"}}," +
                "{\"id\":\"add\",\"operation\":\"unity.component.add\",\"version\":1," +
                "\"parameters\":{\"componentType\":\"UnityEngine.Animator\"}}," +
                "{\"id\":\"save\",\"operation\":\"unity.scene.save\",\"version\":1}]," +
                "\"controlEdges\":[{\"from\":\"start\",\"to\":\"create\"}," +
                "{\"from\":\"create\",\"to\":\"add\"},{\"from\":\"add\",\"to\":\"save\"}]," +
                "\"dataEdges\":[{\"from\":{\"node\":\"create\",\"port\":\"gameObject\"}," +
                "\"to\":{\"node\":\"add\",\"port\":\"target\"}}]}");
            Assert.That(read.Ok, Is.True, "the workflow document must parse");
            var doc = read.Value!;

            var build = Build();
            var report = new WorkflowCompiler(
                    fixture.Snapshot(),
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    fixture.Probe,
                    UnityPlanProvenance.Capture(IntegrityLevel.Weak, build.Providers))
                .Validate(new ValidationRequest(
                    doc, null, ValidationMode.Static, ActorKind.Agent));
            Assert.That(report.Ok, Is.True, "compile failed: " + string.Join("; ",
                report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.That(report.Plan, Is.Not.Null);

            var accepted = fixture.Host.AcceptRun(
                report.Plan!, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.That(accepted.Ok, Is.True, () => accepted.ToString());
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId),
                "the create → add → save Run must succeed");

            // The saved scene — not the live one — must hold the GameObject
            // with the Component: reload it from disk and look inside.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var reloaded = EditorSceneManager.OpenScene(harness.TempScenePath);
            Assert.That(reloaded.isLoaded, Is.True, "the saved scene must reopen");
            GameObject? rig = null;
            foreach (var rootObject in reloaded.GetRootGameObjects())
            {
                if (rootObject.name == "FlowRig")
                {
                    rig = rootObject;
                }
            }
            Assert.That(rig, Is.Not.Null, "the saved scene must contain FlowRig");
            Assert.That(rig!.GetComponent<Animator>(), Is.Not.Null,
                "the saved scene must contain the added Animator");
        }
    }
}
