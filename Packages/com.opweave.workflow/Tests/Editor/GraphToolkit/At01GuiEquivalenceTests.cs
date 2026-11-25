#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.4-03 [Edit] tests: local:T-W5.4-03:at01-gui-equivalence — SPEC
// §T.3 AT-01 through the real GUI path (W5.4-R05) and the v1 completion
// criterion 4 equivalence half (W5.4-R06): the same .opweave.json runs
// once through the real `unity command opweave execute_workflow` CLI
// (com.unity.pipeline 0.9.1-exp.1) and once through the action pipeline
// of OpWeaveGraphActionsWindow — GraphActions.Validate →
// GraphActions.Run → the §H4 item 4 confirmation seam the modal panel
// binds → the §H8 item 3 execution display — each from an identical
// clean project state, and the two persisted Run artifacts must agree.
//
// The window itself is a thin IMGUI shell whose buttons invoke exactly
// these GraphActions methods on the selected graph (the §H8 surface of
// T-W5.4-02), so driving GraphActions IS the real GUI path — the same
// convention GraphActionsDisplayTests established.
//
// Test A drives the unmodified AT-01 chain — core.start →
// unity.gameobject.create → proj.transform.reset → unity.scene.save —
// on a RunnerHostFixture whose settings put the §H4.1 human level of
// scene.modify at "ask": the real custom Operation's own effect then
// produces the plan approvals that route Run through the confirmation
// → approval_granted → display path a human meets when their settings
// ask on scene edits. (Under the SPEC §C4 default table the AT-01 chain
// carries no ask — the fixture supplies the ask-producing policy, never
// a synthetic Operation.)
//
// Test B runs the shared .opweave.json through the spawned `unity` CLI
// and through GraphActions against the PRODUCTION runner host — the same
// Registry, Runner, Run Store and Operations in both halves — then
// compares the persisted SPEC §F1 artifacts member-for-member:
//
//   • plan.json — every member exactly, except planId
//     (Identifiers.NewPlanId() is minted per compile; SPEC §D0.2 makes
//     it audit-only, never a planHash input). planHash, workflowHash,
//     registryHash, steps, fingerprints and the environment identifiers
//     must be equal.
//   • workflow.json — CanonicalWorkflow text: the GUI §H7 export
//     carries a layout member the file does not; the §A1 canonical form
//     drops it, so identical workflows compare equal.
//   • journal.ndjson — line count and every record exactly, after the
//     declared run-scoped identities are scrubbed: envelope ts and crc
//     (wall-clock), data.actor (the transport's own actor — CLI
//     "agent", GUI "human" — asserted BEFORE scrubbing), session-scoped
//     handle members and GlobalObjectId string values inside step data
//     (a freshly created scene object gets a fresh session id; the
//     path/guid identity keys stay compared — except the saved scene
//     asset's guid, which each Run mints fresh), run_accepted's
//     planFileDigest (a hash of the planId-bearing plan bytes), and
//     step_started's inputsDigest on exactly the steps whose plan
//     inputs bind an upstream output (the digest inherits the bound
//     output's session-scoped handle; a constant-input step's digest
//     stays compared). The journal header compares modulo runId;
//     planHash is asserted equal.
//   • changes.json — the SPEC §F7 change list exactly, modulo the
//     session-scoped GlobalObjectId values the change targets carry
//     and the saved scene's asset guid (see below).
//   • state.json / index.json entry — modulo runId and the
//     createdAt/updatedAt wall-clock fields.
//   • the saved scene asset — modulo the Unity-generated fileID/anchor
//     integers a fresh GameObject serializes with. scene.save's
//     OW-UNITY-005 never-overwrite contract keeps the target path free
//     of any baseline asset, so each Run creates the asset and mints
//     its guid itself: that guid is one more run-scoped identity,
//     scrubbed wherever it lands (the §F7 target.guids — the save step
//     emits 'created' and 'saved' entries — and the SceneRef of the
//     step's outputs) after both sides assert it present.
//
// Any other difference — a missing member, a different value, a missing
// file — fails the test. Scrubbed positions are asserted present and
// well-formed so the normalization cannot pass an absent field, and the
// volatile identities (runId, planId) are asserted to DIFFER so the
// equivalence can never pass on a same-run capture.
//
// Below 6000.5 the OpWeave.GraphToolkit asmdef is excluded and this
// whole file compiles to nothing (SPEC §T.1 G-07).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.GraphToolkit;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The AT-01 GUI confirmation and the CLI/GUI artifact-equivalence
    /// checks of SPEC §T.3 and v1 completion criterion 4.
    /// </summary>
    internal sealed class At01GuiEquivalenceTests
    {
        private const string OpId = "proj.transform.reset";
        private const string ProbeObjectName = "AT01Probe";
        private const string NodeCreate = "create";
        private const string NodeReset = "reset";
        private const string NodeSave = "save";

        private const string PipelinePackage = "com.unity.pipeline";

        private const string GuiTestDir = "Assets/OpWeaveAt01Gui";
        private const string GuiGraphPath =
            GuiTestDir + "/at01." + OpWeaveGraph.AssetExtension;
        private const string GuiScenePath = GuiTestDir + "/at01.unity";

        private const string EquivDir = "Assets/OpWeaveAt01Equivalence";
        private const string EquivJsonPath =
            EquivDir + "/equivalence.opweave.json";
        private const string EquivGraphPath =
            EquivDir + "/equivalence." + OpWeaveGraph.AssetExtension;
        private const string EquivScenePath = EquivDir + "/at01.unity";

        private const int CliCallTimeoutMs = 90000;
        private const double CliReadyDeadlineSec = 120.0;
        private const double RunDeadlineSec = 180.0;

        // The perturbed Transform the fixture test gives the created
        // GameObject between the create and reset steps — non-default on
        // all three axes, the AT-01 acceptance pattern.
        private static readonly Vector3 s_perturbedPosition =
            new Vector3(4f, 5f, 6f);
        private static readonly Vector3 s_perturbedEuler =
            new Vector3(15f, 30f, 45f);
        private static readonly Vector3 s_perturbedScale =
            new Vector3(2f, 2f, 2f);

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        // The ConfirmAskEffects call count for the resumable
        // criterion-4 test: a static field is the deliberate shape —
        // a local captured by the callback would live in a display
        // class the domain-reload serializer cannot restore (the
        // <>8__N hole), while the static lives outside the enumerator
        // by design and the count is read only on the domain the
        // callbacks could have fired on.
        private static int s_confirmCalls;

        // The AT-01 acceptance chain (SPEC §T.3), emitted with the
        // workflow id the GUI §H7 compile exports through
        // GraphLiveValidation.BaseDocument ("opweave.live"). The shared
        // .opweave.json carries the same id so the resolved workflows —
        // and with them planHash and the whole plan.json content — are
        // equal by construction: criterion 4 compares the same workflow
        // run through two transports, not each transport's document id.
        private static string WorkflowJson(string scenePath) =>
            "{\"schemaVersion\":\"1.0\",\"id\":\"opweave.live\"," +
            "\"nodes\":[" +
            "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
            "{\"id\":\"" + NodeCreate +
            "\",\"operation\":\"unity.gameobject.create\",\"version\":1," +
            "\"parameters\":{\"name\":\"" + ProbeObjectName +
            "\",\"position\":{\"x\":1,\"y\":2,\"z\":3}}}," +
            "{\"id\":\"" + NodeReset + "\",\"operation\":\"" + OpId +
            "\",\"version\":1}," +
            "{\"id\":\"" + NodeSave +
            "\",\"operation\":\"unity.scene.save\",\"version\":1," +
            "\"parameters\":{\"path\":\"" + scenePath + "\"}}]," +
            "\"controlEdges\":[{\"from\":\"start\",\"to\":\"" + NodeCreate +
            "\"},{\"from\":\"" + NodeCreate + "\",\"to\":\"" + NodeReset +
            "\"},{\"from\":\"" + NodeReset + "\",\"to\":\"" + NodeSave + "\"}]," +
            "\"dataEdges\":[{\"from\":{\"node\":\"" + NodeCreate +
            "\",\"port\":\"gameObject\"},\"to\":{\"node\":\"" + NodeReset +
            "\",\"port\":\"target\"}}]," +
            "\"outputs\":[{\"id\":\"gameObject\",\"from\":{\"node\":\"" +
            NodeReset + "\",\"port\":\"gameObject\"}}]}";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string DescriptorPath =>
            Path.Combine(
                ProjectRoot, "Library", "Pipeline", ".unity-pipeline-port");

        private static string CaptureRoot =>
            Path.Combine(
                ProjectRoot, "Library", "OpWeaveAt01EquivalenceCaptures");

        private static string ArtifactDir =>
            Path.Combine(
                ProjectRoot, "Library", "OpWeaveAt01EquivalenceArtifacts");

        private static string DiskPath(string projectRelative) =>
            Path.Combine(ProjectRoot, projectRelative);

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();
        private Func<RegistrySnapshot?>? _previousSource;
        private Func<Result<RunPolicyEnvironment>?>? _previousPolicy;
        private Func<OpWeaveRunnerHost?>? _previousRunnerHost;
        private Func<RunApprovalDetail, bool>? _previousConfirm;
        private SceneSetup[]? _savedSetup;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library",
                "OpWeaveAt01GuiEquivalence",
                "fixture-" + Guid.NewGuid().ToString("N"));
            _previousSource = GraphToolkitRegistry.SnapshotSource;
            _previousPolicy = GraphLiveValidation.PolicyEnvironment;
            _previousRunnerHost = GraphActions.RunnerHost;
            _previousConfirm = GraphActions.ConfirmAskEffects;
            _savedSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            GraphToolkitRegistry.SnapshotSource = _previousSource!;
            GraphLiveValidation.PolicyEnvironment = _previousPolicy!;
            GraphActions.RunnerHost = _previousRunnerHost!;
            GraphActions.ConfirmAskEffects = _previousConfirm!;
            GraphJsonSync.OpenChoiceProvider = null;
            // The runs' per-step Undo groups must not replay against the
            // objects the teardown removes (the AT-01 pattern).
            Undo.ClearAll();
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
            if (_savedSetup is { Length: > 0 } setup)
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
                _savedSetup = null;
            }
            else
            {
                EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            DeleteAssetDir(GuiTestDir);
            DeleteAssetDir(EquivDir);
            if (Directory.Exists(CaptureRoot))
            {
                Directory.Delete(CaptureRoot, recursive: true);
            }
        }

        // ── fixture-entry drain (T-W2.4-13) ────────────────────────────
        //
        // LiveSnapshot reads the production §C3 stage-1 seam — the real
        // Editor compile state — so a compile a predecessor fixture left
        // queued behind the runner's reload lock (isCompiling still
        // false, IsScriptReloadRequested already armed) hands the first
        // live build a stale stage 1. The UnitySetUp drain below is the
        // fixture-entry half of the At01AgentStepsTests DrainEditorState
        // idiom (T-W2.4-07): a pending compile or requested reload is
        // crossed only at the bounded DrainedScriptReload boundary, and
        // everything else settles on bounded plain frames inside the
        // QuietSettle window — before the test body runs at all, plain
        // [Test] included.
        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the drain
        // reaches it through reflection (the At01AgentStepsTests probe,
        // T-W2.4-07). A reload already armed behind the runner's reload
        // lock surfaces here while isCompiling is false — the deferred
        // state an isCompiling-only check misses.
        private const string ScriptReloadProbeType =
            "UnityEditorInternal.InternalEditorUtility";
        private const string ScriptReloadProbeMethod =
            "IsScriptReloadRequested";

        private static readonly MethodInfo? s_scriptReloadRequested =
            typeof(EditorApplication).Assembly
                .GetType(ScriptReloadProbeType)
                ?.GetMethod(
                    ScriptReloadProbeMethod,
                    BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

        // Non-null and shaped as OneTimeSetUp proved — the fixture does
        // not run at all on a Unity version whose probe is absent.
        private static bool ScriptReloadRequested() =>
            (bool)s_scriptReloadRequested!.Invoke(null, null)!;

        // The state a queued compile/reload reports while the runner's
        // lock holds it: a running script compile always ends in a reload
        // queued behind the lock, and a requested reload is already
        // pending — either is one the drain must cross, never ride into
        // a live build below.
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        // The pending probe the drain trusts is only as strong as its
        // shape: prove it once up front (the At01AgentStepsTests pattern,
        // T-W2.4-07) so the fixture fails here — before any test —
        // instead of silently degrading to an isCompiling-only check
        // that misses a requested reload armed behind the lock.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var probe = s_scriptReloadRequested;
            if (probe is null ||
                !probe.IsStatic ||
                probe.GetParameters().Length != 0 ||
                probe.ReturnType != typeof(bool))
            {
                Assert.Fail(
                    "the domain-reload pending probe is unavailable on " +
                    "this Unity version — the setup cannot prove a " +
                    "deferred reload is drained");
            }
        }

        // The fixture-entry guard: every test — [Test] and [UnityTest]
        // alike — starts on a proven-quiet Editor, so the production
        // stage-1 read in LiveSnapshot can never go stale on a
        // predecessor's queued compile.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            // UTF resumes a reload-crossed setup enumerator at its yield
            // with every local reset — a null drain is that resume, not a
            // fresh start: rebuild the idempotent drain and relay on
            // instead of touching the dead reference.
            IEnumerator? drain = null;
            for (;;)
            {
                drain ??= DrainEditorState(nameof(UnitySetUp));
                if (!drain.MoveNext())
                {
                    break;
                }
                yield return drain.Current;
            }
        }

        // The contract the fixture hands each of its tests: no pending
        // script reload, no compile or import in flight and a clean
        // compile. A pending compile or requested reload is crossed only
        // at DrainedScriptReload — the bounded instruction that unlocks
        // once the pending state is proven — and everything else settles
        // on bounded plain frames. A queued compile surfaces as
        // isCompiling a frame or two after Refresh returns, so the wait
        // only trusts a sustained quiet spell (the QuietSettle
        // convention): quiet flags reset on every busy frame and a
        // periodic Refresh flushes the import queue so a queued compile
        // surfaces inside the window, never behind it. The instruction
        // the nested enumerator produces is yielded DIRECTLY from the
        // setup's own top level through the caller's MoveNext relay — a
        // nested yield would never reach Perform — and the whole
        // sequence is idempotent: a crossed reload resumes the caller's
        // enumerator at its yield with every local reset, so the caller
        // rebuilds the drain when it arrives null rather than relaying a
        // dead reference.
        private static IEnumerator DrainEditorState(string phase)
        {
            AssetDatabase.Refresh();
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var nextRefreshAt = double.NegativeInfinity;
            var quietAt = -1.0;
            for (;;)
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "At01GuiEquivalenceTests." + phase +
                        " timed out after " + WaitTimeoutSec +
                        " s draining the armed editor state " +
                        "(scriptReloadRequested: " + ScriptReloadRequested() +
                        ", isCompiling: " + EditorApplication.isCompiling +
                        ", isUpdating: " + EditorApplication.isUpdating + ")");
                }
                if (ReloadPending())
                {
                    // A running or requested compile ends in a reload
                    // queued behind the runner's lock — cross it at the
                    // bounded sanctioned boundary, never hand it on.
                    quietAt = -1.0;
                    yield return new DrainedScriptReload(
                        phase, WaitTimeoutSec);
                    continue;
                }
                if (EditorApplication.isUpdating)
                {
                    // An import still in flight may surface a compile —
                    // no quiet counts while it runs.
                    quietAt = -1.0;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - quietAt >=
                         QuietSettleSec)
                {
                    break;
                }
                if (EditorApplication.timeSinceStartup >= nextRefreshAt)
                {
                    AssetDatabase.Refresh();
                    nextRefreshAt = EditorApplication.timeSinceStartup + 1.0;
                }
                yield return null;
            }
            AssertDrainedEditorState(phase);
        }

        // The terminal clean-state proof: the project compiled clean, no
        // compile in flight and no script reload still armed behind the
        // runner's lock — a leftover here is this fixture's failure,
        // never a later suite index's.
        private static void AssertDrainedEditorState(string phase)
        {
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "At01GuiEquivalenceTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "At01GuiEquivalenceTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "At01GuiEquivalenceTests." + phase +
                ": a script reload is still pending");
        }

        // The bounded reload-crossing instruction — the
        // WaitForDomainReload.Perform wait with a deadline, run by UTF
        // through the IEditModeTestYieldInstruction interface so
        // ExpectDomainReload arms the runner's reload bookkeeping
        // exactly as with UTF's own instructions. It is yielded only
        // after ReloadPending() proved a real reload is armed, so the
        // unlock below always meets a queued reload and can never strand
        // the lock released on an idle Editor — the failure mode of the
        // unconditional RecompileScripts(false) unlock the drain idiom
        // replaces. The one path where no reload can follow — a failed
        // compile — re-locks before failing.
        private sealed class DrainedScriptReload : IEditModeTestYieldInstruction
        {
            private readonly string m_phase;
            private readonly double m_timeoutSec;

            internal DrainedScriptReload(string phase, double timeoutSec)
            {
                m_phase = phase;
                m_timeoutSec = timeoutSec;
            }

            public bool ExpectDomainReload => true;
            public bool ExpectedPlaymodeState => false;

            public IEnumerator Perform()
            {
                // UTF yields the same first frame: the runner must see
                // the yielded instruction and arm its expected-domain-
                // reload bookkeeping before the reload can start.
                yield return null;

                var deadline =
                    EditorApplication.timeSinceStartup + m_timeoutSec;

                AssetDatabase.Refresh();
                EditorApplication.UnlockReloadAssemblies();

                // Wait out the armed state as WaitForDomainReload.Perform
                // does — while a script reload stays requested or a
                // compile runs — but bounded: the reload itself kills
                // the domain mid-wait, so a deadline expiry can only
                // mean the proven pending state never crossed.
                while (ReloadPending())
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        EditorApplication.LockReloadAssemblies();
                        Assert.Fail(
                            "At01GuiEquivalenceTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "At01GuiEquivalenceTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // ── SPEC §T.3 AT-01 through the GUI actions (W5.4-R05) ──────

        // The unmodified AT-01 chain through the real action pipeline:
        // the fixture's settings put the §H4.1 human scene.modify level
        // at "ask", so the plan carries the create and reset nodes as
        // approval requirements and GraphActions.Run drives the §H4
        // item 4 confirmation → approval_granted → §H8 item 3 display
        // route a human actually takes.
        [Test]
        public void At01_GuiActions_ValidateRunApprove_ResetsTransformAndPaintsDisplay()
        {
            var snapshot = LiveSnapshot();
            var fixture = NewFixture();
            fixture.SnapshotOverride = snapshot;
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"policy\":{\"human\":{\"scene.modify\":\"ask\"}}}");
            GraphToolkitRegistry.SnapshotSource = () => fixture.Snapshot();
            GraphLiveValidation.PolicyEnvironment =
                () => fixture.Host.CurrentRunPolicyEnvironment();
            GraphActions.RunnerHost = () => fixture.Host;
            var confirmCalls = 0;
            RunApprovalDetail? confirmDetail = null;
            GraphActions.ConfirmAskEffects = detail =>
            {
                confirmCalls++;
                confirmDetail = detail;
                return true;
            };

            EnsureAssetFolder(GuiTestDir);
            WithGraph(GuiGraphPath, WorkflowJson(GuiScenePath), graph =>
            {
                var validate = GraphActions.Validate(graph);
                Assert.IsTrue(validate.Ok,
                    "the imported AT-01 graph must validate: " + Describe(validate));

                // Perturb the created GameObject's Transform on every
                // axis the moment the create step lands — the reset, and
                // the one-Undo restore, are only real if the transform
                // actually moved (the AT-01 acceptance pattern).
                var perturbed = false;
                Action<StepStateChanged> perturb = change =>
                {
                    if (change.NodeId == NodeCreate &&
                        change.State == StepState.Succeeded)
                    {
                        var go = GameObject.Find(ProbeObjectName);
                        if (go is not null)
                        {
                            go.transform.localPosition = s_perturbedPosition;
                            go.transform.localRotation =
                                Quaternion.Euler(s_perturbedEuler);
                            go.transform.localScale = s_perturbedScale;
                            perturbed = true;
                        }
                    }
                };
                fixture.Host.Events.StepStateChanged += perturb;
                var runId = "";
                try
                {
                    var run = GraphActions.Run(graph);
                    Assert.IsTrue(run.Ok, "the GUI Run was refused: " + Describe(run));
                    Assert.That(run.RunId, Is.Not.Null.And.Not.Empty,
                        "an accepted Run must name its Run id");
                    runId = run.RunId!;
                    RunUntilTerminal(fixture, runId);
                }
                finally
                {
                    fixture.Host.Events.StepStateChanged -= perturb;
                }

                // SPEC §H4 item 4: one pre-start confirmation listing
                // every ask the Plan carries, decided before a Run id
                // exists — here the create and reset nodes' scene.modify.
                Assert.AreEqual(1, confirmCalls,
                    "the pre-start panel must be asked exactly once");
                Assert.IsNotNull(confirmDetail,
                    "the confirmation received no RunApprovalDetail");
                Assert.AreEqual("", confirmDetail!.RunId,
                    "the panel decides before any Run exists");
                CollectionAssert.AreEquivalent(
                    new[] { NodeCreate, NodeReset },
                    confirmDetail.Nodes.Select(n => n.NodeId).ToList(),
                    "every scene.modify ask — create and reset — must be listed together");
                foreach (var node in confirmDetail.Nodes)
                {
                    CollectionAssert.Contains(
                        node.ApprovalEffects.ToList(), EffectNames.SceneModify);
                }
                Assert.IsTrue(
                    RunnerHostFixture.JournalHas(
                        _root, runId, "approval_granted"),
                    "the panel's approval must land on the journal's " +
                    "human-only path (SPEC §H4 item 3)");

                // The §H8 item 3 display tracked the accepted Run and
                // detached at its terminal state.
                Assert.IsNull(GraphExecutionDisplay.RunIdOf(graph),
                    "a terminal Run detaches the display");
                var states = graph.executionLog
                    .Where(e => e.nodeId == NodeReset)
                    .Select(e => e.state)
                    .ToList();
                CollectionAssert.Contains(states, StepState.Running,
                    "the reset node's Running state never reached the display");
                CollectionAssert.Contains(states, StepState.Succeeded,
                    "the reset node's Succeeded state never reached the display");
                var resetNode = graph.GetNodes()
                    .OfType<OperationNode>()
                    .Single(n => n.nodeId == NodeReset);
                Assert.AreEqual(
                    GraphExecutionDisplay.ColorFor(StepState.Succeeded),
                    resetNode.DefaultColor,
                    "the reset node must carry its terminal step-state colour");

                // The AT-01 effect itself: the perturbed Transform was
                // reset, and one Undo restores the recorded state (SPEC
                // §G3's one step = one Undo group).
                Assert.IsTrue(perturbed,
                    "the test never perturbed the created GameObject's Transform");
                var target = GameObject.Find(ProbeObjectName);
                Assert.IsNotNull(target, "the created GameObject must exist");
                Assert.AreEqual(
                    Vector3.zero, target!.transform.localPosition,
                    "the reset must zero the position");
                Assert.AreEqual(
                    Quaternion.identity, target.transform.localRotation,
                    "the reset must restore the identity rotation");
                Assert.AreEqual(
                    Vector3.one, target.transform.localScale,
                    "the reset must restore the unit scale");
                Undo.PerformUndo();
                Assert.AreEqual(
                    s_perturbedPosition, target.transform.localPosition,
                    "one Undo must restore the recorded position");
                Assert.That(
                    Quaternion.Angle(
                        target.transform.localRotation,
                        Quaternion.Euler(s_perturbedEuler)),
                    Is.LessThan(0.01f),
                    "one Undo must restore the recorded rotation");
                Assert.AreEqual(
                    s_perturbedScale, target.transform.localScale,
                    "one Undo must restore the recorded scale");

                // The custom Operation's own log record and its §F7
                // change reached the Run's journal and changes.json.
                Assert.IsTrue(
                    RunnerHostFixture.JournalHas(
                        _root, runId, "Reset the Transform"),
                    "the reset step must journal its step log");
                var changes = JArray.Parse(
                    File.ReadAllText(
                        Path.Combine(_root, "Runs", runId, "changes.json")));
                var element = changes.OfType<JObject>().FirstOrDefault(
                    e => (string?)e["nodeId"] == NodeReset &&
                         (string?)e["action"] == "modified");
                Assert.IsNotNull(element,
                    "changes.json lists no 'modified' element for the " +
                    "reset step: " + changes.ToString());
                Assert.AreEqual(
                    "gameObject", (string?)element!["target"]?["kind"]);
                Assert.AreEqual(
                    "/" + ProbeObjectName, (string?)element["target"]?["path"]);
                Assert.AreEqual(
                    true, (bool?)element["undoable"],
                    "a scene-object change is undoable (SPEC §F7)");
            });
        }

        // ── v1 criterion 4: same .opweave.json, CLI ≡ GUI artifacts ──

        // The two halves run one document — Assets/OpWeaveAt01Equivalence/
        // equivalence.opweave.json — over the production host from
        // identical clean states: an empty untitled scene plus the shared
        // document, with the unity.scene.save target absent pre-Run —
        // OW-UNITY-005 never lets the step overwrite an existing asset,
        // so each half's Run creates the scene asset (and mints its guid)
        // itself. The graph asset is only the GUI's input vehicle: it is
        // built from that same file through the §H7.2 import and carries
        // no workflow meaning of its own.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CliVsGui_SameWorkflow_IdenticalArtifactsAndChangeLists()
        {
            if (!PackageInstalled(PipelinePackage))
            {
                Assert.Ignore(
                    PipelinePackage + " is not installed — the CLI half " +
                    "of criterion 4 has no transport in this configuration " +
                    "(SPEC §H2; this check requires the Pipeline " +
                    "acceptance environment 0.9.1-exp.1).");
            }
            var host = OpWeaveRunnerHost.Current;
            Assert.IsNotNull(host,
                "the production runner host is not installed in this " +
                "domain — the adapter bootstrap must produce one");
            s_confirmCalls = 0;
            GraphActions.ConfirmAskEffects = static _ =>
            {
                s_confirmCalls++;
                return true;
            };

            yield return WaitAgentServiceReady();

            // The shared input file is the only arrangement the target
            // path needs: an asset already occupying it would trip
            // unity.scene.save's OW-UNITY-005 never-overwrite contract,
            // so the clean state carries no scene asset at all.
            EnsureAssetFolder(EquivDir);
            File.WriteAllText(
                DiskPath(EquivJsonPath), WorkflowJson(EquivScenePath));

            // ── half 1: the real `unity` CLI ─────────────────────────
            NewCleanScene();
            var execute = new CliResult();
            yield return RunCli(
                execute, CliCallTimeoutMs,
                "opweave", "execute_workflow", "--path", EquivJsonPath);
            var executeJson = CliJson(execute, "execute_workflow");
            AssertCliSucceeded(execute, executeJson, "execute_workflow");
            var envelope = AgentEnvelope(executeJson);
            Assert.That(
                envelope["ok"]?.Value<bool>(), Is.True,
                "execute_workflow: " + DescribeEnvelopeErrors(envelope));
            var runIdCli = (string?)AgentData(executeJson)["runId"];
            Assert.That(runIdCli, Is.Not.Null.And.Not.Empty,
                "a successful execute_workflow names the Run");
            yield return AwaitTerminal(host!, runIdCli!);
            var cliDir = CaptureRunDir(host!.RunStoreRoot, runIdCli!, "cli");
            var cliScene = File.ReadAllBytes(DiskPath(EquivScenePath));
            var cliIndex = IndexEntry(host.RunStoreRoot, runIdCli!);

            // ── identical clean state for the GUI half ───────────────
            // The CLI half's Run saved the scene asset: it must be gone
            // again so the GUI half's scene.save meets the same absent
            // target — OW-UNITY-005 counts an orphan .meta or a
            // remembered mapping as occupied too.
            NewCleanScene();
            Assert.IsTrue(
                AssetDatabase.DeleteAsset(EquivScenePath),
                "the CLI half's saved scene asset could not be removed");
            Assert.IsFalse(
                File.Exists(DiskPath(EquivScenePath)),
                "the saved scene file survived the between-halves cleanup");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // ── half 2: the GUI action pipeline ──────────────────────
            var graph = GraphDatabase.CreateGraph<OpWeaveGraph>(EquivGraphPath);
            Assert.IsNotNull(graph,
                "GraphDatabase.CreateGraph must return a graph");
            graph!.OnEnable();
            var import = GraphJsonConverter.Import(
                graph, File.ReadAllText(DiskPath(EquivJsonPath)));
            Assert.IsTrue(import.Ok,
                "the §H7.2 import of the shared .opweave.json failed: " +
                Describe(import.Diagnostics));
            var validate = GraphActions.Validate(graph);
            Assert.IsTrue(validate.Ok,
                "the GUI Validate action failed: " + Describe(validate));
            var run = GraphActions.Run(graph);
            Assert.IsTrue(run.Ok,
                "the GUI Run action was refused: " + Describe(run));
            Assert.That(run.RunId, Is.Not.Null.And.Not.Empty,
                "an accepted GUI Run must name its Run id");
            var runIdGui = run.RunId!;
            Assert.AreEqual(
                runIdGui, GraphExecutionDisplay.RunIdOf(graph),
                "the execution display must track the GUI Run");
            yield return AwaitTerminal(host, runIdGui);
            var guiDir = CaptureRunDir(host.RunStoreRoot, runIdGui, "gui");
            var guiScene = File.ReadAllBytes(DiskPath(EquivScenePath));
            var guiIndex = IndexEntry(host.RunStoreRoot, runIdGui);

            // The GUI half also lands the AT-01 effect and paints the
            // display — the same substance the fixture test asserts.
            Assert.AreEqual(0, s_confirmCalls,
                "the default policy asks nothing on the AT-01 chain — " +
                "an approval here would skew the journals asymmetrically");
            Assert.IsNull(GraphExecutionDisplay.RunIdOf(graph),
                "a terminal Run detaches the display");
            var guiStates = graph.executionLog
                .Where(e => e.nodeId == NodeReset)
                .Select(e => e.state)
                .ToList();
            CollectionAssert.Contains(guiStates, StepState.Running);
            CollectionAssert.Contains(guiStates, StepState.Succeeded);
            var target = GameObject.Find(ProbeObjectName);
            Assert.IsNotNull(target,
                "the GUI run's GameObject must exist");
            Assert.AreEqual(
                Vector3.zero, target!.transform.localPosition,
                "the GUI run must reset the position");
            Assert.AreEqual(
                Vector3.one, target.transform.localScale,
                "the GUI run must reset the scale");

            // ── the criterion-4 comparison ───────────────────────────
            Assert.AreNotEqual(runIdCli, runIdGui,
                "the two runs must be distinct Runs — comparing a Run " +
                "to itself proves nothing");
            CompareRunDirs(cliDir, guiDir, runIdCli!, runIdGui);
            CompareIndexEntries(cliIndex, guiIndex);
            CompareSceneAssets(cliScene, guiScene);
        }

        // ── artifact comparison (SPEC §F1) ───────────────────────────

        // The SPEC §F1 run-artifact set. lock.json is deliberately
        // absent: it is the transient §E2 OS-exclusion residue, not a
        // persisted artifact — a released lock leaves whatever the
        // release timing left behind, so its presence alone is not a
        // workflow difference.
        private static readonly string[] s_runArtifacts =
        {
            "plan.json", "workflow.json", "journal.ndjson",
            "state.json", "changes.json",
        };

        private static void CompareRunDirs(
            string cliDir, string guiDir, string runIdCli, string runIdGui)
        {
            var cliFiles = Directory.GetFiles(cliDir)
                .Select(Path.GetFileName).OrderBy(n => n).ToList();
            var guiFiles = Directory.GetFiles(guiDir)
                .Select(Path.GetFileName).OrderBy(n => n).ToList();
            var cliExtras = cliFiles
                .Where(f => !s_runArtifacts.Contains(f) && f != "lock.json")
                .ToList();
            var guiExtras = guiFiles
                .Where(f => !s_runArtifacts.Contains(f) && f != "lock.json")
                .ToList();
            Assert.AreEqual(
                0, cliExtras.Count + guiExtras.Count,
                "unexpected run artifacts — cli: [" +
                string.Join(", ", cliExtras) + "], gui: [" +
                string.Join(", ", guiExtras) + "]");
            foreach (var name in s_runArtifacts)
            {
                Assert.IsTrue(
                    File.Exists(Path.Combine(cliDir, name)),
                    "the CLI Run Store holds no " + name);
                Assert.IsTrue(
                    File.Exists(Path.Combine(guiDir, name)),
                    "the GUI Run Store holds no " + name);
            }

            ComparePlanJson(
                Path.Combine(cliDir, "plan.json"),
                Path.Combine(guiDir, "plan.json"));
            CompareWorkflowJson(
                Path.Combine(cliDir, "workflow.json"),
                Path.Combine(guiDir, "workflow.json"));
            var volatileDigestSteps = VolatileInputsDigestSteps(
                Path.Combine(cliDir, "plan.json"));
            CompareJournal(
                Path.Combine(cliDir, "journal.ndjson"),
                Path.Combine(guiDir, "journal.ndjson"),
                volatileDigestSteps);
            CompareChanges(
                Path.Combine(cliDir, "changes.json"),
                Path.Combine(guiDir, "changes.json"));
            CompareStateJson(
                Path.Combine(cliDir, "state.json"),
                Path.Combine(guiDir, "state.json"),
                runIdCli, runIdGui);
        }

        // plan.json: byte-canonical equal except planId — the one member
        // SPEC §D0.2 declares a per-compile audit value outside planHash.
        private static void ComparePlanJson(string cliPath, string guiPath)
        {
            var cli = JObject.Parse(File.ReadAllText(cliPath));
            var gui = JObject.Parse(File.ReadAllText(guiPath));
            Assert.AreEqual(
                (string?)cli["planHash"], (string?)gui["planHash"],
                "criterion 4 fails first here: the same workflow must " +
                "compile to the same planHash on both transports");
            var cliId = (string?)cli["planId"];
            var guiId = (string?)gui["planId"];
            Assert.That(cliId, Is.Not.Null.And.Not.Empty,
                "the CLI plan.json carries no planId");
            Assert.That(guiId, Is.Not.Null.And.Not.Empty,
                "the GUI plan.json carries no planId");
            Assert.AreNotEqual(cliId, guiId,
                "two distinct compiles mint distinct planIds — equal " +
                "planIds would mean the same Plan was persisted twice");
            cli.Remove("planId");
            gui.Remove("planId");
            Assert.IsTrue(JToken.DeepEquals(cli, gui),
                "plan.json differs beyond the per-compile planId: " +
                FirstDifference(cli, gui));
        }

        // workflow.json: the §A1 canonical form — the audit copy's only
        // allowed difference is the GUI export's layout member, which
        // canonicalization drops.
        private static void CompareWorkflowJson(string cliPath, string guiPath)
        {
            var cliRead = WorkflowSerializer.Read(File.ReadAllText(cliPath));
            Assert.IsTrue(cliRead.Ok, "the CLI workflow.json must parse");
            var guiRead = WorkflowSerializer.Read(File.ReadAllText(guiPath));
            Assert.IsTrue(guiRead.Ok, "the GUI workflow.json must parse");
            var cliCanon =
                CanonicalWorkflow.From(cliRead.Value!).CanonicalText;
            var guiCanon =
                CanonicalWorkflow.From(guiRead.Value!).CanonicalText;
            Assert.AreEqual(cliCanon, guiCanon,
                "the persisted workflow documents differ canonically");
        }

        // journal.ndjson: identical line count; the header equal modulo
        // runId (planHash asserted equal); every record equal modulo the
        // envelope's ts/crc and the run-scoped identities inside data —
        // including step_started's inputsDigest on exactly the steps
        // <paramref name="volatileDigestSteps"/> names: the digest of
        // resolved inputs is a run-scoped identity when an input binds an
        // upstream output, and stays strictly compared when every input
        // is a workflow constant.
        private static void CompareJournal(
            string cliPath, string guiPath, HashSet<int> volatileDigestSteps)
        {
            var cliLines = NonEmptyLines(File.ReadAllText(cliPath));
            var guiLines = NonEmptyLines(File.ReadAllText(guiPath));
            Assert.AreEqual(cliLines.Count, guiLines.Count,
                "the journals record a different number of lines");

            var cliHeader = JObject.Parse(cliLines[0]);
            var guiHeader = JObject.Parse(guiLines[0]);
            Assert.AreEqual(
                (string?)cliHeader["planHash"],
                (string?)guiHeader["planHash"],
                "the journal headers record different planHashes");
            Assert.AreNotEqual(
                cliHeader["runId"], guiHeader["runId"],
                "the journals must be two distinct Runs");
            cliHeader.Remove("runId");
            guiHeader.Remove("runId");
            Assert.IsTrue(JToken.DeepEquals(cliHeader, guiHeader),
                "journal headers differ beyond runId");

            var scrubbedIds = 0;
            var scrubbedHandles = 0;
            var scrubbedSceneGuids = 0;
            var scrubbedDigests = 0;
            for (var i = 1; i < cliLines.Count; i++)
            {
                var cli = JObject.Parse(cliLines[i]);
                var gui = JObject.Parse(guiLines[i]);
                var type = (string?)cli["type"];
                Assert.AreEqual(type, (string?)gui["type"],
                    "journal line " + i + " carries different types");
                if (type == "run_accepted")
                {
                    Assert.AreEqual(
                        "agent", (string?)cli["data"]?["actor"],
                        "the CLI run must record actor \"agent\"");
                    Assert.AreEqual(
                        "human", (string?)gui["data"]?["actor"],
                        "the GUI run must record actor \"human\"");
                    Assert.IsNotNull(
                        cli["data"]?["planFileDigest"],
                        "the CLI run_accepted carries no planFileDigest");
                    Assert.IsNotNull(
                        gui["data"]?["planFileDigest"],
                        "the GUI run_accepted carries no planFileDigest");
                }
                ScrubVolatile(cli, ref scrubbedIds, ref scrubbedHandles);
                ScrubVolatile(gui, ref scrubbedIds, ref scrubbedHandles);
                var cliGuids = ScrubSavedSceneGuid(cli);
                Assert.AreEqual(
                    cliGuids, ScrubSavedSceneGuid(gui),
                    "journal line " + i + " (" + type + ") does not " +
                    "carry the CLI line's saved-scene guid member count");
                scrubbedSceneGuids += cliGuids;
                // step_started.data.inputsDigest digests the step's
                // resolved inputs (SPEC §F2): an upstream-bound input —
                // reset's create.gameObject Ref — already carries the
                // session-scoped handle/GlobalObjectId the scrubs above
                // normalize, so the digest over it is a run-scoped
                // identity in exactly the same way. Steps outside
                // volatileDigestSteps keep the member compared.
                if (type == "step_started" &&
                    (int?)cli["step"] is { } digestStep &&
                    volatileDigestSteps.Contains(digestStep))
                {
                    foreach (var side in new[] { cli, gui })
                    {
                        var data = side["data"] as JObject;
                        Assert.IsNotNull(data,
                            "journal line " + i + " (" + type +
                            ") carries no data object");
                        Assert.That(
                            (string?)data!["inputsDigest"],
                            Is.Not.Null.And.Not.Empty,
                            "journal line " + i + " (" + type +
                            ", upstream-bound step " + digestStep +
                            ") carries no inputsDigest — scrubbing an " +
                            "absent member would hide a missing record");
                        data!["inputsDigest"] = "<volatile-inputsDigest>";
                    }
                    scrubbedDigests++;
                }
                Assert.IsTrue(JToken.DeepEquals(cli, gui),
                    "journal line " + i + " (" + type +
                    ") differs after the declared run-scoped identities: " +
                    FirstDifference(cli, gui));
            }
            Assert.Greater(scrubbedIds, 0,
                "no GlobalObjectId was normalized — the AT-01 object " +
                "references are missing, not equal");
            Assert.Greater(scrubbedHandles, 0,
                "no session handle was normalized — the step snapshots " +
                "are missing their scene/object handles");
            Assert.AreEqual(3, scrubbedSceneGuids,
                "the saved scene's guid must land exactly three times " +
                "— the scene.save step's SceneRef output plus the " +
                "'created' and 'saved' §F7 change targets inside the " +
                "step_succeeded record");
            Assert.Greater(scrubbedDigests, 0,
                "no step_started inputsDigest was normalized — the " +
                "upstream-bound step's record is missing, not equal");
        }

        // The plan step indexes whose resolved inputs bind an upstream
        // output — the persisted form of the workflow's port bindings:
        // a steps[].inputs entry tagged {"kind":"from_step"} (SPEC §0.6
        // SG-12). Only such a step's resolved inputs can carry
        // session-scoped values (a Ref's handle/GlobalObjectId), so only
        // its step_started inputsDigest is run-scoped. The two plans are
        // asserted equal modulo planId in ComparePlanJson before this
        // runs, so the CLI document speaks for both.
        private static HashSet<int> VolatileInputsDigestSteps(string planPath)
        {
            var steps = JObject.Parse(File.ReadAllText(planPath))["steps"]
                as JArray;
            Assert.IsNotNull(steps, "plan.json carries no steps array");
            var indexes = new HashSet<int>();
            foreach (var token in steps!)
            {
                var step = token as JObject;
                Assert.IsNotNull(step, "a plan.json step is not an object");
                var index = (int?)step!["index"];
                Assert.IsNotNull(index,
                    "a plan.json step carries no index");
                var inputs = step["inputs"] as JObject;
                Assert.IsNotNull(inputs,
                    "plan step " + index + " carries no inputs map");
                if (inputs!.Properties().Any(
                        p => p.Value is JObject tag &&
                             (string?)tag["kind"] == "from_step"))
                {
                    indexes.Add(index!.Value);
                }
            }
            Assert.Greater(indexes.Count, 0,
                "the AT-01 chain binds create.gameObject into " +
                "reset.target — an empty set would mean the plan's " +
                "port bindings were not read");
            return indexes;
        }

        // changes.json: the SPEC §F7 change list, exact modulo the
        // session-scoped GlobalObjectId values and the saved scene's
        // minted asset guid — path, action, step, attempt, undoable and
        // every other guid stay compared.
        private static void CompareChanges(string cliPath, string guiPath)
        {
            var cli = JArray.Parse(File.ReadAllText(cliPath));
            var gui = JArray.Parse(File.ReadAllText(guiPath));
            foreach (var side in new[] { ("cli", cli), ("gui", gui) })
            {
                var element = side.Item2.OfType<JObject>().FirstOrDefault(
                    e => (string?)e["nodeId"] == NodeReset &&
                         (string?)e["action"] == "modified");
                Assert.IsNotNull(element,
                    "the " + side.Item1 +
                    " changes.json lists no 'modified' reset element");
                Assert.AreEqual(
                    "/" + ProbeObjectName,
                    (string?)element!["target"]?["path"]);
                Assert.That(
                    (string?)element["target"]?["globalId"],
                    Is.Not.Null.And.Not.Empty,
                    "the " + side.Item1 +
                    " reset change must carry a GlobalObjectId target");
                // scene.save emits both a 'created' and a 'saved' §F7
                // entry for the new asset — each must be present and
                // carry the fresh guid.
                foreach (var action in new[] { "created", "saved" })
                {
                    var entry = side.Item2.OfType<JObject>().FirstOrDefault(
                        e => (string?)e["nodeId"] == NodeSave &&
                             (string?)e["action"] == action);
                    Assert.IsNotNull(entry,
                        "the " + side.Item1 +
                        " changes.json lists no '" + action +
                        "' scene.save element");
                    Assert.AreEqual(
                        "asset", (string?)entry!["target"]?["kind"]);
                    Assert.AreEqual(
                        EquivScenePath, (string?)entry["target"]?["path"]);
                    Assert.That(
                        (string?)entry["target"]?["guid"],
                        Is.Not.Null.And.Not.Empty,
                        "the " + side.Item1 + " scene.save '" + action +
                        "' change must carry the new asset's guid");
                    Assert.AreEqual(
                        false, (bool?)entry["undoable"],
                        "a scene save is not undoable (SPEC §G5 item 3)");
                }
            }
            var ids = 0;
            var handles = 0;
            ScrubVolatile(cli, ref ids, ref handles);
            ScrubVolatile(gui, ref ids, ref handles);
            var cliGuids = ScrubSavedSceneGuid(cli);
            Assert.AreEqual(
                cliGuids, ScrubSavedSceneGuid(gui),
                "the change lists carry different saved-scene guid " +
                "member counts");
            Assert.AreEqual(2, cliGuids,
                "the saved scene's guid must appear exactly twice — " +
                "the scene.save 'created' and 'saved' change targets");
            Assert.IsTrue(JToken.DeepEquals(cli, gui),
                "the §F7 change lists differ: " + FirstDifference(cli, gui));
        }

        private static void CompareStateJson(
            string cliPath, string guiPath, string runIdCli, string runIdGui)
        {
            var cli = JObject.Parse(File.ReadAllText(cliPath));
            var gui = JObject.Parse(File.ReadAllText(guiPath));
            Assert.AreEqual((string?)cli["runId"], runIdCli);
            Assert.AreEqual((string?)gui["runId"], runIdGui);
            cli.Remove("runId");
            cli.Remove("updatedAt");
            gui.Remove("runId");
            gui.Remove("updatedAt");
            Assert.IsTrue(JToken.DeepEquals(cli, gui),
                "state.json differs: " + FirstDifference(cli, gui));
            Assert.AreEqual("succeeded", (string?)cli["state"],
                "both Runs must end succeeded");
        }

        // The store's index.json entry — workflowId and terminal state
        // must agree; runId and the wall-clock fields are scrubbed.
        private static void CompareIndexEntries(JObject? cli, JObject? gui)
        {
            Assert.IsNotNull(cli, "index.json has no entry for the CLI Run");
            Assert.IsNotNull(gui, "index.json has no entry for the GUI Run");
            Assert.AreEqual(
                "opweave.live", (string?)cli!["workflowId"],
                "the CLI Run must index under the shared workflow id");
            Assert.AreEqual(
                "opweave.live", (string?)gui!["workflowId"],
                "the GUI Run must index under the shared workflow id");
            Assert.AreEqual("succeeded", (string?)cli["state"]);
            Assert.AreEqual("succeeded", (string?)gui["state"]);
            foreach (var entry in new[] { cli, gui })
            {
                entry!.Remove("runId");
                entry.Remove("createdAt");
                entry.Remove("updatedAt");
            }
            Assert.IsTrue(JToken.DeepEquals(cli, gui),
                "index.json entries differ");
        }

        // The saved scene asset: identical content modulo the
        // fileID/anchor integers Unity serializes a fresh GameObject
        // with — the same object on a second clean scene gets fresh ids.
        private static void CompareSceneAssets(byte[] cli, byte[] gui)
        {
            var cliText = NormalizeScene(s_utf8.GetString(cli));
            var guiText = NormalizeScene(s_utf8.GetString(gui));
            Assert.AreEqual(cliText, guiText,
                "the saved scene assets differ beyond Unity-generated " +
                "fileID/anchor integers");
        }

        private static string NormalizeScene(string text) =>
            Regex.Replace(
                Regex.Replace(text, @"fileID: -?\d+", "fileID: <id>"),
                @"&-?\d+", "&<id>");

        // The declared run-scoped identities of the §F2/§F7 formats:
        // envelope ts/crc (wall-clock), run_accepted's actor (the
        // transport, asserted before this runs) and planFileDigest (a
        // digest over the planId-bearing bytes), the session-scoped
        // handle members of the §G2 snapshots, the §E3 wait tokens
        // (absent from this workflow but scrubbed for symmetry), and
        // every GlobalObjectId string value — the same scene object
        // serializes a fresh id on each fresh scene. Everything else —
        // names, paths, guids, messages, seq, type, step, attempt,
        // session — stays compared. step_started's inputsDigest is
        // deliberately absent: it is run-scoped only on the steps
        // VolatileInputsDigestSteps names, so CompareJournal scrubs it
        // per record — a blanket removal would mask a digest difference
        // on a constant-input step.
        private static readonly HashSet<string> s_volatileKeys =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ts", "crc", "actor", "planFileDigest", "handle",
                "resumeToken", "deadline", "waitingFor",
            };

        private static void ScrubVolatile(
            JToken token, ref int idsScrubbed, ref int handlesScrubbed)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    if (s_volatileKeys.Contains(property.Name))
                    {
                        if (property.Name == "handle")
                        {
                            handlesScrubbed++;
                        }
                        property.Remove();
                        continue;
                    }
                    ScrubVolatile(property.Value, ref idsScrubbed, ref handlesScrubbed);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    ScrubVolatile(item, ref idsScrubbed, ref handlesScrubbed);
                }
            }
            else if (token is JValue value &&
                     value.Type == JTokenType.String &&
                     value.Value<string>() is { } text &&
                     text.StartsWith("GlobalObjectId_V1-", StringComparison.Ordinal))
            {
                value.Value = "<volatile-globalId>";
                idsScrubbed++;
            }
        }

        // The one guid the strict comparison cannot keep: OW-UNITY-005
        // keeps a baseline asset off the scene.save target, so each
        // half's Run creates the asset and mints a fresh guid — a
        // run-scoped identity like the GlobalObjectIds. It is scrubbed
        // only where it can legitimately land — a {path, guid} member
        // pair whose path IS the save target (the §F7 change targets
        // of the save's 'created' and 'saved' entries, and the
        // SceneRef of the step's outputs); every other guid
        // stays compared.
        private static int ScrubSavedSceneGuid(JToken token)
        {
            var count = 0;
            if (token is JObject obj)
            {
                if ((string?)obj["path"] == EquivScenePath &&
                    obj["guid"] is { } guid)
                {
                    Assert.That(
                        guid.Type == JTokenType.String &&
                        ((string?)guid)!.Length > 0,
                        "the saved scene's guid member is missing or " +
                        "empty — scrubbing it would hide absent evidence");
                    guid.Replace("<volatile-sceneGuid>");
                    count++;
                }
                foreach (var property in obj.Properties().ToList())
                {
                    count += ScrubSavedSceneGuid(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    count += ScrubSavedSceneGuid(item);
                }
            }
            return count;
        }

        // The first structural difference between two compared tokens —
        // kept short for the assertion message.
        private static string FirstDifference(JToken a, JToken b)
        {
            var path = FirstDifferencePath(a, b, "$");
            return "first difference at " + path +
                "\ncli: " + a.ToString(Newtonsoft.Json.Formatting.None) +
                "\ngui: " + b.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string FirstDifferencePath(JToken a, JToken b, string path)
        {
            if (a.Type != b.Type)
            {
                return path + " (type " + a.Type + " vs " + b.Type + ")";
            }
            if (a is JObject ao && b is JObject bo)
            {
                var names = ao.Properties().Select(p => p.Name)
                    .Union(bo.Properties().Select(p => p.Name));
                foreach (var name in names)
                {
                    var av = ao[name];
                    var bv = bo[name];
                    if (av is null || bv is null)
                    {
                        return path + "." + name +
                            (av is null ? " (cli missing)" : " (gui missing)");
                    }
                    if (!JToken.DeepEquals(av, bv))
                    {
                        return FirstDifferencePath(av, bv, path + "." + name);
                    }
                }
                return path;
            }
            if (a is JArray aa && b is JArray ba)
            {
                if (aa.Count != ba.Count)
                {
                    return path + " (count " + aa.Count + " vs " + ba.Count + ")";
                }
                for (var i = 0; i < aa.Count; i++)
                {
                    if (!JToken.DeepEquals(aa[i], ba[i]))
                    {
                        return FirstDifferencePath(aa[i], ba[i], path + "[" + i + "]");
                    }
                }
                return path;
            }
            return path + " (" + a + " vs " + b + ")";
        }

        // ── captures ─────────────────────────────────────────────────

        private static string CaptureRunDir(
            string storeRoot, string runId, string label)
        {
            var source = Path.Combine(storeRoot, "Runs", runId);
            Assert.IsTrue(Directory.Exists(source),
                "the Run Store holds no directory for " + runId);
            var target = Path.Combine(CaptureRoot, label);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            DirectoryCopy(source, target);
            return target;
        }

        private static void DirectoryCopy(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var dir in Directory.GetDirectories(
                source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(
                    dir.Replace(source, target));
            }
            foreach (var file in Directory.GetFiles(
                source, "*", SearchOption.AllDirectories))
            {
                File.Copy(
                    file, file.Replace(source, target), overwrite: true);
            }
        }

        private static JObject? IndexEntry(string storeRoot, string runId)
        {
            var indexPath = Path.Combine(storeRoot, "index.json");
            Assert.IsTrue(File.Exists(indexPath), "the Run Store holds no index.json");
            var root = JObject.Parse(File.ReadAllText(indexPath));
            var runs = root["runs"] as JArray ?? new JArray();
            var entry = runs.OfType<JObject>().FirstOrDefault(
                e => (string?)e["runId"] == runId);
            Assert.IsNotNull(entry,
                "index.json carries no entry for " + runId);
            return (JObject?)entry!.DeepClone();
        }

        private static List<string> NonEmptyLines(string text) =>
            text.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();

        // ── fixture / scene helpers ──────────────────────────────────

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        private static RegistrySnapshot LiveSnapshot()
        {
            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale,
                "the live registry build must not be stale");
            Assert.IsNotNull(build.Snapshot,
                "the live registry must publish a snapshot");
            Assert.IsNotNull(build.Snapshot!.Find(OpId, 1),
                "the AT-01 custom Operation must be registered");
            return build.Snapshot;
        }

        private static void RunUntilTerminal(
            RunnerHostFixture fixture, string runId)
        {
            for (var i = 0; i < 50; i++)
            {
                var state = fixture.Host.Coordinator.StateOf(runId);
                if (state is RunState.Succeeded or RunState.Failed
                    or RunState.Cancelled or RunState.NeedsReview)
                {
                    Assert.AreEqual(RunState.Succeeded, state,
                        "the AT-01 GUI Run did not succeed (final " + state + ")");
                    return;
                }
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok,
                    "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("the Run did not reach a terminal state within 50 ticks");
        }

        // The production host ticks on EditorApplication.update — each
        // yield pumps a frame until the Run reports terminal.
        private static IEnumerator AwaitTerminal(
            OpWeaveRunnerHost host, string runId)
        {
            var deadline =
                EditorApplication.timeSinceStartup + RunDeadlineSec;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state is RunState.Succeeded or RunState.Failed
                    or RunState.Cancelled or RunState.NeedsReview)
                {
                    Assert.AreEqual(RunState.Succeeded, state,
                        "Run " + runId +
                        " did not succeed (final " + state + ")");
                    yield break;
                }
                yield return null;
            }
            Assert.Fail(
                "Run " + runId + " never reached a terminal state");
        }

        private static void NewCleanScene()
        {
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.IsNull(GameObject.Find(ProbeObjectName),
                "the clean state must not carry a leftover AT01Probe");
        }

        private static void EnsureAssetFolder(string dir)
        {
            if (!AssetDatabase.IsValidFolder(dir))
            {
                AssetDatabase.CreateFolder(
                    "Assets", dir.Substring("Assets/".Length));
            }
        }

        private static void DeleteAssetDir(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir) ||
                Directory.Exists(DiskPath(dir)))
            {
                AssetDatabase.DeleteAsset(dir);
            }
        }

        private void WithGraph(
            string assetPath, string workflowJson, Action<OpWeaveGraph> body)
        {
            var graph = GraphDatabase.CreateGraph<OpWeaveGraph>(assetPath);
            var created = graph is not null;
            try
            {
                Assert.IsNotNull(graph,
                    "GraphDatabase.CreateGraph must return a graph");
                graph!.OnEnable();
                var import = GraphJsonConverter.Import(graph, workflowJson);
                Assert.IsTrue(import.Ok,
                    "the §H7.2 import of the AT-01 .opweave.json failed: " +
                    Describe(import.Diagnostics));
                body(graph!);
            }
            finally
            {
                if (created)
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
            }
        }

        private static string Describe(GraphActionResult result) =>
            result.Message + " [" + string.Join(
                "; ", result.Diagnostics.Select(
                    d => d.Code + " " + d.Message)) + "]";

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
            string.Join(
                "; ", diagnostics.Select(d => d.Code + " " + d.Message));

        private static bool PackageInstalled(string name)
        {
            try
            {
                return UnityEditor.PackageManager.PackageInfo
                    .FindForPackageName(name) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ── the spawned `unity` CLI (the CliSmokeAtf01Tests plumbing) ──

        private sealed class CliResult
        {
            internal int ExitCode = -1;
            internal string Stdout = "";
            internal string Stderr = "";
            internal bool TimedOut;

            internal string Describe() =>
                "exit " + ExitCode + (TimedOut ? " (timed out)" : "") +
                "\nstdout: " + Stdout + "\nstderr: " + Stderr;
        }

        // The `unity` CLI executable: an owner-installed Windows app
        // alias on PATH (WindowsApps) — resolved explicitly so the
        // failure names what was searched rather than surfacing as an
        // opaque Process.Start miss.
        private static string FindUnityCli()
        {
            var candidates = new List<string>();
            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var entry in path!.Split(Path.PathSeparator))
                {
                    if (entry.Length != 0)
                    {
                        candidates.Add(Path.Combine(entry, "unity.exe"));
                        candidates.Add(Path.Combine(entry, "unity"));
                    }
                }
            }
            var localAppData =
                Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            var windowsApps = Path.Combine(
                localAppData, "Microsoft", "WindowsApps");
            if (localAppData.Length != 0)
            {
                candidates.Add(Path.Combine(windowsApps, "unity.exe"));
            }
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // An unreadable PATH entry is skipped, not fatal.
                }
            }
            Assert.Fail(
                "the `unity` CLI executable was not found on PATH or " +
                "under " + windowsApps + " — it is a required part of " +
                "the local:T-W5.4-03 acceptance environment");
            throw new InvalidOperationException("unreachable");
        }

        // Spawns `unity` with the global non-interactive/JSON flags plus
        // <paramref name="arguments"/>, pumps Editor frames while it runs
        // (a blocking WaitForExit would starve the main-thread commands
        // the call itself invokes), and returns the captured streams.
        private IEnumerator RunCli(
            CliResult result, int timeoutMs, params string[] arguments)
        {
            var cli = FindUnityCli();
            var psi = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.StandardOutputEncoding = s_utf8;
            psi.StandardErrorEncoding = s_utf8;
            psi.Environment["UNITY_NON_INTERACTIVE"] = "1";
            psi.Environment["UNITY_FORMAT"] = "json";
            psi.ArgumentList.Add("command");
            psi.ArgumentList.Add("--json");
            psi.ArgumentList.Add("--no-banner");
            psi.ArgumentList.Add("--non-interactive");
            psi.ArgumentList.Add("--quiet");
            psi.ArgumentList.Add("--color");
            psi.ArgumentList.Add("never");
            psi.ArgumentList.Add("--project-path");
            psi.ArgumentList.Add(ProjectRoot);
            for (var index = 0; index < arguments.Length; index++)
            {
                Assert.That(
                    arguments[index], Is.Not.Null,
                    "the `unity` CLI argument at index " + index +
                    " is null" +
                    (index > 0
                        ? " (the value after " + arguments[index - 1] + ")"
                        : ""));
                psi.ArgumentList.Add(arguments[index]);
            }
            var process = Process.Start(psi)!;
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var deadline =
                    EditorApplication.timeSinceStartup + timeoutMs / 1000.0;
                while (!process.HasExited &&
                       EditorApplication.timeSinceStartup < deadline)
                {
                    yield return null;
                }
                if (!process.HasExited)
                {
                    result.TimedOut = true;
                    try { process.Kill(); }
                    catch (Exception) { /* already gone */ }
                    process.WaitForExit(15000);
                }
                if (process.HasExited)
                {
                    result.ExitCode = process.ExitCode;
                }
                result.Stdout =
                    stdout.Wait(10000) ? stdout.Result : "";
                result.Stderr =
                    stderr.Wait(10000) ? stderr.Result : "";
            }
            finally
            {
                process.Dispose();
            }
        }

        // The CLI's --json envelope {success, command, data, errors,
        // warnings}: the first '{' onward parses as one object. A body
        // that does not parse is persisted to an artifact file before
        // the failure is raised: the next reader needs the bytes.
        private static JObject CliJson(CliResult result, string what)
        {
            var brace = result.Stdout.IndexOf('{');
            Assert.That(brace, Is.GreaterThanOrEqualTo(0),
                what + ": the CLI printed no JSON body — " +
                result.Describe());
            try
            {
                return JObject.Parse(result.Stdout.Substring(brace));
            }
            catch (Newtonsoft.Json.JsonException parse)
            {
                var artifact = PersistCliArtifact(result, what);
                Assert.Fail(
                    what + ": the CLI stdout is not parseable JSON (" +
                    parse.Message + ") — raw capture persisted at " +
                    artifact + " — " + result.Describe());
                throw new InvalidOperationException("unreachable");
            }
        }

        private static int s_cliArtifactSeq;

        private static string PersistCliArtifact(CliResult result, string what)
        {
            try
            {
                Directory.CreateDirectory(ArtifactDir);
                var path = Path.Combine(
                    ArtifactDir,
                    "cli-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") +
                    "-" + (++s_cliArtifactSeq).ToString("D2") +
                    ".stdout.txt");
                File.WriteAllText(
                    path,
                    "unity command " + what + "\n" + result.Describe(),
                    s_utf8);
                return path;
            }
            catch (Exception)
            {
                return "<artifact write failed>";
            }
        }

        private static void AssertCliSucceeded(
            CliResult result, JObject envelope, string what)
        {
            Assert.That(result.TimedOut, Is.False,
                what + ": the `unity` CLI call timed out — " +
                result.Describe());
            Assert.That(envelope["success"]?.Value<bool>(), Is.True,
                what + ": the CLI call failed — " + result.Describe());
        }

        // The AgentApiResponse {ok, data, errors, warnings} the command
        // handler returned — wherever the exec result nests it inside
        // the CLI envelope, the object carrying a boolean "ok" IS the
        // response envelope.
        private static JObject AgentEnvelope(JObject root)
        {
            if (root["ok"] is JValue { Type: JTokenType.Boolean })
            {
                return root;
            }
            foreach (var token in root.Descendants().OfType<JObject>())
            {
                if (token["ok"] is JValue { Type: JTokenType.Boolean })
                {
                    return token;
                }
            }
            throw new InvalidOperationException(
                "no AgentApi envelope ({ok:...}) inside the CLI reply: " +
                root.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static JObject AgentData(JObject root)
        {
            var envelope = AgentEnvelope(root);
            var data = envelope["data"] as JObject;
            Assert.That(data, Is.Not.Null,
                "the AgentApi envelope carries no data object: " +
                envelope.ToString(Newtonsoft.Json.Formatting.None));
            return data!;
        }

        private static string DescribeEnvelopeErrors(JObject envelope) =>
            string.Join(
                "; ",
                ((envelope["errors"] as JArray) ?? new JArray())
                    .Select(e => (string?)e["code"] + " " + (string?)e["message"]));

        // The loopback descriptor the CLI itself reads to find this
        // Editor — present and carrying a port+token means the server
        // published itself.
        private static bool ServerDescriptorPresent()
        {
            try
            {
                if (!File.Exists(DescriptorPath))
                {
                    return false;
                }
                var descriptor = JObject.Parse(
                    File.ReadAllText(DescriptorPath));
                return descriptor["port"]?.Value<int>() > 0 &&
                    !string.IsNullOrEmpty(
                        descriptor["evalToken"]?.ToString());
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The Agent service readiness check through the real CLI: ready
        // means get_registry_status answers ok:true AND a published,
        // non-stale snapshot rides the data. The only refusal the loop
        // tolerates is OW-API-003 while the domain install lands, plus
        // the ~0.6 s EDITOR_NOT_RUNNING window a reload leaves.
        private IEnumerator WaitAgentServiceReady()
        {
            var deadline =
                EditorApplication.timeSinceStartup + CliReadyDeadlineSec;
            var last = "";
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (!ServerDescriptorPresent())
                {
                    last = "no pipeline descriptor yet";
                    yield return null;
                    continue;
                }
                var result = new CliResult();
                yield return RunCli(
                    result, CliCallTimeoutMs,
                    "opweave", "get_registry_status");
                var body = "";
                var brace = result.Stdout.IndexOf('{');
                if (brace >= 0)
                {
                    body = result.Stdout.Substring(brace);
                }
                if (!result.TimedOut && body.Contains("\"OW-API-003\""))
                {
                    last = "OW-API-003 — service install not landed";
                    yield return null;
                    continue;
                }
                JObject? reply = null;
                if (brace >= 0)
                {
                    try { reply = JObject.Parse(body); }
                    catch (Exception) { reply = null; }
                }
                JObject? envelope = null;
                if (reply is not null)
                {
                    try { envelope = AgentEnvelope(reply); }
                    catch (InvalidOperationException) { envelope = null; }
                }
                if (reply is not null &&
                    reply["success"]?.Value<bool>() == true &&
                    envelope is not null &&
                    envelope["ok"]?.Value<bool>() == true)
                {
                    var data = envelope["data"] as JObject;
                    if (data is not null &&
                        !string.IsNullOrEmpty(
                            (string?)data["generationId"]) &&
                        data["stale"]?.Value<bool>() == false)
                    {
                        yield break;
                    }
                    last = "get_registry_status answered ok:true " +
                        "before a snapshot published (no " +
                        "generationId yet, or still stale)";
                }
                else if (envelope is not null &&
                         envelope["ok"]?.Value<bool>() == false)
                {
                    last = DescribeEnvelopeErrors(envelope);
                }
                else
                {
                    last = result.Describe();
                }
                yield return null;
            }
            Assert.Fail(
                "the Agent service never came up behind the CLI: " +
                last);
        }
    }
}
#endif
