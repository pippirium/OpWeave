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
using System.Reflection;
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

        // A per-Editor-session nonce phase-1 arms in SessionState and on
        // disk under the test-owned Library dir (the At03NoDuplicatesTests
        // discipline, T-W2.4-07): both survive a domain reload but die
        // with the Editor, so a probe file whose nonce still matches the
        // armed one is THIS session's live in-flight state a replayed
        // setup must never reclaim — anything else is stale residue. The
        // nonce is the session-token the stale-resume re-drive and the
        // post-reload gate check.
        private const string ProbeSessionKey = "OpWeave.At01Accept.probe-state";

        private static string NonceFile => Path.Combine(TestOwnedDir, "probe.nonce");

        // T-W2.4-07: bound on phase-1 re-drive legs after a predecessor's
        // stale resume is detected. leg is a method local, so a real
        // reload resets it; the live-nonce probe check is what actually
        // ends the loop (the ChangeSetDomainReloadTests pattern).
        private const int MaxReDriveLegs = 3;

        private static void Crumb(string message) => Debug.Log("[W4507] " + message);

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
        // lock holds it: a running script compile always ends in a
        // reload queued behind the lock, and a requested reload is
        // already pending — either is one the drain must cross, never
        // forward to the next fixture whose registry-freshness checks
        // read isCompiling directly (SPEC §C3 rule 9: a stale build).
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

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
                    "this Unity version — the teardown cannot prove a " +
                    "deferred reload is drained");
            }
        }

        // UTF replays the before/after commands from their first
        // statement on every domain a [UnityTest] crosses, so the probe
        // nonce gates the setup: a replay around the in-flight test
        // restores nothing and deletes nothing (the At03NoDuplicatesTests
        // discipline). What the commands may NEVER yield is an
        // unconditional emitter — a bare `yield return new
        // RecompileScripts(false)` whose ExpectDomainReload=false keeps
        // UTF from recording any resume state, so a real reload crossing
        // the unlock abandons the test on cleared locals (the stale-PC
        // silent-PASS vector, T-W2.4-07). The compile the probe-folder
        // deletion queues is instead drained at this command's own
        // bounded conditional drain — DrainedScriptReload, yielded only
        // after ReloadPending() proves a reload is armed, with UTF's
        // resume bookkeeping armed and the drain rebuilt on resume —
        // so a [Test] body like Steps6To9 finds a drained editor: its
        // RegistryBuildHost.Rebuild would read isCompiling true and
        // mark the build stale (SPEC §C3 rule 9) over a pending compile
        // nobody drained.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            if (ProbeStateIsLive())
            {
                Crumb("setup replayed around the in-flight test — " +
                    "the live probe state is left untouched");
                yield break;
            }
            Cleanup();
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
            // below really does reclaim the probe state.
            DisarmProbeNonce();
            Cleanup();
            // The teardown owns the deferred reload its probe-folder
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

        // The session-token pair the nonce proves: the file under the
        // test-owned Library dir carries the nonce the compile-probe
        // phase armed in SessionState, and both die together on any
        // cleanup that legitimately reclaims the dir — a file without
        // the armed session's nonce is residue of an earlier Editor
        // session, never this run's phase-1.
        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(TestOwnedDir);
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

        // The probe-state cleanup — safe inside UnitySetUp and
        // UnityTearDown, which must never schedule a reload. A replayed
        // command around a live test leaves the probe state untouched:
        // only a dir whose nonce is not the armed one is stale residue.
        private static bool Cleanup()
        {
            if (ProbeStateIsLive())
            {
                return false;
            }
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

        // The sanctioned setup/teardown drain (the At01AgentStepsTests
        // idiom, T-W2.4-07): a queued compile surfaces as isCompiling a
        // frame or two after Refresh returns, so the wait only trusts a
        // sustained quiet spell (the QuietSettleSec convention) — quiet
        // flags reset on every busy frame and a periodic Refresh flushes
        // the import queue so a queued compile surfaces inside the
        // window, never behind it. A pending compile or requested reload
        // is crossed only at DrainedScriptReload — the bounded
        // instruction that unlocks once the pending state is proven;
        // never an unconditional RecompileScripts(false) unlock, which
        // releases the runner's reload lock even when nothing is queued
        // and arms UTF's resume bookkeeping with a stale program counter.
        // The instruction the nested enumerator produces is yielded
        // DIRECTLY from the caller's own top level through its MoveNext
        // relay — a nested yield would never reach Perform — and the
        // whole sequence is idempotent: a crossed reload resumes the
        // caller's enumerator at its yield with every local reset (or
        // replays the command), so the caller rebuilds the drain when it
        // arrives null rather than relaying a dead reference.
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
                        "At01CustomOperationAcceptanceTests." + phase +
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
                "At01CustomOperationAcceptanceTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "At01CustomOperationAcceptanceTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "At01CustomOperationAcceptanceTests." + phase +
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
                            "At01CustomOperationAcceptanceTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "At01CustomOperationAcceptanceTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Steps2And3_Compile_ThenRegistryDiffShowsAddedCustom()
        {
            // A real source addition drives the recompile the step-2 pass
            // condition measures (an untouched project compiles nothing);
            // the nonce arms THIS session's probe identity the
            // stale-resume re-drive and the post-reload gate check.
            ArmProbeNonce();
            Directory.CreateDirectory(CompileProbeDiskDir);
            File.WriteAllText(
                Path.Combine(CompileProbeDiskDir, "CompileProbe.cs"), CompileProbeSource);
            AssetDatabase.Refresh();
            ArmDomainSentinel();

            // T-W2.4-07 stale-resume self-heal: a predecessor's captured
            // UTF resume PC applies to this fresh enumerator and lands it
            // at one of the two post-yield states inside this loop — with
            // every local (leg included) reset to default. Both
            // continuations drain, then MUST pass the this-session probe
            // check: a probe phase-1 never wrote re-drives the probe
            // write + compile + reload instead of skipping silently into
            // the registry-diff assertions. If the probe still cannot be
            // produced after the bound, the loop exits and the live-nonce
            // gate below fails loudly (the ChangeSetDomainReloadTests
            // pattern).
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the probe compile did not produce a domain reload");

                if (ProbeStateIsLive() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // Deleting the compiled probe guarantees the re-write
                // queues a real compile rather than a no-op Refresh.
                Cleanup();
                ArmProbeNonce();
                Directory.CreateDirectory(CompileProbeDiskDir);
                File.WriteAllText(
                    Path.Combine(CompileProbeDiskDir, "CompileProbe.cs"),
                    CompileProbeSource);
                AssetDatabase.Refresh();
                ArmDomainSentinel();
            }
            Assert.IsTrue(
                ProbeStateIsLive(),
                "the phase-1 probe nonce is not this Editor session's — " +
                "a skipped phase-1 must fail closed, never pass silently");

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
