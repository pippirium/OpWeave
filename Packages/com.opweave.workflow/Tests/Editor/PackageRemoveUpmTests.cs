#nullable enable

// T-W4.2-05 [Edit] tests: local:T-W4.2-05:package-remove-reconcile — the
// real-UPM half of the SPEC §F5 unity.package.remove row. A default
// registry probe package (com.unity.editorcoroutines, installed
// test-side by a manifest edit plus Client.Resolve — never through the
// Operation) supplies the install the real manifest.json,
// packages-lock.json and registered package list judge against; the
// probe's own Editor assembly forces the compile and domain reload the
// interrupted-execution cases cross (the reload is proven by
// DomainReloadCounter, the persisted snapshot/token file playing the
// Runner journal's role). The end-to-end half drives the real Operation
// — Preflight's persisted evidence, Execute's Client.Remove + resume
// token, Poll's post-reload registered-list judgement (SPEC §T.4 L-07)
// and Reconcile's Completed — over the removal the probe's compile
// triggers. Injected-seam verdicts live in
// PackageRemoveOperationTests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W4.2-05:package-remove-reconcile — real-UPM evidence and
    /// domain-reload coverage for <c>unity.package.remove</c>.
    /// </summary>
    internal sealed class PackageRemoveUpmTests
    {
        // The default-allowlisted registry package driving the real
        // Preflight → Execute → Poll → Reconcile path. Its Editor
        // assembly makes both the install and the removal compile — each
        // compile ends in the domain reload the interrupted-execution
        // cases cross.
        private const string ProbePackage = "com.unity.editorcoroutines";
        private const string ProbeVersion = "1.0.0";

        private const string ProbeDirName = "OpWeavePackageRemoveProbe";
        private const string BackupDirName = "OpWeavePackageRemoveBackup";

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");

        private static string LockPath =>
            Path.Combine(ProjectRoot, "Packages", "packages-lock.json");

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", ProbeDirName);

        private static string BackupDir =>
            Path.Combine(ProjectRoot, "Library", BackupDirName);

        private static string ManifestBackupPath =>
            Path.Combine(BackupDir, "orig-manifest.json");

        private static string LockBackupPath =>
            Path.Combine(BackupDir, "orig-packages-lock.json");

        // The persisted-evidence file a domain reload does not lose —
        // the test's stand-in for the Runner-journaled preflight snapshot
        // and StepWaiting resume token (SPEC §F5, §G4).
        private static string StatePath => Path.Combine(ProbeDir, "state.json");

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[F5-PackageRemove] " + message);

        // ---------- project state capture / restore ----------

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

        // The install-set names the baseline checks membership against —
        // the registry probe, the only package the fixture ever borrows.
        private static ISet<string> InstalledPackages()
        {
            var installed = new HashSet<string>(StringComparer.Ordinal);
            if (PackageInstalled(ProbePackage))
            {
                installed.Add(ProbePackage);
            }
            return installed;
        }

        private static byte[]? ReadBytesOrNull(string path) =>
            File.Exists(path) ? File.ReadAllBytes(path) : null;

        // Byte-for-byte capture of the project's package state before the
        // test borrows it — the same backup form
        // PackageResolvedReloadTests persists across domain reloads.
        private static void CaptureOriginal()
        {
            Directory.CreateDirectory(BackupDir);
            var capture = PackageProbePlan.Snapshot(
                File.ReadAllBytes(ManifestPath),
                ReadBytesOrNull(LockPath));
            if (capture.Lock is { } lockBytes)
            {
                File.WriteAllBytes(LockBackupPath, lockBytes);
            }
            else if (File.Exists(LockBackupPath))
            {
                File.Delete(LockBackupPath);
            }
            File.WriteAllBytes(ManifestBackupPath, capture.Manifest);
        }

        private static PackageProbePlan.Capture? LoadCapture()
        {
            if (!File.Exists(ManifestBackupPath))
            {
                return null;
            }
            return PackageProbePlan.Snapshot(
                File.ReadAllBytes(ManifestBackupPath),
                ReadBytesOrNull(LockBackupPath));
        }

        private static void RestoreOriginalSync()
        {
            var capture = LoadCapture();
            if (capture is null)
            {
                return;
            }
            var manifestChanged = ApplyRestore(
                ManifestPath,
                PackageProbePlan.ManifestRestore(
                    capture, ReadBytesOrNull(ManifestPath)),
                capture.Manifest);
            var lockChanged = ApplyRestore(
                LockPath,
                PackageProbePlan.LockRestore(
                    capture, ReadBytesOrNull(LockPath)),
                capture.Lock);
            if (manifestChanged || lockChanged)
            {
                try { Client.Resolve(); }
                catch (Exception) { /* a partial restore still settles below */ }
            }
        }

        private static bool ApplyRestore(
            string path, PackageProbePlan.RestoreAction action, byte[]? bytes)
        {
            switch (action)
            {
                case PackageProbePlan.RestoreAction.Write:
                    File.WriteAllBytes(path, bytes!);
                    return true;
                case PackageProbePlan.RestoreAction.Delete:
                    try { File.Delete(path); }
                    catch (Exception) { return false; }
                    return true;
                default:
                    return false;
            }
        }

        // The settle check setup and teardown wait on: the manifest and
        // lockfile hold the captured bytes again and the borrowed package
        // is not registered (the admission check below refuses a project
        // that already depended on it).
        private static bool ProjectMatchesOriginal()
        {
            var capture = LoadCapture();
            return capture is not null &&
                PackageProbePlan.MatchesCapture(
                    capture,
                    ReadBytesOrNull(ManifestPath),
                    ReadBytesOrNull(LockPath),
                    InstalledPackages(),
                    ProbePackage);
        }

        private static void DeleteProbeDir()
        {
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a held file is left for the next run */ }
            }
        }

        private static void DeleteBackupDir()
        {
            if (Directory.Exists(BackupDir))
            {
                try { Directory.Delete(BackupDir, recursive: true); }
                catch (Exception) { /* a held file is left for the next run */ }
            }
        }

        // ---------- persisted-evidence helpers ----------

        // The journal-side write: the preflight snapshot text plus the
        // reload-count baseline at capture time, persisted like the
        // Runner's StepStarted snapshot record — the post-reload phase
        // gets nothing but durable state.
        private static void PersistState(JsonValue snapshot, string? tokenJson)
        {
            var state = new JObject
            {
                ["snapshot"] = snapshot.ToToken(),
                ["reloads"] = DomainReloadCounter.Current,
                ["session"] = ArmProbeNonce(),
            };
            if (tokenJson is not null)
            {
                state["token"] = JToken.Parse(tokenJson);
            }
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(StatePath, state.ToString());
            Crumb("persisted evidence state");
        }

        private static bool TryReadState(
            out JsonValue? snapshot, out int reloadBaseline, out JsonValue? token)
        {
            snapshot = null;
            token = null;
            reloadBaseline = 0;
            if (!File.Exists(StatePath))
            {
                return false;
            }
            var state = JObject.Parse(File.ReadAllText(StatePath));
            snapshot = JsonValue.From(state["snapshot"]!);
            reloadBaseline = (int)state["reloads"]!;
            if (state["token"] is { } persistedToken)
            {
                token = JsonValue.From(persistedToken);
            }
            return true;
        }

        private static JsonStepContext Context(
            string? name = null, JsonValue? resumeToken = null)
        {
            var inputs = new Dictionary<string, InputValue>();
            if (name is not null)
            {
                inputs[UnityPackageRemoveOperation.NamePort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(name)));
            }
            return new JsonStepContext(
                inputs,
                new StepChangeRecorder(0, "remove"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                resumeToken);
        }

        private static string Describe(ReconcileResult verdict) =>
            verdict switch
            {
                ReconcileResult.Completed _ => "Completed",
                ReconcileResult.NotApplied _ => "NotApplied",
                ReconcileResult.Unknown unknown => "Unknown(" + unknown.Reason + ")",
                _ => verdict?.ToString() ?? "null",
            };

        // ---------- pending-reload proof + per-session identity ----------

        // The same pending-request flag WaitForDomainReload.Perform
        // polls — Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the wait
        // reaches it through reflection. A reload already armed behind
        // the runner's reload lock surfaces here while isCompiling is
        // false, which is exactly the deferred state the quiet window
        // missed. The probe is resolved deterministically from the
        // UnityEditor assembly and its shape is proven once at fixture
        // start: a Unity version without it fails the fixture outright —
        // there is no isCompiling-only fallback.
        private const string ScriptReloadProbeType =
            "UnityEditorInternal.InternalEditorUtility";
        private const string ScriptReloadProbeMethod =
            "IsScriptReloadRequested";

        private static readonly System.Reflection.MethodInfo? s_scriptReloadRequested =
            typeof(EditorApplication).Assembly
                .GetType(ScriptReloadProbeType)
                ?.GetMethod(
                    ScriptReloadProbeMethod,
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

        // Non-null and shaped as OneTimeSetUp proved — the fixture does
        // not run at all on a Unity version whose probe is absent.
        private static bool ScriptReloadRequested() =>
            (bool)s_scriptReloadRequested!.Invoke(null, null)!;

        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        // The pending proof WaitForDomainReload needs before it is
        // yielded: its Perform throws the moment neither flag is set, so
        // the instruction may only be armed once a reload is provably
        // queued — a requested script reload, or an in-flight compile
        // that ends in one. The wait only waits — it never throws and
        // never yields an edit-mode yield instruction, which a nested
        // enumerator is not allowed to yield; the caller checks
        // ReloadPending() afterwards so teardown can record a timeout
        // and still drain the reloads the failure left queued.
        private static IEnumerator WaitForReloadPending()
        {
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var nextRefreshAt = double.NegativeInfinity;
            while (!ReloadPending() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                if (EditorApplication.timeSinceStartup >= nextRefreshAt)
                {
                    AssetDatabase.Refresh();
                    nextRefreshAt = EditorApplication.timeSinceStartup + 1.0;
                }
                yield return null;
            }
        }

        private static string ReloadTimeoutMessage(string what) =>
            "no domain reload became pending within " + WaitTimeoutSec +
            " s of " + what;

        // The per-Editor-session resume-owner record behind each body
        // boundary: armed only by this test's own pre-boundary statement
        // and SessionState-mirrored across the reload, so a continuation
        // UTF resumes on a stale enumerator position — one that never
        // ran the arming segment — finds the previous test's mark or
        // none and fails the boundary check closed instead of silently
        // continuing into the post-reload phase.
        private const string BoundaryOwnerSessionKey =
            "OpWeave.PackageRemove.boundary-owner";

        private static string CurrentTestFullName =>
            TestContext.CurrentContext.Test.FullName ??
            TestContext.CurrentContext.Test.Name ?? "unknown";

        private static void ArmBoundary(string boundaryId) =>
            SessionState.SetString(
                BoundaryOwnerSessionKey,
                CurrentTestFullName + "|" + boundaryId);

        private static void AssertBoundaryOwned(string boundaryId)
        {
            var owner = SessionState.GetString(BoundaryOwnerSessionKey, "");
            Assert.That(
                owner,
                Is.EqualTo(CurrentTestFullName + "|" + boundaryId),
                "the \"" + boundaryId + "\" reload boundary was not armed by " +
                "this test in this Editor session (armed: \"" + owner +
                "\") — the continuation resumed on a stale position");
        }

        // The persisted evidence's session identity: PersistState writes
        // the nonce into the file's "session" member AND SessionState —
        // both survive the reload, so post-reload phases prove the file
        // belongs to this session's arm (a stale resume that skipped it,
        // a deleted file, or cross-session residue each fail closed).
        private const string ProbeSessionKey =
            "OpWeave.PackageRemove.state-session";

        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeSessionKey, nonce);
            return nonce;
        }

        private static void AssertLiveSession(string what)
        {
            var expected = SessionState.GetString(ProbeSessionKey, "");
            if (!File.Exists(StatePath))
            {
                Assert.Fail(
                    expected.Length == 0
                        ? what + " never persisted in this Editor session (" +
                            StatePath + " is absent)"
                        : what + " " + StatePath +
                            " was deleted after it was written");
                return;
            }
            var session =
                (string?)JObject.Parse(File.ReadAllText(StatePath))["session"];
            if (expected.Length == 0 || session != expected)
            {
                Assert.Fail(
                    what + " is stale — a previous Editor session or an " +
                    "abandoned leg wrote " + StatePath +
                    "; refusing to trust it");
            }
        }

        // The quiet-settle window from PackageResolvedReloadTests: the
        // install/removal's compile may finish inside the synchronous
        // resolve while the runner holds the reload lock, so the flags go
        // quiet before the deferred reload surfaces. Wait on quiet for a
        // settle window, then the caller crosses at the sanctioned
        // boundary — inline, where the saved program counter resumes.
        private static IEnumerator WaitForIdleQuiet()
        {
            var settleDeadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var quietAt = -1.0;
            while (EditorApplication.timeSinceStartup < settleDeadline)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    quietAt = EditorApplication.timeSinceStartup + QuietSettleSec;
                }
                else if (quietAt >= 0.0 &&
                         EditorApplication.timeSinceStartup >= quietAt)
                {
                    break;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup + QuietSettleSec;
                }
                yield return null;
            }
        }

        // ---------- setUp / tearDown ----------

        // The pending-reload proof every wait below arms on is only as
        // strong as the probe it polls: if this Unity version does not
        // carry it, the fixture fails here — before any install —
        // instead of silently degrading to an isCompiling-only check
        // that re-opens the missed-request race the probe exists to
        // close.
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
                    "Unity " + Application.unityVersion + ": " +
                    ScriptReloadProbeType + "." + ScriptReloadProbeMethod +
                    " in the UnityEditor assembly must be a static, " +
                    "parameterless method returning bool");
            }
        }

        // Installing/removing a package makes the Editor emit a benign
        // [Error] log ("Asset Packages/<name>/package.json has no meta
        // file, but it's in an immutable folder") — the expected
        // byproduct the T-W2.12-05 probe documents.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            RestoreOriginalSync();
            DeleteProbeDir();
            CaptureOriginal();

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!ProjectMatchesOriginal() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            if (!ProjectMatchesOriginal())
            {
                AssetDatabase.Refresh();
                yield return WaitForReloadPending();
                Assert.That(ReloadPending(), Is.True,
                    ReloadTimeoutMessage("the manifest/lock restore"));
                yield return new WaitForDomainReload();
            }
            // A reload the restore left queued still fires on the next
            // reload-lock release — drain it from this method's own
            // enumerator (a nested enumerator may not yield
            // WaitForDomainReload) so the test body starts on a settled
            // domain and the reload it crosses is provably the
            // install's own compile.
            while (ReloadPending() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                if (ScriptReloadRequested())
                {
                    yield return new WaitForDomainReload();
                }
                yield return null;
            }
            Assert.That(ProjectMatchesOriginal(), Is.True,
                "the project did not settle on its captured manifest/lock state");

            // The fixture borrows the project's manifest: it is
            // meaningful — and non-destructive — only while the project
            // does not already depend on the package the tests install
            // and remove. An already-present dependency is an unmet
            // environment precondition, never a silent pass.
            var baseline = LoadCapture();
            if (baseline is null)
            {
                Assert.Inconclusive(
                    "precondition unmet: no captured project baseline");
            }
            if (baseline!.Mentions(ProbePackage) ||
                PackageInstalled(ProbePackage))
            {
                Assert.Inconclusive(
                    "precondition unmet: the project already depends on " +
                    ProbePackage + " — the package probe would add and " +
                    "remove a real project dependency");
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            string? restoreFailure = null;
            try
            {
                RestoreOriginalSync();
            }
            catch (Exception e)
            {
                restoreFailure = e.ToString();
            }

            while (!ProjectMatchesOriginal() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            string? pendingFailure = null;
            if (!ProjectMatchesOriginal())
            {
                AssetDatabase.Refresh();
                yield return WaitForReloadPending();
                if (ReloadPending())
                {
                    yield return new WaitForDomainReload();
                }
                else
                {
                    pendingFailure = ReloadTimeoutMessage(
                        "the manifest/lock restore");
                }
            }
            // A reload the install or the restore left queued still
            // fires on the next reload-lock release — cross each one
            // here so the next test's setup meets a settled domain. The
            // WaitForDomainReload sits in this method's own enumerator:
            // the Unity Test Framework rejects it from a nested
            // enumerator. The loop also runs on the failure paths above
            // — a settle or restore failure is recorded and re-reported
            // only after the queue is drained, so a failure cannot
            // leave the probe assembly loaded for the next test
            // without a clear failure of its own.
            while (ReloadPending() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                if (ScriptReloadRequested())
                {
                    yield return new WaitForDomainReload();
                }
                yield return null;
            }
            Assert.That(restoreFailure, Is.Null,
                "manifest/lock restore threw: " + restoreFailure);
            Assert.That(pendingFailure, Is.Null, pendingFailure!);
            Assert.That(ProjectMatchesOriginal(), Is.True,
                "the project was not restored to its captured " +
                "manifest/lock state after the package-remove fixture");

            DeleteProbeDir();
            DeleteBackupDir();
        }

        // ---------- §F5 verdicts over real UPM state ----------

        // SPEC §F5 case 1 for the remove row: the step never ran — the
        // persisted snapshot records proven absence and the name is
        // absent everywhere now, so the verdict is NotApplied (a re-run
        // only earns the UPM "not installed" error, never a mis-delete).
        // A manifest entry written without a resolve is the
        // snapshot-vs-reality divergence — a same-name artifact the
        // recorded evidence never saw — and stays Unknown.
        [Test]
        public void Reconcile_AbsentEverywhere_NotApplied_ThenManifestOnly_Unknown()
        {
            var snapshot = PackageRemoveEvidence.BuildSnapshot(
                ProbePackage, PackageInstallView.Capture());
            var op = new UnityPackageRemoveOperation();

            Assert.That(
                op.Reconcile(Context(), snapshot),
                Is.InstanceOf<ReconcileResult.NotApplied>(),
                "absence everywhere plus recorded absence is NotApplied — " +
                "got " + Describe(op.Reconcile(Context(), snapshot)));

            var manifest = File.ReadAllText(ManifestPath);
            File.WriteAllText(ManifestPath,
                SetManifestDependency(manifest, ProbePackage, ProbeVersion));
            try
            {
                Assert.That(
                    op.Reconcile(Context(), snapshot),
                    Is.InstanceOf<ReconcileResult.Unknown>(),
                    "a package the snapshot recorded as absent cannot be " +
                    "attributed — Unknown, never an automatic re-remove");
            }
            finally
            {
                File.WriteAllText(ManifestPath, manifest);
            }
        }

        // SPEC §F5: the real remove end to end across the two domain
        // reloads the probe's own compile forces — the install crossing
        // and the removal crossing. UTF resurrects a [UnityTest]
        // iterator across each WaitForDomainReload but not its fields —
        // the managed locals a phase builds (the Operation instance,
        // PreflightResult/StepResult, contexts, the display class of a
        // () => assertion message) come back null or default and the
        // first dereference after the boundary is a bare, stackless
        // NullReferenceException — so the iterator holds no work of its
        // own between the crossings: it calls static phases, polls,
        // yields at the sanctioned boundary and asserts on plain text,
        // the L-14 iterator-hygiene pattern of
        // CoordinatorRunLockReloadTests / PackageResolvedReloadTests.
        // Phase A installs the probe as a test-side direct operation
        // (manifest edit + Client.Resolve, never through the Operation).
        // Phase B runs the real Operation's Preflight — its snapshot is
        // the persisted evidence — proves the still-installed package
        // reconciles NotApplied and a manifest-stripped state Unknown,
        // then Executes the real Client.Remove and persists the resume
        // token. Phase C, one domain later, resumes the wait on the
        // persisted token alone (SPEC §C2 item 4, §G4) and reconciles
        // Completed on the persisted snapshot.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator RegistryRemove_ExecuteWaitPoll_SucceedsAcrossDomainReload()
        {
            LogAssert.ignoreFailingMessages = true;

            // Phase A (test-side fixture): install the probe directly —
            // the manifest edit plus Client.Resolve the T-W2.12-05 probe
            // established; the install's compile forces the first domain
            // reload.
            var install = RunPhaseInstallProbe();
            Crumb("install returned ok=" + install.Ok +
                " failure=" + install.Failure);
            Assert.That(install.Ok, Is.True,
                "probe install failed: " + install.Failure);

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!PackageInstalled(ProbePackage) &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(PackageInstalled(ProbePackage), Is.True,
                "the probe package never registered with UPM after the manifest install");
            AssetDatabase.Refresh();
            yield return WaitForIdleQuiet();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the probe install"));
            ArmBoundary("install");
            yield return new WaitForDomainReload();
            AssertBoundaryOwned("install");

            // Phase B (post-install-reload, pre-remove): every result of
            // the real Operation's work lives inside the helper — the
            // iterator keeps only its verdict text.
            var executed = RunPhasePreflightExecuteRemove();
            Crumb("preflight/execute returned ok=" + executed.Ok +
                " failure=" + executed.Failure);
            Assert.That(executed.Ok, Is.True,
                "preflight/execute failed: " + executed.Failure);

            var removeDeadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (PackageInstalled(ProbePackage) &&
                   EditorApplication.timeSinceStartup < removeDeadline)
            {
                yield return null;
            }
            Assert.That(PackageInstalled(ProbePackage), Is.False,
                "the probe package never left the registered list after Client.Remove");
            AssetDatabase.Refresh();
            yield return WaitForIdleQuiet();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the probe removal"));
            ArmBoundary("remove");
            yield return new WaitForDomainReload();
            AssertBoundaryOwned("remove");

            // Phase C (post-remove-reload): the operation is a fresh
            // instance inside each helper and the token reloads from its
            // persisted text — the only state the new domain gets
            // (SPEC §C2 item 4, §G4).
            var resumed = RunPhaseVerifyResume();
            Crumb("resume check returned ok=" + resumed.Ok +
                " failure=" + resumed.Failure);
            Assert.That(resumed.Ok, Is.True,
                "resume check failed: " + resumed.Failure);

            var resolved = false;
            var pollDeadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (EditorApplication.timeSinceStartup < pollDeadline)
            {
                var check = PollPersistedRemoveWait();
                if (check.Done)
                {
                    resolved = true;
                    break;
                }
                Assert.That(check.Failure, Is.Empty,
                    "the resumed remove poll faulted: " + check.Failure);
                yield return null;
            }
            Assert.That(resolved, Is.True,
                "the resumed wait must succeed on the registered-list " +
                "absence judgement (SPEC §T.4 L-07)");

            // And the interrupted-step verdict on the persisted snapshot:
            // the recorded install plus proven absence everywhere is the
            // §F5 row's Completed evidence.
            var reconciled = RunPhaseReconcileRemove();
            Crumb("reconcile returned ok=" + reconciled.Ok +
                " failure=" + reconciled.Failure);
            Assert.That(reconciled.Ok, Is.True,
                "reconcile failed: " + reconciled.Failure);
        }

        // ---------- reload phases (no yields: static state only) ----------

        // A serializable result a phase helper hands back across the
        // reload — an exception before the crossing would otherwise be
        // swallowed by the domain it is thrown in.
        [Serializable]
        private sealed class ProbePhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static ProbePhaseResult Pass() => new ProbePhaseResult { Ok = true };

            public static ProbePhaseResult Fail(string message) =>
                new ProbePhaseResult { Ok = false, Failure = message };
        }

        // One resumed Poll verdict handed back to the iterator: the new
        // resume token a Waiting result carries is re-persisted inside
        // the helper, so the iterator never carries it.
        [Serializable]
        private sealed class PersistedPoll
        {
            public bool Done;
            public string Failure = string.Empty;
        }

        // Phase A: the manifest edit plus Client.Resolve — the test-side
        // install the T-W2.12-05 probe established, never through the
        // Operation. Its compile forces the first domain reload.
        private static ProbePhaseResult RunPhaseInstallProbe()
        {
            try
            {
                var manifest = File.ReadAllText(ManifestPath);
                File.WriteAllText(ManifestPath,
                    SetManifestDependency(manifest, ProbePackage, ProbeVersion));
                Client.Resolve();
                Crumb("probe install armed: " + ProbePackage + "@" + ProbeVersion);
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("install threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail(
                    "install threw an unexpected exception: " + e);
            }
        }

        // Phase B (post-install-reload, pre-remove): the real
        // Operation's Preflight persists the §F5 evidence; on the
        // still-installed package the verdict is the F5 case 1
        // NotApplied, and a manifest-stripped divergence is the row's
        // disagreement Unknown; Execute issues the real Client.Remove
        // and the resume token is persisted like the StepWaiting
        // record's content.
        private static ProbePhaseResult RunPhasePreflightExecuteRemove()
        {
            try
            {
                var op = new UnityPackageRemoveOperation();
                var preflight = op.Preflight(Context(ProbePackage));
                if (preflight.Diagnostics.Count != 0)
                {
                    return ProbePhaseResult.Fail(
                        "Preflight reported " + string.Join(
                            "; ", preflight.Diagnostics.Select(
                                d => d.Code + " " + d.Message)));
                }

                var notApplied = op.Reconcile(Context(), preflight.Snapshot);
                if (notApplied is not ReconcileResult.NotApplied)
                {
                    return ProbePhaseResult.Fail(
                        "the recorded artifact still installed everywhere is " +
                        "NotApplied — got " + Describe(notApplied));
                }

                var stripped = File.ReadAllText(ManifestPath);
                File.WriteAllText(ManifestPath,
                    RemoveManifestDependency(stripped, ProbePackage));
                try
                {
                    var partial = op.Reconcile(Context(), preflight.Snapshot);
                    if (partial is not ReconcileResult.Unknown)
                    {
                        return ProbePhaseResult.Fail(
                            "manifest cleared while still registered is the " +
                            "§F5 disagreement — got " + Describe(partial));
                    }
                }
                finally
                {
                    File.WriteAllText(ManifestPath, stripped);
                }

                var execute = op.Execute(Context(ProbePackage));
                if (execute.Kind != StepResultKind.Waiting)
                {
                    return ProbePhaseResult.Fail(
                        "Execute must suspend on the package-resolution " +
                        "wait — got " + execute.Kind);
                }
                var resumeToken = execute.ResumeToken;
                if (resumeToken is null)
                {
                    return ProbePhaseResult.Fail("Execute produced no resume token");
                }
                PersistState(preflight.Snapshot, resumeToken.CanonicalText);
                Crumb("registry remove executed: resumeToken persisted");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("preflight/execute threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail(
                    "preflight/execute threw an unexpected exception: " + e);
            }
        }

        // Phase C open: the persisted evidence and resume token reload
        // from disk — the only state the new domain gets — and the
        // removal's domain crossing is proven.
        private static ProbePhaseResult RunPhaseVerifyResume()
        {
            try
            {
                if (!TryReadState(out _, out var baseline, out var resumed))
                {
                    return ProbePhaseResult.Fail(
                        "the persisted evidence state is missing after the reload");
                }
                AssertLiveSession("the persisted evidence state");
                if (DomainReloadCounter.Current <= baseline)
                {
                    return ProbePhaseResult.Fail(
                        "the package removal did not cross a domain reload");
                }
                if (resumed is null)
                {
                    return ProbePhaseResult.Fail("no persisted resume token");
                }
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("resume check threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail(
                    "resume check threw an unexpected exception: " + e);
            }
        }

        // One fresh Poll of the persisted resume token — the call a
        // resumed Waitable step makes in this domain. A Waiting result's
        // new token is written back to the persisted state inside the
        // helper: the iterator never carries it.
        private static PersistedPoll PollPersistedRemoveWait()
        {
            var check = new PersistedPoll();
            try
            {
                if (!TryReadState(out _, out _, out var token) || token is null)
                {
                    check.Failure = "the persisted wait state is missing";
                    return check;
                }
                var result = new UnityPackageRemoveOperation()
                    .Poll(Context(resumeToken: token));
                switch (result.Kind)
                {
                    case StepResultKind.Succeeded:
                        check.Done = true;
                        break;
                    case StepResultKind.Waiting:
                        var next = result.ResumeToken;
                        if (next is null)
                        {
                            check.Failure =
                                "the remove poll stayed Waiting without a resume token";
                            break;
                        }
                        PersistPollToken(next);
                        break;
                    default:
                        check.Failure = result.Error is { } error
                            ? error.Code + " " + error.Message
                            : "the remove poll failed without a Diagnostic";
                        break;
                }
            }
            catch (Exception e)
            {
                Crumb("poll threw " + e.GetType().Name + ": " + e.Message);
                check.Failure =
                    "the remove poll threw an unexpected exception: " + e;
            }
            return check;
        }

        // The new resume token a Waiting poll returns is written back
        // over the persisted state — the snapshot and the reload
        // baseline the post-reload phases still need are untouched.
        private static void PersistPollToken(JsonValue token)
        {
            var state = JObject.Parse(File.ReadAllText(StatePath));
            state["token"] = token.ToToken();
            File.WriteAllText(StatePath, state.ToString());
        }

        // Phase C close: the interrupted-step verdict on the persisted
        // snapshot — the recorded install plus proven absence everywhere
        // is the §F5 row's Completed evidence.
        private static ProbePhaseResult RunPhaseReconcileRemove()
        {
            try
            {
                if (!TryReadState(out var snapshot, out _, out _) ||
                    snapshot is null)
                {
                    return ProbePhaseResult.Fail(
                        "the persisted evidence state is missing after the reload");
                }
                AssertLiveSession("the persisted evidence state");
                var verdict = new UnityPackageRemoveOperation()
                    .Reconcile(Context(), snapshot);
                return verdict is ReconcileResult.Completed
                    ? ProbePhaseResult.Pass()
                    : ProbePhaseResult.Fail(
                        "recorded presence plus absence everywhere reconciles " +
                        "Completed — got " + Describe(verdict));
            }
            catch (Exception e)
            {
                Crumb("reconcile threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail(
                    "reconcile threw an unexpected exception: " + e);
            }
        }

        // ---------- helpers ----------

        private static readonly System.Text.RegularExpressions.Regex s_depsBlock =
            new System.Text.RegularExpressions.Regex(
                "\"dependencies\"\\s*:\\s*\\{(?<body>[^{}]*)\\}");
        private static readonly System.Text.RegularExpressions.Regex s_depPair =
            new System.Text.RegularExpressions.Regex(
                "\"(?<n>[^\"]+)\"\\s*:\\s*\"(?<v>[^\"]*)\"");

        private static List<KeyValuePair<string, string>> ParseDeps(string manifestText)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            var match = s_depsBlock.Match(manifestText);
            Assert.That(match.Success, Is.True,
                "manifest.json has no \"dependencies\" object");
            foreach (System.Text.RegularExpressions.Match pair in
                     s_depPair.Matches(match.Groups["body"].Value))
            {
                pairs.Add(new KeyValuePair<string, string>(
                    pair.Groups["n"].Value, pair.Groups["v"].Value));
            }
            return pairs;
        }

        private static string RebuildManifest(
            string manifestText, List<KeyValuePair<string, string>> pairs)
        {
            var match = s_depsBlock.Match(manifestText);
            var body = string.Join(",\n",
                pairs.Select(p => "    \"" + p.Key + "\": \"" + p.Value + "\""));
            return manifestText.Substring(0, match.Index) +
                "\"dependencies\": {\n" + body + "\n  }" +
                manifestText.Substring(match.Index + match.Length);
        }

        private static string SetManifestDependency(
            string manifestText, string name, string value)
        {
            var pairs = ParseDeps(manifestText).Where(p => p.Key != name).ToList();
            pairs.Add(new KeyValuePair<string, string>(name, value));
            return RebuildManifest(manifestText, pairs);
        }

        // The manifest-side half of a removal that never reached the
        // registered list — the §F5 disagreement state: the dependency
        // entry is gone but UPM still holds the package.
        private static string RemoveManifestDependency(
            string manifestText, string name)
        {
            var pairs = ParseDeps(manifestText).Where(p => p.Key != name).ToList();
            return RebuildManifest(manifestText, pairs);
        }
    }
}
