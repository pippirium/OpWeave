#nullable enable

// T-W3.2-05 [Edit] tests: local:T-W3.2-05:agent-e2e-three-groups — SPEC §H1, §1.1.
// The Agent end-to-end flow of SPEC §1.1: an EditMode test drives AgentApi
// directly — search_operations → describe_operation → validate_workflow →
// execute_workflow → get_run_status — for one workflow that combines the
// three P2 Operation groups (W2.5 Scene/GameObject, W2.6 Prefab, W2.7 Asset).
// The service is constructed over a RunnerHostFixture with a real production
// Registry build and the live §G1 requirement probes (the fixture's
// LiveRequirements seam), the same seams the production installer binds —
// no Pipeline boundary, no synthetic Operation stand-ins. Every asset the
// workflow creates lives under one unique Assets/ folder the teardown
// removes, and every wait is bounded by a deadline.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    [TestFixture]
    internal sealed class AgentApiThreeGroupE2ETests
    {
        // The deadline for the run-drive loop — generous for a slow CI box;
        // every operation in the workflow is Immediate and finishes in one
        // tick, so reaching this bound is a failure, not a slow pass.
        private const double DeadlineSeconds = 120.0;
        private const int MaxTicks = 1000;
        private const string ApprovalHintText =
            "Approval is required in the Unity Run Viewer.";

        private string _root = string.Empty;
        private string _tag = string.Empty;
        private string _assetsRoot = string.Empty;
        private string _baseFolder = string.Empty;
        private SceneSetup[]? _savedSetup;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<RunStatusTracker> _trackers = new List<RunStatusTracker>();

        [SetUp]
        public void SetUp()
        {
            _tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot,
                "Library", "OpWeaveAgentE2ETests", "run-" + _tag);
            _assetsRoot = "Assets/OpWeaveAgentE2E_" + _tag;
            if (AssetDatabase.IsValidFolder(_assetsRoot))
            {
                AssetDatabase.DeleteAsset(_assetsRoot);
            }
            _savedSetup = EditorSceneManager.GetSceneManagerSetup();
            // A saved, clean base scene is the state the additive
            // unity.scene.create step needs: an unsaved untitled scene
            // still loaded refuses it OW-UNITY-011, so the fixture saves
            // the fresh scene to a temp path — the same starting state
            // the merged scene-Operation tests establish.
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _baseFolder = "Assets/OpWeaveAgentE2EBase_" + _tag;
            if (!AssetDatabase.IsValidFolder(_baseFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveAgentE2EBase_" + _tag);
            }
            Assert.That(
                EditorSceneManager.SaveScene(scene, _baseFolder + "/Base.unity"),
                Is.True,
                "the fixture's base scene must save");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var tracker in _trackers)
            {
                tracker.Dispose();
            }
            _trackers.Clear();
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
            // The run's per-step Undo groups must not replay against the
            // scenes and assets the teardown removes.
            Undo.ClearAll();
            RestoreSavedSetup();
            if (_assetsRoot.Length != 0 && AssetDatabase.IsValidFolder(_assetsRoot))
            {
                AssetDatabase.DeleteAsset(_assetsRoot);
            }
            var physical = Path.Combine(RunnerHostFixture.ProjectRoot, _assetsRoot);
            if (Directory.Exists(physical))
            {
                Directory.Delete(physical, recursive: true);
            }
            if (_baseFolder.Length != 0 && AssetDatabase.IsValidFolder(_baseFolder))
            {
                AssetDatabase.DeleteAsset(_baseFolder);
            }
            var basePhysical = Path.Combine(RunnerHostFixture.ProjectRoot, _baseFolder);
            if (Directory.Exists(basePhysical))
            {
                Directory.Delete(basePhysical, recursive: true);
            }
            AssetDatabase.Refresh();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        // ── fixture-entry drain (T-W2.4-13) ────────────────────────────
        //
        // BuildService drives a real production Registry build, which
        // reads the §C3 stage-1 live Editor compile state — so a compile
        // a predecessor fixture left queued behind the runner's reload
        // lock (isCompiling still false, IsScriptReloadRequested already
        // armed) hands the first live build a stale stage 1. The
        // UnitySetUp drain below is the fixture-entry half of the
        // At01AgentStepsTests DrainEditorState idiom (T-W2.4-07): a
        // pending compile or requested reload is crossed only at the
        // bounded DrainedScriptReload boundary, and everything else
        // settles on bounded plain frames inside the QuietSettle
        // window — before the test body runs at all.
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

        // The fixture-entry guard: every test starts on a proven-quiet
        // Editor, so the production stage-1 read inside the live build
        // can never go stale on a predecessor's queued compile.
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
                        "AgentApiThreeGroupE2ETests." + phase +
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
                "AgentApiThreeGroupE2ETests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "AgentApiThreeGroupE2ETests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "AgentApiThreeGroupE2ETests." + phase +
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
                            "AgentApiThreeGroupE2ETests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "AgentApiThreeGroupE2ETests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // ── the §1.1 completion flow over the three P2 groups ────────────────

        [UnityTest]
        public IEnumerator ThreeP2Groups_RunToSucceeded_ThroughTheFiveCommands()
        {
            var api = BuildService(out var host);

            // 1. search_operations discovers every Operation the workflow
            // uses, one query per group the plan names.
            AssertSearchFinds(api,
                new SearchOperationsRequest(Category: "Scene", Limit: 50),
                "unity.scene.create", "unity.scene.save");
            AssertSearchFinds(api,
                new SearchOperationsRequest(Category: "GameObject", Limit: 50),
                "unity.gameobject.create");
            AssertSearchFinds(api,
                new SearchOperationsRequest(Category: "Prefab", Limit: 50),
                "unity.prefab.create_from_gameobject");
            AssertSearchFinds(api,
                new SearchOperationsRequest(Category: "Asset", Limit: 50),
                "unity.asset.create_folder", "unity.asset.create_material");

            // 2. describe_operation answers the full contract for each of
            // them — the port names and requirements the workflow wires.
            AssertDescribedPorts(api, "unity.asset.create_folder",
                inputs: new[] { "path" }, outputs: new[] { "path" });
            AssertDescribedPorts(api, "unity.scene.create",
                inputs: new[] { "path" }, outputs: new[] { "scene" });
            AssertDescribedPorts(api, "unity.gameobject.create",
                inputs: new[] { "name", "scene" }, outputs: new[] { "gameObject" });
            AssertDescribedPorts(api, "unity.prefab.create_from_gameobject",
                inputs: new[] { "source", "path" }, outputs: new[] { "prefab", "instance" });
            AssertDescribedPorts(api, "unity.asset.create_material",
                inputs: new[] { "path", "shader" }, outputs: new[] { "material" });
            AssertDescribedPorts(api, "unity.scene.save",
                inputs: new[] { "scene" }, outputs: new[] { "scene" });

            // 3. validate_workflow compiles the schema-v1 document the Agent
            // assembled: one folder → one scene → one GameObject in it → one
            // Prefab saved from that GameObject → one Material → one save.
            var workflow = ThreeGroupWorkflow(_assetsRoot);
            var validated = api.ValidateWorkflow(
                new ValidateWorkflowRequest(workflow, Path: null));
            AssertOk(validated, "validate_workflow");
            var summary = (JObject)validated.Data!;
            var planHash = (string?)summary["planHash"];
            Assert.IsNotNull(planHash,
                "validate_workflow must answer the planHash of the compiled Plan");
            var approvals = summary["approvals"] as JArray;
            Assert.IsNotNull(approvals, "the Plan summary must carry the approvals list");
            Assert.That(approvals!, Has.Count.EqualTo(0),
                "this workflow needs no human approval — every effect is allowed");

            // 4. execute_workflow admits the Run under the planHash the
            // validate call just computed (OW-RUN-003 stays clean).
            var executed = api.ExecuteWorkflow(
                new ExecuteWorkflowRequest(
                    workflow, Path: null, Inputs: null, ExpectedPlanHash: planHash));
            AssertOk(executed, "execute_workflow");
            var runId = (string?)((JObject)executed.Data!)["runId"];
            Assert.IsNotNull(runId, "execute_workflow must answer the runId");

            // 5. get_run_status polls the journaled §E8 snapshot until the
            // Run ends — bounded by the deadline above.
            var deadline = EditorApplication.timeSinceStartup + DeadlineSeconds;
            var ticks = 0;
            JObject? status = null;
            var state = string.Empty;
            for (;;)
            {
                var polled = api.GetRunStatus(new GetRunStatusRequest(runId));
                AssertOk(polled, "get_run_status");
                status = (JObject)polled.Data!;
                state = (string?)status["state"] ?? string.Empty;
                if (state is "succeeded" or "failed" or "cancelled" or "needs_review")
                {
                    break;
                }
                if (++ticks > MaxTicks || EditorApplication.timeSinceStartup > deadline)
                {
                    Assert.Fail(
                        "the Run did not reach a terminal state before the deadline: " +
                        status.ToString(Newtonsoft.Json.Formatting.None));
                }
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "host tick failed: " + Describe(tick.Diagnostics));
                yield return null;
            }
            Assert.AreEqual("succeeded", state,
                "the Run must succeed: " + status!.ToString(Newtonsoft.Json.Formatting.None));

            var statusSteps = status["steps"] as JArray;
            Assert.IsNotNull(statusSteps, "the status must carry the step list");
            foreach (var step in statusSteps!)
            {
                Assert.AreEqual("succeeded", (string?)((JObject)step!)["state"],
                    "step must succeed: " + step.ToString(Newtonsoft.Json.Formatting.None));
            }
            Assert.That(status["outputs"] is JObject outputs && outputs.Count > 0,
                "the terminal outputs carry the published artifacts");

            // The workflow's durable evidence: the Run's §F6/F7 journal.
            var logs = api.GetRunLogs(new GetRunLogsRequest(runId));
            AssertOk(logs, "get_run_logs");
            // SPEC §H3 item 1: nextCursor and truncated appear only
            // together on a cut page.
            var wire = logs.ToWire();
            Assert.AreEqual(
                wire["nextCursor"] is JValue { Value: string },
                wire["truncated"] is JValue { Value: bool cut } && cut,
                "a truncated page must carry its nextCursor (SPEC §H3 item 3)");
            var logData = (JObject)logs.Data!;
            Assert.AreEqual(runId, (string?)logData["runId"],
                "get_run_logs must answer for the requested Run");
            // SPEC §F6: the log list is always served, but no clause makes
            // a clean Run hold entries — every source (Runner, ctx.Log,
            // Console Warning 以上) fires only on demand, and none of the
            // standard Operations here logs on success. Shape, not count.
            var logEntries = logData["logs"] as JArray;
            Assert.IsNotNull(logEntries, "get_run_logs must answer the §F6 log list");
            foreach (var item in logEntries!)
            {
                var entry = item as JObject;
                Assert.IsNotNull(entry, "every §F6 log entry must be an object");
                Assert.IsNotNull(entry!["ts"], "a §F6 log entry must carry ts");
                Assert.IsNotNull(entry["level"], "a §F6 log entry must carry level");
                Assert.IsNotNull(entry["message"], "a §F6 log entry must carry message");
            }
            var changes = logData["changes"] as JArray;
            Assert.IsNotNull(changes, "the §F7 change list must be present");
            Assert.That(changes!, Has.Count.GreaterThan(0),
                "the §F7 change list must record what the Run created");
            var changedPaths = changes!
                .Select(c => (string?)((JObject)c!)["target"]?["path"])
                .Where(p => p is not null)
                .ToList();
            foreach (var artifact in new[]
            {
                _assetsRoot + "/E2EScene.unity",
                _assetsRoot + "/E2EProbe.prefab",
                _assetsRoot + "/E2EMaterial.mat",
            })
            {
                Assert.That(changedPaths, Does.Contain(artifact),
                    "the change list must record the artifact " + artifact);
            }
            Assert.That(
                RunnerHostFixture.JournalHas(
                    host.RunStoreRoot, runId!, "\"type\":\"run_finished\""),
                Is.True,
                "the journal must record the run_finished record");

            // The artifacts the §F7 change list reported exist on disk.
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<SceneAsset>(_assetsRoot + "/E2EScene.unity"),
                "the created scene asset must exist");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(_assetsRoot + "/E2EProbe.prefab"),
                "the created Prefab asset must exist");
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<Material>(_assetsRoot + "/E2EMaterial.mat"),
                "the created Material asset must exist");
            var created = EditorSceneManager.GetSceneByPath(_assetsRoot + "/E2EScene.unity");
            Assert.IsTrue(created.IsValid() && created.isLoaded,
                "the created scene must still be loaded");
            var probe = created.GetRootGameObjects()
                .FirstOrDefault(g => g.name == "E2EProbe");
            Assert.IsNotNull(probe, "the created scene must contain the created GameObject");
            Assert.IsTrue(
                UnityEditor.PrefabUtility.IsAnyPrefabInstanceRoot(probe!),
                "the GameObject must be the connected Prefab instance");
        }

        // ── the §H4 approval boundary of item 2 ─────────────────────────────

        [Test]
        public void ApprovalRequiringNode_StopsAwaitingApproval_AgentCannotApprove()
        {
            var api = BuildService(out var host);

            var workflow = ApprovalWorkflow(_assetsRoot);

            // validate_workflow reports the approval the Plan requires —
            // the asset.delete effect is Ask for the Agent under §H4.
            var validated = api.ValidateWorkflow(
                new ValidateWorkflowRequest(workflow, Path: null));
            AssertOk(validated, "validate_workflow");
            var summary = (JObject)validated.Data!;
            var planHash = (string?)summary["planHash"];
            Assert.IsNotNull(planHash, "validate_workflow must answer a planHash");
            var approvals = summary["approvals"] as JArray;
            Assert.IsNotNull(approvals,
                "the Plan summary must carry the approvals list");
            Assert.That(approvals!, Has.Count.EqualTo(1),
                "the Plan must carry the single delete approval: " +
                summary.ToString(Newtonsoft.Json.Formatting.None));
            var approval = (JObject)approvals![0]!;
            Assert.AreEqual("gone", (string?)approval["nodeId"]);
            Assert.AreEqual("asset.delete", (string?)approval["effect"]);

            // execute_workflow admits the Run — and it stops in
            // AwaitingApproval before any step, as §H4 requires.
            var executed = api.ExecuteWorkflow(
                new ExecuteWorkflowRequest(
                    workflow, Path: null, Inputs: null, ExpectedPlanHash: planHash));
            AssertOk(executed, "execute_workflow");
            var execData = (JObject)executed.Data!;
            Assert.AreEqual("awaiting_approval", (string?)execData["state"],
                "an approval-bearing Plan must enter AwaitingApproval at admission");
            var runId = (string?)execData["runId"];
            Assert.IsNotNull(runId, "execute_workflow must answer the runId");

            // get_run_status shows the pending approval and the GUI hint —
            // the only approval surface the Agent ever sees.
            var status = api.GetRunStatus(new GetRunStatusRequest(runId));
            AssertOk(status, "get_run_status");
            var statusData = (JObject)status.Data!;
            Assert.AreEqual("awaiting_approval", (string?)statusData["state"]);
            Assert.That(statusData["approvals"] as JArray, Is.Not.Null.And.Count.GreaterThan(0),
                "the status must list the pending approval");
            Assert.AreEqual(ApprovalHintText, (string?)statusData["hint"],
                "the hint must direct the Agent to the Unity Run Viewer");

            // Nothing the Agent can call moves the Run forward: no tick,
            // no status read, no command admits the step. The fixture host
            // is driven three times to prove the wait is real, not a race.
            for (var i = 0; i < 3; i++)
            {
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "host tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.AreEqual(RunState.AwaitingApproval, host.Coordinator.StateOf(runId!),
                "the Run must still be waiting for a human");
            var again = api.GetRunStatus(new GetRunStatusRequest(runId));
            Assert.AreEqual("awaiting_approval",
                (string?)((JObject)again.Data!)["state"]);

            // SPEC §H4 item 5: the Agent surface has no approve/reject or
            // NeedsReview-resolution member at all — the Run cannot be
            // self-approved because no such command exists to call.
            var offenders = typeof(AgentApiService)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.Name)
                .Where(n =>
                    n.Contains("Approv", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("Reject", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("Resolve", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("Review", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("Decide", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.That(offenders, Is.Empty,
                "AgentApiService must expose no approval or review decision member: " +
                string.Join(", ", offenders));

            // SPEC §0.6 SG-14: the Agent's own cancel command cannot end the
            // approval wait either — cancel_workflow accepts only
            // Queued/Running/Waiting/Suspended, and AwaitingApproval is
            // refused OW-RUN-014 without touching state, approval or journal.
            var cancelled = api.CancelWorkflow(new CancelWorkflowRequest(runId));
            Assert.IsFalse(cancelled.Ok,
                "cancel_workflow on AwaitingApproval must be refused");
            var refusal = (JObject)cancelled.ToWire()["errors"]![0]!;
            Assert.AreEqual("OW-RUN-014", (string?)refusal["code"]);
            Assert.AreEqual("error", (string?)refusal["severity"]);
            Assert.AreEqual(false, (bool?)refusal["retryable"]);
            Assert.AreEqual(RunState.AwaitingApproval, host.Coordinator.StateOf(runId!),
                "the refused cancel must leave the Run waiting for a human");

            // The sanctioned end is the human decision: the approval service
            // the Run Viewer drives rejects the pending approval (SPEC §E2
            // IRunCoordinator.Reject — human only), and the Run ends
            // cancelled without having run a step.
            var rejected = host.Approvals.Reject(runId!, planHash!, "e2e: not approved");
            Assert.IsTrue(rejected.Ok, "the human reject must succeed");
            var ended = api.GetRunStatus(new GetRunStatusRequest(runId));
            AssertOk(ended, "get_run_status");
            Assert.AreEqual("cancelled", (string?)((JObject)ended.Data!)["state"],
                "the rejected approval wait must end as cancelled");
        }

        // A setup entry is only restorable when it names a scene asset: an
        // untitled scene (empty path) cannot be reopened, so a pathless
        // entry is dropped — and when nothing restorable remains, the blank
        // state the untitled scenes stood for is one fresh empty scene.
        // RestoreSceneManagerSetup also refuses a setup with no active
        // scene, which dropping the untitled entries can produce — the same
        // restore discipline OperationTestHarness follows.
        private void RestoreSavedSetup()
        {
            var restorable = new List<SceneSetup>();
            if (_savedSetup is { } setup)
            {
                for (var i = 0; i < setup.Length; i++)
                {
                    if (!string.IsNullOrEmpty(setup[i].path))
                    {
                        restorable.Add(setup[i]);
                    }
                }
            }
            _savedSetup = null;
            if (restorable.Count == 0)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                return;
            }
            var hasActive = false;
            for (var i = 0; i < restorable.Count; i++)
            {
                var entry = restorable[i];
                entry.isActive = !hasActive && entry.isLoaded;
                hasActive |= entry.isActive;
                restorable[i] = entry;
            }
            if (!hasActive)
            {
                var entry = restorable[0];
                entry.isLoaded = true;
                entry.isActive = true;
                restorable[0] = entry;
            }
            EditorSceneManager.RestoreSceneManagerSetup(restorable.ToArray());
        }

        // ── the fixture and the service under test ───────────────────────────

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root)
            {
                LiveRequirements = true,
            };
            _fixtures.Add(fixture);
            return fixture;
        }

        // The production backends bound to the fixture's seams — the same
        // wiring AgentApiInstaller.Install performs over the production
        // host, except the catalog reads the build this test made rather
        // than the ambient publication (SPEC §H1 test 1 — no Pipeline).
        private AgentApiService BuildService(out OpWeaveRunnerHost host)
        {
            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale,
                "the production Registry build must not be stale: " +
                Describe(build.SettingsDiagnostics));
            Assert.IsNotNull(build.Snapshot, "the production Registry build must publish a snapshot");
            var fixture = NewFixture();
            fixture.SnapshotOverride = build.Snapshot;
            host = fixture.Host;
            var runnerHost = host;
            var root = runnerHost.RunStoreRoot;
            var tracker = new RunStatusTracker(
                runnerHost.Events,
                runId => runnerHost.Store.Load(runId),
                () => runnerHost.Store.List(new RunQuery()));
            _trackers.Add(tracker);
            return new AgentApiService(
                runnerHost.Store,
                new AgentApiCursorCodec(new byte[32]),
                runnerHost.Coordinator.Cancel,
                runStatus: tracker,
                runStoreRoot: root,
                debugLogging: () =>
                    fixture.LoadSettings() is { Ok: true, Value: { } settings } &&
                    settings.DebugLogging,
                readChanges: runId =>
                {
                    var records = JournaledChanges.Read(
                        JournaledChanges.PathOf(root, runId), out var failure);
                    return failure is null
                        ? Result<IReadOnlyList<ChangeRecord>>.Success(records)
                        : Result<IReadOnlyList<ChangeRecord>>.Failure(
                            ErrorCodes.Create(ErrorCodes.OW_STORE_002, failure));
                },
                isDeletedRun: runnerHost.Store.IsDeletedRun,
                workflows: new UnityAgentWorkflowBackend(
                    RunnerHostFixture.ProjectRoot,
                    runnerHost.CurrentRunPolicyEnvironment,
                    runnerHost.CurrentRegistry,
                    () => build.Providers,
                    runnerHost.AcceptRun,
                    typeProbe: new UnityTypeProbe(),
                    editorState: fixture.Editor),
                catalog: new UnityAgentCatalogBackend(
                    RunnerHostFixture.ProjectRoot,
                    WorkflowSchemaPath(),
                    () => fixture.Snapshot(),
                    () => false,
                    () => null,
                    () => build.SettingsDiagnostics,
                    () => "en"));
        }

        // The shipped §A2 artifact's resolved path — the same resolution the
        // production installer performs (PackageInfo first, the development
        // layout as the fallback).
        private static string WorkflowSchemaPath()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(AgentApiService).Assembly);
            var packageRoot = info?.resolvedPath;
            return Path.Combine(
                string.IsNullOrEmpty(packageRoot)
                    ? Path.Combine(RunnerHostFixture.ProjectRoot, "Packages", "com.opweave.workflow")
                    : packageRoot!,
                "Schemas~", "opweave-1.0.schema.json");
        }

        // ── the workflow documents ───────────────────────────────────────────

        // The schema-v1 workflow an Agent assembles from the described
        // contracts: one asset folder, one scene in it, one GameObject in
        // the scene, one Prefab saved from that GameObject, one Material,
        // and the scene save that persists them — the three P2 groups in
        // one completed Run. All artifact paths live under the unique
        // <paramref name="root"/> the teardown deletes.
        private static JObject ThreeGroupWorkflow(string root)
        {
            var scenePath = root + "/E2EScene.unity";
            var prefabPath = root + "/E2EProbe.prefab";
            var materialPath = root + "/E2EMaterial.mat";
            return new JObject
            {
                ["schemaVersion"] = "1.0",
                ["id"] = "example.agent-e2e-three-groups",
                ["title"] = "Agent E2E: scene + prefab + asset in one run",
                ["nodes"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "start", ["operation"] = "core.start", ["version"] = 1,
                    },
                    new JObject
                    {
                        ["id"] = "folder",
                        ["operation"] = "unity.asset.create_folder",
                        ["version"] = 1,
                        ["parameters"] = new JObject { ["path"] = root },
                    },
                    new JObject
                    {
                        ["id"] = "scene",
                        ["operation"] = "unity.scene.create",
                        ["version"] = 1,
                        ["parameters"] = new JObject
                        {
                            ["path"] = scenePath,
                            ["setup"] = "empty",
                            ["mode"] = "additive",
                        },
                    },
                    new JObject
                    {
                        ["id"] = "go",
                        ["operation"] = "unity.gameobject.create",
                        ["version"] = 1,
                        ["parameters"] = new JObject { ["name"] = "E2EProbe" },
                    },
                    new JObject
                    {
                        ["id"] = "prefab",
                        ["operation"] = "unity.prefab.create_from_gameobject",
                        ["version"] = 1,
                        ["parameters"] = new JObject
                        {
                            ["path"] = prefabPath,
                            ["connect"] = true,
                        },
                    },
                    new JObject
                    {
                        ["id"] = "material",
                        ["operation"] = "unity.asset.create_material",
                        ["version"] = 1,
                        ["parameters"] = new JObject
                        {
                            ["path"] = materialPath,
                            ["shader"] = "Standard",
                        },
                    },
                    new JObject
                    {
                        ["id"] = "save",
                        ["operation"] = "unity.scene.save",
                        ["version"] = 1,
                    },
                },
                ["controlEdges"] = new JArray
                {
                    Edge("start", "folder"),
                    Edge("folder", "scene"),
                    Edge("scene", "go"),
                    Edge("go", "prefab"),
                    Edge("prefab", "material"),
                    Edge("material", "save"),
                },
                ["dataEdges"] = new JArray
                {
                    Edge("scene", "scene", "go", "scene"),
                    Edge("go", "gameObject", "prefab", "source"),
                    Edge("scene", "scene", "save", "scene"),
                },
                // SPEC §A4: node, input and output ids share one uniqueness
                // scope inside the workflow, so each output id must differ
                // from every node id — only the from.port names the source.
                ["outputs"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "createdPrefab",
                        ["from"] = new JObject { ["node"] = "prefab", ["port"] = "prefab" },
                    },
                    new JObject
                    {
                        ["id"] = "prefabInstance",
                        ["from"] = new JObject { ["node"] = "prefab", ["port"] = "instance" },
                    },
                    new JObject
                    {
                        ["id"] = "createdMaterial",
                        ["from"] = new JObject { ["node"] = "material", ["port"] = "material" },
                    },
                    new JObject
                    {
                        ["id"] = "savedScene",
                        ["from"] = new JObject { ["node"] = "save", ["port"] = "scene" },
                    },
                },
            };
        }

        // The approval-bearing counterpart: create the folder and a Material
        // in it, then delete the Material — unity.asset.delete is Ask for
        // the Agent under the §H4 default policy, so the Plan carries an
        // approval the admission gate parks before any step runs.
        private static JObject ApprovalWorkflow(string root)
        {
            return new JObject
            {
                ["schemaVersion"] = "1.0",
                ["id"] = "example.agent-e2e-approval",
                ["title"] = "Agent E2E: an approval the Agent cannot grant",
                ["nodes"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "start", ["operation"] = "core.start", ["version"] = 1,
                    },
                    new JObject
                    {
                        ["id"] = "folder",
                        ["operation"] = "unity.asset.create_folder",
                        ["version"] = 1,
                        ["parameters"] = new JObject { ["path"] = root },
                    },
                    new JObject
                    {
                        ["id"] = "mat",
                        ["operation"] = "unity.asset.create_material",
                        ["version"] = 1,
                        ["parameters"] = new JObject
                        {
                            ["path"] = root + "/ToDelete.mat",
                            ["shader"] = "Standard",
                        },
                    },
                    new JObject
                    {
                        ["id"] = "gone",
                        ["operation"] = "unity.asset.delete",
                        ["version"] = 1,
                    },
                },
                ["controlEdges"] = new JArray
                {
                    Edge("start", "folder"),
                    Edge("folder", "mat"),
                    Edge("mat", "gone"),
                },
                ["dataEdges"] = new JArray
                {
                    Edge("mat", "material", "gone", "asset"),
                },
            };
        }

        private static JObject Edge(string from, string to) =>
            new JObject { ["from"] = from, ["to"] = to };

        private static JObject Edge(
            string fromNode, string fromPort, string toNode, string toPort) =>
            new JObject
            {
                ["from"] = new JObject { ["node"] = fromNode, ["port"] = fromPort },
                ["to"] = new JObject { ["node"] = toNode, ["port"] = toPort },
            };

        // ── assertions ───────────────────────────────────────────────────────

        private static void AssertOk(AgentApiResponse response, string command)
        {
            Assert.IsTrue(response.Ok,
                command + " failed: " + Describe(response.Errors) +
                (response.Data is null
                    ? string.Empty
                    : " — data: " + response.Data!.ToString(Newtonsoft.Json.Formatting.None)));
        }

        private static void AssertSearchFinds(
            AgentApiService api, SearchOperationsRequest request, params string[] expectedIds)
        {
            var response = api.SearchOperations(request);
            AssertOk(response, "search_operations " + (request.Category ?? request.Query ?? ""));
            var items = (JArray)((JObject)response.Data!)["items"]!;
            var ids = items.Select(i => (string?)((JObject)i!)["id"]).ToList();
            foreach (var expected in expectedIds)
            {
                Assert.That(ids, Does.Contain(expected),
                    "search_operations must find " + expected);
            }
        }

        private static void AssertDescribedPorts(
            AgentApiService api, string operationId,
            string[] inputs, string[] outputs)
        {
            var response = api.DescribeOperation(
                new DescribeOperationRequest(operationId));
            AssertOk(response, "describe_operation " + operationId);
            var data = (JObject)response.Data!;
            Assert.AreEqual(operationId, (string?)data["id"]);
            var describedInputs = PortNames(data, "inputs");
            var describedOutputs = PortNames(data, "outputs");
            foreach (var port in inputs)
            {
                Assert.That(describedInputs, Does.Contain(port),
                    operationId + " must describe the input port " + port);
            }
            foreach (var port in outputs)
            {
                Assert.That(describedOutputs, Does.Contain(port),
                    operationId + " must describe the output port " + port);
            }
        }

        private static List<string> PortNames(JObject data, string member)
        {
            var ports = data[member] as JArray;
            Assert.IsNotNull(ports, "describe_operation must answer the " + member + " list");
            return ports!
                .Select(p => (string?)((JObject)p!)["name"])
                .Where(n => n is not null)
                .Cast<string>()
                .ToList();
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message));
    }
}
