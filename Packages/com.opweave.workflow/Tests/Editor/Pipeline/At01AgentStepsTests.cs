#nullable enable
// T-W3.7-01 [Edit] tests: local:T-W3.7-01:at01-p3-steps — SPEC §T.3 AT-01
// steps 4, 5, 9 (the API side) and 10, driven through the Agent entry
// point: every command rides the pipeline server's published loopback —
// POST /api/exec {"command":"opweave <name>","parameters":{...}} is the
// wire `unity command opweave` itself produces (the L-01 probe verified
// the `unity` CLI issues exactly these requests). The assertions read the
// §H3 envelope inside the exec result, so a business failure is checked
// as ok:false with its OW-* diagnostic while OW-API-* misuse — only ever
// the expected install-window OW-API-003 — surfaces through Pipeline's
// error channel as HTTP 400 (SPEC §H2 item 5).
//
// This file lives in the OpWeave.Pipeline.Tests assembly, which compiles
// only when com.unity.pipeline 0.9.1-exp.1 is installed (versionDefines +
// defineConstraints): without the package the fixture compiles out
// entirely, keeping the AT-04 no-Pipeline suite green instead of
// reporting un-runnable commands.
//
// Step 10's [UnityTest] crosses real domain reloads; state that must
// survive a reload lives only in constants and files (the
// RegistryDiffTests / At01CustomOperationAcceptanceTests pattern). The
// churned tracked files — the AT-01 Operation and the test files that
// compile-depend on it — are saved under
// Library/OpWeaveAt01PipelineSteps/ before deletion and restored
// byte-identically by [UnityTearDown] (also by [UnitySetUp] of a
// following run, so a crashed run never leaves the tree dirty). Only the
// .cs/.asmdef payloads are deleted and rewritten: every .meta stays in
// place, so a restored file keeps its GUID and the worktree ends
// byte-identical.
//
// Every [UnitySetUp]/[UnityTearDown] ends at DrainEditorState: a compile
// or reload the file churn armed is crossed only at the bounded
// DrainedScriptReload boundary — never left queued for the next suite
// index — and the editor is handed on with no pending script reload, no
// compile or import in flight and a clean compile. What may NOT close a
// teardown is an unconditional `yield return new RecompileScripts(false)`:
// its Perform unlocks the runner's reload lock even when nothing is
// queued, and with no reload following the lock stays released — the next
// fixture's real reload then crosses mid-pump on the stale armed state.
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.Pipeline.Tests
{
    /// <summary>
    /// The AT-01 P3 acceptance steps of SPEC §T.3, executed over the same
    /// /api/exec transport <c>unity command opweave</c> uses.
    /// </summary>
    internal sealed class At01AgentStepsTests
    {
        private const string OpId = "proj.transform.reset";
        private const string OpAssetDir = "Assets/OpWeave/Editor/Operations/Transform";
        private const string OpTestsDir = OpAssetDir + "/Tests";
        private const string OpFileName = "ResetTransformOperation.cs";
        private const string SaveProbeDir = "Assets/OpWeave/At01PipelineStepsProbe";
        private const string SaveScenePath = SaveProbeDir + "/at01.unity";
        private const string ProbeObjectName = "AT01PipelineProbe";
        private const string NodeCreate = "create";
        private const string NodeReset = "reset";

        private const string DescriptorRelPath = "Library/Pipeline/.unity-pipeline-port";
        private const int HttpTimeoutMs = 20000;

        // The bound shared by the setup/teardown drain waits — the
        // suite-wide convention (PackageAddUpmTests, the git-package
        // reload fixtures and the operation-create fixtures all bound
        // their compile/import/reload waits at 240 s).
        private const double WaitTimeoutSec = 240.0;

        // The sustained-idle window the drain demands before it trusts a
        // quiet Editor: Refresh returns before a queued compile surfaces
        // as isCompiling, and queued imports flush about once a second —
        // the quiet spell covers the surfacing gap (the
        // WaitForIdleQuiet/QuietSettle convention).
        private const double QuietSettleSec = 5.0;

        // Mirror of OpWeave.Core.Registry.RegistryChangeKinds — this
        // assembly names no product type, so the kinds are restated.
        private const string ChangeKindAdded = "added";
        private const string ChangeKindRemoved = "removed";
        private const string ChangeKindImplementationChanged = "implementationChanged";

        // The step-10 delete phase must leave a compiling project while
        // proj.transform.reset is gone: ResetTransformOperationTests.cs
        // names the deleted type, so it — and, because an asmdefless test
        // file would drop into Assembly-CSharp-Editor without nunit, the
        // whole Tests/ payload including its asmdef — goes with it. The
        // set is closed under compile references: after it nothing that
        // still compiles names the missing assembly or types.
        private static readonly string[] ChurnedFiles =
        {
            OpAssetDir + "/" + OpFileName,
            OpTestsDir + "/OpWeave.Custom.Tests.asmdef",
            OpTestsDir + "/ResetTransformOperationTests.cs",
            OpTestsDir + "/ScriptsCompiledWaitTests.cs",
        };

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string DescriptorPath => Path.Combine(
            ProjectRoot, DescriptorRelPath.Replace('/', Path.DirectorySeparatorChar));

        // The test-owned Library subtree: the saved originals of the
        // churned files and the captured pre-change planHash — everything
        // a domain reload would otherwise drop.
        private static string StateDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveAt01PipelineSteps");

        private static string OriginalsDir => Path.Combine(StateDir, "originals");
        private static string PlanHashFile => Path.Combine(StateDir, "planhash.txt");

        private static string DiskPath(string repoPath) =>
            Path.Combine(ProjectRoot, repoPath.Replace('/', Path.DirectorySeparatorChar));

        // The AT-01 workflow of SPEC §T.3 step 6, executed entirely
        // through the CLI: core.start → unity.gameobject.create →
        // proj.transform.reset → unity.scene.save.
        private static JObject AcceptanceWorkflow =>
            JObject.Parse(
                "{\"schemaVersion\":\"1.0\",\"id\":\"example.at01-pipeline\"," +
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
                "\",\"port\":\"gameObject\"}}]}");

        // ── AT-01 step 4: search_operations finds it first, origin filters ──

        [Test]
        public void Step4_SearchRanksCustomOperationFirst_AndOriginCustomFilters()
        {
            AssertReady();

            // Both the English and the Japanese query rank the custom
            // Operation first — SPEC §T.3 step 4 / §C5.
            foreach (var query in new[] { "transform reset", "トランスフォーム リセット" })
            {
                var data = ExecOk("search_operations",
                    new JObject { ["query"] = query, ["limit"] = 10 });
                var items = data["items"] as JArray;
                Assert.That(items, Is.Not.Null.And.Not.Empty,
                    "search \"" + query + "\" returned no items");
                Assert.That((string?)items![0]["id"], Is.EqualTo(OpId),
                    "search \"" + query + "\" must rank proj.transform.reset first; got " +
                    string.Join(", ", items.Select(i => (string?)i["id"])));
                Assert.That((string?)items[0]["origin"], Is.EqualTo("custom"),
                    "proj.transform.reset must report origin custom");
            }

            // origin=custom narrows the listing to custom Operations —
            // and still contains the AT-01 one.
            var custom = ExecOk("search_operations",
                new JObject { ["query"] = "transform reset", ["origin"] = "custom", ["limit"] = 10 });
            var customItems = custom["items"] as JArray;
            Assert.That(customItems, Is.Not.Null.And.Not.Empty,
                "the origin=custom search returned no items");
            foreach (var item in customItems!)
            {
                Assert.That((string?)item["origin"], Is.EqualTo("custom"),
                    "origin=custom must filter to custom Operations: " + item);
            }
            Assert.That(
                customItems.Any(i => (string?)i["id"] == OpId), Is.True,
                "the origin=custom search must keep proj.transform.reset");
        }

        // ── AT-01 step 5: describe_operation returns the full contract ──

        [Test]
        public void Step5_DescribeReturnsTargetTypeOutputsExamplesAndSourceFile()
        {
            AssertReady();

            var data = ExecOk("describe_operation", new JObject { ["id"] = OpId });
            Assert.That((string?)data["id"], Is.EqualTo(OpId));

            var inputs = data["inputs"] as JArray;
            Assert.That(inputs, Is.Not.Null.And.Not.Empty,
                "describe must return the input ports");
            var target = inputs!.OfType<JObject>()
                .FirstOrDefault(p => (string?)p["name"] == "target");
            Assert.That(target, Is.Not.Null, "inputs must name a 'target' port");
            Assert.That((string?)target!["type"], Is.EqualTo("unity.GameObjectRef"),
                "inputs.target must be unity.GameObjectRef (SPEC §T.3 step 5)");

            var outputs = data["outputs"] as JArray;
            Assert.That(outputs, Is.Not.Null.And.Not.Empty,
                "describe must return the output ports");
            Assert.That(
                outputs!.OfType<JObject>().Any(
                    p => (string?)p["name"] == "gameObject" &&
                         (string?)p["type"] == "unity.GameObjectRef"),
                Is.True, "outputs must contain the gameObject output");

            var examples = data["examples"] as JArray;
            Assert.That(examples, Is.Not.Null.And.Not.Empty,
                "describe must return at least one Operation example (SPEC §C8)");
            Assert.That((string?)examples![0]["title"], Is.Not.Null.And.Not.Empty);
            Assert.That(examples[0]["workflow"] as JObject, Is.Not.Null,
                "an Operation example is a complete validatable workflow (SPEC §C8)");

            Assert.That((string?)data["sourceFile"], Does.Contain(OpFileName),
                "a custom Operation's describe must name its definition source (SPEC §C8)");
        }

        // ── AT-01 step 9 (API side): get_run_logs serves the step log and
        //    the Modified change of a Run accepted through execute_workflow ──

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Step9_ExecuteThenGetRunLogsServesStepLogAndModifiedChange()
        {
            AssertReady();
            EnsureSceneProbeDir();

            var runId = ExecuteWorkflow(AcceptanceWorkflow, expectedPlanHash: null);
            yield return AwaitRunTerminal(
                runId, "succeeded", "the AT-01 workflow Run");

            var data = ExecOk("get_run_logs", new JObject { ["runId"] = runId });
            var changes = data["changes"] as JArray;
            var modified = changes?.OfType<JObject>().FirstOrDefault(
                c => (string?)c["nodeId"] == NodeReset &&
                     (string?)c["action"] == "modified");
            Assert.That(modified, Is.Not.Null,
                "get_run_logs must list a Modified change for the reset step: " + data);
            var stepIndex = (int)modified!["step"]!;
            var target = modified["target"] as JObject;
            Assert.That((string?)target?["kind"], Is.EqualTo("gameObject"));
            Assert.That((string?)target?["path"], Is.EqualTo("/" + ProbeObjectName));
            Assert.That((bool?)modified["undoable"], Is.True,
                "the scene-object change is undoable (SPEC §F7)");

            var logs = data["logs"] as JArray;
            Assert.That(
                logs?.OfType<JObject>().Any(
                    e => e["step"] is { } step &&
                         (int)step == stepIndex &&
                         ((string?)e["message"] ?? "").Contains("Reset the Transform")),
                Is.True,
                "get_run_logs must serve the reset step's own log record: " + data);
        }

        // ── setup / teardown: restore the churned files, then drain ──

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the drain
        // reaches it through reflection (the PackageAddUpmTests probe). A
        // reload already armed behind the runner's reload lock surfaces
        // here while isCompiling is false — the deferred state an
        // isCompiling-only check misses and hands to the next fixture.
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
        // pending — either is one the drain must cross, never forward.
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        // The pending probe the drain trusts is only as strong as its
        // shape: prove it once up front (the PackageAddUpmTests pattern)
        // so the fixture fails here — before any test — instead of
        // silently degrading to an isCompiling-only check that re-opens
        // the missed-request race the probe exists to close.
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
                    "this Unity version — the teardown cannot prove a " +
                    "deferred reload is drained");
            }
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            RestoreChurnedFiles();
            if (Directory.Exists(StateDir))
            {
                Directory.Delete(StateDir, recursive: true);
            }
            CleanupProbe();
            // UTF resumes a reload-crossed setup/teardown enumerator at
            // its yield with every local reset — a null drain is that
            // resume, not a fresh start: rebuild the idempotent drain
            // and relay on instead of touching the dead reference.
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

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            s_domainSentinel = null;
            RestoreChurnedFiles();
            CleanupProbe();
            // The run's per-step Undo groups must not replay against the
            // objects teardown removes.
            Undo.ClearAll();
            // UTF resumes a reload-crossed setup/teardown enumerator at
            // its yield with every local reset — a null drain is that
            // resume, not a fresh start: rebuild the idempotent drain
            // and relay on instead of touching the dead reference.
            IEnumerator? drain = null;
            for (;;)
            {
                drain ??= DrainEditorState(nameof(UnityTearDown));
                if (!drain.MoveNext())
                {
                    break;
                }
                yield return drain.Current;
            }
        }

        private static bool CleanupProbe()
        {
            var dirty = false;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(SaveProbeDir))
            {
                AssetDatabase.DeleteAsset(SaveProbeDir);
                dirty = true;
            }
            var diskDir = DiskPath(SaveProbeDir);
            if (Directory.Exists(diskDir))
            {
                Directory.Delete(diskDir, recursive: true);
                dirty = true;
            }
            var metaFile = diskDir + ".meta";
            if (File.Exists(metaFile))
            {
                File.Delete(metaFile);
                dirty = true;
            }
            // EnsureSceneProbeDir may have created Assets/OpWeave itself:
            // drop it only while nothing under it remains but orphan .meta
            // files — a tree still carrying the AT-01 Operation payloads
            // (tracked files) is never touched.
            var opRoot = DiskPath("Assets/OpWeave");
            if (Directory.Exists(opRoot) &&
                Directory
                    .EnumerateFiles(opRoot, "*", SearchOption.AllDirectories)
                    .All(path => path.EndsWith(
                        ".meta", StringComparison.OrdinalIgnoreCase)))
            {
                if (AssetDatabase.IsValidFolder("Assets/OpWeave"))
                {
                    AssetDatabase.DeleteAsset("Assets/OpWeave");
                }
                if (Directory.Exists(opRoot))
                {
                    Directory.Delete(opRoot, recursive: true);
                }
                var opRootMeta = opRoot + ".meta";
                if (File.Exists(opRootMeta))
                {
                    File.Delete(opRootMeta);
                }
                dirty = true;
            }
            return dirty;
        }

        private static void EnsureSceneProbeDir()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(SaveProbeDir))
            {
                return;
            }
            // AssetDatabase.CreateFolder builds one level only and silently
            // fails when the parent is missing: create the whole chain on
            // disk and let the refresh import it.
            Directory.CreateDirectory(DiskPath(SaveProbeDir));
            AssetDatabase.Refresh();
        }

        // Snapshots every churned file's current bytes under the Library
        // state dir, then deletes the payloads (never the .meta — GUID and
        // worktree stay intact through the restore).
        private static void SaveChurnedFiles()
        {
            foreach (var repoPath in ChurnedFiles)
            {
                var disk = DiskPath(repoPath);
                Assert.IsTrue(File.Exists(disk), "expected tracked file missing: " + repoPath);
                var saved = Path.Combine(OriginalsDir, repoPath.Replace('/', '_'));
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                File.WriteAllBytes(saved, File.ReadAllBytes(disk));
                File.Delete(disk);
            }
            AssetDatabase.Refresh();
        }

        // Rewrites any churned file that is missing or whose bytes differ
        // from the saved original; true when anything was written.
        private static bool RestoreChurnedFiles()
        {
            var dirty = false;
            foreach (var repoPath in ChurnedFiles)
            {
                var saved = Path.Combine(OriginalsDir, repoPath.Replace('/', '_'));
                var disk = DiskPath(repoPath);
                if (!File.Exists(saved))
                {
                    continue;
                }
                var original = File.ReadAllBytes(saved);
                if (File.Exists(disk) &&
                    original.AsEnumerable().SequenceEqual(File.ReadAllBytes(disk)))
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                File.WriteAllBytes(disk, original);
                dirty = true;
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            return dirty;
        }

        // The contract the fixture hands the next suite test: no pending
        // script reload, no compile or import in flight and a clean
        // compile — the failure mode this drain replaces was the
        // unconditional `yield return new RecompileScripts(false)`: its
        // Perform unlocks the runner's reload lock even when nothing is
        // queued, and with no reload following the lock stays released,
        // so the next fixture's real reload crosses mid-pump on the stale
        // armed state. Here a pending compile or requested reload is
        // crossed only at DrainedScriptReload — the bounded instruction
        // that unlocks once the pending state is proven — and everything
        // else settles on bounded plain frames. A queued compile surfaces
        // as isCompiling a frame or two after Refresh returns, so the
        // wait only trusts a sustained quiet spell (the QuietSettle
        // convention): quiet flags reset on every busy frame and a
        // periodic Refresh flushes the import queue so a queued compile
        // surfaces inside the window, never behind it. The instruction
        // the nested enumerator produces is yielded DIRECTLY from the
        // setup/teardown's own top level through the callers' MoveNext
        // relay — a nested yield would never reach Perform — and the
        // whole sequence is idempotent: a crossed reload resumes the
        // caller's enumerator at its yield with every local reset, so
        // the caller rebuilds the drain when it arrives null rather
        // than relaying a dead reference.
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
                        "At01AgentStepsTests." + phase +
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
                "At01AgentStepsTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "At01AgentStepsTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "At01AgentStepsTests." + phase +
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
        // unconditional RecompileScripts(false) unlock it replaces. The
        // one path where no reload can follow — a failed compile —
        // re-locks before failing.
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
                            "At01AgentStepsTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "At01AgentStepsTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // ── AT-01 step 10: delete → removed + OW-OP-001 hint; re-add →
        //    added + executable; modify → implementationChanged + the
        //    pre-change Plan refused with OW-RUN-003 ──

        // Each phase asserts inside its own domain; nothing non-serializable
        // crosses a reload — the sentinel wait is inlined per phase (the
        // RegistryDiffTests pattern: a nested IEnumerator yield would not
        // survive the reload). The captured planHash lives on disk.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Step10_DeleteReaddModify_RegistryDiffHintAndStalePlanRefusal()
        {
            AssertReady();
            EnsureSceneProbeDir();

            // The Plan this test later refuses: a validate over the
            // unchanged Operation fixes the pre-change planHash the
            // §D7 item-7 check compares against.
            var planHash = (string?)ExecOk("validate_workflow",
                new JObject { ["workflow"] = AcceptanceWorkflow })["planHash"];
            Assert.That(planHash, Is.Not.Null.And.Not.Empty,
                "validate_workflow must report the compiled planHash");
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(PlanHashFile, planHash);

            // ── delete → removed, validate → OW-OP-001 with the
            //    definition-source hint ──
            SaveChurnedFiles();
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "deleting the custom Operation file did not produce a domain reload");
            AssertReady();
            var removed = PollRegistryChange(ChangeKindRemoved);
            Assert.That((string?)removed["sourceFile"], Does.Contain(OpFileName),
                "the removed record must name the deleted file");

            var failed = ExecEnvelope("validate_workflow",
                new JObject { ["workflow"] = AcceptanceWorkflow });
            Assert.That((bool?)failed["ok"], Is.False,
                "validating a workflow on a deleted Operation must fail: " + failed);
            var unknown = (failed["errors"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(e => (string?)e["code"] == "OW-OP-001");
            Assert.That(unknown, Is.Not.Null,
                "the deleted Operation must report OW-OP-001: " + failed["errors"]);
            Assert.That((string?)unknown!["hint"],
                Does.Contain("previously defined in").And.Contain(OpFileName),
                "OW-OP-001 must carry the definition-source hint (SPEC §C3 rule 7)");

            // ── re-add → added, and executable again ──
            Assert.IsTrue(RestoreChurnedFiles(),
                "the re-add phase must rewrite the churned files");
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "re-adding the custom Operation file did not produce a domain reload");
            AssertReady();
            var added = PollRegistryChange(ChangeKindAdded);
            Assert.That((string?)added["sourceFile"], Does.Contain(OpFileName));

            var restoredRun = ExecuteWorkflow(AcceptanceWorkflow, expectedPlanHash: null);
            yield return AwaitRunTerminal(
                restoredRun, "succeeded", "the re-added Operation's Run");

            // ── modify the Execute body → implementationChanged; the
            //    pre-change Plan is refused with OW-RUN-003 ──
            var opDisk = DiskPath(ChurnedFiles[0]);
            var source = File.ReadAllText(opDisk);
            const string originalLog = "\"Reset the Transform of '\"";
            const string modifiedLog = "\"Reset the transform state of '\"";
            Assert.IsTrue(source.Contains(originalLog),
                "the probe replacement must anchor in the Operation source");
            File.WriteAllText(opDisk, source.Replace(originalLog, modifiedLog));
            AssetDatabase.Refresh();
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "editing the Execute body did not produce a domain reload");
            AssertReady();
            PollRegistryChange(ChangeKindImplementationChanged);

            Assert.IsTrue(File.Exists(PlanHashFile),
                "the captured pre-change planHash did not survive the reload");
            var expected = File.ReadAllText(PlanHashFile).Trim();
            var refused = ExecEnvelope("execute_workflow",
                new JObject
                {
                    ["workflow"] = AcceptanceWorkflow,
                    ["expectedPlanHash"] = expected,
                });
            Assert.That((bool?)refused["ok"], Is.False,
                "a Plan made before the implementation change must be refused: " + refused);
            Assert.That(
                (refused["errors"] as JArray)?.OfType<JObject>().Any(
                    e => (string?)e["code"] == "OW-RUN-003"),
                Is.True,
                "the stale Plan must fail with OW-RUN-003: " + refused["errors"]);
        }

        // ── command helpers (the /api/exec wire of `unity command opweave`) ──

        // Waits for the pipeline server and the installed AgentApi service:
        // after a domain reload both restart on the first delayCall, so an
        // early command can still meet the OW-API-003 install-window refusal.
        private void AssertReady()
        {
            Assert.IsTrue(WaitForServerReady(60),
                "the pipeline server did not report status ready");
            Assert.IsTrue(WaitForAgentApi(60),
                "the AgentApi service did not install within the deadline");
        }

        private static bool WaitForAgentApi(double seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (AgentApiInstalled())
                {
                    return true;
                }
                TickOnce();
                Thread.Sleep(100);
            }
            return false;
        }

        // The domain-load install of AgentApiHost.Api — read over
        // reflection so this fixture names no AgentApi type.
        private static bool AgentApiInstalled()
        {
            try
            {
                var host = FindAssembly("OpWeave.AgentApi")
                    ?.GetType("OpWeave.AgentApi.AgentApiHost");
                return host?.GetProperty("Api")?.GetValue(null) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static JObject ExecBody(string command, JObject parameters) =>
            new JObject
            {
                ["command"] = "opweave " + command,
                ["parameters"] = parameters,
            };

        // The exec result's §H3 envelope — whether it arrived as a nested
        // result object or as a serialized string.
        private static JObject? EnvelopeFrom(JObject doc)
        {
            var result = doc["result"];
            if (result is JObject envelope)
            {
                return envelope;
            }
            if (result is JValue { Type: JTokenType.String } text &&
                (string?)text is { } json)
            {
                return JObject.Parse(json);
            }
            return null;
        }

        // One command exchange: the §H3 envelope of the exec result — or,
        // for a Pipeline-channel refusal (HTTP 400), the envelope lifted
        // into errorDetails (SPEC §H2 item 5).
        private JObject ExecEnvelope(string command, JObject parameters)
        {
            var exchange = HttpSync("POST", "/api/exec",
                ExecBody(command, parameters).ToString(Newtonsoft.Json.Formatting.None));
            Assert.IsFalse(exchange.TransportFailed,
                command + " transport failed: " + exchange.Describe());
            var doc = JObject.Parse(exchange.Body);
            var envelope = EnvelopeFrom(doc);
            if (envelope is null && doc["errorDetails"] is { } details)
            {
                var text = (string?)details;
                var start = text?.IndexOf('{') ?? -1;
                if (start >= 0)
                {
                    envelope = JObject.Parse(text!.Substring(start));
                }
            }
            Assert.IsNotNull(envelope,
                command + " returned no envelope: " + exchange.Describe());
            return envelope!;
        }

        // The business-success path: ok:true and data. The only Pipeline-
        // channel outcome a well-formed command can meet while ready is the
        // install-window OW-API-003 refusal — retried once the domain-load
        // install lands.
        private JObject ExecOk(string command, JObject parameters, int retries = 3)
        {
            for (var attempt = 0; ; attempt++)
            {
                var envelope = ExecEnvelope(command, parameters);
                if ((bool?)envelope["ok"] == true)
                {
                    var data = envelope["data"] as JObject;
                    Assert.IsNotNull(data,
                        command + " returned ok without a data object: " + envelope);
                    return data!;
                }
                var codes = (envelope["errors"] as JArray)?
                    .OfType<JObject>()
                    .Select(e => (string?)e["code"])
                    .ToArray() ?? Array.Empty<string?>();
                if (attempt >= retries || !codes.Contains("OW-API-003"))
                {
                    Assert.Fail(command + " failed: " + envelope);
                }
                Assert.IsTrue(WaitForAgentApi(60),
                    "the AgentApi service did not install within the deadline");
            }
        }

        // execute_workflow → runId — the run then advances on
        // EditorApplication.update while the [UnityTest] yields.
        private string ExecuteWorkflow(JObject workflow, string? expectedPlanHash)
        {
            var parameters = new JObject { ["workflow"] = workflow };
            if (expectedPlanHash is not null)
            {
                parameters["expectedPlanHash"] = expectedPlanHash;
            }
            var data = ExecOk("execute_workflow", parameters);
            var runId = (string?)data["runId"];
            Assert.That(runId, Is.Not.Null.And.Not.Empty,
                "execute_workflow returned no runId: " + data);
            return runId!;
        }

        // Long-polls get_run_status on a background request while the
        // coroutine's yields pump the editor update — the production host
        // ticks the Run, the §E8 snapshot replaces and the wait wakes. The
        // command is off-main (SPEC §H2 item 3), so no dispatcher pump is
        // needed here. The terminal state is asserted inside the iterator:
        // an IEnumerator cannot return a value, and a callback lambda's
        // captured local would sit in the caller's display class — which
        // the domain-reload PC restore never re-allocates (the
        // At01CustomOperationAcceptanceTests trap), leaving a null holder.
        // The client timeout is doubled so the 20-second server hold still
        // has headroom at the SPEC maximum.
        private static IEnumerator AwaitRunTerminal(
            string runId, string expectedState, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                var task = HttpAsync("POST", "/api/exec",
                    ExecBody("get_run_status", new JObject
                    {
                        ["runId"] = runId,
                        ["waitSeconds"] = 20,
                    }).ToString(Newtonsoft.Json.Formatting.None),
                    timeoutMs: HttpTimeoutMs * 2);
                while (!task.IsCompleted)
                {
                    yield return null;
                }
                var exchange = task.Result;
                Assert.IsFalse(exchange.TransportFailed,
                    "get_run_status transport failed: " + exchange.Describe());
                var state = (string?)EnvelopeFrom(JObject.Parse(exchange.Body))?["data"]?["state"];
                if (state is "succeeded" or "failed" or "cancelled" or "needs_review")
                {
                    Assert.That(state, Is.EqualTo(expectedState),
                        what + " did not reach " + expectedState +
                        "; final state: " + state);
                    yield break;
                }
            }
            Assert.Fail("the Run did not reach a terminal state within the deadline");
        }

        // get_registry_status until the latest recorded diff carries the
        // expected change kind for proj.transform.reset — the production
        // startup rebuild lands on the first delayCall after each reload.
        private JObject PollRegistryChange(string kind)
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                var data = ExecOk("get_registry_status", new JObject());
                var changes = data["diff"]?["changes"] as JArray ?? new JArray();
                var record = changes.OfType<JObject>().FirstOrDefault(
                    c => (string?)c["kind"] == kind && (string?)c["operationId"] == OpId);
                if (record is not null)
                {
                    return record;
                }
                TickOnce();
                Thread.Sleep(250);
            }
            Assert.Fail("get_registry_status never recorded a '" + kind +
                "' change for " + OpId);
            return null!;
        }

        // ── HTTP loopback (the L-01 probe's verified wire pattern, kept
        //    verbatim with PipelineCommandSurfaceTests) ──

        private static Assembly? FindAssembly(string assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }
            try
            {
                return Assembly.Load(new AssemblyName(assemblyName));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed class Exchange
        {
            public int StatusCode = -1;
            public string StatusName = "";
            public string Body = "";
            public string Error = "";

            public bool TransportFailed => StatusCode < 0;

            public string Describe() =>
                TransportFailed
                    ? "transport:" + Error
                    : "HTTP " + StatusCode + " " + StatusName + " " +
                      (Body.Length <= 400 ? Body : Body.Substring(0, 400) + "...");
        }

        [Serializable]
        public sealed class PipelineDescriptor
        {
            public int port;
            public string evalToken = "";
        }

        private static PipelineDescriptor? ReadDescriptor()
        {
            try
            {
                if (!File.Exists(DescriptorPath))
                {
                    return null;
                }
                var descriptor = JsonUtility.FromJson<PipelineDescriptor>(
                    File.ReadAllText(DescriptorPath));
                return descriptor != null && descriptor.port > 0 &&
                    !string.IsNullOrEmpty(descriptor.evalToken)
                    ? descriptor
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadAll(WebResponse response)
        {
            using (var stream = response.GetResponseStream())
            {
                if (stream is null)
                {
                    return "";
                }
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static Exchange HttpRoundTrip(
            string method, string path, string? bodyJson, bool authorize, int timeoutMs)
        {
            var exchange = new Exchange();
            try
            {
                var descriptor = ReadDescriptor();
                if (descriptor is null)
                {
                    exchange.Error = "no pipeline descriptor at " + DescriptorPath;
                    return exchange;
                }
                var request = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:" + descriptor.port + path);
                request.Method = method;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.KeepAlive = false;
                if (authorize)
                {
                    request.Headers["Authorization"] = "Bearer " + descriptor.evalToken;
                }
                if (bodyJson is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(bodyJson);
                    request.ContentType = "application/json";
                    request.ContentLength = bytes.Length;
                    using (var stream = request.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        exchange.StatusCode = (int)response.StatusCode;
                        exchange.StatusName = response.StatusDescription ?? "";
                        exchange.Body = ReadAll(response);
                    }
                }
                catch (WebException exception)
                {
                    if (exception.Response is HttpWebResponse errorResponse)
                    {
                        exchange.StatusCode = (int)errorResponse.StatusCode;
                        exchange.StatusName = errorResponse.StatusDescription ?? "";
                        exchange.Body = ReadAll(errorResponse);
                        errorResponse.Close();
                    }
                    else
                    {
                        exchange.Error = exception.Status + ": " + FirstLine(exception.Message);
                    }
                }
            }
            catch (Exception exception)
            {
                exchange.Error = exception.GetType().Name + ": " + FirstLine(exception.Message);
            }
            return exchange;
        }

        private static Task<Exchange> HttpAsync(string method, string path, string? bodyJson,
            bool authorize = true, int timeoutMs = HttpTimeoutMs) =>
            Task.Run(() => HttpRoundTrip(method, path, bodyJson, authorize, timeoutMs));

        // The synchronous exchange for [Test] methods: keeps the server's
        // dispatcher pumped while a request is outstanding — a blocked main
        // thread that never ticks cannot dispatch a main-thread command
        // (L-01). TickOnce pumps the server's own work queue, never
        // EditorApplication.update itself.
        private static Exchange HttpSync(string method, string path, string? bodyJson,
            bool authorize = true, int timeoutMs = HttpTimeoutMs)
        {
            var task = HttpAsync(method, path, bodyJson, authorize, timeoutMs + 5000);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs + 8000);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                TickOnce();
                Thread.Sleep(2);
            }
            return task.IsCompleted ? task.Result : new Exchange { Error = "wait deadline" };
        }

        private static void TickOnce()
        {
            try
            {
                var startup = Type.GetType(
                    "Unity.Pipeline.Editor.PipelineServerStartup, Unity.Pipeline.Editor");
                var server = startup?.GetProperty("Server",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var dispatcher = server?.GetType().GetProperty("Dispatcher")?.GetValue(server);
                dispatcher?.GetType().GetMethod("ProcessWorkQueue", Type.EmptyTypes)
                    ?.Invoke(dispatcher, null);
            }
            catch (Exception)
            {
                // Best-effort pump — an in-flight request ends by its own
                // timeout instead of dying inside the wait loop.
            }
        }

        private static bool WaitForServerReady(double seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                var status = HttpSync("GET", "/api/status", null, true, 8000);
                if (status.StatusCode == 200 &&
                    (status.Body.Contains("\"status\": \"ready\"") ||
                     status.Body.Contains("\"status\":\"ready\"")))
                {
                    return true;
                }
                TickOnce();
                Thread.Sleep(250);
            }
            // A server that never answered ready — including one that
            // never answers at all — is not ready.
            return false;
        }

        private static string FirstLine(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            var end = text!.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? text : text.Substring(0, end);
        }
    }
}
