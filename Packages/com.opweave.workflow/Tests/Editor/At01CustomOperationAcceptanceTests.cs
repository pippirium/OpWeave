#nullable enable

// T-W2.11-01 [Edit] tests: local:T-W2.11-01:at01-p2-steps — SPEC §T.3 AT-01
// steps 1–3 and 6–9, plus the SPEC §F7 AT-01 half of the change-list tests
// (the custom Operation's change lands in the Run's changes.json).
//
// Step 1: the proj.transform.reset file and OpWeave.Custom.asmdef exist under
// Assets/OpWeave/Editor/Operations/Transform/ and the compiled assembly is a
// project assembly — the Operation was added without touching Core or the
// UPM package. Step 2: a real recompile ends with scriptCompilationFailed
// false. Step 3: a registry rebuild over a store that last saw the world
// without the OpWeave.Custom assembly diffs proj.transform.reset as added,
// origin custom, no error. Steps 6–7: the
// start → unity.gameobject.create → proj.transform.reset → unity.scene.save
// workflow validates ok with no warnings. Step 8: the Run resets the
// Transform and one Undo restores it. Step 9: the step's log record and the
// Modified change are recorded.
//
// The [UnityTest] crosses a real domain reload; state that must survive it
// lives only in constants and files (the RegistryDiffTests pattern).
using System;
using System.Collections;
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
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class At01CustomOperationAcceptanceTests
    {
        private const string OpId = "proj.transform.reset";
        private const string CustomAssemblyName = "OpWeave.Custom";
        private const string OpAssetDir = "Assets/OpWeave/Editor/Operations/Transform";
        private const string OpFileName = "ResetTransformOperation.cs";
        private const string AsmdefFileName = "OpWeave.Custom.asmdef";
        private const string CompileProbeDir = "Assets/OpWeaveAt01CompileProbe";
        private const string SaveProbeDir = "Assets/OpWeaveAt01RunProbe";
        private const string SaveScenePath = SaveProbeDir + "/at01.unity";
        private const string ProbeObjectName = "AT01Probe";
        private const string NodeCreate = "create";
        private const string NodeReset = "reset";

        // The perturbed Transform the test gives the created GameObject between
        // the create and reset steps: non-default on all three axes, so step 8's
        // reset and the one-Undo restore are proven on each of them.
        private static readonly Vector3 s_perturbedPosition = new Vector3(4f, 5f, 6f);
        private static readonly Vector3 s_perturbedEuler = new Vector3(15f, 30f, 45f);
        private static readonly Vector3 s_perturbedScale = new Vector3(2f, 2f, 2f);

        private const string CompileProbeSource =
            "namespace OpWeaveAt01CompileProbe\n{\n" +
            "    internal sealed class Marker\n    {\n    }\n" +
            "}\n";

        // The AT-01 step-6 workflow (SPEC §T.3): core.start →
        // unity.gameobject.create → proj.transform.reset → unity.scene.save.
        private const string AcceptanceWorkflow =
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.at01-acceptance\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"" + NodeCreate + "\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"" + ProbeObjectName + "\",\"position\":{\"x\":1,\"y\":2,\"z\":3}}}," +
            "{\"id\":\"" + NodeReset + "\",\"operation\":\"" + OpId + "\",\"version\":1}," +
            "{\"id\":\"save\",\"operation\":\"unity.scene.save\",\"version\":1," +
            "\"parameters\":{\"path\":\"" + SaveScenePath + "\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"" + NodeCreate + "\"}," +
            "{\"from\":\"" + NodeCreate + "\",\"to\":\"" + NodeReset + "\"}," +
            "{\"from\":\"" + NodeReset + "\",\"to\":\"save\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"" + NodeCreate + "\",\"port\":\"gameObject\"}," +
            "\"to\":{\"node\":\"" + NodeReset + "\",\"port\":\"target\"}}]," +
            "\"outputs\":[{\"id\":\"gameObject\",\"from\":{\"node\":\"" + NodeReset +
            "\",\"port\":\"gameObject\"}}]}";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // The test-owned Library dir, one for the registry-diff store and one
        // run-store subtree — recomputed in the current domain so a UnityTest
        // that crosses a domain reload still finds it.
        private static string TestOwnedDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveAt01Acceptance");

        private static string DiffStoreDir => Path.Combine(TestOwnedDir, "RegistryStore");

        private static string CompileProbeDiskDir => Path.Combine(ProjectRoot, CompileProbeDir);

        // ── AT-01 step 1: the Operation lives in the project, not the package ──

        [Test]
        public void Step1_FileAndAsmdefExist_AndOperationIsProjectSide()
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(ProjectRoot, OpAssetDir, OpFileName)),
                "the proj.transform.reset source file must exist");
            Assert.IsTrue(
                File.Exists(Path.Combine(ProjectRoot, OpAssetDir, AsmdefFileName)),
                "OpWeave.Custom.asmdef must exist next to the Operation");

            var assembly = CompilationPipeline
                .GetAssemblies(AssembliesType.Editor)
                .FirstOrDefault(a =>
                    string.Equals(a.name, CustomAssemblyName, StringComparison.Ordinal));
            Assert.IsNotNull(assembly, "the OpWeave.Custom assembly must compile");
            // "No diff under Packages/com.opweave.workflow" in test terms: every
            // source of the Operation's assembly is a project asset and no
            // package claims the assembly — the UPM package needed no change.
            var opType = TypeCache
                .GetTypesWithAttribute<OpWeaveOperationAttribute>()
                .FirstOrDefault(type => string.Equals(
                    type.Assembly.GetName().Name, CustomAssemblyName,
                    StringComparison.Ordinal));
            Assert.IsNotNull(opType,
                "the compiled OpWeave.Custom assembly must carry the Operation type");
            Assert.IsNull(
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(opType!.Assembly),
                "OpWeave.Custom must be a project assembly, not a package assembly");
            foreach (var source in assembly!.sourceFiles)
            {
                Assert.That(
                    source.Replace('\\', '/'), Does.StartWith("Assets/"),
                    "every OpWeave.Custom source must live under Assets/: " + source);
            }
        }

        // ── AT-01 steps 2–3: compile, then auto-register as added/custom ──

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var dirty = Cleanup();
            if (dirty)
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var dirty = Cleanup();
            if (dirty)
            {
                yield return new RecompileScripts(false);
            }
        }

        private static bool Cleanup()
        {
            var dirty = false;
            if (Directory.Exists(TestOwnedDir))
            {
                Directory.Delete(TestOwnedDir, recursive: true);
            }
            if (AssetDatabase.IsValidFolder(CompileProbeDir))
            {
                AssetDatabase.DeleteAsset(CompileProbeDir);
                dirty = true;
            }
            if (Directory.Exists(CompileProbeDiskDir))
            {
                Directory.Delete(CompileProbeDiskDir, recursive: true);
                dirty = true;
            }
            return dirty;
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Steps2And3_Compile_ThenRegistryDiffShowsAddedCustom()
        {
            // A real source addition drives the recompile the step-2 pass
            // condition measures (an untouched project compiles nothing).
            Directory.CreateDirectory(CompileProbeDiskDir);
            File.WriteAllText(
                Path.Combine(CompileProbeDiskDir, "CompileProbe.cs"), CompileProbeSource);
            AssetDatabase.Refresh();
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

            // Step 2: the project — the probe and the AT-01 Operation alike —
            // compiled clean.
            Assert.IsFalse(
                EditorUtility.scriptCompilationFailed,
                "step 2: scriptCompilationFailed must be false");
            Assert.IsFalse(EditorApplication.isCompiling);

            // Step 3: the build the file addition causes, replayed
            // deterministically — a baseline build that has not seen the
            // OpWeave.Custom assembly, then the production rebuild over the
            // same store, whose diff is exactly the Operation's addition.
            var recorder = new RegistryDiffRecorder(DiffStoreDir, static () => null);
            var withoutCustom = RegistryTypeStage
                .Production(AutoExtensionExclusion.Instance) with
            {
                OperationTypes = () => TypeCache
                    .GetTypesWithAttribute<OpWeaveOperationAttribute>()
                    .Where(type => !string.Equals(
                        type.Assembly.GetName().Name, CustomAssemblyName,
                        StringComparison.Ordinal)),
            };
            var baseline = RegistryBuildHost.Rebuild(
                withoutCustom, RegistryOperationStage.Production(),
                new RegistryPublication(), diffRecorder: recorder);
            Assert.IsFalse(baseline.Stale, "the baseline build must not be stale");
            Assert.IsNull(
                baseline.Snapshot!.Find(OpId, 1),
                "the baseline must not know proj.transform.reset");

            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication(), diffRecorder: recorder);
            Assert.IsFalse(build.Stale, "the registry build must not be stale");

            var diff = build.Diff!;
            Assert.That(diff.PreviousSnapshot, Is.EqualTo("available"),
                "the recorded baseline must back the diff");
            var added = diff.Changes.FirstOrDefault(
                c => c.Kind == RegistryChangeKinds.Added && c.OperationId == OpId);
            Assert.IsNotNull(
                added, "the diff must record proj.transform.reset as added");
            Assert.That(added!.Version, Is.EqualTo(1));
            Assert.That(added.SourceFile, Does.Contain(OpFileName));
            Assert.That(added.Diagnostics, Is.Empty,
                "the added record must carry no error");
            Assert.That(
                diff.Changes.Any(
                    c => c.Kind == RegistryChangeKinds.Failed && c.OperationId == OpId),
                Is.False, "no failed record may name the added Operation");

            var registered = build.Snapshot!.Find(OpId, 1);
            Assert.IsNotNull(registered, "proj.transform.reset must register");
            Assert.That(registered!.Origin, Is.EqualTo(OperationOrigin.Custom));
            Assert.That(registered.AssemblyName, Is.EqualTo(CustomAssemblyName));
            Assert.That(registered.SourceFile, Does.Contain(OpFileName));
            // The failure message must be evaluated eagerly: a lazy () =>
            // would capture build into the iterator's display class, which is
            // allocated only on the first MoveNext entry — after the
            // RecompileScripts domain reload the framework restores only the
            // enumerator PC, so the holder is null and the build assignment
            // itself dies on a NullReferenceException.
            Assert.That(
                build.OperationFailures.Any(
                    f => f.TypeFullName.Contains(CustomAssemblyName)),
                Is.False,
                "registration errors: " + string.Join(
                    "; ", build.OperationFailures.SelectMany(
                        f => f.Diagnostics.Select(d => f.TypeFullName + ": " + d.Code))));
        }

        // ── AT-01 steps 6–9: validate, execute, reset, undo, log, change ──

        private static string RunStoreDir => Path.Combine(TestOwnedDir, "RunStore");

        private SceneSetup[]? _savedSetup;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private static bool s_perturbed;

        [SetUp]
        public void SetUp()
        {
            if (Directory.Exists(RunStoreDir))
            {
                Directory.Delete(RunStoreDir, recursive: true);
            }
            _savedSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!AssetDatabase.IsValidFolder(SaveProbeDir))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveAt01RunProbe");
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            // The run's per-step Undo groups must not replay against the
            // objects the teardown removes.
            Undo.ClearAll();
            if (_savedSetup is not null && _savedSetup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(_savedSetup);
                _savedSetup = null;
            }
            else
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            if (AssetDatabase.IsValidFolder(SaveProbeDir))
            {
                AssetDatabase.DeleteAsset(SaveProbeDir);
            }
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(RunStoreDir);
            _fixtures.Add(fixture);
            return fixture;
        }

        [Test]
        public void Steps6To9_ValidateRunResetUndoLogAndChange()
        {
            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale, "the registry build must not be stale");
            var snapshot = build.Snapshot!;

            var read = WorkflowSerializer.Read(AcceptanceWorkflow);
            Assert.IsTrue(read.Ok, "the AT-01 workflow must parse");

            // Steps 6–7: the JSON workflow validates ok with no warnings.
            var compiler = new WorkflowCompiler(
                snapshot,
                (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                new UnityTypeProbe(),
                UnityPlanProvenance.Capture(IntegrityLevel.Weak, build.Providers));
            var report = compiler.Validate(new ValidationRequest(
                read.Value!, null, ValidationMode.Static, ActorKind.Agent));
            Assert.That(report.Ok, Is.True,
                () => "validation errors: " + string.Join(
                    "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.That(report.Warnings, Is.Empty,
                () => "step 7: no warnings, got " + string.Join(
                    "; ", report.Warnings.Select(d => d.Code + " " + d.Message)));
            Assert.IsNotNull(report.Plan, "a clean validation must build the Plan");
            var resetStep = report.Plan!.Steps.First(s => s.NodeId == NodeReset);

            // Step 8: run the Plan against the real snapshot; when the create
            // step completes, perturb the Transform on every axis so the reset
            // — and the single Undo restoring the recorded state — is real.
            var fixture = NewFixture();
            fixture.SnapshotOverride = snapshot;
            s_perturbed = false;
            Action<StepStateChanged> perturb = change =>
            {
                if (change.NodeId == NodeCreate && change.State == StepState.Succeeded)
                {
                    var go = GameObject.Find(ProbeObjectName);
                    if (go != null)
                    {
                        go.transform.localPosition = s_perturbedPosition;
                        go.transform.localRotation = Quaternion.Euler(s_perturbedEuler);
                        go.transform.localScale = s_perturbedScale;
                        s_perturbed = true;
                    }
                }
            };
            string runId;
            fixture.Host.Events.StepStateChanged += perturb;
            try
            {
                var accepted = fixture.Host.AcceptRun(
                    report.Plan!, read.Value!, ActorKind.Agent, RunnerHostFixture.Options());
                Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
                runId = accepted.Value!.RunId;
                Drive(fixture.Host, runId);
            }
            finally
            {
                fixture.Host.Events.StepStateChanged -= perturb;
            }

            Assert.IsTrue(s_perturbed,
                "the test never perturbed the created GameObject's Transform");
            var target = GameObject.Find(ProbeObjectName);
            Assert.IsNotNull(target, "the created GameObject must exist");
            Assert.AreEqual(Vector3.zero, target!.transform.localPosition,
                "step 8: the reset must zero the position");
            Assert.AreEqual(Quaternion.identity, target.transform.localRotation,
                "step 8: the reset must restore the identity rotation");
            Assert.AreEqual(Vector3.one, target.transform.localScale,
                "step 8: the reset must restore the unit scale");

            // Step 8's other half: exactly one Undo restores the Transform the
            // reset step recorded — SPEC §G3's one step = one Undo group.
            Undo.PerformUndo();
            Assert.AreEqual(s_perturbedPosition, target.transform.localPosition,
                "one Undo must restore the recorded position");
            Assert.That(
                Quaternion.Angle(
                    target.transform.localRotation, Quaternion.Euler(s_perturbedEuler)),
                Is.LessThan(0.01f),
                "one Undo must restore the recorded rotation");
            Assert.AreEqual(s_perturbedScale, target.transform.localScale,
                "one Undo must restore the recorded scale");

            // Step 9: the step's own log record exists — ctx.Log from the
            // custom Operation, bound to the reset step — and its Modified
            // change reached the Run's changes.json (SPEC §F7, AT-01 test).
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    RunStoreDir, runId,
                    "\"type\":\"log\"",
                    "\"step\":" + resetStep.Index.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    "Reset the Transform"),
                Is.True,
                "the reset step must journal its step log");

            var changes = ReadChanges(RunStoreDir, runId);
            var element = changes.OfType<JObject>().FirstOrDefault(
                e => (string?)e["nodeId"] == NodeReset &&
                     (string?)e["action"] == "modified");
            Assert.IsNotNull(
                element,
                "changes.json lists no 'modified' element for the reset step: " +
                changes.ToString());
            Assert.AreEqual(
                resetStep.Index, (int)element!["step"]!,
                "the Modified element must name the reset step");
            Assert.AreEqual("gameObject", (string?)element["target"]?["kind"]);
            Assert.AreEqual("/" + ProbeObjectName, (string?)element["target"]?["path"]);
            Assert.AreEqual(true, (bool)element["undoable"]!,
                "a scene-object change is undoable (SPEC §F7)");
        }

        // ── helpers ─────────────────────────────────────────────────────────

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
                throw new NotSupportedException("the fixture context resolves nothing");
        }

        private static JArray ReadChanges(string root, string runId)
        {
            var path = Path.Combine(root, "Runs", runId, "changes.json");
            Assert.IsTrue(
                File.Exists(path), "the Run Store holds no changes.json for " + runId);
            var parsed = JsonValue.Parse(File.ReadAllText(path));
            Assert.IsTrue(parsed.Ok, "changes.json must parse");
            var token = parsed.Value!.ToToken();
            Assert.IsInstanceOf<JArray>(token, "changes.json is a change array (SPEC §F7)");
            return (JArray)token;
        }

        private static void Drive(OpWeaveRunnerHost host, string runId, int maxTicks = 50)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    Assert.AreEqual(RunState.Succeeded, state, "the Run did not succeed");
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
    }
}
