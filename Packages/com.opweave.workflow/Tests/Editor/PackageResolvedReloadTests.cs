#nullable enable

// T-W4.1-02 [Edit] tests: local:T-W4.1-02:package-resolved — the SPEC §G4
// PackageResolved(id) row against real UPM requests. A small registry
// package is added and removed while the wait is armed on the Request; the
// induced domain reload drops the tracked Request, and a fresh condition
// reading only the persisted resumeToken confirms the change through the
// registered package list once the Editor is idle — SPEC §T.4 L-07's
// confirmation method, recorded under
// <project>/Library/OpWeavePackageProbe/l07.json. A nonexistent registry id
// fails the Request itself → OW-UNITY-009.
//
// Installing or removing a package with scripts compiles and forces a
// domain reload; reloads are locked while the runner pumps a test, so each
// one fires inside `yield return new WaitForDomainReload()` (SPEC §T.3 —
// the UpmPackageProvenanceTests pattern). UTF locals do not survive the
// boundary: every phase is a static helper and the persisted input — the
// resumeToken plus the reload-counter baseline — lives under
// Library/OpWeavePackageProbe/, removed again by [UnityTearDown]. The
// project's own package state is never rewritten surgically: setup
// snapshots manifest.json and packages-lock.json byte-for-byte under
// Library/OpWeavePackageBackup/ and teardown puts those exact bytes back,
// so a dependency the project already had can never be stripped.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W4.1-02:package-resolved — registry add/remove waits across
    /// the induced domain reload via the registered package list, and the
    /// OW-UNITY-009 request failure.
    /// </summary>
    internal sealed class PackageResolvedReloadTests
    {
        // The probe package: a small registry package whose Editor
        // assembly makes the install and the removal compile — and each
        // compile ends in the domain reload the L-07 post-reload judgement
        // is proven across.
        private const string ProbePackage = "com.unity.editorcoroutines";
        private const string ProbeAddId = "com.unity.editorcoroutines@1.0.0";

        // A well-formed registry name no registry carries: the Request
        // itself fails, which is SPEC §G4's OW-UNITY-009 path.
        private const string MissingPackage = "com.opweave.fixture.nosuchpackage";

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");

        private static string LockPath =>
            Path.Combine(ProjectRoot, "Packages", "packages-lock.json");

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeavePackageProbe");

        private static string BackupDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeavePackageBackup");

        private static string ManifestBackupPath =>
            Path.Combine(BackupDir, "orig-manifest.json");

        private static string LockBackupPath =>
            Path.Combine(BackupDir, "orig-packages-lock.json");

        private static string StatePath => Path.Combine(ProbeDir, "wait-state.txt");
        private static string L07Path => Path.Combine(ProbeDir, "l07.json");

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[G4-PackageResolved] " + message);

        [Serializable]
        private sealed class ProbePhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static ProbePhaseResult Pass() => new ProbePhaseResult { Ok = true };

            public static ProbePhaseResult Fail(string message) =>
                new ProbePhaseResult { Ok = false, Failure = message };
        }

        private sealed class PersistedCheck
        {
            internal WaitStatusKind Kind;
            internal string Code = string.Empty;
            internal string Hint = string.Empty;
        }

        // ---------- project baseline ----------

        private static bool PackageInstalled(string name)
        {
            try
            {
                return UnityEditor.PackageManager.PackageInfo.FindForPackageName(name) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The install-set names the pure plan checks membership against —
        // the probe package and the fixture's missing id, the only two
        // the fixture ever asks about.
        private static ISet<string> InstalledPackages()
        {
            var installed = new HashSet<string>(StringComparer.Ordinal);
            if (PackageInstalled(ProbePackage))
            {
                installed.Add(ProbePackage);
            }
            if (PackageInstalled(MissingPackage))
            {
                installed.Add(MissingPackage);
            }
            return installed;
        }

        // ---------- original-state capture and restore ----------

        // Snapshots the project's package state byte-for-byte before the
        // test borrows it: the teardown restores exactly these bytes, so
        // whatever the project depended on beforehand — the probe package
        // itself included — is put back untouched instead of stripped.
        // Both backups live under Library/ and survive the induced domain
        // reloads; the manifest copy is written last so its presence marks
        // a complete capture.
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

        private static byte[]? ReadBytesOrNull(string path) =>
            File.Exists(path) ? File.ReadAllBytes(path) : null;

        // The persisted capture — rebuilt from the backup files, never
        // the live ones, so every check below stays correct across the
        // domain reloads between arming and judgement.
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

        // Restores the captured manifest and lockfile bytes synchronously,
        // then resolves so the install set follows the original files. A
        // project that had no lockfile gets none recreated by hand — the
        // resolve owns whatever it regenerates. No yields — callers cross
        // a pending reload at the sanctioned boundary themselves.
        private static void RestoreOriginalSync()
        {
            var capture = LoadCapture();
            if (capture is null)
            {
                return; // no capture on record — nothing was borrowed
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

        // Executes one planned file restore; returns whether the file
        // changed — the signal that a resolve must follow.
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

        // The settle check both setup and teardown wait on: the manifest
        // and lockfile hold the captured bytes again and the install set
        // agrees — the probe present exactly when the original manifest
        // declared it.
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

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            LogAssert.ignoreFailingMessages = true;

            // A run that died before its teardown may have left the probe
            // installed with the originals still backed up — restore from
            // the backup first so the capture below records the project's
            // own baseline, never the probe's leftovers. The stale
            // per-test probe state goes too; each phase re-arms it.
            RestoreOriginalSync();
            DeleteProbeDir();
            CaptureOriginal();

            // A leftover restore may still be resolving, importing or
            // compiling; its reload stays deferred at the test runner's
            // assembly lock — possibly with every public flag already
            // quiet (6000.0 has no public "reload requested" member). Wait
            // on the observable original state, then cross a still-pending
            // reload at the sanctioned boundary.
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!ProjectMatchesOriginal() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            if (!ProjectMatchesOriginal())
            {
                AssetDatabase.Refresh();
                yield return new WaitForDomainReload();
            }
            Assert.That(ProjectMatchesOriginal(), Is.True,
                "the project did not settle on its captured manifest/lock state");

            // The probe borrows the project's manifest: it is meaningful —
            // and non-destructive — only while the project does not
            // already depend on the probe package (or the fixture's
            // missing id), because adding or removing a real dependency
            // would rewrite state the fixture may only restore, never
            // discard. An already-present probe is an unmet environment
            // precondition, never a silent pass.
            var baseline = LoadCapture();
            if (baseline is null)
            {
                Assert.Inconclusive(
                    "precondition unmet: no captured project baseline");
            }
            if (!PackageProbePlan.TryAdmit(
                    baseline!.Manifest, InstalledPackages(),
                    ProbePackage, MissingPackage, out var refusal))
            {
                Assert.Inconclusive(refusal);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            RestoreOriginalSync();

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!ProjectMatchesOriginal() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            if (!ProjectMatchesOriginal())
            {
                AssetDatabase.Refresh();
                yield return new WaitForDomainReload();
            }
            Assert.That(ProjectMatchesOriginal(), Is.True,
                "the project was not restored to its captured " +
                "manifest/lock state after the package probe");

            // Only a fully restored project drops its evidence: a failed
            // restore keeps the backups on disk for the next setup's
            // recovery pass and for inspection.
            DeleteProbeDir();
            DeleteBackupDir();
        }

        // ---------- local:T-W4.1-02:package-resolved ----------

        // The add+remove case: each operation is armed on its real UPM
        // Request, the install/removal compiles and forces a domain
        // reload, and the resumed wait — rebuilt from the persisted
        // resumeToken alone — completes through the registered package
        // list (SPEC §T.4 L-07: "登録済みパッケージの一覧で確認する").
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator PackageResolved_AddThenRemove_CompletesViaRegisteredList()
        {
            LogAssert.ignoreFailingMessages = true;

            // Phase A1 (pre-reload domain): issue the real add and arm the
            // wait on its Request — the call a Waitable unity.package.add
            // step makes. Persist the resumeToken and the reload-counter
            // baseline: the only state the post-reload domain gets.
            var arm = ArmAddWait();
            Crumb("add arm returned ok=" + arm.Ok + " failure=" + arm.Failure);
            Assert.That(arm.Ok, Is.True, "add arm failed: " + arm.Failure);

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!PackageInstalled(ProbePackage) &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(PackageInstalled(ProbePackage), Is.True,
                "the probe package never registered with UPM after Client.Add");

            // The install's import and compile may run inside the
            // synchronous resolve while the runner holds the reload lock,
            // leaving the flags already quiet with the reload deferred —
            // or the compile may only be starting. Refresh flushes queued
            // imports, then a quiet settle window lets in-flight work
            // surface before the sanctioned crossing.
            AssetDatabase.Refresh();
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
            yield return new WaitForDomainReload();

            // Phase A2 (post-reload domain): the tracked Request died with
            // the old domain — a fresh condition reading the persisted
            // token can only judge through the registered package list.
            if (!TryReadWait(out _, out var addBaseline, out _))
            {
                Assert.Fail("the persisted add wait state is missing after the reload");
            }
            Assert.That(DomainReloadCounter.Current, Is.GreaterThan(addBaseline),
                "the package install did not cross a domain reload");

            var resolved = false;
            deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                var check = CheckPersistedWait();
                if (check.Kind == WaitStatusKind.Done)
                {
                    resolved = true;
                    break;
                }
                Assert.That(check.Kind, Is.Not.EqualTo(WaitStatusKind.Failed),
                    "the resumed add wait failed: " + check.Code + " " + check.Hint);
                yield return null;
            }
            Assert.That(resolved, Is.True,
                "the resumed add wait stayed Pending after the induced reload");
            RecordL07("add", addBaseline);

            // Phase B1 (this domain): the same wait lifecycle for the
            // removal — the Request is tracked, the token persisted, the
            // unload compile forces the next reload.
            var removeArm = ArmRemoveWait();
            Crumb("remove arm returned ok=" + removeArm.Ok + " failure=" + removeArm.Failure);
            Assert.That(removeArm.Ok, Is.True,
                "remove arm failed: " + removeArm.Failure);

            deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (PackageInstalled(ProbePackage) &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(PackageInstalled(ProbePackage), Is.False,
                "the probe package stayed registered after Client.Remove");

            AssetDatabase.Refresh();
            settleDeadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            quietAt = -1.0;
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
            yield return new WaitForDomainReload();

            // Phase B2 (post-reload domain): the registered list confirms
            // the removal — the awaited state is the name absent.
            if (!TryReadWait(out _, out var removeBaseline, out _))
            {
                Assert.Fail("the persisted remove wait state is missing after the reload");
            }
            Assert.That(DomainReloadCounter.Current, Is.GreaterThan(removeBaseline),
                "the package removal did not cross a domain reload");

            resolved = false;
            deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                var check = CheckPersistedWait();
                if (check.Kind == WaitStatusKind.Done)
                {
                    resolved = true;
                    break;
                }
                Assert.That(check.Kind, Is.Not.EqualTo(WaitStatusKind.Failed),
                    "the resumed remove wait failed: " + check.Code + " " + check.Hint);
                yield return null;
            }
            Assert.That(resolved, Is.True,
                "the resumed remove wait stayed Pending after the induced reload");
            RecordL07("remove", removeBaseline);
        }

        // The failure case: a registry id no registry carries makes the
        // Request itself fail — the §G4 same-domain judgement reports
        // OW-UNITY-009 without a reload.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator PackageResolved_MissingPackage_FailsWithOwUnity009()
        {
            LogAssert.ignoreFailingMessages = true;

            var arm = ArmMissingWait();
            Crumb("missing arm returned ok=" + arm.Ok + " failure=" + arm.Failure);
            Assert.That(arm.Ok, Is.True, "missing arm failed: " + arm.Failure);

            var failed = false;
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                var check = CheckPersistedWait();
                if (check.Kind == WaitStatusKind.Failed)
                {
                    Assert.That(check.Code, Is.EqualTo("OW-UNITY-009"),
                        "a failed package request must surface OW-UNITY-009, got " +
                        check.Code);
                    Assert.That(check.Hint, Is.Not.Empty,
                        "the OW-UNITY-009 verdict must carry the request's error text");
                    failed = true;
                    break;
                }
                Assert.That(check.Kind, Is.EqualTo(WaitStatusKind.Pending),
                    "a missing package must fail, never complete — got " + check.Kind);
                yield return null;
            }
            Assert.That(failed, Is.True,
                "the missing-package wait never failed — the Request never reported");
        }

        // ---------- phase helpers (no yields: static state only) ----------

        // Arms the add wait exactly the way a Waitable unity.package.add
        // step does: the issued AddRequest is tracked and its slot written
        // into the resumeToken, which is persisted with the reload-count
        // baseline for the post-reload domain.
        private static ProbePhaseResult ArmAddWait()
        {
            try
            {
                var request = Client.Add(ProbeAddId);
                var token = PackageResolvedCondition.InitialToken(
                    ProbeAddId, expectPresent: true, request);
                PersistWait(token, "add");
                Crumb("add armed: AddRequest tracked, resumeToken persisted");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("add arm threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("add arm threw an unexpected exception: " + e);
            }
        }

        private static ProbePhaseResult ArmRemoveWait()
        {
            try
            {
                var request = Client.Remove(ProbePackage);
                var token = PackageResolvedCondition.InitialToken(
                    ProbePackage, expectPresent: false, request);
                PersistWait(token, "remove");
                Crumb("remove armed: RemoveRequest tracked, resumeToken persisted");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("remove arm threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("remove arm threw an unexpected exception: " + e);
            }
        }

        private static ProbePhaseResult ArmMissingWait()
        {
            try
            {
                var request = Client.Add(MissingPackage);
                var token = PackageResolvedCondition.InitialToken(
                    MissingPackage, expectPresent: true, request);
                PersistWait(token, "missing");
                Crumb("missing armed: AddRequest tracked, resumeToken persisted");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("missing arm threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("missing arm threw an unexpected exception: " + e);
            }
        }

        // One fresh check of the persisted resumeToken — the same call a
        // resumed Waitable step's Poll makes in this domain.
        private static PersistedCheck CheckPersistedWait()
        {
            var check = new PersistedCheck();
            if (!TryReadWait(out var token, out _, out _))
            {
                check.Kind = WaitStatusKind.Failed;
                check.Code = "missing-state";
                return check;
            }
            var verdict = new PackageResolvedCondition().Check(token);
            check.Kind = verdict.Kind;
            if (verdict.Error is { } error)
            {
                check.Code = error.Code;
                check.Hint = error.Hint ?? string.Empty;
            }
            return check;
        }

        // The wait state file: the resumeToken plus the reload-count
        // baseline at request time — persisted like the journal's
        // StepWaiting record keeps the token, so the post-reload phase
        // gets nothing but durable state.
        private static void PersistWait(JsonValue token, string phase)
        {
            var state = new JObject
            {
                ["phase"] = phase,
                ["token"] = token.ToToken(),
                ["reloads"] = DomainReloadCounter.Current,
            };
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(StatePath, state.ToString());
            Crumb("persisted wait state: " +
                state.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static bool TryReadWait(
            out JsonValue token, out int reloadBaseline, out string phase)
        {
            token = null!;
            reloadBaseline = 0;
            phase = string.Empty;
            if (!File.Exists(StatePath))
            {
                return false;
            }
            var state = JObject.Parse(File.ReadAllText(StatePath));
            token = JsonValue.From(state["token"]!);
            reloadBaseline = (int)state["reloads"]!;
            phase = (string)state["phase"]!;
            return true;
        }

        // The SPEC §T.4 L-07 record: the method the resumed wait actually
        // used to confirm completion — the registered package list, judged
        // only while the Editor was neither compiling nor updating —
        // plus the reload the judgement crossed.
        private static void RecordL07(string phase, int reloadBaseline)
        {
            var records = File.Exists(L07Path)
                ? JArray.Parse(File.ReadAllText(L07Path))
                : new JArray();
            var record = new JObject
            {
                ["verification"] = "local:T-W4.1-02:package-resolved",
                ["phase"] = phase,
                ["package"] = ProbePackage,
                ["confirmationMethod"] = "registered-package-list",
                ["idleGate"] = "not-compiling-and-not-updating",
                ["reloadDelta"] = DomainReloadCounter.Current - reloadBaseline,
            };
            records.Add(record);
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(L07Path, records.ToString());
            Crumb("L-07 record (" + phase + "): " +
                record.ToString(Newtonsoft.Json.Formatting.None));
        }
    }

    // The fixture's borrow/restore contract modelled on bytes alone — no
    // file IO, no Unity API, no UPM Client — so the precondition, capture
    // and restore the reload tests' UnitySetUp/teardown drive are
    // exercisable in a plain [Test] domain. The fixture feeds it real
    // manifest/lockfile bytes and installed-package names; the
    // regression tests feed it fixtures.
    internal static class PackageProbePlan
    {
        private static readonly Regex s_depsBlock =
            new Regex("\"dependencies\"\\s*:\\s*\\{(?<body>[^{}]*)\\}");
        private static readonly Regex s_depPair =
            new Regex("\"(?<n>[^\"]+)\"\\s*:\\s*\"(?<v>[^\"]*)\"");

        // The byte-for-byte capture the backup dir persists across
        // domain reloads: the manifest always, the lockfile only when
        // the project already had one.
        internal sealed class Capture
        {
            internal readonly byte[] Manifest;
            internal readonly byte[]? Lock;

            internal Capture(byte[] manifest, byte[]? lockFile)
            {
                Manifest = manifest;
                Lock = lockFile;
            }

            internal bool Mentions(string name) =>
                ManifestMentions(Manifest, name);
        }

        internal enum RestoreAction
        {
            Leave,
            Write,
            Delete,
        }

        internal static Capture Snapshot(byte[] manifestBytes, byte[]? lockBytes) =>
            new Capture(manifestBytes, lockBytes);

        // Whether the manifest's "dependencies" block declares the
        // package — the same regex parse the fixture ran on the file
        // text, now over the captured bytes.
        internal static bool ManifestMentions(byte[] manifestBytes, string name)
        {
            var match = s_depsBlock.Match(Encoding.UTF8.GetString(manifestBytes));
            if (!match.Success)
            {
                return false;
            }
            foreach (Match pair in s_depPair.Matches(match.Groups["body"].Value))
            {
                if (pair.Groups["n"].Value == name)
                {
                    return true;
                }
            }
            return false;
        }

        // The probe may borrow the project's manifest only while the
        // project does not already depend on the probe package — adding
        // then removing a real dependency would rewrite state the
        // fixture may restore, never discard — and while the fixture's
        // missing id is genuinely absent, or the OW-UNITY-009 case
        // would run against a real dependency. A refusal names the
        // offending dependency explicitly; it is an unmet environment
        // precondition, never a silent pass.
        internal static bool TryAdmit(
            byte[] manifestBytes,
            ISet<string> installedPackages,
            string probePackage,
            string missingPackage,
            out string refusal)
        {
            if (ManifestMentions(manifestBytes, probePackage) ||
                installedPackages.Contains(probePackage))
            {
                refusal = "precondition unmet: the project already depends on " +
                    probePackage + " — the package probe would add and " +
                    "remove a real project dependency";
                return false;
            }
            if (ManifestMentions(manifestBytes, missingPackage))
            {
                refusal = "precondition unmet: the project already depends on " +
                    missingPackage + " — the OW-UNITY-009 case needs a " +
                    "genuinely absent registry id";
                return false;
            }
            refusal = string.Empty;
            return true;
        }

        // What the restore must do to one file to put the captured bytes
        // back: leave it when it already holds them, write the capture
        // when it differs or vanished, and delete a lockfile the
        // original project never had.
        internal static RestoreAction ManifestRestore(
            Capture capture, byte[]? current) =>
            BytesEqual(current, capture.Manifest)
                ? RestoreAction.Leave
                : RestoreAction.Write;

        internal static RestoreAction LockRestore(
            Capture capture, byte[]? current) =>
            capture.Lock is { } lockBytes
                ? (BytesEqual(current, lockBytes)
                    ? RestoreAction.Leave
                    : RestoreAction.Write)
                : (current is null
                    ? RestoreAction.Leave
                    : RestoreAction.Delete);

        // The settle check both setup and teardown wait on: the manifest
        // and lockfile hold the captured bytes again and the install set
        // agrees — the probe present exactly when the original manifest
        // declared it.
        internal static bool MatchesCapture(
            Capture capture,
            byte[]? manifestNow,
            byte[]? lockNow,
            ISet<string> installedPackages,
            string probePackage)
        {
            if (!BytesEqual(manifestNow, capture.Manifest))
            {
                return false;
            }
            if (capture.Lock is { } lockBytes)
            {
                if (!BytesEqual(lockNow, lockBytes))
                {
                    return false;
                }
            }
            else if (lockNow is not null)
            {
                return false;
            }
            return installedPackages.Contains(probePackage) ==
                capture.Mentions(probePackage);
        }

        internal static bool BytesEqual(byte[]? a, byte[]? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }
            if (a.Length != b.Length)
            {
                return false;
            }
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }

    // Codex F3 (T-W4.1-02) regression evidence for the fixture's
    // borrow/restore plan: a pre-existing probe dependency refuses the
    // probe outright — it is restored, never removed — and a capture →
    // edit → restore round trip puts the exact original bytes back.
    // Pure [Test] cases over PackageProbePlan; no UnitySetUp borrow, no
    // domain reload.
    internal sealed class PackageProbePlanTests
    {
        private const string Probe = "com.unity.editorcoroutines";
        private const string Missing = "com.opweave.fixture.nosuchpackage";

        private static readonly byte[] s_manifestWithProbe =
            Encoding.UTF8.GetBytes(
                "{\n" +
                "  \"dependencies\": {\n" +
                "    \"com.unity.editorcoroutines\": \"1.0.0\",\n" +
                "    \"com.unity.test-framework\": \"1.1.33\"\n" +
                "  }\n" +
                "}\n");

        private static readonly byte[] s_manifestPlain =
            Encoding.UTF8.GetBytes(
                "{\n" +
                "  \"dependencies\": {\n" +
                "    \"com.unity.test-framework\": \"1.1.33\"\n" +
                "  }\n" +
                "}\n");

        private static readonly byte[] s_lock =
            Encoding.UTF8.GetBytes(
                "{\n" +
                "  \"dependencies\": {\n" +
                "    \"com.unity.test-framework\": {\n" +
                "      \"version\": \"1.1.33\",\n" +
                "      \"depth\": 0,\n" +
                "      \"source\": \"builtin\"\n" +
                "    }\n" +
                "  }\n" +
                "}\n");

        // What RestoreOriginalSync's ApplyRestore does to a file, at the
        // byte level: write the captured bytes, delete what the original
        // lacked, leave the rest.
        private static byte[]? Apply(
            byte[]? current, byte[]? captured,
            PackageProbePlan.RestoreAction action)
        {
            switch (action)
            {
                case PackageProbePlan.RestoreAction.Write:
                    return captured;
                case PackageProbePlan.RestoreAction.Delete:
                    return null;
                default:
                    return current;
            }
        }

        [Test]
        public void PreExistingProbeDependency_RefusesProbe_PreservesOriginalBytes()
        {
            var installed = new HashSet<string>(StringComparer.Ordinal)
            {
                Probe,
                "com.unity.test-framework",
            };

            // The manifest already declares the probe: the plan refuses
            // to run an add/remove probe against it and names the
            // dependency.
            Assert.That(
                PackageProbePlan.TryAdmit(
                    s_manifestWithProbe, installed, Probe, Missing,
                    out var refusal),
                Is.False,
                "the plan must refuse to run the probe against a real project dependency");
            Assert.That(refusal, Does.Contain(Probe),
                "the refusal must name the pre-existing dependency");

            // The same refusal when the manifest is clean but the probe
            // is still installed — a leftover a dead run never removed.
            Assert.That(
                PackageProbePlan.TryAdmit(
                    s_manifestPlain,
                    new HashSet<string>(StringComparer.Ordinal) { Probe },
                    Probe, Missing, out var installedRefusal),
                Is.False,
                "an installed probe package is the same unmet precondition");
            Assert.That(installedRefusal, Does.Contain(Probe));

            // And when the fixture's missing id is itself a real
            // dependency, the OW-UNITY-009 case is refused too.
            var manifestWithMissing = Encoding.UTF8.GetBytes(
                "{\n" +
                "  \"dependencies\": {\n" +
                "    \"" + Missing + "\": \"1.0.0\"\n" +
                "  }\n" +
                "}\n");
            Assert.That(
                PackageProbePlan.TryAdmit(
                    manifestWithMissing,
                    new HashSet<string>(StringComparer.Ordinal),
                    Probe, Missing, out var missingRefusal),
                Is.False);
            Assert.That(missingRefusal, Does.Contain(Missing));

            // A capture of that project restores byte-identical bytes:
            // the pre-existing dependency is put back, never stripped —
            // nothing is rewritten or deleted.
            var capture = PackageProbePlan.Snapshot(s_manifestWithProbe, s_lock);
            Assert.That(capture.Mentions(Probe), Is.True);
            Assert.That(
                PackageProbePlan.ManifestRestore(capture, s_manifestWithProbe),
                Is.EqualTo(PackageProbePlan.RestoreAction.Leave));
            Assert.That(
                PackageProbePlan.LockRestore(capture, s_lock),
                Is.EqualTo(PackageProbePlan.RestoreAction.Leave));
            Assert.That(capture.Manifest, Is.EqualTo(s_manifestWithProbe));
            Assert.That(capture.Lock, Is.EqualTo(s_lock));
        }

        [Test]
        public void CaptureRestoreRoundTrip_RestoresOriginalBytes_AfterSimulatedEdits()
        {
            var capture = PackageProbePlan.Snapshot(s_manifestPlain, s_lock);

            // Simulate the probe's edits mid-run: the manifest gains the
            // probe dependency, the lockfile is rewritten, the install
            // set gains the package.
            var editedManifest = Encoding.UTF8.GetBytes(
                "{\n" +
                "  \"dependencies\": {\n" +
                "    \"com.unity.editorcoroutines\": \"1.0.0\",\n" +
                "    \"com.unity.test-framework\": \"1.1.33\"\n" +
                "  }\n" +
                "}\n");
            var editedLock = Encoding.UTF8.GetBytes(
                "{\n  \"dependencies\": {}\n}\n");
            var installedDuringProbe =
                new HashSet<string>(StringComparer.Ordinal)
                {
                    Probe,
                    "com.unity.test-framework",
                };

            // The edited state is not settled: bytes differ and the
            // probe is present though the original never declared it.
            Assert.That(
                PackageProbePlan.MatchesCapture(
                    capture, editedManifest, editedLock,
                    installedDuringProbe, Probe),
                Is.False);

            // The plan orders a write-back of both files; applying it
            // restores the captured bytes exactly.
            Assert.That(
                PackageProbePlan.ManifestRestore(capture, editedManifest),
                Is.EqualTo(PackageProbePlan.RestoreAction.Write));
            Assert.That(
                PackageProbePlan.LockRestore(capture, editedLock),
                Is.EqualTo(PackageProbePlan.RestoreAction.Write));

            var manifestNow = Apply(
                editedManifest, capture.Manifest,
                PackageProbePlan.ManifestRestore(capture, editedManifest));
            var lockNow = Apply(
                editedLock, capture.Lock,
                PackageProbePlan.LockRestore(capture, editedLock));
            var installedNow = new HashSet<string>(StringComparer.Ordinal)
            {
                "com.unity.test-framework",
            };

            Assert.That(manifestNow, Is.EqualTo(s_manifestPlain),
                "the restored manifest must be byte-identical to the original");
            Assert.That(lockNow, Is.EqualTo(s_lock),
                "the restored lockfile must be byte-identical to the original");
            Assert.That(
                PackageProbePlan.MatchesCapture(
                    capture, manifestNow, lockNow, installedNow, Probe),
                Is.True,
                "the restored project must satisfy the fixture's settle check");

            // A project that never had a lockfile gets none recreated —
            // the plan deletes a stray one and leaves an absent one.
            var noLockCapture = PackageProbePlan.Snapshot(s_manifestPlain, null);
            Assert.That(
                PackageProbePlan.LockRestore(noLockCapture, editedLock),
                Is.EqualTo(PackageProbePlan.RestoreAction.Delete));
            Assert.That(
                Apply(editedLock, null,
                    PackageProbePlan.LockRestore(noLockCapture, editedLock)),
                Is.Null);
            Assert.That(
                PackageProbePlan.LockRestore(noLockCapture, null),
                Is.EqualTo(PackageProbePlan.RestoreAction.Leave));
            Assert.That(
                PackageProbePlan.MatchesCapture(
                    noLockCapture, s_manifestPlain, null, installedNow, Probe),
                Is.True);
        }
    }
}
