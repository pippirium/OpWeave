#nullable enable

// T-W2.13-02 [Edit] tests: local:T-W2.13-02:sample-install-remove — the
// SPEC §T.2 AT-F01 / §X.4 / §X.9 / §X.11 / §T.1 G-02 integration pass over
// the independent external sample extension package. The package is
// installed and removed through the real Unity Package Manager Client API
// at pinned full-SHA Git URLs — never a local clone or a file: substitute —
// and every install or removal crosses the domain reload its Editor
// assembly forces (SPEC §T.3). UTF iterator locals do not survive that
// boundary — including the display-class instances lambdas allocate at
// method entry for captured variables, so no local declared at method
// scope may be captured: every post-reload phase is a static helper
// that re-derives state from the live domain, and the two values that
// must cross — the reload-counter baseline and the revision-A planHash —
// ride SessionState. The project manifest and lockfile are captured and
// restored byte-exactly exactly like PackageResolvedReloadTests, and a
// settled restore includes the sample's Editor assembly unloaded — the
// removal's pending reload must be crossed, not just resolved.
// Registry assertions read only the atomically published
// RegistryPublication.Shared snapshot and the persisted
// registry-changes diff; provenance comes from the registered
// Operation's PackageId/ResolvedCommit, never from the sample's own
// declared metadata.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.13-02:sample-install-remove — install → register →
    /// validate → execute → invalidate → remove → reinstall of the
    /// independent <c>OpWeave-Sample-Operations</c> Git package.
    /// </summary>
    internal sealed class SampleExtensionInstallRemoveTests
    {
        // ---------- fixture identities ----------

        // The independent external sample extension: a public Git
        // repository UPM installs at a pinned full commit SHA. Two
        // owner-authorized immutable revisions (2026-11-20) supply the
        // source-change invalidation case — revision B changes only the
        // compiled implementation (a behaviour-preserving private
        // helper), so the Descriptor stays identical while the
        // defining-assembly implementationHash differs (SPEC §X.11
        // item 4).
        private const string SampleRepository =
            "https://github.com/pippirium/OpWeave-Sample-Operations.git";
        private const string RevisionA =
            "92865779755002c4b928a2d788d55dceddc99c86";
        private const string RevisionB =
            "c1ec78ebd4eefafb1621eaa15f7d88152509fc23";
        private const string SamplePackage = "com.opweave.sample.operations";
        private const string SampleEditorAssembly =
            "OpWeave.Sample.Operations.Editor";
        private const string SampleOperationId =
            "org.opweave.sample.make_pose";
        private const int SampleOperationVersion = 1;
        private const string SampleProviderId = "org.opweave.sample.types";
        private const string SampleTypeId = "org.opweave.sample.BonePose";

        // The package whose tree must survive the whole suite
        // byte-identical: the §T.2 pass condition is "no file under
        // Packages/com.opweave.workflow/ changes".
        private const string OpWeavePackage = "com.opweave.workflow";

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;
        private const double RunPollDeadlineSec = 120.0;

        // The SessionState slots that carry state across a domain
        // reload: the reload-counter baseline of the crossing just
        // armed, the lazily captured com.opweave.workflow tree digest
        // and the revision-A planHash the source-change test holds.
        private const string ReloadBaselineKey =
            "OpWeave.SampleE2E.ReloadBaseline";
        private const string TreeDigestKey = "OpWeave.SampleE2E.TreeDigest";
        private const string PlanHashKey = "OpWeave.SampleE2E.PlanHash";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");

        private static string LockPath =>
            Path.Combine(ProjectRoot, "Packages", "packages-lock.json");

        private static string ProbeDir =>
            Path.Combine(
                ProjectRoot, "Library", "OpWeaveSampleInstallRemoveProbe");

        private static string BackupDir =>
            Path.Combine(
                ProjectRoot, "Library", "OpWeaveSampleInstallRemoveBackup");

        private static string ManifestBackupPath =>
            Path.Combine(BackupDir, "orig-manifest.json");

        private static string LockBackupPath =>
            Path.Combine(BackupDir, "orig-packages-lock.json");

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[W2.13-SampleInstallRemove] " + message);

        private static string Dependency(string revision) =>
            SampleRepository + "#" + revision;

        // The §E1 states in which a Run stops advancing for this test:
        // the three terminal states, plus needs_review and
        // awaiting_approval — both resolvable only by a human decision
        // the Agent path can never supply (SPEC §H4 item 5), so polling
        // past them cannot succeed.
        private static readonly HashSet<string> s_decidedStates =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "succeeded", "failed", "cancelled",
                "needs_review", "awaiting_approval",
            };

        // ---------- project state ----------

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

        private static bool AssemblyLoaded(string name) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == name);

        private static ISet<string> InstalledPackages()
        {
            var installed = new HashSet<string>(StringComparer.Ordinal);
            if (PackageInstalled(SamplePackage))
            {
                installed.Add(SamplePackage);
            }
            return installed;
        }

        // The packages-lock.json entry of the sample, or null when the
        // file cannot be read or the package is absent — a read failure
        // is a distinct state the caller asserts, never silently
        // "absent" (SPEC §X.9's read-failure rule).
        private static JObject? LockEntry()
        {
            if (!File.Exists(LockPath))
            {
                return null;
            }
            try
            {
                var root = JObject.Parse(File.ReadAllText(LockPath));
                return root["dependencies"]?[SamplePackage] as JObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? LockHash() =>
            (string?)LockEntry()?["hash"];

        private static string? LockSource() =>
            (string?)LockEntry()?["source"];

        // ---------- capture / restore ----------

        private static byte[]? ReadBytesOrNull(string path) =>
            File.Exists(path) ? File.ReadAllBytes(path) : null;

        // Byte-for-byte capture of the project's package state before
        // the test borrows it — the same backup form
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

        private static bool ProjectMatchesOriginal()
        {
            var capture = LoadCapture();
            return capture is not null &&
                PackageProbePlan.MatchesCapture(
                    capture,
                    ReadBytesOrNull(ManifestPath),
                    ReadBytesOrNull(LockPath),
                    InstalledPackages(),
                    SamplePackage);
        }

        // The byte/settle check alone misses the removal's last
        // observable effect: removing the sample only unloads its
        // Editor assembly at the domain reload the removal forces, so
        // a byte-exact restore can settle the manifest, lock and
        // package list while the assembly is still loaded — a leftover
        // the next setup's guard would read as a project dependency.
        // The fixture is settled only once the assembly is gone, except
        // when the captured project declared the sample itself: there
        // keeping it loaded IS the captured state being restored.
        private static bool SampleFullyRemoved()
        {
            var capture = LoadCapture();
            return capture is not null &&
                (capture.Mentions(SamplePackage) ||
                 !AssemblyLoaded(SampleEditorAssembly));
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

        // ---------- package tree digest ----------

        // One SHA-256 over the sorted relative paths and content hashes
        // of every file under the com.opweave.workflow package root —
        // the §T.2 "no file under Packages/com.opweave.workflow/
        // changes" evidence, captured once per session and re-checked
        // by teardown.
        private static string OpWeaveTreeDigest()
        {
            var info = UnityEditor.PackageManager.PackageInfo
                .FindForPackageName(OpWeavePackage);
            Assert.That(info, Is.Not.Null,
                "the com.opweave.workflow package must resolve for the " +
                "tree digest");
            var root = Path.GetFullPath(info!.resolvedPath);
            Assert.That(Directory.Exists(root), Is.True,
                "the resolved com.opweave.workflow path must exist: " + root);
            var lines = new StringBuilder();
            var files = Directory.GetFiles(
                root, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var relative = file.Substring(root.Length)
                    .TrimStart(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                lines.Append(relative)
                    .Append(' ')
                    .Append(Sha256Hex(File.ReadAllBytes(file)))
                    .Append('\n');
            }
            return Sha256Hex(Encoding.UTF8.GetBytes(lines.ToString()));
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    text.Append(b.ToString("x2"));
                }
                return text.ToString();
            }
        }

        // ---------- registry / api readiness ----------

        // The domain-startup build has published its snapshot
        // atomically — the only snapshot the assertions below read
        // (SPEC §C3 step 8).
        private static bool RegistrySettled()
        {
            var latest = RegistryBuildHost.Latest;
            return latest?.Snapshot is { } snapshot &&
                ReferenceEquals(
                    RegistryPublication.Shared.Snapshot, snapshot) &&
                !RegistryPublication.Shared.Stale;
        }

        private static bool AgentApiReady()
        {
            try
            {
                _ = AgentApiHost.Api;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool LiveReady() =>
            RegistrySettled() && AgentApiReady();

        private static AgentApiService Api()
        {
            try
            {
                return AgentApiHost.Api;
            }
            catch (InvalidOperationException exception)
            {
                Assert.Fail(
                    "the AgentApi service is not installed in this " +
                    "Editor domain: " + exception.Message);
                throw;
            }
        }

        // ---------- bounded waits ----------

        // Every wait below yields only Editor frames — the domain
        // reload a package change forces is always crossed at the
        // sanctioned `yield return new WaitForDomainReload()` boundary
        // inside the test method itself, never inside a nested
        // enumerator.
        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!condition() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
        }

        private static IEnumerator WaitForRequest(
            UnityEditor.PackageManager.Requests.Request request)
        {
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!request.IsCompleted &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
        }

        // The compile/import quiet window of
        // PackageResolvedReloadTests: an install's compile may still be
        // running when its Request completes; wait for a sustained
        // quiet spell so the pending reload is provably armed before
        // the sanctioned WaitForDomainReload crossing.
        private static IEnumerator QuietSettle()
        {
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var quietAt = -1.0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (EditorApplication.isCompiling ||
                    EditorApplication.isUpdating)
                {
                    quietAt = EditorApplication.timeSinceStartup +
                        QuietSettleSec;
                }
                else if (quietAt >= 0.0 &&
                         EditorApplication.timeSinceStartup >= quietAt)
                {
                    break;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup +
                        QuietSettleSec;
                }
                yield return null;
            }
        }

        private static void AssertUpmRequest(
            UnityEditor.PackageManager.Requests.Request request, string what)
        {
            Assert.That(request.IsCompleted, Is.True,
                what + " did not complete within " + WaitTimeoutSec + "s");
            var detail = request.Error is { } error
                ? error.errorCode + " " + error.message
                : "<no error detail>";
            Assert.That(request.Status, Is.EqualTo(StatusCode.Success),
                what + " failed: " + detail);
        }

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
        // boundary: ArmReloadBoundary writes the reload-counter
        // baseline AND "test|boundary" — SessionState survives the
        // reload, so a continuation UTF resumes on a stale enumerator
        // position — one that never ran the arming segment — finds the
        // previous test's mark or none and AssertReloadCrossed fails
        // closed instead of silently continuing into the post-reload
        // phase.
        private const string BoundaryOwnerSessionKey =
            "OpWeave.SampleE2E.boundary-owner";

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

        // The one sanctioned pre-boundary arm: the reload-counter
        // baseline plus the owner record, both in SessionState — a
        // stale-resume continuation that skipped this statement fails
        // AssertReloadCrossed's owner check even when the counter
        // comparison alone would pass on an older baseline.
        private static void ArmReloadBoundary(string what)
        {
            SessionState.SetInt(
                ReloadBaselineKey, DomainReloadCounter.Current);
            ArmBoundary(what);
        }

        private static void AssertReloadCrossed(string what)
        {
            var baseline = SessionState.GetInt(ReloadBaselineKey, -1);
            Assert.That(DomainReloadCounter.Current,
                Is.GreaterThan(baseline),
                what + " did not cross a domain reload");
            AssertBoundaryOwned(what);
        }

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

        // ---------- sample fixture helpers ----------

        // Every workflow the installed package ships under
        // Samples~/Workflows/, found through the installed package's
        // own resolved path — never a hard-coded or local sample path.
        private static string[] SampleWorkflowFiles()
        {
            var info = UnityEditor.PackageManager.PackageInfo
                .FindForPackageName(SamplePackage);
            Assert.That(info, Is.Not.Null,
                "the sample package must be installed to enumerate " +
                "its workflows");
            var directory = Path.Combine(
                info!.resolvedPath, "Samples~", "Workflows");
            Assert.That(Directory.Exists(directory), Is.True,
                "the installed sample package must ship " +
                "Samples~/Workflows/: " + directory);
            var files = Directory.GetFiles(directory, "*.opweave.json");
            Array.Sort(files, StringComparer.Ordinal);
            Assert.That(files.Length, Is.GreaterThan(0),
                "the installed sample package ships no " +
                "*.opweave.json workflows");
            return files;
        }

        private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
            string.Join(
                "; ",
                diagnostics.Select(d => d.Code + " " + d.Message));

        // The atomically published snapshot, asserted fresh — never a
        // stale publication and never a stale registry (SPEC §C3
        // rules 8–9).
        private static RegistrySnapshot PublishedSnapshot()
        {
            Assert.That(RegistrySettled(), Is.True,
                "the production Registry never published a fresh " +
                "snapshot in this domain");
            return RegistryBuildHost.Latest!.Snapshot!;
        }

        private static RegisteredOperation RegisteredSampleOperation()
        {
            var registration =
                PublishedSnapshot().Find(
                    SampleOperationId, SampleOperationVersion);
            Assert.That(registration, Is.Not.Null,
                SampleOperationId + "@" + SampleOperationVersion +
                " must be registered in the published snapshot");
            return registration!;
        }

        // ---------- suite setup / teardown ----------

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

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            LogAssert.ignoreFailingMessages = true;

            // A run that died before its teardown may have left the
            // sample installed with the originals still backed up —
            // restore from the backup first so the capture below
            // records the project's own baseline, never the sample's
            // leftovers. The stale per-test probe state goes too.
            RestoreOriginalSync();
            DeleteProbeDir();
            CaptureOriginal();

            // The §T.2 tree immutability baseline is captured lazily
            // once per Editor session: it lives in SessionState, so it
            // survives the domain reloads the tests force.
            if (SessionState.GetString(TreeDigestKey, "").Length == 0)
            {
                SessionState.SetString(TreeDigestKey, OpWeaveTreeDigest());
            }

            // A leftover restore may still be resolving, importing or
            // compiling; its reload stays deferred at the test runner's
            // assembly lock. Wait on the observable original state,
            // then cross a still-pending reload at the sanctioned
            // boundary.
            var deadline =
                EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!ProjectMatchesOriginal() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            if (!ProjectMatchesOriginal() || !SampleFullyRemoved())
            {
                // A still-loaded sample assembly means the removal's
                // domain reload is still armed: re-apply the restore
                // (idempotent), flush the import and cross it at the
                // sanctioned boundary — a leftover the fixture must
                // shed, never pass over.
                RestoreOriginalSync();
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
                "the project did not settle on its captured " +
                "manifest/lock state");
            Assert.That(SampleFullyRemoved(), Is.True,
                "the sample's Editor assembly stayed loaded — the " +
                "previous teardown never crossed the removal's " +
                "domain reload");

            // The fixture borrows the project's manifest: it is
            // meaningful — and non-destructive — only while the project
            // does not already depend on the sample package, because
            // installing then removing a real dependency would rewrite
            // state the fixture may only restore, never discard. The
            // genuine case is a dependency DECLARED in the captured
            // original manifest — an unmet environment precondition,
            // never a silent pass. A sample the captured manifest never
            // declared is this fixture's own leftover: the settle above
            // already cleaned it by crossing its unload reload, or it
            // failed loudly there — it never reaches this guard.
            var baseline = LoadCapture();
            if (baseline is null)
            {
                Assert.Inconclusive(
                    "precondition unmet: no captured project baseline");
            }
            if (baseline!.Mentions(SamplePackage))
            {
                Assert.Inconclusive(
                    "precondition unmet: the project already depends on " +
                    SamplePackage + " — the fixture would install and " +
                    "remove a real project dependency");
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            RestoreOriginalSync();

            var deadline =
                EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!ProjectMatchesOriginal() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            // The restore settles the file bytes and the package list,
            // but the sample's Editor assembly unloads only at the
            // domain reload the removal forces — cross it whenever the
            // assembly is still loaded so the fixture never poisons
            // the next setup with a leftover.
            string? pendingFailure = null;
            if (!ProjectMatchesOriginal() || !SampleFullyRemoved())
            {
                RestoreOriginalSync();
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
            // leave the sample assembly loaded for the next test
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
            Assert.That(pendingFailure, Is.Null, pendingFailure!);
            Assert.That(ProjectMatchesOriginal(), Is.True,
                "the project was not restored to its captured " +
                "manifest/lock state after the sample package probe");
            Assert.That(SampleFullyRemoved(), Is.True,
                "the sample's Editor assembly stayed loaded after " +
                "the restore — the removal's domain reload was " +
                "never crossed");

            // §T.2: nothing the tests did may change any file under
            // Packages/com.opweave.workflow/.
            var baseline = SessionState.GetString(TreeDigestKey, "");
            Assert.That(baseline, Is.Not.Empty,
                "no com.opweave.workflow tree digest was captured");
            Assert.That(OpWeaveTreeDigest(), Is.EqualTo(baseline),
                "a file under Packages/com.opweave.workflow/ changed " +
                "during the sample install/remove tests");

            // Only a fully restored project drops its evidence: a
            // failed restore keeps the backups on disk for the next
            // setup's recovery pass and for inspection.
            DeleteProbeDir();
            DeleteBackupDir();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            SessionState.EraseString(PlanHashKey);
            SessionState.EraseString(TreeDigestKey);
            SessionState.EraseInt(ReloadBaselineKey);
            SessionState.EraseString(BoundaryOwnerSessionKey);
        }

        // ---------- §X.4 install + atomic registration ----------

        // The install acceptance (SPEC §X.4): the real Git UPM add
        // attests the pinned commit in packages-lock.json and in the
        // Registry's own provenance (PackageId + ResolvedCommit on the
        // published RegisteredOperation), and the sample Operation,
        // TypeProvider and Codec appear together in ONE published
        // snapshot — the §C3 step-8 atomic registration.
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator Install_RegistersAtomically_AndAttestsPinnedCommit()
        {
            LogAssert.ignoreFailingMessages = true;

            // Phase 1 (pre-reload domain): the real Client.Add at the
            // pinned full-SHA Git URL — never a local clone or a file:
            // substitute.
            var request = Client.Add(Dependency(RevisionA));
            yield return WaitForRequest(request);
            AssertUpmRequest(request, "Client.Add(" + RevisionA + ")");

            yield return WaitUntil(() => PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.True,
                "the sample package never registered with UPM after " +
                "Client.Add");

            // The install's import and compile may finish while the
            // runner holds the reload lock; the quiet settle leaves the
            // reload provably armed before the sanctioned crossing.
            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the sample install"));
            ArmReloadBoundary("the sample install");
            yield return new WaitForDomainReload();

            // Phase 2 (post-reload domain): prove the crossing, then
            // wait for the domain-startup build to publish and the
            // AgentApi delayCall install.
            AssertReloadCrossed("the sample install");
            yield return WaitUntil(LiveReady);
            Assert.That(LiveReady(), Is.True,
                "the Registry never published a fresh snapshot or the " +
                "AgentApi never installed after the sample install");

            // §X.4 attestation 1: the resolved commit in
            // packages-lock.json is exactly the pinned SHA.
            Assert.That(LockEntry(), Is.Not.Null,
                "packages-lock.json must carry a " + SamplePackage +
                " entry after the install");
            Assert.That(LockSource(), Is.EqualTo("git"),
                "the lockfile entry must record a git dependency");
            Assert.That(LockHash(), Is.EqualTo(RevisionA),
                "the resolved commit in packages-lock.json must be " +
                "the pinned SHA " + RevisionA);

            // §X.4 attestation 2 + §C3 step 8 atomicity: in ONE
            // published snapshot the Operation is registered with
            // Custom provenance bound to the sample package and the
            // pinned commit, the TypeProvider is admitted, and the
            // BonePose Type ID resolves through its Codec.
            var registered = RegisteredSampleOperation();
            Assert.That(registered.Origin,
                Is.EqualTo(OperationOrigin.Custom));
            Assert.That(registered.PackageId,
                Is.EqualTo(SamplePackage),
                "the Registry's own provenance must name the " +
                "defining package");
            Assert.That(registered.ResolvedCommit,
                Is.EqualTo(RevisionA),
                "the Registry's own provenance must attest the " +
                "resolved commit " + RevisionA);
            Assert.That(registered.AssemblyName,
                Is.EqualTo(SampleEditorAssembly));

            var latest = RegistryBuildHost.Latest!;
            Assert.That(
                latest.Providers.Any(
                    p => p.ProviderId == SampleProviderId),
                Is.True,
                "the TypeProvider " + SampleProviderId +
                " must be admitted in the same build");
            var types = latest.Types;
            Assert.That(types, Is.Not.Null,
                "the published build must carry a type registry");
            var bonePose = types!.All.FirstOrDefault(
                t => t.Id == SampleTypeId);
            Assert.That(bonePose, Is.Not.Null,
                "the custom type " + SampleTypeId +
                " must be registered in the same snapshot");
            var codec = bonePose!.Codec;
            Assert.That(codec, Is.Not.Null,
                "the custom type must publish its Codec");
            Assert.That(
                codec.GetType().Name,
                Does.Contain("BonePose"),
                "the published Codec must be the sample's own " +
                "BonePose codec");

            // The persisted diff records the registration as an
            // `added` change when a previous summary exists to diff
            // against (SPEC §C3 rule 7).
            var diff = latest.Diff;
            if (diff is not null && diff.Previous is not null)
            {
                Assert.That(
                    diff.Changes.Any(
                        c => c.Kind == RegistryChangeKinds.Added &&
                             c.OperationId == SampleOperationId),
                    Is.True,
                    "the registry diff must record the sample " +
                    "Operation as added");
            }
        }

        // ---------- §X.7 sample workflow static validation ----------

        // Every Samples~/Workflows document validates through the
        // public validate_workflow command against the live
        // production Registry (SPEC §X.7 item 2 — static validation,
        // a separate test from execution).
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator SampleWorkflows_ValidateThroughAgentApi()
        {
            LogAssert.ignoreFailingMessages = true;

            var request = Client.Add(Dependency(RevisionA));
            yield return WaitForRequest(request);
            AssertUpmRequest(request, "Client.Add(" + RevisionA + ")");

            yield return WaitUntil(() => PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.True,
                "the sample package never registered with UPM after " +
                "Client.Add");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the sample install"));
            ArmReloadBoundary("the sample install");
            yield return new WaitForDomainReload();

            AssertReloadCrossed("the sample install");
            yield return WaitUntil(LiveReady);
            Assert.That(LiveReady(), Is.True,
                "the Registry never published a fresh snapshot or the " +
                "AgentApi never installed after the sample install");

            var api = Api();
            foreach (var path in SampleWorkflowFiles())
            {
                var body = JObject.Parse(File.ReadAllText(path));
                var validated = api.ValidateWorkflow(
                    new ValidateWorkflowRequest(body, null));
                Assert.That(validated.Ok, Is.True,
                    () => path + " validate_workflow: " +
                          Describe(validated.Errors));
                var data = (JObject)validated.Data!;
                Assert.That(
                    (string?)data["planHash"],
                    Is.Not.Null.And.Not.Empty,
                    path + ": validate_workflow must report the §D0.1 " +
                    "planHash");
                Assert.That(
                    (int?)data["steps"], Is.EqualTo(2),
                    path + ": the shipped workflow compiles to exactly " +
                    "the start + make_pose steps");
            }
        }

        // ---------- §X.7 sample workflow execution ----------

        // Every Samples~/Workflows document executes end to end
        // through the public execute_workflow → get_run_status Agent
        // path until a terminal state — never a Runner or Registry
        // internal (SPEC §X.7 item 2, T-W3.2-03).
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator SampleWorkflows_ExecuteThroughAgentApi()
        {
            LogAssert.ignoreFailingMessages = true;

            var request = Client.Add(Dependency(RevisionA));
            yield return WaitForRequest(request);
            AssertUpmRequest(request, "Client.Add(" + RevisionA + ")");

            yield return WaitUntil(() => PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.True,
                "the sample package never registered with UPM after " +
                "Client.Add");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the sample install"));
            ArmReloadBoundary("the sample install");
            yield return new WaitForDomainReload();

            AssertReloadCrossed("the sample install");
            yield return WaitUntil(LiveReady);
            Assert.That(LiveReady(), Is.True,
                "the Registry never published a fresh snapshot or the " +
                "AgentApi never installed after the sample install");

            var api = Api();
            foreach (var path in SampleWorkflowFiles())
            {
                var body = JObject.Parse(File.ReadAllText(path));

                // The planHash an Agent binds: validate compiles, then
                // execute_workflow re-checks it at §E2 acceptance.
                var validated = api.ValidateWorkflow(
                    new ValidateWorkflowRequest(body, null));
                Assert.That(validated.Ok, Is.True,
                    () => path + " validate_workflow: " +
                          Describe(validated.Errors));
                var planHash =
                    (string?)((JObject)validated.Data!)["planHash"];
                Assert.That(planHash, Is.Not.Null.And.Not.Empty,
                    "validate_workflow must report the §D0.1 planHash");

                var executed = api.ExecuteWorkflow(
                    new ExecuteWorkflowRequest(
                        body, null, null, ExpectedPlanHash: planHash));
                Assert.That(executed.Ok, Is.True,
                    () => path + " execute_workflow: " +
                          Describe(executed.Errors));
                var admission = (JObject)executed.Data!;
                var runId = (string?)admission["runId"];
                Assert.That(runId, Is.Not.Null.And.Not.Empty,
                    "a successful execute_workflow names the admitted " +
                    "Run");

                // Poll get_run_status once per Editor frame until the
                // Run is decided or the deadline passes — the bounded
                // waitSeconds:0 form, since a positive wait sleeps the
                // main thread the EditorApplication.update pump needs
                // (SPEC §H2 item 3).
                var deadline =
                    EditorApplication.timeSinceStartup +
                    RunPollDeadlineSec;
                JObject? status = null;
                var state = "";
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    var polled = api.GetRunStatus(
                        new GetRunStatusRequest(runId, WaitSeconds: 0));
                    if (!polled.Ok)
                    {
                        // The just-admitted Run arrives through the §E8
                        // feed the next frames deliver; a not-yet-
                        // enrolled id answers OW-RUN-010 until then.
                        if (polled.Errors.Count == 1 &&
                            polled.Errors[0].Code == ErrorCodes.OW_RUN_010)
                        {
                            yield return null;
                            continue;
                        }
                        Assert.Fail(path + " get_run_status(" + runId +
                                    "): " + Describe(polled.Errors));
                    }
                    status = (JObject)polled.Data!;
                    state = (string?)status["state"] ?? "";
                    if (s_decidedStates.Contains(state))
                    {
                        break;
                    }
                    yield return null;
                }

                Assert.That(status, Is.Not.Null,
                    () => path + " get_run_status never observed Run " +
                          runId);
                Assert.That(state, Is.EqualTo("succeeded"),
                    () => path + " reached '" + state +
                          "' — final status: " +
                          status!.ToString(
                              Newtonsoft.Json.Formatting.None));

                // Every Plan step of the Run itself succeeded — the
                // whole workflow ran, not one Operation.
                var steps = (JArray)status!["steps"]!;
                Assert.That(
                    steps.Select(s => (string?)s["state"]).ToArray(),
                    Is.All.EqualTo("succeeded"),
                    "every step of a Succeeded sample Run is itself " +
                    "succeeded");

                // The documented result: exactly the 'pose' output, a
                // BonePose with Position (0, 1, 0), read back as plain
                // JSON — the Codec contract the package itself
                // documents.
                var outputs = status["outputs"] as JObject;
                Assert.That(outputs, Is.Not.Null,
                    "a Succeeded Run carries the workflow's outputs");
                Assert.That(
                    outputs!.Properties().Select(p => p.Name).ToArray(),
                    Is.EqualTo(new[] { "pose" }),
                    "the run produces the 'pose' output and nothing " +
                    "else");
                var pose = outputs!["pose"] as JObject;
                Assert.That(pose, Is.Not.Null,
                    "the 'pose' output must be a BonePose JSON object");
                Assert.That((double?)pose!["x"], Is.EqualTo(0.0));
                Assert.That((double?)pose!["y"], Is.EqualTo(1.0));
                Assert.That((double?)pose!["z"], Is.EqualTo(0.0));

                // The sample performs no scene, asset, package or
                // other project-state change: the §F7 journaled change
                // union of the whole Run is empty.
                var logs = api.GetRunLogs(new GetRunLogsRequest(runId));
                Assert.That(logs.Ok, Is.True,
                    () => "get_run_logs(" + runId + "): " +
                          Describe(logs.Errors));
                var changes =
                    (JArray)((JObject)logs.Data!)["changes"]!;
                Assert.That(changes, Is.Empty,
                    "the sample Run records no project-state change");
            }
        }

        // ---------- §X.11 item 4 source-change invalidation ----------

        // A Plan made on revision A becomes invalid after the package
        // moves to revision B: the Descriptor is identical, so the
        // invalidation is the defining-assembly implementationHash
        // change alone (SPEC §X.11 item 4). Both revisions are
        // pre-existing immutable remote commits — nothing is
        // rewritten, nothing is mutated in place.
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator SourceChange_InvalidatesPreChangePlan()
        {
            LogAssert.ignoreFailingMessages = true;

            // Phase 1: install revision A and hold its planHash across
            // the boundary in SessionState.
            var request = Client.Add(Dependency(RevisionA));
            yield return WaitForRequest(request);
            AssertUpmRequest(request, "Client.Add(" + RevisionA + ")");

            yield return WaitUntil(() => PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.True,
                "the sample package never registered with UPM after " +
                "Client.Add");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the revision-A install"));
            ArmReloadBoundary("the revision-A install");
            yield return new WaitForDomainReload();

            AssertReloadCrossed("the revision-A install");
            yield return WaitUntil(LiveReady);
            Assert.That(LiveReady(), Is.True,
                "the Registry never published a fresh snapshot or the " +
                "AgentApi never installed after the sample install");

            var api = Api();
            var path = SampleWorkflowFiles()[0];
            var body = JObject.Parse(File.ReadAllText(path));
            var validatedA = api.ValidateWorkflow(
                new ValidateWorkflowRequest(body, null));
            // The message is built eagerly on purpose: a () => message
            // would capture validatedA, and a variable captured into a
            // display class is allocated at method entry — before the
            // reload — so its first post-reload touch is a
            // NullReferenceException. Captured variables must be
            // scoped strictly inside post-reload blocks.
            Assert.That(validatedA.Ok, Is.True,
                "revision-A validate_workflow: " +
                Describe(validatedA.Errors));
            var planHashA =
                (string?)((JObject)validatedA.Data!)["planHash"];
            Assert.That(planHashA, Is.Not.Null.And.Not.Empty,
                "validate_workflow must report the §D0.1 planHash");
            SessionState.SetString(PlanHashKey, planHashA!);

            // Phase 2: re-add the same package at revision B's full
            // SHA — the dependency entry is replaced, the lockfile
            // re-pins, and the changed Editor assembly forces the next
            // compile and reload.
            Crumb("revision-A planHash held; switching to revision B");
            var switchRequest = Client.Add(Dependency(RevisionB));
            yield return WaitForRequest(switchRequest);
            AssertUpmRequest(
                switchRequest, "Client.Add(" + RevisionB + ")");

            yield return WaitUntil(
                () => string.Equals(
                    LockHash(), RevisionB, StringComparison.Ordinal));
            Assert.That(LockHash(), Is.EqualTo(RevisionB),
                "the package never re-pinned to revision B — " +
                "Client.Add could not switch the Git dependency");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the revision-B switch"));
            ArmReloadBoundary("the revision-B switch");
            yield return new WaitForDomainReload();

            // Phase 3 (post-reload domain): the Registry rebuilt over
            // revision B. The persisted diff reports the implementation
            // change on the identical descriptor identity. Every
            // pre-switch local — the api handle, the workflow body —
            // died at the boundary; re-derive all of them.
            AssertReloadCrossed("the revision-B switch");
            yield return WaitUntil(LiveReady);
            Assert.That(LiveReady(), Is.True,
                "the Registry never published a fresh snapshot or the " +
                "AgentApi never installed after the revision switch");

            Assert.That(LockHash(), Is.EqualTo(RevisionB),
                "packages-lock.json must attest revision B after the " +
                "switch");
            var registeredB = RegisteredSampleOperation();
            Assert.That(registeredB.ResolvedCommit,
                Is.EqualTo(RevisionB),
                "the Registry's own provenance must attest revision B");

            var diff = RegistryBuildHost.Latest!.Diff;
            Assert.That(diff, Is.Not.Null,
                "the revision-B build must record a persisted diff");
            Assert.That(
                diff!.Changes.Any(
                    c => c.Kind ==
                         RegistryChangeKinds.ImplementationChanged &&
                         c.OperationId == SampleOperationId),
                Is.True,
                "the registry diff must record the implementation " +
                "change of the identical descriptor");

            // The planHash moved — the identical workflow now compiles
            // to a different §D0.1 planHash because the
            // implementationHash is a planHash input.
            var apiB = Api();
            var bodyB = JObject.Parse(
                File.ReadAllText(SampleWorkflowFiles()[0]));
            var validatedB = apiB.ValidateWorkflow(
                new ValidateWorkflowRequest(bodyB, null));
            Assert.That(validatedB.Ok, Is.True,
                "revision-B validate_workflow: " +
                Describe(validatedB.Errors));
            var planHashB =
                (string?)((JObject)validatedB.Data!)["planHash"];
            Assert.That(planHashB, Is.Not.Null.And.Not.Empty,
                "validate_workflow must report the §D0.1 planHash");
            var heldHashA = SessionState.GetString(PlanHashKey, "");
            Assert.That(heldHashA, Is.Not.Empty,
                "the revision-A planHash did not survive the reload");
            Assert.That(planHashB, Is.Not.EqualTo(heldHashA),
                "the pre-change Plan must not share the post-change " +
                "planHash");

            // The §E2 acceptance re-check refuses the stale Plan:
            // execute_workflow with the pre-change expectedPlanHash is
            // OW-RUN-003 — the public contract a Runner-facing caller
            // sees for "the Plan is invalid after the source change".
            var rejected = apiB.ExecuteWorkflow(
                new ExecuteWorkflowRequest(
                    bodyB, null, null, ExpectedPlanHash: heldHashA));
            Assert.That(rejected.Ok, Is.False,
                "execute_workflow must refuse the pre-change planHash");
            Assert.That(
                rejected.Errors.Any(
                    d => d.Code == ErrorCodes.OW_RUN_003),
                Is.True,
                "the refusal must be OW-RUN-003 (planHash mismatch), " +
                "got: " + Describe(rejected.Errors));
        }

        // ---------- §X.11 item 5 null vs Missing + planFileDigest ----------

        // The sample ships no Nullable port, so the complete workflow
        // composes the sample Operation with the existing core
        // Operation whose optional core.Json `properties` port admits
        // both an explicit null and an omitted value (SPEC §X.11 item
        // 5 as the ticket permits). The compiled Plan preserves the
        // distinction in its resolvedWorkflow, the two plans hash
        // differently, and the Run created through the real Run Store
        // passes the §D7 item-6 planFileDigest check.
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ExplicitNullVsMissing_PlanFileDigestPasses()
        {
            LogAssert.ignoreFailingMessages = true;

            var request = Client.Add(Dependency(RevisionA));
            yield return WaitForRequest(request);
            AssertUpmRequest(request, "Client.Add(" + RevisionA + ")");

            yield return WaitUntil(() => PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.True,
                "the sample package never registered with UPM after " +
                "Client.Add");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the sample install"));
            ArmReloadBoundary("the sample install");
            yield return new WaitForDomainReload();

            AssertReloadCrossed("the sample install");
            yield return WaitUntil(LiveReady);
            Assert.That(LiveReady(), Is.True,
                "the Registry never published a fresh snapshot or the " +
                "AgentApi never installed after the sample install");

            var api = Api();

            // The composed complete workflow: the sample Operation
            // contributes its BonePose output while
            // unity.asset.create_material's optional core.Json
            // `properties` port is the Nullable-position the
            // distinction is made on. The workflow never executes — it
            // exists to be compiled.
            const string workflowJson =
                "{\n" +
                "  \"schemaVersion\": \"1.0\",\n" +
                "  \"id\": \"example.sample-null-vs-missing\",\n" +
                "  \"nodes\": [\n" +
                "    {\"id\": \"start\", \"operation\": \"core.start\", \"version\": 1},\n" +
                "    {\"id\": \"makePose\", \"operation\": \"org.opweave.sample.make_pose\", \"version\": 1,\n" +
                "     \"parameters\": {\"position\": {\"x\": 0, \"y\": 1, \"z\": 0}}},\n" +
                "    {\"id\": \"createMaterial\", \"operation\": \"unity.asset.create_material\", \"version\": 1,\n" +
                "     \"parameters\": {\"path\": \"Assets/opweave-sample-e2e.mat\", \"shader\": \"Standard\", \"properties\": null}}\n" +
                "  ],\n" +
                "  \"controlEdges\": [\n" +
                "    {\"from\": \"start\", \"fromPort\": \"next\", \"to\": \"makePose\"},\n" +
                "    {\"from\": \"makePose\", \"fromPort\": \"next\", \"to\": \"createMaterial\"}\n" +
                "  ],\n" +
                "  \"outputs\": [\n" +
                "    {\"id\": \"pose\", \"from\": {\"node\": \"makePose\", \"port\": \"pose\"}}\n" +
                "  ]\n" +
                "}\n";

            var explicitNullBody = JObject.Parse(workflowJson);
            var missingBody = JObject.Parse(workflowJson);
            var materialParameters = ((JArray)missingBody["nodes"]!)
                .First(n => (string?)n["id"] == "createMaterial")!
                    ["parameters"] as JObject;
            Assert.That(materialParameters, Is.Not.Null,
                "the composed workflow's createMaterial node must " +
                "carry parameters");
            materialParameters!.Remove("properties");

            // Both variants compile — explicit null is a value on a
            // core.Json port, Missing is the sanctioned absence. The
            // messages are built eagerly: a () => message would capture
            // nullValidated/missingValidated into a display class that
            // the compiler allocates at method entry — before the
            // reload — so its first post-reload touch would be a
            // NullReferenceException (the r1 line-1178 failure).
            var nullValidated = api.ValidateWorkflow(
                new ValidateWorkflowRequest(
                    explicitNullBody, null, null, IncludePlan: true));
            Assert.That(nullValidated.Ok, Is.True,
                "explicit-null validate_workflow: " +
                Describe(nullValidated.Errors));
            var missingValidated = api.ValidateWorkflow(
                new ValidateWorkflowRequest(
                    missingBody, null, null, IncludePlan: true));
            Assert.That(missingValidated.Ok, Is.True,
                "missing-port validate_workflow: " +
                Describe(missingValidated.Errors));

            var nullPlan = (JObject)((JObject)nullValidated.Data!)["plan"]!;
            var missingPlan =
                (JObject)((JObject)missingValidated.Data!)["plan"]!;

            // The distinction survives compilation: the explicit null
            // is serialized as a written null in resolvedWorkflow
            // (core.Json keeps the written token), while the omitted
            // port is absent — Missing is never serialized as null.
            var nullParameters = ResolvedParameters(nullPlan);
            var missingParameters = ResolvedParameters(missingPlan);
            Assert.That(
                nullParameters["properties"]?.Type,
                Is.EqualTo(JTokenType.Null),
                "the explicit null must be preserved in the Plan's " +
                "resolvedWorkflow");
            Assert.That(
                missingParameters["properties"], Is.Null,
                "the omitted port must stay absent — Missing is never " +
                "serialized as null");

            // The distinction is planHash material: the two plans hash
            // differently.
            Assert.That(
                (string?)missingPlan["planHash"],
                Is.Not.EqualTo((string?)nullPlan["planHash"]),
                "explicit null and Missing must hash differently");

            // The planFileDigest check passes through the real Run
            // Store path: the store writes plan.json, journals
            // RunAccepted.data.planFileDigest as the SHA-256 of those
            // exact bytes, and Load re-verifies the digest against the
            // persisted plan — a mismatch would surface OW-STORE-002
            // and NeedsReview (SPEC §D7 item 6, §F2).
            var decoded = PlanJsonCodec.Read(
                nullPlan.ToString(Newtonsoft.Json.Formatting.None));
            Assert.That(decoded.Status,
                Is.EqualTo(PlanJsonStatus.Decoded),
                "the explicit-null plan.json must decode: " +
                (decoded.Diagnostic?.Message ?? "<no diagnostic>"));
            var doc = WorkflowSerializer.Read(workflowJson);
            Assert.That(doc.Ok, Is.True,
                "the composed workflow must serialize back to a " +
                "WorkflowDocument");

            var storeRoot = Path.Combine(ProbeDir, "runs");
            var store = new FileRunStore(
                storeRoot, "sample-e2e-" + Guid.NewGuid().ToString("N"));
            var runId = Identifiers.NewRunId();
            var created = store.CreateRun(runId, decoded.Plan!, doc.Value!);
            Assert.That(created.Ok, Is.True,
                "CreateRun failed: " + Describe(created.Diagnostics));

            // The persisted plan bytes' own SHA-256 equals the
            // journaled RunAccepted planFileDigest — the direct
            // attestation of what Load re-checks.
            var planBytes = File.ReadAllBytes(
                Path.Combine(
                    storeRoot, "Runs", runId, "plan.json"));
            var expectedDigest = Sha256Hex(planBytes);
            var journalLines = File.ReadAllLines(
                Path.Combine(
                    storeRoot, "Runs", runId, "journal.ndjson"));
            Assert.That(journalLines.Length, Is.GreaterThan(0),
                "the Run journal must hold the RunAccepted record");
            JObject? accepted = null;
            foreach (var line in journalLines)
            {
                JObject? parsed = null;
                try { parsed = JObject.Parse(line); }
                catch (Exception) { /* a partial tail line is not a record */ }
                if (parsed is not null &&
                    (string?)parsed["type"] == "run_accepted")
                {
                    accepted = parsed;
                    break;
                }
            }
            Assert.That(accepted, Is.Not.Null,
                "the Run journal must hold the RunAccepted record");
            Assert.That(
                (string?)accepted!["data"]?["planFileDigest"],
                Is.EqualTo(expectedDigest),
                "RunAccepted.planFileDigest must equal SHA-256 of the " +
                "persisted plan.json bytes");

            var loaded = store.Load(runId);
            Assert.That(loaded.Ok, Is.True,
                "the planFileDigest check failed on Load: " +
                Describe(loaded.Diagnostics));
            Assert.That(loaded.Value!.State,
                Is.EqualTo(RunState.Queued),
                "a Run that passed planFileDigest verification is " +
                "Queued, not NeedsReview");
        }

        // ---------- §X.9 removal, no stale entries, reinstall ----------

        // The removal acceptance (SPEC §X.9 item 5): after the real
        // UPM removal and its domain reload the sample Operation,
        // TypeProvider and Codec are gone from the published snapshot,
        // the persisted diff reports `removed`, no stale Registry
        // entry survives, a workflow that still references the
        // Operation fails validation with OW-OP-001 — and
        // re-installing the same pinned SHA registers it again.
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator Remove_Unregisters_DiffRemoved_ReinstallSucceeds()
        {
            LogAssert.ignoreFailingMessages = true;

            // Phase 1: install revision A.
            var request = Client.Add(Dependency(RevisionA));
            yield return WaitForRequest(request);
            AssertUpmRequest(request, "Client.Add(" + RevisionA + ")");

            yield return WaitUntil(() => PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.True,
                "the sample package never registered with UPM after " +
                "Client.Add");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the sample install"));
            ArmReloadBoundary("the sample install");
            yield return new WaitForDomainReload();

            AssertReloadCrossed("the sample install");
            yield return WaitUntil(LiveReady);
            Assert.That(LiveReady(), Is.True,
                "the Registry never published a fresh snapshot or the " +
                "AgentApi never installed after the sample install");
            Assert.That(RegisteredSampleOperation(), Is.Not.Null,
                "the sample Operation must register before removal is " +
                "meaningful");

            // Phase 2: the real UPM removal; the assembly unload
            // compiles and forces the next domain reload.
            var remove = Client.Remove(SamplePackage);
            yield return WaitForRequest(remove);
            AssertUpmRequest(remove, "Client.Remove(" + SamplePackage + ")");

            yield return WaitUntil(() => !PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.False,
                "the sample package stayed registered after " +
                "Client.Remove");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the sample removal"));
            ArmReloadBoundary("the sample removal");
            yield return new WaitForDomainReload();

            // Phase 3 (post-removal domain): everything that came from
            // the package is gone from the published snapshot — no
            // stale Registry entries (SPEC §X.9).
            AssertReloadCrossed("the sample removal");
            yield return WaitUntil(LiveReady);
            Assert.That(LiveReady(), Is.True,
                "the Registry never published a fresh snapshot or the " +
                "AgentApi never installed after the removal");

            var snapshot = PublishedSnapshot();
            Assert.That(
                snapshot.Find(
                    SampleOperationId, SampleOperationVersion),
                Is.Null,
                "the removed sample Operation must not survive in the " +
                "published snapshot");
            Assert.That(
                snapshot.Operations.Any(
                    o => o.PackageId == SamplePackage ||
                         o.AssemblyName == SampleEditorAssembly),
                Is.False,
                "no stale Registry entry from the removed package may " +
                "survive");
            Assert.That(
                snapshot.Types.All.Any(t => t.Id == SampleTypeId),
                Is.False,
                "the removed sample's custom type must be gone");
            Assert.That(
                RegistryBuildHost.Latest!.Providers.Any(
                    p => p.ProviderId == SampleProviderId),
                Is.False,
                "the removed sample's TypeProvider must be gone");

            // The persisted diff reports `removed` for the Operation
            // identity (SPEC §C3 rule 7).
            var diff = RegistryBuildHost.Latest!.Diff;
            Assert.That(diff, Is.Not.Null,
                "the post-removal build must record a persisted diff");
            Assert.That(
                diff!.Changes.Any(
                    c => c.Kind == RegistryChangeKinds.Removed &&
                         c.OperationId == SampleOperationId),
                Is.True,
                "the registry diff must report the sample Operation " +
                "as removed");

            // A workflow still referencing the removed Operation fails
            // validation with OW-OP-001 — through the public
            // validate_workflow path, never a Registry internal.
            var api = Api();
            var staleBody = JObject.Parse(
                "{\n" +
                "  \"schemaVersion\": \"1.0\",\n" +
                "  \"id\": \"example.stale-sample-reference\",\n" +
                "  \"nodes\": [\n" +
                "    {\"id\": \"start\", \"operation\": \"core.start\", \"version\": 1},\n" +
                "    {\"id\": \"makePose\", \"operation\": \"org.opweave.sample.make_pose\", \"version\": 1,\n" +
                "     \"parameters\": {\"position\": {\"x\": 0, \"y\": 1, \"z\": 0}}}\n" +
                "  ],\n" +
                "  \"controlEdges\": [\n" +
                "    {\"from\": \"start\", \"fromPort\": \"next\", \"to\": \"makePose\"}\n" +
                "  ]\n" +
                "}\n");
            var rejected = api.ValidateWorkflow(
                new ValidateWorkflowRequest(staleBody, null));
            Assert.That(rejected.Ok, Is.False,
                "a workflow referencing the removed Operation must " +
                "fail validation");
            Assert.That(
                rejected.Errors.Any(
                    d => d.Code == ErrorCodes.OW_OP_001),
                Is.True,
                "the rejection must be OW-OP-001 (unknown Operation), " +
                "got: " + Describe(rejected.Errors));

            // Phase 4: re-install the same pinned SHA after removal —
            // it registers again in a fresh published snapshot.
            Crumb("removal verified; re-installing revision A");
            var reinstall = Client.Add(Dependency(RevisionA));
            yield return WaitForRequest(reinstall);
            AssertUpmRequest(
                reinstall, "Client.Add re-install(" + RevisionA + ")");

            yield return WaitUntil(() => PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.True,
                "the sample package never re-registered with UPM");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the sample re-install"));
            ArmReloadBoundary("the sample re-install");
            yield return new WaitForDomainReload();

            AssertReloadCrossed("the sample re-install");
            yield return WaitUntil(LiveReady);
            Assert.That(LiveReady(), Is.True,
                "the Registry never published a fresh snapshot or the " +
                "AgentApi never installed after the re-install");

            var registeredAgain = RegisteredSampleOperation();
            Assert.That(registeredAgain.PackageId,
                Is.EqualTo(SamplePackage));
            Assert.That(registeredAgain.ResolvedCommit,
                Is.EqualTo(RevisionA),
                "the re-install must attest the same pinned SHA " +
                RevisionA);
            Assert.That(LockHash(), Is.EqualTo(RevisionA),
                "packages-lock.json must re-attest the pinned SHA");
            Assert.That(
                RegistryBuildHost.Latest!.Providers.Any(
                    p => p.ProviderId == SampleProviderId),
                Is.True,
                "the TypeProvider must register again after " +
                "re-install");
        }

        // The resolved parameters of the composed workflow's
        // createMaterial node inside a plan.json — the §D0.2
        // resolvedWorkflow whose parameters carry the codec-resolved
        // values (core.Json kept as written).
        private static JObject ResolvedParameters(JObject plan)
        {
            var nodes =
                plan["planHashInput"]?["resolvedWorkflow"]?["nodes"]
                    as JArray;
            Assert.That(nodes, Is.Not.Null,
                "plan.json planHashInput.resolvedWorkflow must carry " +
                "the node list");
            var node = nodes!.FirstOrDefault(
                n => (string?)n["id"] == "createMaterial") as JObject;
            Assert.That(node, Is.Not.Null,
                "the resolved workflow must carry the createMaterial " +
                "node");
            var parameters = node!["parameters"] as JObject;
            Assert.That(parameters, Is.Not.Null,
                "the resolved node must carry its parameters");
            return parameters!;
        }
    }
}
