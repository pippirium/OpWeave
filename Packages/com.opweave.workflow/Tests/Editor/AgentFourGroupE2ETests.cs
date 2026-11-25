#nullable enable

// T-W4.5-06 [Edit] tests: local:T-W4.5-06:four-group-e2e — SPEC §H1 test 2,
// §1.1, §T.3. The v1 completion criterion 1: an EditMode test drives AgentApi
// directly — search_operations → describe_operation → validate_workflow →
// execute_workflow → get_run_status — for one workflow that combines all four
// standard Operation groups (W2.5 Scene/GameObject, W2.6 Prefab, W2.7 Asset
// and W2.8 Compile/Package): unity.gameobject.create,
// unity.prefab.create_from_gameobject, unity.asset.create_material,
// unity.script.create_from_template and unity.script.compile, all
// allow-by-default effects under §H4 so the Plan needs no approval.
// The unity.script.compile step's real wait crosses the domain reload its
// compile causes: UTF resurrected locals do not survive (the SPEC §T.3 /
// L-14 pattern of CoordinatorRunLockReloadTests), so each side of the
// reload is a static phase helper and everything the second domain needs —
// the runId — is persisted under <project>/Library/OpWeaveAgentE2E4G/. The
// first domain's fixture host is Installed before the Run is admitted so
// its §E2 item 3 owned-Run record and its beforeAssemblyReload journal
// flush carry the waiting Run over; the second domain's host runs the §F4
// recovery pass that re-attaches and resumes it — the
// ResumeRegistrySessionHostTests cross-domain pattern. The T-W3.2-05
// three-group fixture is the harness this file extends: the same seams the
// production installer binds, a real production Registry build, no Pipeline
// boundary, every artifact under one unique Assets/ folder the teardown
// removes.
//
// Every [UnitySetUp]/[UnityTearDown] ends at DrainEditorState: a compile
// or reload the generated-script delete armed is crossed only at the
// bounded DrainedScriptReload boundary — never left queued for the next
// suite index — and the editor is handed on with no pending script
// reload, no compile or import in flight and a clean compile (the
// At01CustomOperationAcceptanceTests idiom, T-W2.4-07).
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
using OpWeave.Core.Recovery;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    [TestFixture]
    internal sealed class AgentFourGroupE2ETests
    {
        // The bound on the run-drive loops. Every step before the compile is
        // Immediate and finishes in one tick, and the resumed compile wait
        // lands on its first post-reload Poll, so reaching this cap is a
        // failure, not a slow pass.
        private const int MaxTicks = 4000;
        private const string ApprovalHintText =
            "Approval is required in the Unity Run Viewer.";

        // Fixed probe paths — every post-reload input is recomputed from
        // these constants because nothing in memory crosses the reload.
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveAgentE2E4G");
        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string StateFile => Path.Combine(ProbeDir, "state.txt");
        private static string SetupFile => Path.Combine(ProbeDir, "setup.txt");

        // A per-Editor-session nonce the phase-1 state write arms in
        // SessionState and on disk (the At03NoDuplicatesTests discipline,
        // T-W2.4-07): SessionState and the Library file both survive a
        // domain reload but die with the Editor, so a probe root whose
        // nonce still matches the armed one is THIS session's live
        // in-flight state a replayed setup must never reclaim — anything
        // else is stale residue. The nonce is the session-token the
        // phase-2 state read and the stale-resume re-drive gate on.
        private const string ProbeSessionKey = "OpWeave.AgentFourGroup.probe-state";

        private static string NonceFile => Path.Combine(ProbeDir, "probe.nonce");

        private const string AssetsRoot = "Assets/OpWeaveAgentE2E4G";
        private const string BaseFolder = "Assets/OpWeaveAgentE2E4GBase";
        private static string AssetsDisk =>
            Path.Combine(ProjectRoot, AssetsRoot.Replace('/', Path.DirectorySeparatorChar));
        private static string BaseDisk =>
            Path.Combine(ProjectRoot, BaseFolder.Replace('/', Path.DirectorySeparatorChar));

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // T-W2.4-07: bound on phase-1 re-drive legs after a predecessor's
        // stale resume is detected. leg is a method local, so a real
        // reload resets it; the live-nonce probe check is what actually
        // ends the loop (the ChangeSetDomainReloadTests pattern).
        private const int MaxReDriveLegs = 3;

        // A "[W4506] " breadcrumb so editmode.log/editmode.xml shows exactly
        // which phase the run reached before or after the reload.
        private static void Crumb(string message) => Debug.Log("[W4506] " + message);

        // The plain result a phase helper hands back to the iterator: no
        // captured state, only a pass flag plus the failure text the
        // iterator asserts on. [Serializable] so UTF's iterator
        // preservation across the domain reload can carry it too.
        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;
            public static PhaseResult Pass() => new PhaseResult { Ok = true };
            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        // Leaves the probe Run Store with nothing a later host could mistake
        // for live state, whatever the phase that just ran did or failed to
        // do. The exclusion this process still holds on this root — the
        // previous domain's after the reload, or a fixture's that aborted
        // before it could end its Run — is taken through the same seam the
        // recovery pass uses and ended the way an orderly Run end does, which
        // empties lock.json BEFORE freeing the handle: a dropped-only end
        // would leave the abandoned-state record behind, and that record
        // alone keeps every later Run on this root refusing with OW-RUN-002
        // even once the exclusion is free. Then both hand-off records for
        // this root are dropped. With a live owner elsewhere nothing here is
        // possible: the obtain is denied, the probe Store stays for the next
        // run, and that is reported rather than destroyed.
        private static void ReleaseLeakedState()
        {
            var obtained = RunLockHandoff.TryObtain(StoreRoot);
            if (!obtained.Ok || obtained.Value is not { } held)
            {
                Crumb("the probe Run Store is excluded elsewhere: " +
                    Describe(obtained.Diagnostics));
                RunLockHandoff.Clear(StoreRoot);
                return;
            }
            if (!held.Release().Ok)
            {
                held.Dispose();
            }
            RunLockHandoff.Clear(StoreRoot);
        }

        // Removes every artifact a previous (possibly crashed) run left:
        // the probe Run Store under Library/, the Assets/ folders holding
        // the generated scene/prefab/material and — above all — the
        // generated .cs whose removal needs a recompile of its own. The
        // returned flag is true when an Assets/ folder was deleted; the
        // compile that delete queues stays behind the runner's
        // LockReloadAssemblies and is owned by the calling command's own
        // bounded conditional drain — the DrainEditorState relay both
        // commands end at — never handed on to a sibling test whose
        // synchronous [Test] body asserts registry freshness over it:
        // RegistryBuildHost.Rebuild reads isCompiling and marks the
        // build stale (SPEC §C3 rule 9), the exact Steps6To9 failure
        // signature a pending compile produced in
        // At01CustomOperationAcceptanceTests (T-W2.4-07). What stays
        // forbidden is the unconditional emitter — a bare
        // `yield return new RecompileScripts(false)` whose
        // ExpectDomainReload=false keeps UTF from recording resume
        // state (the stale-PC silent-PASS vector); the drain crosses
        // only a proven pending reload. A replayed command around a
        // live test leaves the probe root untouched: only a ProbeDir
        // whose nonce is not the armed one is stale residue, so the
        // Run-lock reclaim and the deletes run only while no in-flight
        // test owns the probe state.
        private static bool CleanupArtifacts()
        {
            if (ProbeStateIsLive())
            {
                return false;
            }
            ReleaseLeakedState();
            var dirty = false;
            if (AssetDatabase.IsValidFolder(AssetsRoot))
            {
                AssetDatabase.DeleteAsset(AssetsRoot);
                dirty = true;
            }
            if (Directory.Exists(AssetsDisk))
            {
                Directory.Delete(AssetsDisk, recursive: true);
                dirty = true;
            }
            if (AssetDatabase.IsValidFolder(BaseFolder))
            {
                AssetDatabase.DeleteAsset(BaseFolder);
                dirty = true;
            }
            if (Directory.Exists(BaseDisk))
            {
                Directory.Delete(BaseDisk, recursive: true);
                dirty = true;
            }
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            CompileErrorCapture.Clear();
            CompileWarningCapture.Clear();
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            return dirty;
        }

        // The session-token pair the nonce proves: the file under the
        // probe root carries the nonce the phase-1 state write armed in
        // SessionState, and both die together on any cleanup that
        // legitimately reclaims the root — a file without the armed
        // session's nonce is residue of an earlier Editor session, never
        // this run's phase-1.
        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(NonceFile, nonce + "\n");
            SessionState.SetString(ProbeSessionKey, nonce);
            return nonce;
        }

        private static void DisarmProbeNonce() =>
            SessionState.EraseString(ProbeSessionKey);

        private static bool ProbeStateIsLive()
        {
            var nonce = SessionState.GetString(ProbeSessionKey, "");
            return nonce.Length > 0 && File.Exists(NonceFile) &&
                string.Equals(
                    File.ReadAllText(NonceFile).Trim(), nonce,
                    StringComparison.Ordinal);
        }

        // Persists the scene setup the test found, so the teardown — which
        // may run in a different domain than this setup — can restore it
        // from disk instead of memory. Each line keeps the loaded flag with
        // the path, so a recorded-but-unloaded scene does not come back
        // loaded.
        private static void WriteSetupFile()
        {
            Directory.CreateDirectory(ProbeDir);
            var lines = new List<string>();
            foreach (var entry in EditorSceneManager.GetSceneManagerSetup())
            {
                if (!string.IsNullOrEmpty(entry.path))
                {
                    lines.Add((entry.isLoaded ? "1" : "0") + "\t" + entry.path);
                }
            }
            File.WriteAllLines(SetupFile, lines);
        }

        // Restores the scene setup persisted by WriteSetupFile — a pathless
        // entry cannot be reopened, so only real scene paths were recorded.
        // Each entry's recorded isLoaded is kept and the first loaded entry
        // is made active — the discipline RestoreSavedSetup follows, because
        // RestoreSceneManagerSetup refuses a setup with no active scene. A
        // missing file or a restore failure leaves one fresh empty scene,
        // the same fallback RestoreSavedSetup uses.
        private static void RestoreSetupFromFile()
        {
            var restorable = new List<SceneSetup>();
            if (File.Exists(SetupFile))
            {
                foreach (var line in File.ReadAllLines(SetupFile))
                {
                    var isLoaded = true;
                    var path = line;
                    var separator = line.IndexOf('\t');
                    if (separator >= 0)
                    {
                        isLoaded = string.Equals(
                            line.Substring(0, separator), "1", StringComparison.Ordinal);
                        path = line.Substring(separator + 1);
                    }
                    if (!string.IsNullOrEmpty(path))
                    {
                        restorable.Add(new SceneSetup { path = path, isLoaded = isLoaded });
                    }
                }
            }
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
            try
            {
                EditorSceneManager.RestoreSceneManagerSetup(restorable.ToArray());
            }
            catch (Exception)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        // A saved, clean base scene is the state the additive
        // unity.scene.create step needs: an unsaved untitled scene still
        // loaded refuses it OW-UNITY-011 — the same starting state the
        // T-W3.2-05 fixture establishes.
        private static void EnsureBaseScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!AssetDatabase.IsValidFolder(BaseFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveAgentE2E4GBase");
            }
            Assert.That(
                EditorSceneManager.SaveScene(scene, BaseFolder + "/Base.unity"),
                Is.True,
                "the fixture's base scene must save");
        }

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the drain
        // reaches it through reflection (the
        // At01CustomOperationAcceptanceTests probe, T-W2.4-07). A reload
        // already armed behind the runner's reload lock surfaces here
        // while isCompiling is false — the deferred state an
        // isCompiling-only check misses.
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
        // lock holds it: a running script compile always ends in a
        // reload queued behind the lock, and a requested reload is
        // already pending — either is one the drain must cross, never
        // forward to the next fixture whose registry-freshness checks
        // read isCompiling directly (SPEC §C3 rule 9: a stale build).
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // The pending probe the drain trusts is only as strong as its
        // shape: prove it once up front (the
        // At01CustomOperationAcceptanceTests pattern, T-W2.4-07) so the
        // fixture fails here — before any test — instead of silently
        // degrading to an isCompiling-only check that misses a requested
        // reload armed behind the lock.
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

        // UTF replays the before/after commands from their first
        // statement on every domain a [UnityTest] crosses, so the probe
        // nonce gates the setup: a replay around the in-flight test
        // restores nothing and deletes nothing (the body it resumes into
        // owns the probe state — the At03NoDuplicatesTests discipline).
        // What the commands may NEVER yield is an unconditional emitter —
        // a bare `yield return new RecompileScripts(false)` whose
        // ExpectDomainReload=false keeps UTF from recording any resume
        // state, so a real reload crossing the unlock abandons the test
        // on cleared locals (the stale-PC silent-PASS vector, T-W2.4-07).
        // A leftover generated script's delete instead queues its
        // compile behind the runner's LockReloadAssemblies and drains at
        // this command's own bounded conditional drain —
        // DrainedScriptReload, yielded only after ReloadPending() proves
        // a reload is armed, with UTF's resume bookkeeping armed and the
        // drain rebuilt on resume — so a synchronous [Test] like
        // ApprovalRequiringNode finds a drained editor: its
        // RegistryBuildHost.Rebuild would read isCompiling true and mark
        // the build stale (SPEC §C3 rule 9) over a pending compile nobody
        // drained — the exact Steps6To9 failure signature
        // At01CustomOperationAcceptanceTests produced.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            if (ProbeStateIsLive())
            {
                Crumb("setup replayed around the in-flight test — " +
                    "the live probe state is left untouched");
                yield break;
            }
            var dirty = CleanupArtifacts();
            Crumb("setup cleanup exit; assetsDirty=" + dirty);
            WriteSetupFile();
            EnsureBaseScene();
            // UTF resumes a reload-crossed setup enumerator at its yield
            // with every local reset — a null drain is that resume, not
            // a fresh start: rebuild the idempotent drain and relay on
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

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            // The test is over — drop the live-state nonce so the cleanup
            // below really does reclaim the probe root.
            DisarmProbeNonce();
            RestoreSetupFromFile();
            var dirty = CleanupArtifacts();
            Crumb("teardown cleanup exit; assetsDirty=" + dirty);
            // The teardown owns the deferred reload its generated-script
            // delete queues: left pending, it rides into the next fixture
            // whose registry freshness asserts do not expect one — it
            // crosses at this command's own bounded drain
            // (proven-pending only), whose resume rebuilds the drain
            // rather than touching a dead reference.
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

        // The sanctioned setup/teardown drain (the
        // At01CustomOperationAcceptanceTests idiom, T-W2.4-07): a queued
        // compile surfaces as isCompiling a frame or two after Refresh
        // returns, so the wait only trusts a sustained quiet spell (the
        // QuietSettleSec convention) — quiet flags reset on every busy
        // frame and a periodic Refresh flushes the import queue so a
        // queued compile surfaces inside the window, never behind it. A
        // pending compile or requested reload is crossed only at
        // DrainedScriptReload — the bounded instruction that unlocks
        // once the pending state is proven; never an unconditional
        // RecompileScripts(false) unlock, which releases the runner's
        // reload lock even when nothing is queued and arms UTF's resume
        // bookkeeping with a stale program counter. The instruction the
        // nested enumerator produces is yielded DIRECTLY from the
        // caller's own top level through its MoveNext relay — a nested
        // yield would never reach Perform — and the whole sequence is
        // idempotent: a crossed reload resumes the caller's enumerator
        // at its yield with every local reset (or replays the command),
        // so the caller rebuilds the drain when it arrives null rather
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
                        "AgentFourGroupE2ETests." + phase +
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
                "AgentFourGroupE2ETests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "AgentFourGroupE2ETests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "AgentFourGroupE2ETests." + phase +
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
                            "AgentFourGroupE2ETests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "AgentFourGroupE2ETests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // ── the §1.1 completion flow over the four groups, across a reload ──

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator FourGroups_RunToSucceeded_ThroughTheFiveCommands()
        {
            var phase1 = PhaseDriveToCompileWait();
            Crumb("phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True,
                "pre-reload phase failed: " + phase1.Failure);

            // T-W2.4-07 stale-resume self-heal: a predecessor's captured
            // UTF resume PC applies to this fresh enumerator and lands it
            // at one of the two post-yield states inside this loop — with
            // every local (leg included) reset to default. Both
            // continuations drain, then MUST pass the this-session probe
            // check: a probe phase-1 never wrote re-drives a real phase-1
            // + compile + reload instead of skipping silently into
            // phase-2. If the probe still cannot be produced after the
            // bound, the loop exits and phase-2's nonce validation fails
            // loudly (the ChangeSetDomainReloadTests pattern).
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                // The unity.script.compile step already requested the
                // compilation; RecompileScripts crosses the domain reload
                // it causes (the SPEC §T.3 boundary — WaitForDomainReload
                // is deliberately not used because the reload can
                // complete inside RecompileScripts' own wait).
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Crumb("iterator resumed; domain sentinel alive=" + DomainSentinelAlive);
                Assert.IsFalse(DomainSentinelAlive,
                    "the workflow's script compile did not produce a domain reload");

                if (ProbeStateIsLive() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // The abandoned leg's generated .cs carries identical
                // bytes, so deleting the probe assets guarantees the
                // re-run's compile step queues a real compile rather
                // than a no-op Refresh. setup.txt is the teardown's
                // scene-restore record, not phase state — it survives
                // the reclaim verbatim.
                var setupLines = File.Exists(SetupFile)
                    ? File.ReadAllLines(SetupFile)
                    : null;
                CleanupArtifacts();
                if (setupLines is not null)
                {
                    Directory.CreateDirectory(ProbeDir);
                    File.WriteAllLines(SetupFile, setupLines);
                }
                var redrive = PhaseDriveToCompileWait();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var phase2 = PhaseRecoverAndFinish();
            Crumb("phase-2 ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True,
                "post-reload phase failed: " + phase2.Failure);
        }

        // Phase 1 (pre-reload): the four read commands and the Run's drive
        // to the compile wait. The fixture host is Installed before the Run
        // is admitted — the §E2 item 3 owned-Run record is written on the
        // lock-holding state change at admission, and the installed
        // beforeAssemblyReload hook flushes the step_waiting record the
        // recovery pass resumes from. The fixture is deliberately not
        // disposed on the pass path: it must live until the domain dies
        // with it. An abort disposes it instead — the RunRecoveryHostTests
        // phase-1 discipline.
        private static PhaseResult PhaseDriveToCompileWait()
        {
            // An abort must not leave this host installed: its update
            // subscription would keep driving this Store through every later
            // test of the domain, and its exclusion would keep those tests
            // out.
            RunnerHostFixture? fixture = null;
            RunStatusTracker? tracker = null;
            try
            {
                var build = RegistryBuildHost.Rebuild(
                    RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                    RegistryOperationStage.Production(),
                    new RegistryPublication());
                Assert.IsFalse(build.Stale,
                    "the production Registry build must not be stale: " +
                    Describe(build.SettingsDiagnostics));
                Assert.IsNotNull(build.Snapshot,
                    "the production Registry build must publish a snapshot");
                fixture = new RunnerHostFixture(StoreRoot)
                {
                    LiveRequirements = true,
                    OpWeaveBusy = true,
                    SnapshotOverride = build.Snapshot,
                };
                fixture.Install();
                var host = fixture.Host;
                tracker = new RunStatusTracker(
                    host.Events,
                    id => host.Store.Load(id),
                    () => host.Store.List(new RunQuery()));
                var api = BuildService(host, fixture, build, tracker);

                // 1. search_operations discovers every Operation the
                // workflow uses, one query per group the plan names.
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
                AssertSearchFinds(api,
                    new SearchOperationsRequest(Category: "Script", Limit: 50),
                    "unity.script.create_from_template", "unity.script.compile");

                // 2. describe_operation answers the full contract for each
                // of them — the port names the workflow wires.
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
                AssertDescribedPorts(api, "unity.script.create_from_template",
                    inputs: new[] { "template", "className", "namespace", "folder" },
                    outputs: new[] { "script", "typeName" });
                AssertDescribedPorts(api, "unity.script.compile",
                    inputs: Array.Empty<string>(), outputs: new[] { "result" });

                // 3. validate_workflow compiles the schema-v1 document the
                // Agent assembled: folder → scene → GameObject → Prefab →
                // Material → save → script → compile.
                var workflow = FourGroupWorkflow(AssetsRoot);
                var validated = api.ValidateWorkflow(
                    new ValidateWorkflowRequest(workflow, Path: null));
                AssertOk(validated, "validate_workflow");
                var summary = (JObject)validated.Data!;
                var planHash = (string?)summary["planHash"];
                Assert.IsNotNull(planHash,
                    "validate_workflow must answer the planHash of the compiled Plan");
                var approvals = summary["approvals"] as JArray;
                Assert.IsNotNull(approvals,
                    "the Plan summary must carry the approvals list");
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

                // 5. get_run_status polls the journaled §E8 snapshot while
                // host ticks drive the Run — a Waiting Run still reads
                // "running" (SPEC §E4), so the loop watches the step list
                // for the compile step's own "waiting" state: the wait
                // that cannot finish in this domain.
                var ticks = 0;
                for (;;)
                {
                    var polled = api.GetRunStatus(new GetRunStatusRequest(runId));
                    AssertOk(polled, "get_run_status");
                    var status = (JObject)polled.Data!;
                    var state = (string?)status["state"] ?? string.Empty;
                    if (state is "succeeded" or "failed" or "cancelled" or "needs_review")
                    {
                        tracker.Dispose();
                        fixture.Dispose();
                        return PhaseResult.Fail(
                            "the Run ended in this domain instead of parking on the " +
                            "compile wait: " +
                            status.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    var steps = status["steps"] as JArray;
                    var compileWaiting = steps is not null && steps
                        .OfType<JObject>()
                        .Any(s => (string?)s["state"] == "waiting" &&
                                  string.Equals(
                                      (string?)s["node"], "compile", StringComparison.Ordinal));
                    if (compileWaiting)
                    {
                        break;
                    }
                    if (++ticks > MaxTicks)
                    {
                        tracker.Dispose();
                        fixture.Dispose();
                        return PhaseResult.Fail(
                            "the Run never reached the compile wait: " +
                            status.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    var tick = host.Tick();
                    Assert.IsTrue(tick.Ok,
                        "host tick failed: " + Describe(tick.Diagnostics));
                }

                // Persist what the next domain needs before the sentinel is
                // armed — after this point nothing in memory may be read
                // again on the other side of the reload. The state write
                // also arms the probe-state nonce: from here on the probe
                // root is live in-flight state a replayed setup or
                // cleanup must not reclaim (T-W2.4-07).
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(StateFile, runId + "\n");
                ArmProbeNonce();
                ArmDomainSentinel();
                Crumb("phase-1 complete: run " + runId +
                    " waits on unity.script.compile");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                tracker?.Dispose();
                fixture?.Dispose();
                return PhaseResult.Fail(
                    "phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2 (post-reload): a fresh fixture over the same Run Store
        // runs the SPEC §F4 item 1 recovery pass — the OS handle the
        // previous domain held is confirmed through the SessionState record
        // (SPEC §E2 item 3), the unfinished Run is re-attached and resumed
        // — then drives it to Succeeded through get_run_status alone and
        // reads back the journal and §F7 change list.
        private static PhaseResult PhaseRecoverAndFinish()
        {
            RunnerHostFixture? fixture = null;
            RunStatusTracker? tracker = null;
            try
            {
                if (!File.Exists(StateFile))
                {
                    return PhaseResult.Fail("the persisted run id is missing");
                }
                // The session token proves the run id this domain reads
                // is the state THIS session's phase-1 wrote — residue of
                // an earlier session must fail closed here, never feed
                // phase-2 a silent pass (T-W2.4-07).
                if (!ProbeStateIsLive())
                {
                    return PhaseResult.Fail(
                        "the phase-1 probe state is stale — the nonce on " +
                        "disk is not this Editor session's, so what " +
                        "phase-2 would read is residue, not the state " +
                        "this session's phase-1 wrote");
                }
                var runId = File.ReadAllLines(StateFile)[0];

                var build = RegistryBuildHost.Rebuild(
                    RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                    RegistryOperationStage.Production(),
                    new RegistryPublication());
                Assert.IsFalse(build.Stale,
                    "the production Registry build must not be stale: " +
                    Describe(build.SettingsDiagnostics));
                fixture = new RunnerHostFixture(StoreRoot)
                {
                    LiveRequirements = true,
                    OpWeaveBusy = true,
                    SnapshotOverride = build.Snapshot,
                };
                fixture.Install();
                var host = fixture.Host;
                var recovered = host.RecoverRuns();
                Assert.IsTrue(recovered.Ok,
                    "the recovery pass failed: " + Describe(recovered.Diagnostics));
                var verdict = recovered.Value?.Runs
                    .FirstOrDefault(r => r.RunId == runId);
                Assert.IsNotNull(verdict,
                    "the recovery pass did not re-attach the Run");
                Assert.AreEqual(RunRecoveryOutcome.Resumed, verdict!.Outcome,
                    "the interrupted Run must resume, not end NeedsReview");

                tracker = new RunStatusTracker(
                    host.Events,
                    id => host.Store.Load(id),
                    () => host.Store.List(new RunQuery()));
                var api = BuildService(host, fixture, build, tracker);

                // The resumed Run's compile wait lands on its first Poll —
                // the compilation finished inside the reload — and the
                // remaining ticks take it to the terminal record.
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
                    if (++ticks > MaxTicks)
                    {
                        return PhaseResult.Fail(
                            "the resumed Run did not reach a terminal state: " +
                            status.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                    var tick = host.Tick();
                    Assert.IsTrue(tick.Ok,
                        "host tick failed: " + Describe(tick.Diagnostics));
                }
                Assert.AreEqual("succeeded", state,
                    "the Run must succeed: " +
                    status!.ToString(Newtonsoft.Json.Formatting.None));

                var statusSteps = status["steps"] as JArray;
                Assert.IsNotNull(statusSteps, "the status must carry the step list");
                foreach (var step in statusSteps!)
                {
                    Assert.AreEqual("succeeded", (string?)((JObject)step!)["state"],
                        "step must succeed: " +
                        step.ToString(Newtonsoft.Json.Formatting.None));
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
                var logEntries = logData["logs"] as JArray;
                Assert.IsNotNull(logEntries,
                    "get_run_logs must answer the §F6 log list");
                foreach (var item in logEntries!)
                {
                    var entry = item as JObject;
                    Assert.IsNotNull(entry, "every §F6 log entry must be an object");
                    Assert.IsNotNull(entry!["ts"], "a §F6 log entry must carry ts");
                    Assert.IsNotNull(entry["level"], "a §F6 log entry must carry level");
                    Assert.IsNotNull(entry["message"],
                        "a §F6 log entry must carry message");
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
                    AssetsRoot + "/E2EScene.unity",
                    AssetsRoot + "/E2EProbe.prefab",
                    AssetsRoot + "/E2EMaterial.mat",
                    AssetsRoot + "/E2EProbe.cs",
                })
                {
                    Assert.That(changedPaths, Does.Contain(artifact),
                        "the change list must record the artifact " + artifact);
                }
                Assert.That(
                    RunnerHostFixture.JournalHas(
                        host.RunStoreRoot, runId, "\"type\":\"run_finished\""),
                    Is.True,
                    "the journal must record the run_finished record");

                // The artifacts the §F7 change list reported exist on disk —
                // the created scene stays loaded across the reload.
                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(
                        AssetsRoot + "/E2EScene.unity"),
                    "the created scene asset must exist");
                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<GameObject>(
                        AssetsRoot + "/E2EProbe.prefab"),
                    "the created Prefab asset must exist");
                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<Material>(
                        AssetsRoot + "/E2EMaterial.mat"),
                    "the created Material asset must exist");
                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<MonoScript>(
                        AssetsRoot + "/E2EProbe.cs"),
                    "the generated script must exist as a MonoScript");
                var created = EditorSceneManager.GetSceneByPath(
                    AssetsRoot + "/E2EScene.unity");
                Assert.IsTrue(created.IsValid() && created.isLoaded,
                    "the created scene must still be loaded");
                var probe = created.GetRootGameObjects()
                    .FirstOrDefault(g => g.name == "E2EProbe");
                Assert.IsNotNull(probe,
                    "the created scene must contain the created GameObject");
                Assert.IsTrue(
                    UnityEditor.PrefabUtility.IsAnyPrefabInstanceRoot(probe!),
                    "the GameObject must be the connected Prefab instance");

                Crumb("phase-2 complete: run " + runId + " succeeded");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail(
                    "phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                tracker?.Dispose();
                fixture?.Dispose();
            }
        }

        // ── the §H4 approval boundary ─────────────────────────────────────

        // The same workflow plus one Ask effect — unity.asset.delete is Ask
        // for the Agent under the §H4 default policy — parks in
        // AwaitingApproval at admission, before any step runs, so no domain
        // reload is involved and the instance lifecycle of the T-W3.2-05
        // approval test applies.
        [Test]
        public void ApprovalRequiringNode_StopsAwaitingApproval_AgentCannotApprove()
        {
            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale,
                "the production Registry build must not be stale: " +
                Describe(build.SettingsDiagnostics));
            Assert.IsNotNull(build.Snapshot,
                "the production Registry build must publish a snapshot");
            RunnerHostFixture? fixture = null;
            RunStatusTracker? tracker = null;
            try
            {
                fixture = new RunnerHostFixture(StoreRoot)
                {
                    LiveRequirements = true,
                    SnapshotOverride = build.Snapshot,
                };
                var host = fixture.Host;
                tracker = new RunStatusTracker(
                    host.Events,
                    id => host.Store.Load(id),
                    () => host.Store.List(new RunQuery()));
                var api = BuildService(host, fixture, build, tracker);

                var workflow = ApprovalWorkflow(AssetsRoot);

                // validate_workflow reports the approval the Plan requires.
                var validated = api.ValidateWorkflow(
                    new ValidateWorkflowRequest(workflow, Path: null));
                AssertOk(validated, "validate_workflow");
                var summary = (JObject)validated.Data!;
                var planHash = (string?)summary["planHash"];
                Assert.IsNotNull(planHash,
                    "validate_workflow must answer a planHash");
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

                // get_run_status shows the pending approval and the GUI
                // hint — the only approval surface the Agent ever sees.
                var status = api.GetRunStatus(new GetRunStatusRequest(runId));
                AssertOk(status, "get_run_status");
                var statusData = (JObject)status.Data!;
                Assert.AreEqual("awaiting_approval", (string?)statusData["state"]);
                Assert.That(
                    statusData["approvals"] as JArray,
                    Is.Not.Null.And.Count.GreaterThan(0),
                    "the status must list the pending approval");
                Assert.AreEqual(ApprovalHintText, (string?)statusData["hint"],
                    "the hint must direct the Agent to the Unity Run Viewer");

                // Nothing the Agent can call moves the Run forward: no
                // tick, no status read, no command admits the step. The
                // fixture host is driven three times to prove the wait is
                // real, not a race.
                for (var i = 0; i < 3; i++)
                {
                    var tick = host.Tick();
                    Assert.IsTrue(tick.Ok,
                        "host tick failed: " + Describe(tick.Diagnostics));
                }
                Assert.AreEqual(RunState.AwaitingApproval,
                    host.Coordinator.StateOf(runId!),
                    "the Run must still be waiting for a human");
                var again = api.GetRunStatus(new GetRunStatusRequest(runId));
                Assert.AreEqual("awaiting_approval",
                    (string?)((JObject)again.Data!)["state"]);

                // SPEC §H4 item 5: the Agent surface has no approve/reject
                // or NeedsReview-resolution member at all — the Run cannot
                // be self-approved because no such command exists to call.
                var offenders = typeof(AgentApiService)
                    .GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Select(m => m.Name)
                    .Where(n =>
                        n.Contains("Approv", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Reject", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Resolve", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Review", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Decide", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Assert.That(offenders, Is.Empty,
                    "AgentApiService must expose no approval or review decision " +
                    "member: " + string.Join(", ", offenders));

                // SPEC §0.6 SG-14: the Agent's own cancel command cannot
                // end the approval wait either — cancel_workflow accepts
                // only Queued/Running/Waiting/Suspended, and
                // AwaitingApproval is refused OW-RUN-014 without touching
                // state, approval or journal.
                var cancelled = api.CancelWorkflow(new CancelWorkflowRequest(runId));
                Assert.IsFalse(cancelled.Ok,
                    "cancel_workflow on AwaitingApproval must be refused");
                var refusal = (JObject)cancelled.ToWire()["errors"]![0]!;
                Assert.AreEqual("OW-RUN-014", (string?)refusal["code"]);
                Assert.AreEqual("error", (string?)refusal["severity"]);
                Assert.AreEqual(false, (bool?)refusal["retryable"]);
                Assert.AreEqual(RunState.AwaitingApproval,
                    host.Coordinator.StateOf(runId!),
                    "the refused cancel must leave the Run waiting for a human");

                // The sanctioned end is the human decision: the approval
                // service the Run Viewer drives rejects the pending
                // approval (SPEC §E2 IRunCoordinator.Reject — human only),
                // and the Run ends cancelled without having run a step.
                var rejected = host.Approvals.Reject(
                    runId!, planHash!, "e2e: not approved");
                Assert.IsTrue(rejected.Ok, "the human reject must succeed");
                var ended = api.GetRunStatus(new GetRunStatusRequest(runId));
                AssertOk(ended, "get_run_status");
                Assert.AreEqual("cancelled",
                    (string?)((JObject)ended.Data!)["state"],
                    "the rejected approval wait must end as cancelled");
            }
            finally
            {
                tracker?.Dispose();
                fixture?.Dispose();
            }
        }

        // ── the fixture and the service under test ─────────────────────────

        // The production backends bound to the fixture's seams — the same
        // wiring AgentApiInstaller.Install performs over the production
        // host, except the catalog reads the build this test made rather
        // than the ambient publication (SPEC §H1 test 1 — no Pipeline).
        private static AgentApiService BuildService(
            OpWeaveRunnerHost runnerHost,
            RunnerHostFixture fixture,
            RegistryBuildResult build,
            RunStatusTracker tracker)
        {
            var root = runnerHost.RunStoreRoot;
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
                    ? Path.Combine(
                        RunnerHostFixture.ProjectRoot, "Packages", "com.opweave.workflow")
                    : packageRoot!,
                "Schemas~", "opweave-1.0.schema.json");
        }

        // ── the workflow documents ───────────────────────────────────────────

        // The schema-v1 workflow an Agent assembles from the described
        // contracts: one asset folder, one scene in it, one GameObject in
        // the scene, one Prefab saved from that GameObject, one Material,
        // the scene save that persists them, one generated MonoBehaviour
        // script and the compile wait — the four standard groups in one
        // completed Run. The compile step is last: it is the only Waitable,
        // and the domain reload it causes is the boundary the two phases
        // straddle. All artifact paths live under the unique
        // <paramref name="root"/> the teardown deletes.
        private static JObject FourGroupWorkflow(string root)
        {
            var scenePath = root + "/E2EScene.unity";
            var prefabPath = root + "/E2EProbe.prefab";
            var materialPath = root + "/E2EMaterial.mat";
            return new JObject
            {
                ["schemaVersion"] = "1.0",
                ["id"] = "example.agent-e2e-four-groups",
                ["title"] = "Agent E2E: scene + prefab + asset + compile in one run",
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
                    new JObject
                    {
                        ["id"] = "script",
                        ["operation"] = "unity.script.create_from_template",
                        ["version"] = 1,
                        ["parameters"] = new JObject
                        {
                            ["template"] = "mono_behaviour",
                            ["className"] = "E2EProbe",
                            ["namespace"] = "OpWeave.E2E",
                            ["folder"] = root,
                        },
                    },
                    new JObject
                    {
                        ["id"] = "compile",
                        ["operation"] = "unity.script.compile",
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
                    Edge("save", "script"),
                    Edge("script", "compile"),
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
                    new JObject
                    {
                        ["id"] = "createdScript",
                        ["from"] = new JObject { ["node"] = "script", ["port"] = "script" },
                    },
                    new JObject
                    {
                        ["id"] = "compileResult",
                        ["from"] = new JObject { ["node"] = "compile", ["port"] = "result" },
                    },
                },
            };
        }

        // The approval-bearing counterpart: the same workflow, plus a
        // unity.asset.delete node taking the Material — Ask for the Agent
        // under the §H4 default policy, so the Plan carries an approval the
        // admission gate parks before any step runs.
        private static JObject ApprovalWorkflow(string root)
        {
            var workflow = FourGroupWorkflow(root);
            workflow["id"] = "example.agent-e2e-four-approval";
            workflow["title"] = "Agent E2E: an approval the Agent cannot grant";
            var nodes = (JArray)workflow["nodes"]!;
            var gone = new JObject
            {
                ["id"] = "gone",
                ["operation"] = "unity.asset.delete",
                ["version"] = 1,
            };
            // Insert the delete between material and save, rewiring the
            // control chain material → save into material → gone → save.
            var insertAt = 0;
            for (var i = 0; i < nodes.Count; i++)
            {
                if (string.Equals(
                        (string?)((JObject)nodes[i]!)["id"], "save", StringComparison.Ordinal))
                {
                    insertAt = i;
                    break;
                }
            }
            nodes.Insert(insertAt, gone);
            var control = (JArray)workflow["controlEdges"]!;
            for (var i = 0; i < control.Count; i++)
            {
                var edge = (JObject)control[i]!;
                if (string.Equals((string?)edge["from"], "material", StringComparison.Ordinal) &&
                    string.Equals((string?)edge["to"], "save", StringComparison.Ordinal))
                {
                    control[i] = Edge("gone", "save");
                    break;
                }
            }
            control.Insert(0, Edge("material", "gone"));
            var data = (JArray)workflow["dataEdges"]!;
            data.Add(Edge("material", "material", "gone", "asset"));
            return workflow;
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
