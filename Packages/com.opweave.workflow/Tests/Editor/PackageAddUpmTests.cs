#nullable enable

// T-W4.2-04 [Edit] tests: local:T-W4.2-04:package-add-reconcile — the
// real-UPM half of SPEC §G8.1. A local Git fixture repository (file://
// bare repo, ?path= sub-folder package — the T-W2.12-05 form, prepared
// by test-side manifest edits plus Client.Resolve, never through the
// Operation) supplies the tag and full-SHA installs the §G8.1 evidence
// conditions decide over the real manifest.json, the real
// packages-lock.json and the real registered package list; installing
// the fixture's Editor assembly forces the compile and domain reload
// the §F5 restart case needs (the reload is proven by
// DomainReloadCounter, the persisted snapshot/token file playing the
// Runner journal's role). The registry half drives the real Operation
// end to end — Preflight's persisted snapshot, Execute's Client.Add +
// resume token, Poll's post-reload registered-list judgement (SPEC
// §T.4 L-07) and Reconcile's Completed — on a default-allowlisted
// com.unity.* id. Injected-seam verdicts live in
// PackageAddOperationTests; static-validation rejections in
// PackageAddValidationTests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
using SysProcess = System.Diagnostics.Process;
using SysStartInfo = System.Diagnostics.ProcessStartInfo;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W4.2-04:package-add-reconcile — real-UPM evidence and
    /// domain-reload coverage for <c>unity.package.add</c>.
    /// </summary>
    internal sealed class PackageAddUpmTests
    {
        // ---------- fixture identities ----------

        // The fixture package: package.json plus one Editor assembly so
        // its install/removal compiles — each compile ends in the domain
        // reload the interrupted-execution cases cross.
        private const string FixturePackage = "com.opweave.fixture.pkgadd";
        private const string FixtureAssemblyName = "OpWeave.Fixture.PkgAdd.Editor";
        private const string TagName = "vadd1";

        // The default-allowlisted registry package driving the real
        // Preflight → Execute → Poll → Reconcile path.
        private const string RegistryPackage = "com.unity.editorcoroutines";
        private const string RegistryDep = "com.unity.editorcoroutines@1.0.0";

        private const string FixtureDirName = "OpWeavePackageAddFixture";
        private const string SrcDirName = "pkgadd-src";
        private const string BareDirName = "pkgadd-repo.git";
        private const string EnvGitExe = "OPWEAVE_LOCAL_GIT_EXE";

        private const double WaitTimeoutSec = 240.0;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");

        private static string LockPath =>
            Path.Combine(ProjectRoot, "Packages", "packages-lock.json");

        private static string FixtureRoot =>
            Path.Combine(ProjectRoot, "Library", FixtureDirName);

        private static string SrcRepo => Path.Combine(FixtureRoot, SrcDirName);
        private static string BareRepo => Path.Combine(FixtureRoot, BareDirName);
        private static string BareRepoUrl => "file:///" + BareRepo.Replace('\\', '/');
        private static string FixtureDepPrefix => BareRepoUrl + "?path=/pkgs/pkgadd#";

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeavePackageAddProbe");

        private static string BackupDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeavePackageAddBackup");

        private static string ManifestBackupPath =>
            Path.Combine(BackupDir, "orig-manifest.json");

        private static string LockBackupPath =>
            Path.Combine(BackupDir, "orig-packages-lock.json");

        // The persisted-evidence file a domain reload does not lose —
        // the test's stand-in for the Runner-journaled preflight snapshot
        // and StepWaiting resume token (SPEC §G8.1, §G4).
        private static string StatePath => Path.Combine(ProbeDir, "state.json");

        private static string GitExe =>
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvGitExe))
                ? "git"
                : Environment.GetEnvironmentVariable(EnvGitExe)!;

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[G8.1-PackageAdd] " + message);

        // ---------- git fixture construction ----------

        private sealed class GitResult
        {
            public int Exit = -1;
            public string StdOut = "";
            public string StdErr = "";
            public string FirstLine =>
                (StdOut + " " + StdErr).Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        }

        private static GitResult Git(string args, string? workDir)
        {
            var result = new GitResult();
            var psi = new SysStartInfo
            {
                FileName = GitExe,
                Arguments = args,
                WorkingDirectory = workDir ?? ProjectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var proc = SysProcess.Start(psi);
            Assert.That(proc, Is.Not.Null, "git failed to start");
            using (var p = proc!)
            {
                var stdOut = p.StandardOutput.ReadToEndAsync();
                var stdErr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch { /* best effort */ }
                    Assert.Fail("git timed out: " + args);
                }
                result.Exit = p.ExitCode;
                result.StdOut = stdOut.Result;
                result.StdErr = stdErr.Result;
            }
            return result;
        }

        private static void AssertGit(GitResult result, string what) =>
            Assert.That(result.Exit, Is.EqualTo(0),
                $"git {what} failed (exit {result.Exit}): {result.FirstLine}");

        // One fixture package: package.json, an Editor asmdef and a plain
        // class — the assembly forces the compile + domain reload a real
        // package add performs. Git-resolved packages are immutable, so
        // every asset ships a committed .meta file (the Editor ignores
        // assets that lack one).
        private static void WriteFixturePackage(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "Editor"));
            WriteAssetMeta(Path.Combine(root, "Editor"), "pkgadd:Editor", folder: true);
            File.WriteAllText(Path.Combine(root, "package.json"),
                "{\n" +
                "  \"name\": \"" + FixturePackage + "\",\n" +
                "  \"version\": \"1.0.0\",\n" +
                "  \"displayName\": \"OpWeave package-add fixture\",\n" +
                "  \"unity\": \"6000.0\"\n" +
                "}\n");
            WriteAssetMeta(Path.Combine(root, "package.json"), "pkgadd:package.json");
            File.WriteAllText(Path.Combine(root, "Editor", FixtureAssemblyName + ".asmdef"),
                "{\n" +
                "    \"name\": \"" + FixtureAssemblyName + "\",\n" +
                "    \"references\": [],\n" +
                "    \"includePlatforms\": [\n" +
                "        \"Editor\"\n" +
                "    ],\n" +
                "    \"excludePlatforms\": [],\n" +
                "    \"autoReferenced\": true\n" +
                "}\n");
            WriteAssetMeta(
                Path.Combine(root, "Editor", FixtureAssemblyName + ".asmdef"),
                "pkgadd:asmdef");
            File.WriteAllText(Path.Combine(root, "Editor", "PkgAddMarker.cs"),
                "#nullable enable\n" +
                "namespace " + FixturePackage + "\n" +
                "{\n" +
                "    internal static class PkgAddMarker\n" +
                "    {\n" +
                "        internal const int Value = 1;\n" +
                "    }\n" +
                "}\n");
            WriteAssetMeta(Path.Combine(root, "Editor", "PkgAddMarker.cs"), "pkgadd:marker");
        }

        // Deterministic 32-hex GUID for a fixture asset's .meta file —
        // stable across machines so the fixture repos stay identical.
        private static string FixtureGuid(string seed)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(seed));
                var text = new StringBuilder(32);
                foreach (var b in hash)
                {
                    text.Append(b.ToString("x2"));
                }
                return text.ToString();
            }
        }

        private static void WriteAssetMeta(string assetPath, string seed, bool folder = false)
        {
            var importer =
                Path.GetFileName(assetPath) == "package.json" ? "PackageManifestImporter" :
                assetPath.EndsWith(".asmdef", StringComparison.Ordinal) ? "AssemblyDefinitionImporter" :
                assetPath.EndsWith(".cs", StringComparison.Ordinal) ? "" :
                "TextScriptImporter";
            var body = "";
            if (folder)
            {
                body = "folderAsset: yes\n" + ImporterBody("DefaultImporter");
            }
            else if (importer.Length > 0)
            {
                body = ImporterBody(importer);
            }
            File.WriteAllText(assetPath + ".meta",
                "fileFormatVersion: 2\n" +
                "guid: " + FixtureGuid(seed) + "\n" +
                body);

            static string ImporterBody(string name) =>
                name + ":\n" +
                "  externalObjects: {}\n" +
                "  userData: \n" +
                "  assetBundleName: \n" +
                "  assetBundleVariant: \n";
        }

        // Deletes a fixture tree; git object and pack files are read-only
        // on Windows, so attributes are cleared first.
        private static void DeleteTree(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, true);
        }

        // Idempotent: creates the working + bare repos once per machine —
        // commit 1 carries the package and gets the tag, commit 2 a marker
        // change so HEAD differs from the tagged commit (the moved-tag
        // fixture) — then (re)points the tag at commit 1. Returns both
        // SHAs; callers re-derive them post-reload, when every local is
        // gone.
        private static (string Tagged, string Head) EnsureFixtures()
        {
            Directory.CreateDirectory(FixtureRoot);
            if (!Directory.Exists(SrcRepo))
            {
                WriteFixturePackage(Path.Combine(SrcRepo, "pkgs", "pkgadd"));
                AssertGit(Git("init -b main", SrcRepo), "init");
                AssertGit(Git("add -A", SrcRepo), "add");
                AssertGit(Git(
                    "-c user.name=OpWeavePkgAddTest " +
                    "-c user.email=pkgadd@opweave.invalid commit -m fixture",
                    SrcRepo), "commit package");
                File.WriteAllText(
                    Path.Combine(SrcRepo, "MARKER.txt"), "second commit marker\n");
                AssertGit(Git("add -A", SrcRepo), "add marker");
                AssertGit(Git(
                    "-c user.name=OpWeavePkgAddTest " +
                    "-c user.email=pkgadd@opweave.invalid commit -m marker",
                    SrcRepo), "commit marker");
            }
            if (!Directory.Exists(BareRepo))
            {
                AssertGit(Git("clone --bare \"" + SrcRepo + "\" \"" + BareRepo + "\"",
                    FixtureRoot), "clone --bare");
            }
            var head = Git("rev-parse HEAD", BareRepo);
            AssertGit(head, "rev-parse HEAD");
            var tagged = Git("rev-parse HEAD~1", BareRepo);
            AssertGit(tagged, "rev-parse HEAD~1");
            var headSha = head.StdOut.Trim();
            var taggedSha = tagged.StdOut.Trim();
            Assert.That(headSha, Does.Match("^[0-9a-f]{40}$"));
            Assert.That(taggedSha, Does.Match("^[0-9a-f]{40}$"));
            Assert.That(taggedSha, Is.Not.EqualTo(headSha),
                "the moved-tag fixture needs two distinct commits");
            AssertGit(Git("tag -f " + TagName + " " + taggedSha, BareRepo),
                "tag -f " + TagName);
            return (taggedSha, headSha);
        }

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

        private static bool AssemblyLoaded(string name) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == name);

        // The install-set names the baseline checks membership against —
        // the Git fixture and the registry probe, the only packages the
        // fixture ever borrows.
        private static ISet<string> InstalledPackages()
        {
            var installed = new HashSet<string>(StringComparer.Ordinal);
            if (PackageInstalled(FixturePackage))
            {
                installed.Add(FixturePackage);
            }
            if (PackageInstalled(RegistryPackage))
            {
                installed.Add(RegistryPackage);
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
        // lockfile hold the captured bytes again and neither borrowed
        // package is registered (the admission check below refuses a
        // project that already depended on either).
        private static bool ProjectMatchesOriginal()
        {
            var capture = LoadCapture();
            return capture is not null &&
                PackageProbePlan.MatchesCapture(
                    capture,
                    ReadBytesOrNull(ManifestPath),
                    ReadBytesOrNull(LockPath),
                    InstalledPackages(),
                    FixturePackage) &&
                PackageProbePlan.MatchesCapture(
                    capture,
                    ReadBytesOrNull(ManifestPath),
                    ReadBytesOrNull(LockPath),
                    InstalledPackages(),
                    RegistryPackage);
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
            string? id = null, JsonValue? resumeToken = null)
        {
            var inputs = new Dictionary<string, InputValue>();
            if (id is not null)
            {
                inputs[UnityPackageAddOperation.IdPort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(id)));
            }
            return new JsonStepContext(
                inputs,
                new StepChangeRecorder(0, "add"),
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
        // that ends in one. The package manager can register the package
        // before its import and compile are queued, so a quiet window is
        // not evidence. Queued imports are flushed about once a second
        // (a synchronous Refresh is also how the package's own compile
        // surfaces). The wait only waits — it never throws and never
        // yields an edit-mode yield instruction, which a nested
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

        // The timeout report the WaitForReloadPending callers raise or
        // record — a timed-out wait means the install produced no
        // compile, and the message names the one state in which no
        // install could ever have caused a reload.
        private static string ReloadTimeoutMessage() =>
            "no domain reload became pending within " + WaitTimeoutSec +
            " s of the package install (fixture assembly " +
            FixtureAssemblyName + " already loaded: " +
            AssemblyLoaded(FixtureAssemblyName) + ")";

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
                Assert.That(ReloadPending(), Is.True, ReloadTimeoutMessage());
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
            // does not already depend on either package the tests
            // install. An already-present dependency is an unmet
            // environment precondition, never a silent pass.
            var baseline = LoadCapture();
            if (baseline is null)
            {
                Assert.Inconclusive(
                    "precondition unmet: no captured project baseline");
            }
            if (baseline!.Mentions(FixturePackage) ||
                baseline.Mentions(RegistryPackage) ||
                PackageInstalled(FixturePackage) ||
                PackageInstalled(RegistryPackage))
            {
                Assert.Inconclusive(
                    "precondition unmet: the project already depends on a " +
                    "package the fixture would add and remove");
            }
            // The reload the tests cross is the compile of the fixture's
            // own Editor assembly; a domain that already holds it can
            // produce no compile for its install, so the reload is
            // unprovable — an unmet precondition, not a silent pass.
            if (AssemblyLoaded(FixtureAssemblyName))
            {
                Assert.Inconclusive(
                    "precondition unmet: the fixture assembly " +
                    FixtureAssemblyName + " is already loaded, so the " +
                    "package install cannot force the domain reload the " +
                    "tests cross");
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
                    pendingFailure = ReloadTimeoutMessage();
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
            // leave the fixture assembly loaded for the next test
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
                "manifest/lock state after the package-add fixture");

            DeleteProbeDir();
            DeleteBackupDir();
        }

        // ---------- §G8.1 verdicts over real UPM state ----------

        // SPEC §G8.1 test 4 + §F5 case 1: the step never ran — the
        // persisted snapshot records absence and the target is absent
        // everywhere now, so the verdict is NotApplied. A manifest entry
        // written without a resolve is the manifest-only partial state —
        // evidence across one channel only — and stays Unknown, never a
        // re-add.
        [Test]
        public void Reconcile_AbsentEverywhere_NotApplied_ThenManifestOnly_Unknown()
        {
            var shas = EnsureFixtures();
            var dep = FixtureDepPrefix + shas.Tagged;
            Assert.That(PackageAddSource.TryParse(dep, out var source), Is.True);
            var snapshot =
                PackageAddEvidence.BuildSnapshot(dep, source!, PackageInstallView.Capture());
            var op = new UnityPackageAddOperation();

            Assert.That(
                op.Reconcile(Context(), snapshot),
                Is.InstanceOf<ReconcileResult.NotApplied>(),
                "absence everywhere plus recorded absence is NotApplied — " +
                "got " + Describe(op.Reconcile(Context(), snapshot)));

            var manifest = File.ReadAllText(ManifestPath);
            File.WriteAllText(ManifestPath,
                SetManifestDependency(manifest, FixturePackage, dep));
            try
            {
                Assert.That(
                    op.Reconcile(Context(), snapshot),
                    Is.InstanceOf<ReconcileResult.Unknown>(),
                    "a manifest-only trace is partial evidence — Unknown");
            }
            finally
            {
                File.WriteAllText(ManifestPath, manifest);
            }
        }

        // SPEC §G8.1 tests 1-2 + §F5 case 6: the interrupted SHA install —
        // the persisted snapshot crosses the domain reload the package's
        // own assembly compile forces, and the same source plus the same
        // full resolved commit SHA reconciles Completed.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator GitShaInstall_ReconcilesCompleted_AcrossDomainReload()
        {
            LogAssert.ignoreFailingMessages = true;

            // Phase 1 (pre-reload): persist the §G8.1 evidence the
            // preflight would have journaled — expected source identity
            // plus the SHA the request names — then drive the install as
            // a test-side direct operation.
            var shas = EnsureFixtures();
            var dep = FixtureDepPrefix + shas.Tagged;
            Assert.That(PackageAddSource.TryParse(dep, out var source), Is.True);
            var snapshot =
                PackageAddEvidence.BuildSnapshot(dep, source!, PackageInstallView.Capture());
            PersistState(snapshot, null);
            var manifest = File.ReadAllText(ManifestPath);
            File.WriteAllText(ManifestPath,
                SetManifestDependency(manifest, FixturePackage, dep));
            Client.Resolve();
            Crumb("sha install armed: " + dep);

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!PackageInstalled(FixturePackage) &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(PackageInstalled(FixturePackage), Is.True,
                "the fixture package never registered with UPM after the manifest install");
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True, ReloadTimeoutMessage());
            yield return new WaitForDomainReload();

            // Phase 2 (post-reload): the snapshot comes back off its
            // persisted text alone — no in-memory state survived
            // (SPEC §F5 case 6) — and the reload is proven.
            if (!TryReadState(out var reloaded, out var baseline, out _))
            {
                Assert.Fail("the persisted evidence state is missing after the reload");
            }
            Assert.That(DomainReloadCounter.Current, Is.GreaterThan(baseline),
                "the package install did not cross a domain reload");

            var verdict =
                new UnityPackageAddOperation().Reconcile(Context(), reloaded!);

            var completed = verdict as ReconcileResult.Completed;
            Assert.That(completed, Is.Not.Null,
                "same source + same full resolved commit SHA is the §G8.1 " +
                "Completed evidence row — got " + Describe(verdict));
            var output = (JObject)completed!
                .Outputs[UnityPackageAddOperation.PackagePort].ToToken();
            Assert.That(output["name"]!.ToString(), Is.EqualTo(FixturePackage));
            Assert.That(output["source"]!.ToString(), Is.EqualTo("git"));
        }

        // SPEC §G8.1 test 3: a tag request persists no determinable
        // expected commit — the installed package, same name and same
        // origin, can never reconcile Completed.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator GitTagInstall_NoExpectedCommit_ReconcilesUnknown()
        {
            LogAssert.ignoreFailingMessages = true;

            EnsureFixtures();
            var dep = FixtureDepPrefix + TagName;
            Assert.That(PackageAddSource.TryParse(dep, out var source), Is.True);
            var snapshot =
                PackageAddEvidence.BuildSnapshot(dep, source!, PackageInstallView.Capture());
            Assert.That(
                ((JObject)snapshot.ToToken())["expectedCommit"], Is.Null,
                "a tag has no side-effect-free expected commit to persist");
            PersistState(snapshot, null);
            var manifest = File.ReadAllText(ManifestPath);
            File.WriteAllText(ManifestPath,
                SetManifestDependency(manifest, FixturePackage, dep));
            Client.Resolve();
            Crumb("tag install armed: " + dep);

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!PackageInstalled(FixturePackage) &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(PackageInstalled(FixturePackage), Is.True,
                "the fixture package never registered with UPM after the manifest install");
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True, ReloadTimeoutMessage());
            yield return new WaitForDomainReload();

            if (!TryReadState(out var reloaded, out var baseline, out _))
            {
                Assert.Fail("the persisted evidence state is missing after the reload");
            }
            Assert.That(DomainReloadCounter.Current, Is.GreaterThan(baseline),
                "the package install did not cross a domain reload");

            var verdict =
                new UnityPackageAddOperation().Reconcile(Context(), reloaded!);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "same-name evidence without a persisted expected commit " +
                "is insufficient — got " + Describe(verdict));
        }

        // SPEC §G8.1: the tag moved between the evidence write and the
        // resolve — the dependency text still names the tag, but UPM
        // resolved a different commit than the persisted expected one.
        // Unknown, never Completed, never an automatic re-add.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MovedTag_ResolvedCommitDiffers_ReconcilesUnknown()
        {
            LogAssert.ignoreFailingMessages = true;

            var shas = EnsureFixtures();
            var dep = FixtureDepPrefix + TagName;
            // The persisted evidence a commit-pinning preflight would
            // have written for this request — the tag carrying the
            // tagged commit as its expectation. The tag then moves.
            var expected = PackageAddSource.Git(
                NormalizedOriginOf(dep), "/pkgs/pkgadd", TagName, shas.Tagged);
            var snapshot =
                PackageAddEvidence.BuildSnapshot(dep, expected, PackageInstallView.Capture());
            PersistState(snapshot, null);
            AssertGit(Git("tag -f " + TagName + " " + shas.Head, BareRepo),
                "move " + TagName + " to HEAD");
            var manifest = File.ReadAllText(ManifestPath);
            File.WriteAllText(ManifestPath,
                SetManifestDependency(manifest, FixturePackage, dep));
            Client.Resolve();
            Crumb("moved-tag install armed: " + dep);

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!PackageInstalled(FixturePackage) &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(PackageInstalled(FixturePackage), Is.True,
                "the fixture package never registered with UPM after the manifest install");
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True, ReloadTimeoutMessage());
            yield return new WaitForDomainReload();

            if (!TryReadState(out var reloaded, out var baseline, out _))
            {
                Assert.Fail("the persisted evidence state is missing after the reload");
            }
            Assert.That(DomainReloadCounter.Current, Is.GreaterThan(baseline),
                "the package install did not cross a domain reload");

            var verdict =
                new UnityPackageAddOperation().Reconcile(Context(), reloaded!);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "a tag resolved at a different commit is never " +
                "Completed — got " + Describe(verdict));
        }

        // SPEC §G8.1 tests 1-2 + test 9's domain-reload form on the real
        // registry path: Preflight persists the evidence, Execute issues
        // the real Client.Add and suspends on the resume token, the
        // package install forces the reload, and Poll — resumed on the
        // persisted token alone — reports the resolved package output
        // while Reconcile answers Completed.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator RegistryAdd_ExecuteWaitPoll_SucceedsAcrossDomainReload()
        {
            LogAssert.ignoreFailingMessages = true;

            // Phase 1 (pre-reload): the real Operation end to end —
            // Preflight's persisted evidence, Execute's real Client.Add
            // and the resume token the StepWaiting record would journal.
            var op = new UnityPackageAddOperation();
            var preflight = op.Preflight(Context(RegistryDep));
            Assert.That(preflight.Diagnostics, Is.Empty, () => string.Join(
                "; ", preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var execute = op.Execute(Context(RegistryDep));
            Assert.That(execute.Kind, Is.EqualTo(StepResultKind.Waiting),
                "Execute must suspend on the package-resolution wait");
            Assert.That(execute.ResumeToken, Is.Not.Null);
            PersistState(preflight.Snapshot, execute.ResumeToken!.CanonicalText);
            Crumb("registry add executed: resumeToken persisted");

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!PackageInstalled(RegistryPackage) &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(PackageInstalled(RegistryPackage), Is.True,
                "the registry package never resolved after Client.Add");
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True, ReloadTimeoutMessage());
            yield return new WaitForDomainReload();

            // Phase 2 (post-reload): the operation is a fresh instance
            // and the token reloads from its persisted text — the only
            // state the new domain gets (SPEC §C2 item 4, §G4).
            if (!TryReadState(out var snapshot, out var baseline, out var resumed))
            {
                Assert.Fail("the persisted evidence state is missing after the reload");
            }
            Assert.That(DomainReloadCounter.Current, Is.GreaterThan(baseline),
                "the package install did not cross a domain reload");
            Assert.That(resumed, Is.Not.Null, "no persisted resume token");

            var resumedOp = new UnityPackageAddOperation();
            var result = resumedOp.Poll(Context(resumeToken: resumed));
            var pollDeadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (result.Kind == StepResultKind.Waiting &&
                   EditorApplication.timeSinceStartup < pollDeadline)
            {
                resumed = result.ResumeToken!;
                result = resumedOp.Poll(Context(resumeToken: resumed));
                yield return null;
            }
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                "the resumed wait must succeed on the registered-list " +
                "judgement (SPEC §T.4 L-07)");

            var outputCtx = Context(resumeToken: resumed);
            Assert.That(resumedOp.Poll(outputCtx).Kind,
                Is.EqualTo(StepResultKind.Succeeded),
                "a terminal wait verdict is stable");
            var output = (JObject)outputCtx
                .Outputs[UnityPackageAddOperation.PackagePort].ToToken();
            Assert.That(output["name"]!.ToString(), Is.EqualTo(RegistryPackage));
            Assert.That(output["version"]!.ToString(), Is.EqualTo("1.0.0"));
            Assert.That(output["source"]!.ToString(), Is.EqualTo("registry"));

            // And the interrupted-step verdict on the persisted snapshot:
            // name + resolved version prove the registry add.
            var verdict = resumedOp.Reconcile(Context(), snapshot!);
            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Completed>(),
                "registry identity evidence reconciles Completed — " +
                "got " + Describe(verdict));
        }

        // ---------- helpers ----------

        // The repository origin half of a ?path=/# dep — the normalized
        // form PackageAddSource.Git expects for a read-back.
        private static string NormalizedOriginOf(string dep)
        {
            var body = dep;
            var query = body.IndexOf('?');
            if (query >= 0)
            {
                body = body.Substring(0, query);
            }
            return body;
        }

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
    }
}
