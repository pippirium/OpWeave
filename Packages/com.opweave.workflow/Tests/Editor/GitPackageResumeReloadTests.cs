#nullable enable

// T-W4.3-05 [Edit] tests: local:T-W4.3-05:git-package-resume — the
// SPEC §G8.1 test 9 outcome through the T-W4.3-03 resume path across
// REAL domain reloads (SPEC §T.3): a Run interrupted at the
// unity.package.add step of a Git URL package is rebuilt and decided
// by the fresh domain's §F4 pass —
//
//   * interrupted at a bare step_started (the journal ends exactly
//     after the write-ahead record): the real PackageAddEvidence.Judge
//     verdict decides — Completed only on the full evidence row
//     (identity + resolved full commit SHA + three-channel agreement),
//     NotApplied only on proven absence, every other state NeedsReview;
//   * interrupted mid-StepWaiting — and with a truncated final journal
//     line dropped per §F3 — the persisted resumeToken reaches the
//     restored Poll verbatim and the Run completes;
//   * no case adds the package twice and no case removes it.
//
// Every path in this file is the PRODUCTION unity.package.add
// Operation — the real ValidateStatic, Preflight, Execute (the real
// Client.Add), Poll and Reconcile, including the NotApplied
// re-execution through ResumePolicy. The step's id is a Git URL the
// SG-19 source contract admits — an https origin on a lowercase
// non-local host, allowlisted through the Operation's loadSettings
// seam — and the admissible host exists nowhere: the test harness
// arms a process-scoped git url.<local>.insteadOf rewrite (the
// GIT_CONFIG_* environment config) BEFORE the Editor launches, so the
// git subprocess the UPM package server spawns inherits it and the
// origin is served from the local bare fixture repository —
// Client.Add's git resolution stays offline while every persisted
// channel — the manifest dep, the lockfile and the registered
// packageId — carries the admissible URL verbatim. The seams stay
// below the Operation
// boundary: the fixture repository, the settings feed, the UPM request
// issue (the real Client.Add, or the requestless token the production
// InitialToken writes for the interrupted-before-request case), the
// token-recording wait condition and the journal cut points.
//
// UTF resumes a domain-reload-interrupted [UnityTest] by bumping the
// enumerator PC, so the segment between the last completed yield and
// the reload re-runs — and its before/after command state is a plain
// object that does NOT survive the reload, so UnitySetUp and
// UnityTearDown replay from their first statement every domain.
// Every phase therefore runs once behind a SessionState progress
// record, a "test in progress" mark armed at the head of the test
// body keeps a replayed setup from restoring or deleting the live
// fixture state, every recompile boundary is marked in SessionState
// BEFORE it yields and never issued twice, the reload-causing marker
// write is idempotent (deterministic content, written only on a byte
// difference), and a boundary yield fires only while the armed
// sentinel lives. UnitySetUp may never cross a domain reload — a
// reload inside a before-command re-runs the test it guards — so
// cleanup that needs no compile (the Library/ probe and backup
// dirs, the manifest/lock restore) is synchronous everywhere, and
// setup-side cleanup that schedules a compile (a leftover install's
// restore, the marker script folder at the body's head) leaves its
// reload deferred for the body's own first recompile boundary.
// UnityTearDown ends at the sanctioned WaitForDomainReload boundary
// whenever its cleanup scheduled a compile — the reload replays
// only the after-command, never the test it already recorded, the
// same release ScriptsCompiledReloadTests and PackageRemoveUpmTests
// make at their teardowns — so the deferred reload the class queued
// is never handed to the next fixture, whose waits do not expect
// one. An [InitializeOnLoad] hook counts EVERY domain load
// unconditionally into a per-test and a class-wide SessionState
// counter no replayed body can clear — past either cap an abort flag
// turns every recompile request into a no-op and fails every
// remaining test at UnitySetUp, so a request/reload cycle can never
// hang the Editor.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Recovery;
using OpWeave.Core.Registry;
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
    /// <c>local:T-W4.3-05:git-package-resume</c> in Unity 6000.0 EditMode:
    /// Git <c>unity.package.add</c> interruption at each journal boundary
    /// followed by a real domain reload, resumed by the fresh domain's
    /// own §F4 pass (SPEC §G8.1 test 9, §F4, §F5, §X.9).
    /// </summary>
    internal sealed class GitPackageResumeReloadTests
    {
        // ---------- fixture identities ----------

        // The fixture package: package.json plus one Editor assembly so
        // its install/removal compiles — the compile ends in the domain
        // reload the interruption crosses. Distinct name/repo from
        // PackageAddUpmTests' fixture so a leftover from one suite can
        // never satisfy the other.
        private const string FixturePackage = "com.opweave.fixture.gitresume";
        private const string FixtureAlias = "com.opweave.fixture.gitresume.dup";
        private const string FixtureAssemblyName = "OpWeave.Fixture.GitResume.Editor";
        private const string TagUndeterminable = "vresumetag";
        private const string TagMoved = "vresumemove";

        private const string SrcDirName = "gitresume-src";
        private const string BareDirName = "repo.git";
        private const string MissingDirName = "missing-repo.git";

        private const string WorkflowId = "example.gitpackage.resume";
        private const string HoldOp = "test.gitpkg.resume.hold";

        // The descriptor-fingerprint seeds of the two registered
        // Operations — hexadecimal characters only, since a seed becomes
        // a 64-digit descriptor hash on both the plan step and the
        // registration ('g'/'h' are not hex).
        private const char GitSeed = 'a';
        private const char HoldSeed = 'd';

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;
        private const int PumpTicks = 40;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");

        private static string LockPath =>
            Path.Combine(ProjectRoot, "Packages", "packages-lock.json");

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveGitPackageResume");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string StateFile => Path.Combine(ProbeDir, "state.txt");
        private static string ReleaseFlag => Path.Combine(ProbeDir, "release.flag");
        private static string TokenSeenFile => Path.Combine(ProbeDir, "token-seen.txt");

        private static string BackupDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveGitResumeBackup");

        private static string ManifestBackupPath =>
            Path.Combine(BackupDir, "orig-manifest.json");

        private static string LockBackupPath =>
            Path.Combine(BackupDir, "orig-packages-lock.json");

        // The trusted fixture root the harness exports before the
        // Editor launches — RequireHarnessEnvironment Assert.Fails the
        // test before any use if it is unset, and the armed
        // GIT_CONFIG_* rewrite maps the two admissible https origins
        // onto <root>/repo.git (served) and <root>/missing-repo.git
        // (never created, so the missing origin fails locally).
        private const string FixtureRootVar = "OPWEAVE_GIT_FIXTURE_ROOT";

        private static string? FixtureRootEnv =>
            Environment.GetEnvironmentVariable(FixtureRootVar);

        private static string FixtureRoot => FixtureRootEnv!;

        private static string SrcRepo => Path.Combine(FixtureRoot, SrcDirName);
        private static string BareRepo => Path.Combine(FixtureRoot, BareDirName);
        private static string BareRepoUrl => "file:///" + BareRepo.Replace('\\', '/');
        private static string MissingRepoDir => Path.Combine(FixtureRoot, MissingDirName);
        private static string MissingRepoUrl =>
            "file:///" + MissingRepoDir.Replace('\\', '/');

        // The Git origins the production SG-19 contract admits —
        // lowercase https on a non-local host (the .invalid TLD the
        // origin grammar tolerates and DNS can never resolve). The
        // url.<local>.insteadOf rewrites the harness arms before the
        // Editor launches serve the first from the local bare
        // repository and fail the second — the never-resolving source
        // the manifest-only and source-mismatch tamper states need —
        // instantly and offline.
        private const string AdmissibleOrigin =
            "https://fixture.opweave.invalid/gitresume/repo.git";
        private const string MissingOrigin =
            "https://fixture.opweave.invalid/gitresume/missing-repo.git";

        private static string DepFor(string revision) =>
            AdmissibleOrigin + "?path=/pkgs/pkgadd#" + revision;

        private static string MissingDepFor(string revision) =>
            MissingOrigin + "?path=/pkgs/pkgadd#" + revision;

        // The tagged fixture commit is re-derived after a domain
        // reload straight from the bare repository on disk — never
        // read through an iterator local a lambda captured. UTF
        // restores an interrupted [UnityTest] enumerator without the
        // compiler display class a post-reload closure over a hoisted
        // local would need, so building that delegate inside MoveNext
        // throws a bare ArgumentException before the callee is ever
        // entered; a static helper's own closure is created fresh in
        // the live domain and stays valid.
        private static string ReloadTaggedSha()
        {
            var tagged = Git("rev-parse HEAD~1", BareRepo);
            AssertGit(tagged, "rev-parse HEAD~1");
            return tagged.StdOut.Trim();
        }

        private static Action AliasManifestTamper(string dep) =>
            () => WriteManifestDependency(FixtureAlias, dep);

        private static Action MissingSourceTamper(string revision) =>
            () => WriteManifestDependency(
                FixturePackage, MissingDepFor(revision));

        private static string MarkerFolder => "Assets/Editor/OpWeaveGitPkgResume";
        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveGitPkgResume\n" +
            "{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        // The probe marker body is deterministic per test and arm —
        // never a fresh nonce. UTF resumes the test at the last
        // completed yield, so the segment that armed the reload
        // re-runs whenever ArmReload's own Refresh ends the domain
        // mid-call; a content-equal rewrite must never schedule
        // another compile or the write/refresh/reload cycle loops
        // forever. The first write of a run still lands on a folder
        // setup deleted, so the "identical bytes never schedule a
        // compile" correction-2 hazard stays covered.
        private static string MarkerSourceFor(string markerId) =>
            MarkerSource + "// reload probe " + markerId + "\n";

        // The phase-state nonce: state.txt carries it as its last line
        // and SessionState mirrors it across domain reloads. A resumed
        // domain may re-enter UnitySetUp/UnityTearDown (UTF replays the
        // before/after commands around the restored test — their
        // command state is a plain object, so both restart from their
        // first statement every domain), and a naive cleanup would
        // delete the live probe state — the run-root and the state
        // file — before the test body resumes. The nonce marks the
        // probe dir as owned by the in-flight test; only a dir whose
        // nonce is not the armed one is stale residue. It arms only
        // when phase-1 writes state.txt, so the earlier
        // "test in progress" mark — set at the head of the body — is
        // the primary replay guard and this the second layer.
        private const string ProbeSessionKey =
            "OpWeave.GitPackageResume.state";

        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeSessionKey, nonce);
            return nonce;
        }

        private static void DisarmProbeNonce() =>
            SessionState.EraseString(ProbeSessionKey);

        private static bool ProbeStateIsLive()
        {
            var nonce = SessionState.GetString(ProbeSessionKey, "");
            if (nonce.Length == 0 || !File.Exists(StateFile))
            {
                return false;
            }
            var lines = File.ReadAllLines(StateFile);
            return lines.Length > 0 && lines[lines.Length - 1] == nonce;
        }

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // ---------- replay state ----------
        //
        // UTF's EnumeratorStepHelper resumes a reload-interrupted
        // [UnityTest] at the recorded enumerator PC: the segment
        // between the last completed yield and the reload re-runs, so
        // every side effect in it must be gated or idempotent. UTF's
        // before/after command state is a plain object that does not
        // survive the reload, so UnitySetUp and UnityTearDown replay
        // from their first statement on every domain — the probe
        // nonce alone could not cover that window (it arms mid-body
        // and disarms at teardown entry). All records live in
        // SessionState (it survives the reload): BeginTest clears the
        // segment records and the per-test request marks at a
        // genuinely fresh start — a different test name, or the same
        // name never started before — while a re-entry of the SAME
        // test (its started or in-progress mark still persisted, or a
        // reload already counted for it) is a restart after a reload
        // and fails closed rather than silently re-running: a resumed
        // body continues at its saved yield and never re-runs the
        // body's first statement.
        //
        // The reload counters are the one records nothing but
        // OneTimeSetUp may reset: the [InitializeOnLoad] hook bumps
        // the class-wide total on EVERY domain load — gated only by
        // the class-active mark — and bumps the in-flight test's own
        // count whenever an in-progress mark exists. Four reloads per test is the
        // ceiling the bodies can legitimately cross (the install
        // reload, the marker reload, the NotApplied case's
        // re-execution install, and one cleanup reload — the test's
        // own manifest restore and marker-folder delete — drained at
        // its teardown's own boundary); (4 × tests) + 4 is the
        // class ceiling. Past either cap the abort flag arms once,
        // every recompile request becomes a no-op and UnitySetUp fails
        // every remaining test, so a request/reload cycle fails the
        // suite closed — with a result XML — instead of hanging the
        // Editor.
        private const int MaxDomainReloadsPerTest = 4;
        private const int TestCount = 9;
        private const int MaxDomainReloadsTotal =
            MaxDomainReloadsPerTest * TestCount + 4;
        private const string PhasesDoneSessionKey =
            "OpWeave.GitPackageResume.phases-done";
        private const string SentinelsArmedSessionKey =
            "OpWeave.GitPackageResume.sentinels-armed";
        private const string ReloadCountSessionKey =
            "OpWeave.GitPackageResume.reload-count";
        private const string ReloadTotalSessionKey =
            "OpWeave.GitPackageResume.reload-total";
        private const string StartedSessionKey =
            "OpWeave.GitPackageResume.started";
        private const string ReloadRequestsSessionKey =
            "OpWeave.GitPackageResume.reload-requests";
        private const string TestInProgressSessionKey =
            "OpWeave.GitPackageResume.test-in-progress";
        // The body-owner resume record behind NoteDomainReload's
        // misplaced-resume check: written ONLY by a test's own
        // BeginTest and read at every reload boundary. It is kept out
        // of EndTest and ResetTest deliberately: UTF 1.7.0 rebuilds the
        // fixture work item on every domain a mid-suite reload resumes
        // into and re-executes [OneTimeSetUp] (the unfinished suite is
        // never in alreadyExecutedTests, so m_DontRunRestoringResult
        // stays false), so a record any suite-level or per-test reset
        // touches is lost at every reload a test legitimately crosses —
        // the T-W4.6-03 failure mode on 6000.5. Only a genuine
        // (non-replayed) OneTimeSetUp and the OneTimeTearDown erase it,
        // at the suite's real start and end. A body UTF resumes from
        // another test's stale enumerator position never ran its own
        // BeginTest, so the record names the previous owner or nothing
        // and the boundary check still fails closed.
        private const string ResumeOwnerSessionKey =
            "OpWeave.GitPackageResume.resume-owner";
        private const string ReloadAbortSessionKey =
            "OpWeave.GitPackageResume.reload-abort";
        // "A compile the class scheduled may still hold a deferred
        // reload": armed wherever cleanup schedules a compile without
        // a boundary in front of it — UnitySetUp's leftover-install
        // restore and every marker-folder delete — and cleared by the
        // first confirmed reload after it (NoteDomainReload) or by the
        // teardown drain itself. SessionState, because the mark must
        // survive the very reload it predicts.
        private const string CompilePendingSessionKey =
            "OpWeave.GitPackageResume.compile-pending";
        // "This class owns the run": armed by OneTimeSetUp, cleared by
        // OneTimeTearDown — the [InitializeOnLoad] hook counts domain
        // loads only while it is set, so a later fixture's reloads
        // never write the class's keys or arm its abort flag.
        private const string ClassActiveSessionKey =
            "OpWeave.GitPackageResume.class-active";

        private static void ResetTest()
        {
            // Segment records only — the reload counters, the started
            // marks and the abort flag are the run-level guard records
            // nothing below OneTimeSetUp may reset: clearing them here
            // would let a runaway re-entering this body disarm its own
            // guard.
            SessionState.SetString(PhasesDoneSessionKey, "");
            SessionState.SetString(SentinelsArmedSessionKey, "");
        }

        private static string CurrentTestFullName =>
            TestContext.CurrentContext.Test.FullName ??
            TestContext.CurrentContext.Test.Name ?? "unknown";

        private static bool TestInProgressActive() =>
            SessionState.GetString(TestInProgressSessionKey, "").Length > 0;

        private static void EndTest() =>
            SessionState.EraseString(TestInProgressSessionKey);

        // The persisted "this test started" record: a ";name;" set that
        // survives both the reloads a test legitimately crosses and the
        // teardown that clears the in-progress mark — so a test the
        // domain re-runs from its first statement is caught even after
        // EndTest. Cleared only in OneTimeSetUp.
        private static bool StartedBefore(string name) =>
            SessionState.GetString(StartedSessionKey, "")
                .Contains(";" + name + ";");

        private static void MarkStarted(string name)
        {
            if (!StartedBefore(name))
            {
                SessionState.SetString(
                    StartedSessionKey,
                    SessionState.GetString(StartedSessionKey, "") +
                        ";" + name + ";");
            }
        }

        // Marks "test in progress" in SessionState as the test body's
        // FIRST statement — before EnsureFixtures and before any
        // reload-causing action — so a replayed setup on the next
        // domain can tell the fixture state still belongs to a live
        // test even when the reload landed before the probe nonce was
        // armed. A genuinely fresh start clears the test's segment
        // records and request marks — never the reload counters or
        // the abort flag — then performs every piece of cleanup,
        // including the marker-folder delete whose deferred compile
        // rides this body's first recompile boundary (a setup may
        // never request one; the teardown's drain is the sanctioned
        // release at its own boundary). A re-entry of the same
        // started test is a restart after a reload and fails closed.
        private static void BeginTest()
        {
            var name = CurrentTestFullName;
            var inProgress =
                SessionState.GetString(TestInProgressSessionKey, "");
            if (string.Equals(inProgress, name, StringComparison.Ordinal) ||
                StartedBefore(name) ||
                GuardCount(name) > 0)
            {
                Assert.Fail(
                    "\"" + name + "\" restarted from its first " +
                    "statement after " + GuardCount(name) +
                    " domain reload(s) (in-progress mark \"" + inProgress +
                    "\", started mark " + StartedBefore(name) + ") — a " +
                    "test must resume at its saved yield, never re-run " +
                    "from the top; refusing to silently re-run");
            }
            if (inProgress.Length > 0)
            {
                Crumb("dropping a stale in-progress mark for \"" +
                    inProgress + "\" left by a previous test");
            }
            ResetTest();
            SessionState.SetString(ReloadRequestsSessionKey, "");
            SessionState.SetString(TestInProgressSessionKey, name);
            SessionState.SetString(ResumeOwnerSessionKey, name);
            MarkStarted(name);
            if (AbortRequested())
            {
                Assert.Fail(
                    "domain-reload loop guard tripped before \"" + name +
                    "\" could start: " +
                    SessionState.GetString(ReloadAbortSessionKey, ""));
            }
            CleanupProbeArtifacts();
            CleanupCompileArtifacts();
        }

        // The per-test reload counts — one ";name:count;" map written
        // ONLY by the [InitializeOnLoad] hook, so it counts every real
        // domain reload while the named test is in flight whether the
        // test body, its setup or its teardown ever got to run.
        private static int GuardCount(string name)
        {
            var map = SessionState.GetString(ReloadCountSessionKey, "");
            var marker = ";" + name + ":";
            var at = map.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return 0;
            }
            var start = at + marker.Length;
            var end = map.IndexOf(';', start);
            if (end < 0)
            {
                end = map.Length;
            }
            return int.TryParse(map.Substring(start, end - start), out var n)
                ? n
                : 0;
        }

        private static void BumpGuardCount(string name)
        {
            var map = SessionState.GetString(ReloadCountSessionKey, "");
            var marker = ";" + name + ":";
            var at = map.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                SessionState.SetString(
                    ReloadCountSessionKey, map + marker + "1;");
                return;
            }
            var start = at + marker.Length;
            var end = map.IndexOf(';', start);
            if (end < 0)
            {
                end = map.Length;
            }
            var bumped =
                int.TryParse(map.Substring(start, end - start), out var n)
                    ? n + 1
                    : 1;
            SessionState.SetString(
                ReloadCountSessionKey,
                map.Substring(0, start) + bumped + map.Substring(end));
        }

        private static bool AbortRequested() =>
            SessionState.GetString(ReloadAbortSessionKey, "").Length > 0;

        private static void NoteCompilePending() =>
            SessionState.SetString(CompilePendingSessionKey, "1");

        private static bool CompilePending =>
            SessionState.GetString(CompilePendingSessionKey, "").Length > 0;

        private static void ClearCompilePending() =>
            SessionState.EraseString(CompilePendingSessionKey);

        // Persists "requested <boundary>" BEFORE the caller yields —
        // the mark is what a domain that died inside the request sees
        // on the next load. A resumed segment that finds the mark
        // must only wait for or confirm the reload it already caused
        // (the domain sentinel), never issue the request again.
        private static bool RequestOnce(string boundaryId)
        {
            var name = CurrentTestFullName;
            var requested =
                SessionState.GetString(ReloadRequestsSessionKey, "");
            if (requested.Contains(";" + boundaryId + ";"))
            {
                Crumb("boundary \"" + boundaryId + "\" recompile " +
                    "already requested for \"" + name + "\" — " +
                    "confirming only (reloads=" + GuardCount(name) + ")");
                return false;
            }
            SessionState.SetString(
                ReloadRequestsSessionKey,
                requested + ";" + boundaryId + ";");
            Crumb("boundary \"" + boundaryId + "\" recompile " +
                "requested for \"" + name + "\" (reloads=" +
                GuardCount(name) + ")");
            return true;
        }

        // Every explicit recompile/refresh point goes through here:
        // the abort flag skips the request entirely and fails the
        // test closed, otherwise the once-per-boundary mark decides.
        private static bool MayRequest(string boundaryId)
        {
            if (AbortRequested())
            {
                Assert.Fail(
                    "domain-reload loop guard: \"" + CurrentTestFullName +
                    "\" exceeded " + MaxDomainReloadsPerTest +
                    " domain reloads — recompile request for boundary \"" +
                    boundaryId + "\" suppressed (" +
                    SessionState.GetString(ReloadAbortSessionKey, "") + ")");
            }
            return RequestOnce(boundaryId);
        }

        private static bool PhaseDone(string phaseId) =>
            SessionState.GetString(PhasesDoneSessionKey, "")
                .Contains(";" + phaseId + ";");

        private static void MarkPhaseDone(string phaseId)
        {
            if (!PhaseDone(phaseId))
            {
                SessionState.SetString(
                    PhasesDoneSessionKey,
                    SessionState.GetString(PhasesDoneSessionKey, "") +
                        ";" + phaseId + ";");
            }
        }

        // Runs a phase body once per test: a resumed segment calls it
        // again, sees the done mark and takes the recorded Pass
        // without re-running the side effects — Execute calls, journal
        // truncation and marker writes happen exactly once.
        private static PhaseResult PhaseGate(
            string phaseId, Func<PhaseResult> body)
        {
            if (PhaseDone(phaseId))
            {
                return PhaseResult.Pass();
            }
            var result = body();
            if (result.Ok)
            {
                MarkPhaseDone(phaseId);
            }
            return result;
        }

        // Arms the domain sentinel once per boundary id. A phase arms
        // at entry so a reload crossing anywhere in its segment —
        // before or after the boundary statement — still leaves the
        // sentinel dead for the boundary check; on a resumed segment
        // the persisted mark keeps a dead sentinel dead instead of
        // re-arming it into a wait for a second reload.
        private static void ArmBoundary(string boundaryId)
        {
            var armed = SessionState.GetString(SentinelsArmedSessionKey, "");
            if (armed.Contains(";" + boundaryId + ";"))
            {
                return;
            }
            SessionState.SetString(
                SentinelsArmedSessionKey, armed + ";" + boundaryId + ";");
            ArmDomainSentinel();
        }

        // The fail-closed half of the loop guard: confirms a boundary
        // crossing the test expected — a crumb for the log plus the
        // check against the authoritative [InitializeOnLoad] count.
        // A runaway trips here even when the test body's own
        // bookkeeping was replayed — the per-test [Timeout] cannot
        // fire while reloads keep restarting it.
        private static void NoteDomainReload(string phase)
        {
            // The misplaced-resume guard: a body continuing across a
            // domain reload must still own the suite's resume record.
            // UTF 1.7.0 can restore an aborted test's saved
            // enumerator position into the NEXT test's body, which then
            // continues mid-body with none of the fixture records its
            // own BeginTest would have armed; a resume that reaches a
            // boundary note without the resume record naming this
            // test is not this test's continuation — fail closed with a
            // fixed message instead of letting it run into a misleading
            // assertion (the missing phase-1 state file). The record is
            // keyed on ResumeOwnerSessionKey, not the in-progress mark:
            // UTF 1.7.0 re-executes [OneTimeSetUp] on every domain a
            // mid-suite reload resumes into, and EndTest — which the
            // suite reset calls — erases the in-progress mark at every
            // such boundary (the same replay also zeroed the reload
            // counters in the T-W4.6-03 run). The resume record is
            // armed only in BeginTest, so it survives every reload a
            // test legitimately crosses.
            var name = CurrentTestFullName;
            var owner =
                SessionState.GetString(ResumeOwnerSessionKey, "");
            if (!string.Equals(owner, name, StringComparison.Ordinal))
            {
                Assert.Fail(
                    "\"" + name + "\" resumed after a domain reload " +
                    "without its own in-progress record (in-progress " +
                    "mark: \"" + owner + "\") — refusing to " +
                    "continue into a misleading assertion");
            }
            // A confirmed boundary consumed whatever reload the class
            // had queued — the deferred-compile mark must not outlive
            // the reload it described.
            ClearCompilePending();
            var count = GuardCount(name);
            Crumb("boundary \"" + phase + "\" confirmed for \"" + name +
                "\" (reloads=" + count + ")");
            if (AbortRequested() || count > MaxDomainReloadsPerTest)
            {
                Assert.Fail(
                    "domain-reload loop guard: \"" + name + "\" crossed " +
                    count + " domain reloads at \"" + phase + "\" — " +
                    "the suite needs at most " + MaxDomainReloadsPerTest);
            }
        }

        // The un-bypassable half of the loop guard: called from the
        // [InitializeOnLoad] hook below on EVERY freshly loaded
        // domain — gated only by the class-active mark, not even the
        // in-progress mark — so a reload that lands outside any
        // counted window (between a teardown's EndTest and the next
        // test's BeginTest, inside a replayed command, anywhere) still
        // moves the class-wide total, and the in-flight test's own
        // counter whenever one is armed — while no later fixture's
        // reloads can write the class's keys or arm its abort flag:
        // the mark lives from OneTimeSetUp to OneTimeTearDown only.
        // ResetTest and BeginTest cannot touch either record;
        // only OneTimeSetUp resets them. Past either cap the abort
        // flag arms once, every recompile point then skips its request
        // and fails the test, and UnitySetUp fails every remaining
        // test — the Editor stops reloading instead of only recording
        // a failure, and the run ends with a result XML.
        internal static void OnDomainLoaded()
        {
            if (SessionState.GetString(ClassActiveSessionKey, "").Length == 0)
            {
                return;
            }
            var total = SessionState.GetInt(ReloadTotalSessionKey, 0) + 1;
            SessionState.SetInt(ReloadTotalSessionKey, total);
            var name = SessionState.GetString(TestInProgressSessionKey, "");
            if (name.Length > 0)
            {
                BumpGuardCount(name);
            }
            var count = name.Length > 0 ? GuardCount(name) : 0;
            if (!AbortRequested() &&
                (count > MaxDomainReloadsPerTest || total > MaxDomainReloadsTotal))
            {
                SessionState.SetString(
                    ReloadAbortSessionKey,
                    (name.Length > 0 ? name : "<no test in progress>") +
                        " testReloads=" + count + " totalReloads=" + total);
                Crumb("domain-reload loop guard tripped" +
                    (name.Length > 0 ? " on \"" + name + "\"" : "") +
                    ": " + count + " reloads for the in-flight test " +
                    "(cap " + MaxDomainReloadsPerTest + "), " + total +
                    " for the class (cap " + MaxDomainReloadsTotal +
                    ") — every recompile point is now a no-op and " +
                    "UnitySetUp fails every remaining test");
            }
        }

        // The phase-1 fixture when no natural reload intervenes — a
        // no-install phase-1 parks in the SAME domain, so its host must
        // be uninstalled before the journal cut.
        private static RunnerHostFixture? s_phase1Fixture;

        private static void Crumb(string message) => Debug.Log("[W4305] " + message);

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static PhaseResult Pass() => new PhaseResult { Ok = true };

            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // ---------- git fixture construction ----------

        private static string GitExe =>
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPWEAVE_LOCAL_GIT_EXE"))
                ? "git"
                : Environment.GetEnvironmentVariable("OPWEAVE_LOCAL_GIT_EXE")!;

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

        private static void WriteFixturePackage(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "Editor"));
            WriteAssetMeta(Path.Combine(root, "Editor"), "gitres:Editor", folder: true);
            File.WriteAllText(Path.Combine(root, "package.json"),
                "{\n" +
                "  \"name\": \"" + FixturePackage + "\",\n" +
                "  \"version\": \"1.0.0\",\n" +
                "  \"displayName\": \"OpWeave git-resume fixture\",\n" +
                "  \"unity\": \"6000.0\"\n" +
                "}\n");
            WriteAssetMeta(Path.Combine(root, "package.json"), "gitres:package.json");
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
                "gitres:asmdef");
            File.WriteAllText(Path.Combine(root, "Editor", "GitResumeMarker.cs"),
                "#nullable enable\n" +
                "namespace " + FixturePackage + "\n" +
                "{\n" +
                "    internal static class GitResumeMarker\n" +
                "    {\n" +
                "        internal const int Value = 1;\n" +
                "    }\n" +
                "}\n");
            WriteAssetMeta(Path.Combine(root, "Editor", "GitResumeMarker.cs"), "gitres:marker");
        }

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

        // A fixture-tree delete that cannot leave the partial tree a
        // torn cleanup otherwise would: git marks its object files
        // read-only on Windows, so a plain recursive delete throws
        // partway and the half-deleted directory a later generation
        // check would trust survives — the failure mode that broke the
        // suite at fixture setup. Clear read-only on every entry in
        // the tree first, then delete; a delete that still fails names
        // the path and fails the suite — never ignored, never skipped.
        private static void DeleteFixtureTree(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                             path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(
                        entry,
                        File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
                }
                File.SetAttributes(
                    path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                Directory.Delete(path, recursive: true);
            }
            catch (Exception e)
            {
                Assert.Fail(
                    "the fixture tree " + path + " could not be deleted: " +
                    e.Message);
            }
        }

        // Deterministic, fail-closed generation: commit 1 carries the
        // package and gets both tags, commit 2 a marker change so HEAD
        // differs — the moved-tag fixture. Every call deletes the three
        // fixture trees outright and regenerates them: an existing
        // directory is never trusted or reused, so the partial tree a
        // torn cleanup leaves can never skip generation again. The
        // missing origin must fail — nothing may ever exist at
        // <root>/missing-repo.git, and deleting it with the rest keeps
        // the generated fixture the trusted minimal one. Returns
        // (tagged commit, HEAD commit); callers re-run it in each
        // domain since nothing survives the reload.
        private static (string Tagged, string Head) EnsureFixtures()
        {
            Directory.CreateDirectory(FixtureRoot);
            DeleteFixtureTree(SrcRepo);
            DeleteFixtureTree(BareRepo);
            DeleteFixtureTree(MissingRepoDir);
            WriteFixturePackage(Path.Combine(SrcRepo, "pkgs", "pkgadd"));
            AssertGit(Git("init -b main", SrcRepo), "init");
            AssertGit(Git("add -A", SrcRepo), "add");
            AssertGit(Git(
                "-c user.name=OpWeaveGitResumeTest " +
                "-c user.email=gitresume@opweave.invalid commit -m fixture",
                SrcRepo), "commit package");
            File.WriteAllText(
                Path.Combine(SrcRepo, "MARKER.txt"), "second commit marker\n");
            AssertGit(Git("add -A", SrcRepo), "add marker");
            AssertGit(Git(
                "-c user.name=OpWeaveGitResumeTest " +
                "-c user.email=gitresume@opweave.invalid commit -m marker",
                SrcRepo), "commit marker");
            AssertGit(Git("clone --bare \"" + SrcRepo + "\" \"" + BareRepo + "\"",
                FixtureRoot), "clone --bare");
            // Prove the freshly generated bare repository before any
            // test uses it: a valid git directory resolving both
            // commits and, once stamped, both fixture tags.
            AssertGit(Git("rev-parse --git-dir", BareRepo), "rev-parse --git-dir");
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
            AssertGit(Git("tag -f " + TagUndeterminable + " " + taggedSha, BareRepo),
                "tag -f " + TagUndeterminable);
            AssertGit(Git("tag -f " + TagMoved + " " + taggedSha, BareRepo),
                "tag -f " + TagMoved);
            AssertGit(Git(
                "rev-parse --verify refs/tags/" + TagUndeterminable, BareRepo),
                "rev-parse --verify " + TagUndeterminable);
            AssertGit(Git(
                "rev-parse --verify refs/tags/" + TagMoved, BareRepo),
                "rev-parse --verify " + TagMoved);
            return (taggedSha, headSha);
        }

        private static string MoveTagToHead()
        {
            var head = Git("rev-parse HEAD", BareRepo);
            AssertGit(head, "rev-parse HEAD");
            var headSha = head.StdOut.Trim();
            AssertGit(Git("tag -f " + TagMoved + " " + headSha, BareRepo),
                "tag -f " + TagMoved + " (move)");
            return headSha;
        }

        // The offline serving half of the admissible-URL contract is
        // armed by the test harness BEFORE the Editor launches: UPM's
        // package server is a separate process spawned with the
        // Editor, so a GIT_CONFIG_* pair written inside the running
        // domain would never reach it. The harness exports
        // GIT_CONFIG_COUNT plus, per entry, a
        // GIT_CONFIG_KEY_n=url.<local>.insteadOf /
        // GIT_CONFIG_VALUE_n=<https origin> pair — the process-wide
        // environment config git itself reads, rewriting any URL the
        // UPM git subprocess fetches before the transport is chosen.
        // Every process the Editor spawns inherits it, so Client.Add's
        // git resolution stays offline and every persisted channel
        // carries the admissible URL verbatim. The tests never set or
        // clear GIT_CONFIG_* themselves — they verify the armed map on
        // each domain entry and fail closed, since a skip is not a
        // pass.
        private static void RequireHarnessEnvironment()
        {
            var required = "the Unity test harness must launch the " +
                "Editor with OPWEAVE_GIT_FIXTURE_ROOT=<root>, " +
                "GIT_CONFIG_COUNT=2 and the GIT_CONFIG_KEY_n / " +
                "GIT_CONFIG_VALUE_n pairs \"url.file:///<root>/" +
                BareDirName + ".insteadOf\"=" + AdmissibleOrigin +
                " and \"url.file:///<root>/" + MissingDirName +
                ".insteadOf\"=" + MissingOrigin;
            Assert.That(FixtureRootEnv, Is.Not.Null.And.Not.Empty,
                "OPWEAVE_GIT_FIXTURE_ROOT is unset — " + required);
            var count = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
            Assert.That(count, Is.EqualTo("2"),
                "GIT_CONFIG_COUNT is \"" + (count ?? "<unset>") +
                "\" — " + required);
            var armed = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < 2; i++)
            {
                var key = Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_" + i);
                var value = Environment.GetEnvironmentVariable("GIT_CONFIG_VALUE_" + i);
                Assert.That(key, Is.Not.Null.And.Not.Empty,
                    "GIT_CONFIG_KEY_" + i + " is unset — " + required);
                Assert.That(value, Is.Not.Null.And.Not.Empty,
                    "GIT_CONFIG_VALUE_" + i + " is unset — " + required);
                armed[key!] = value!;
            }
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["url." + BareRepoUrl + ".insteadOf"] = AdmissibleOrigin,
                ["url." + MissingRepoUrl + ".insteadOf"] = MissingOrigin,
            };
            foreach (var pair in expected)
            {
                Assert.That(
                    armed.TryGetValue(pair.Key, out var actual) &&
                    actual == pair.Value,
                    Is.True,
                    "the armed GIT_CONFIG_* map has no " + pair.Key +
                    "=" + pair.Value + " (armed: " +
                    string.Join(
                        "; ",
                        armed.Select(kv => kv.Key + "=" + kv.Value)) +
                    ") — " + required);
            }
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

        private static ISet<string> InstalledPackages()
        {
            var installed = new HashSet<string>(StringComparer.Ordinal);
            if (PackageInstalled(FixturePackage))
            {
                installed.Add(FixturePackage);
            }
            return installed;
        }

        private static byte[]? ReadBytesOrNull(string path) =>
            File.Exists(path) ? File.ReadAllBytes(path) : null;

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
                    FixturePackage);
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

        // ---------- manifest dependency editing ----------

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

        // On a resumed domain UPM can still hold manifest.json while
        // its post-reload resolution settles: with an installed+locked
        // git package the tamper write then surfaces as a bare
        // ArgumentException (E_INVALIDARG), not an IOException. Retry
        // the bounded write past the hold — a deterministic failure
        // (the ParseDeps assert, a malformed manifest) is never one of
        // the retried types and still propagates on the first attempt.
        private static void WriteManifestDependency(string name, string dep)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var text = File.ReadAllText(ManifestPath);
                    var pairs =
                        ParseDeps(text).Where(p => p.Key != name).ToList();
                    pairs.Add(new KeyValuePair<string, string>(name, dep));
                    File.WriteAllText(
                        ManifestPath, RebuildManifest(text, pairs));
                    return;
                }
                catch (Exception e) when (
                    attempt < 20 &&
                    (e is ArgumentException || e is IOException ||
                     e is UnauthorizedAccessException))
                {
                    Crumb("manifest tamper write attempt " + attempt +
                        " failed with " + e.GetType().Name + ": " +
                        e.Message + " — retrying");
                    System.Threading.Thread.Sleep(250);
                }
            }
        }

        private static int ManifestDepCount(string name) =>
            ParseDeps(File.ReadAllText(ManifestPath)).Count(p => p.Key == name);

        // The dependency value the manifest currently holds for a
        // name, read back verbatim — a recovery pass that touched the
        // manifest would have rewritten or dropped the entry.
        private static string? ManifestDep(string name)
        {
            foreach (var pair in ParseDeps(File.ReadAllText(ManifestPath)))
            {
                if (pair.Key == name)
                {
                    return pair.Value;
                }
            }
            return null;
        }

        // ---------- setUp / tearDown ----------

        // The T-W4.3-01 cleanup contract: the exclusion this process
        // still holds on the probe root is taken through the hand-off
        // seam and ended the orderly way; both hand-off records drop.
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

        // The no-compile cleanup — Library/ dirs and the in-memory
        // fixture only — safe inside UnitySetUp and UnityTearDown,
        // which must never schedule a reload. A resumed domain can
        // replay this around the live test body — leave the in-flight
        // probe root (state.txt, the run-root journal, the coordinator
        // lock) untouched; only a dir whose nonce is not the armed one
        // is stale residue.
        private static void CleanupProbeArtifacts()
        {
            if (!ProbeStateIsLive())
            {
                ReleaseLeakedState();
                DeleteProbeDir();
            }
            // A fixture parked by a phase-1 that never reached its
            // mid-phase cut is a live installed host — stop its
            // ticking and release it rather than handing it to the
            // next fixture. The reference can hold an already-disposed
            // fixture (the phase's own fail paths dispose it), so the
            // reclaim is best-effort.
            if (s_phase1Fixture is { } parked)
            {
                try
                {
                    parked.Host.Uninstall();
                    parked.Dispose();
                }
                catch (Exception e)
                {
                    Crumb("parked phase-1 fixture cleanup threw: " +
                        e.Message);
                }
                s_phase1Fixture = null;
            }
        }

        // The compile-scheduling cleanup: the marker script folder
        // under Assets/. Deleting it queues an import whose compile
        // ends in a deferred domain reload — never call this from
        // UnitySetUp; the head of the test body owns it there, where
        // the deferred reload rides the body's own first recompile
        // boundary, and the teardown calls it inside its drained
        // boundary. Returns whether an imported script folder was
        // removed — the deterministic "a deferred reload is now
        // queued" signal — and arms the persisted compile-pending
        // mark with it for the test-dies-before-a-boundary path.
        private static bool CleanupCompileArtifacts()
        {
            var imported = AssetDatabase.IsValidFolder(MarkerFolder);
            if (imported)
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
                NoteCompilePending();
            }
            if (Directory.Exists(MarkerDisk))
            {
                Directory.Delete(MarkerDisk, recursive: true);
            }
            return imported;
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup entry: \"" + CurrentTestFullName +
                "\" in-progress=" + TestInProgressActive() +
                " reloads=" + GuardCount(CurrentTestFullName));
            // The loop guard's word is final: once it trips, every
            // remaining test fails here — before any fixture work —
            // so the run ends with a result XML instead of more
            // reloads.
            if (AbortRequested())
            {
                Assert.Fail(
                    "domain-reload loop guard tripped during \"" +
                    CurrentTestFullName + "\": " +
                    SessionState.GetString(ReloadAbortSessionKey, ""));
            }
            // Fail closed before anything else: the GIT_CONFIG_*
            // rewrite is armed by the harness before the Editor
            // launches and cannot be fixed from inside a running
            // domain.
            RequireHarnessEnvironment();
            // Installing/removing a package makes the Editor emit a
            // benign [Error] log about the immutable-folder meta file —
            // the expected byproduct of a real UPM install.
            LogAssert.ignoreFailingMessages = true;
            // UTF replays the before/after commands around a test the
            // domain reload interrupted — their command state does not
            // survive the reload, so this method restarts from its
            // first statement every domain. The "test in progress"
            // mark armed at the head of the test body — before its
            // first reload-causing action — is the replay guard: a
            // replay around the in-flight test must not restore the
            // manifest or delete the live probe state. The later probe
            // nonce covers the same replay as a second layer.
            if (TestInProgressActive() || ProbeStateIsLive())
            {
                yield break;
            }
            // No domain reload may be crossed from setup — a reload
            // inside a before/after command makes UTF re-run the whole
            // test. Every cleanup below is synchronous and needs no
            // compile; a manifest/lock restore whose removal still
            // needs a recompile leaves its deferred reload pending —
            // never awaited here — for the test body's first recompile
            // boundary to release, and the marker-folder delete lives
            // at the head of the test body itself. The compile-pending
            // mark covers the window where the body dies before that
            // boundary: the teardown's drain releases it.
            if (PackageInstalled(FixturePackage))
            {
                NoteCompilePending();
            }
            RestoreOriginalSync();
            CleanupProbeArtifacts();
            CaptureOriginal();

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!ProjectMatchesOriginal() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.That(ProjectMatchesOriginal(), Is.True,
                "the project did not settle on its captured manifest/lock state");

            // The fixture borrows the project's manifest: it is
            // meaningful — and non-destructive — only while the project
            // does not already depend on the fixture package. An
            // already-present dependency is an unmet environment
            // precondition, never a silent pass.
            var baseline = LoadCapture();
            if (baseline is null)
            {
                Assert.Inconclusive(
                    "precondition unmet: no captured project baseline");
            }
            if (baseline!.Mentions(FixturePackage) ||
                baseline.Mentions(FixtureAlias) ||
                PackageInstalled(FixturePackage))
            {
                Assert.Inconclusive(
                    "precondition unmet: the project already depends on " +
                    "the package the fixture would add and remove");
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var name = CurrentTestFullName;
            Crumb("teardown entry: \"" + name +
                "\" in-progress=" + TestInProgressActive() +
                " reloads=" + GuardCount(name));
            try
            {
                var outcome = TestContext.CurrentContext.Result;
                Crumb("teardown outcome: \"" + name + "\" " +
                    outcome.Outcome +
                    (string.IsNullOrEmpty(outcome.Message)
                        ? ""
                        : " — " + outcome.Message));
            }
            catch (Exception e)
            {
                Crumb("teardown outcome unavailable: " + e.Message);
            }
            LogAssert.ignoreFailingMessages = true;
            // The test is over — drop the live-state nonce so the
            // cleanup below really does reclaim the probe root.
            DisarmProbeNonce();
            try
            {
                var aborting = AbortRequested();
                // The teardown owns the deferred reload its cleanup
                // queues. A restore that removes the installed fixture
                // and the marker folder's delete each schedule a
                // compile whose reload the runner's assembly lock
                // defers; left pending, it rides into the NEXT fixture
                // and an unbounded isCompiling/isUpdating wait there
                // spins forever — the suite hang the T-W4.3-05
                // correction exists to remove. Releasing it at the
                // after-command's own boundary — the same sanctioned
                // drain ScriptsCompiledReloadTests and
                // PackageRemoveUpmTests make — replays only this
                // command: the test itself is already recorded, and
                // UTF never re-runs it for a teardown reload.
                var scriptPending = PackageInstalled(FixturePackage);
                RestoreOriginalSync();
                scriptPending |= CleanupCompileArtifacts();
                if (!aborting)
                {
                    var deadline =
                        EditorApplication.timeSinceStartup + WaitTimeoutSec;
                    while (!ProjectMatchesOriginal() &&
                           EditorApplication.timeSinceStartup < deadline)
                    {
                        yield return null;
                    }
                }
                if (scriptPending || CompilePending ||
                    EditorApplication.isCompiling)
                {
                    // Clear the mark BEFORE the yield: a domain that
                    // dies inside the release replays this command
                    // and must not release a second reload.
                    ClearCompilePending();
                    AssetDatabase.Refresh();
                    yield return new WaitForDomainReload();
                }
                CleanupProbeArtifacts();
                if (aborting)
                {
                    // Fail closed AFTER the drain: releasing a
                    // deferred reload is not issuing a new recompile,
                    // so it cannot feed the runaway the guard is
                    // tripping on — it only hands the next fixture a
                    // quiet project.
                    Assert.Fail(
                        "domain-reload loop guard tripped during \"" +
                        name + "\": " +
                        SessionState.GetString(ReloadAbortSessionKey, ""));
                }
                Assert.That(ProjectMatchesOriginal(), Is.True,
                    "the project was not restored to its captured " +
                    "manifest/lock state after the git-resume fixture");
                DeleteBackupDir();
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                EndTest();
            }
        }

        // The class-level reset of every replay record: the only place
        // the in-progress mark, the request marks, the per-test reload
        // counts, the class-wide reload total, the started marks and
        // the abort flag are cleared — ResetTest and BeginTest must
        // not, or a replayed body could disarm its own guard. The
        // class-active mark arms LAST: the [InitializeOnLoad] hook
        // counts only while it lives.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // UTF 1.7.0 rebuilds the fixture work item on every domain
            // a mid-suite reload resumes into and — the unfinished
            // suite never being in alreadyExecutedTests — re-executes
            // this method before the resumed test. Unconditional, the
            // reset below would erase the in-flight test's replay
            // records on every domain it legitimately crosses: the
            // reload counters and class total (which is why every
            // count read 0 across the T-W4.6-03 reloads), the abort
            // flag, the started and request marks and the phase and
            // sentinel books — the whole loop guard. The class-active
            // mark is armed only between a genuine OneTimeSetUp and
            // OneTimeTearDown, so an armed mark means this entry is a
            // replay on a resumed domain: keep every record and only
            // log. A stale mark left by a killed run takes the same
            // branch — its residue then fails the suite closed instead
            // of being silently trusted.
            if (SessionState.GetString(ClassActiveSessionKey, "")
                    .Length > 0)
            {
                Crumb("OneTimeSetUp re-entered on a resumed domain " +
                    "— the in-flight suite's replay records are kept");
                return;
            }
            EndTest();
            DisarmProbeNonce();
            SessionState.SetString(ReloadCountSessionKey, "");
            SessionState.SetInt(ReloadTotalSessionKey, 0);
            SessionState.SetString(StartedSessionKey, "");
            SessionState.SetString(ReloadRequestsSessionKey, "");
            SessionState.SetString(ReloadAbortSessionKey, "");
            SessionState.EraseString(CompilePendingSessionKey);
            SessionState.EraseString(ResumeOwnerSessionKey);
            ResetTest();
            SessionState.SetString(ClassActiveSessionKey, "1");
        }

        // The suite's trusted fixture lives under the harness root —
        // remove the generated repositories so nothing the run created
        // outlives it (each UnityTearDown already restored the
        // project's manifest and lock and drained its deferred
        // reload). The Assets/ marker folder goes too — residue only:
        // every UnityTearDown already deleted it inside its own
        // drained boundary, so this delete is a no-op unless a
        // teardown never ran. Every SessionState record the class
        // armed is cleared with it — SessionState is process-wide, so
        // the keys and the load-hook's gate must not outlive the
        // fixture.
        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            CleanupCompileArtifacts();
            if (FixtureRootEnv is not null)
            {
                foreach (var dir in new[] { SrcRepo, BareRepo, MissingRepoDir })
                {
                    DeleteFixtureTree(dir);
                }
                try { Directory.Delete(FixtureRoot, recursive: false); }
                catch (Exception) { /* the root stays when it is not empty */ }
            }
            EndTest();
            DisarmProbeNonce();
            SessionState.SetString(ReloadCountSessionKey, "");
            SessionState.SetInt(ReloadTotalSessionKey, 0);
            SessionState.SetString(StartedSessionKey, "");
            SessionState.SetString(ReloadRequestsSessionKey, "");
            SessionState.SetString(ReloadAbortSessionKey, "");
            SessionState.EraseString(CompilePendingSessionKey);
            SessionState.EraseString(ResumeOwnerSessionKey);
            SessionState.EraseString(ClassActiveSessionKey);
            ResetTest();
            LogAssert.ignoreFailingMessages = false;
        }

        // ---------- runner plumbing (the T-W4.3-03 shape) ----------

        private enum Cut
        {
            StartedOnly,
            WaitingTail,
            BrokenTail,
        }

        private static void RegisterAs(
            RunnerHostFixture fixture, IOperation op, char hashSeed, OperationOrigin origin) =>
            fixture.Operations.Add(new RegisteredOperation(
                op.Descriptor,
                origin,
                "Example.Operations",
                null,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64))
            {
                Implementation = op,
            });

        private static PlanStep Step(
            int index,
            string nodeId,
            string operationId,
            char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            int next = -1,
            Idempotency idempotency = Idempotency.Unsafe) =>
            RunnerHostFixture.Step(
                index, nodeId, operationId, hashSeed,
                inputs: inputs, outputTypes: outputTypes,
                next: next, idempotency: idempotency);

        private static WorkflowDocument Doc(
            IReadOnlyList<(string id, string operation)> nodes,
            params (string from, string to)[] edges) =>
            RunnerHostFixture.Doc(
                WorkflowId,
                nodes
                    .Select(n => new NodeData { Id = n.id, Operation = n.operation, Version = 1 })
                    .ToList(),
                edges
                    .Select(e => new ControlEdge(e.from, e.to))
                    .ToList());

        private static string Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, new AcceptOptions(null, false));
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        private static void Tick(RunnerHostFixture fixture)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "the tick failed: " + Describe(tick.Diagnostics));
        }

        private static void PumpUntil(
            RunnerHostFixture fixture, string runId, Func<bool> done, string what)
        {
            for (var i = 0; i < PumpTicks && !done(); i++)
            {
                Tick(fixture);
            }
            Assert.IsTrue(done(), what + "; journal: " +
                string.Join(" | ", Lines(runId)));
        }

        private static void RunUntilTerminal(RunnerHostFixture fixture, string runId)
        {
            for (var i = 0; i < PumpTicks; i++)
            {
                Tick(fixture);
                if (fixture.Host.Coordinator.StateOf(runId) is { } state &&
                    (state == RunState.Succeeded || state == RunState.Failed ||
                     state == RunState.Cancelled))
                {
                    Assert.AreEqual(
                        RunState.Succeeded, state,
                        "the Run " + runId + " ended " + state +
                        "; journal: " + string.Join(" | ", Lines(runId)));
                    return;
                }
            }
            Assert.Fail(
                "the Run " + runId + " did not end within " + PumpTicks +
                " ticks; state=" + fixture.Host.Coordinator.StateOf(runId));
        }

        private static RunRecoveryReport Recover(RunnerHostFixture fixture)
        {
            var recovered = fixture.Host.RecoverRuns();
            Assert.IsTrue(recovered.Ok, "the recovery pass failed: " +
                Describe(recovered.Diagnostics));
            Assert.IsNotNull(recovered.Value);
            return recovered.Value!;
        }

        // Arms the marker-script reload the journal cut crosses: the
        // sentinel for the boundary proof, then the marker write whose
        // compile ends this domain — possibly inside the Refresh call
        // itself, so code after it may never run. The write is
        // idempotent: deterministic content, applied only when the
        // bytes on disk differ, so a resumed segment that reaches this
        // again schedules no second compile and the
        // write/refresh/reload cycle cannot restart.
        private static void ArmReload(string markerId)
        {
            NoteDomainReload("mid");
            // The marker write plus its Refresh is the reload-causing
            // request: mark it BEFORE issuing so a resumed segment
            // only confirms the reload the first pass caused and can
            // never restart the write/refresh/reload cycle. A
            // suppressed re-request leaves the sentinel alone — on the
            // domain the first request reloaded it is already dead,
            // which is the proof the boundary check wants.
            if (!MayRequest("arm"))
            {
                return;
            }
            ArmDomainSentinel();
            Directory.CreateDirectory(MarkerDisk);
            var markerPath = Path.Combine(MarkerDisk, "ReloadMarker.cs");
            var source = MarkerSourceFor(markerId);
            if (!File.Exists(markerPath) ||
                File.ReadAllText(markerPath) != source)
            {
                File.WriteAllText(markerPath, source, s_utf8);
            }
            AssetDatabase.Refresh();
        }

        private static string[] Lines(string runId) =>
            RunnerHostFixture.JournalLines(StoreRoot, runId);

        private static void WriteLines(string runId, IEnumerable<string> lines)
        {
            File.WriteAllText(
                RunnerHostFixture.JournalPath(StoreRoot, runId),
                string.Join("\n", lines) + "\n", s_utf8);
        }

        private static void TruncateThrough(string runId, string typeFragment, string stepFragment)
        {
            var lines = Lines(runId);
            var cut = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(typeFragment) && lines[i].Contains(stepFragment))
                {
                    cut = i;
                    break;
                }
            }
            Assert.GreaterOrEqual(cut, 0,
                "the journal never recorded " + typeFragment + " " + stepFragment +
                ": " + string.Join(" | ", lines));
            WriteLines(runId, lines.Take(cut + 1));
        }

        // The mid-Waiting cut: keep the journal through the step's LAST
        // step_waiting record — the tail a mid-wait interruption leaves.
        private static void TruncateThroughLast(
            string runId, string typeFragment, string stepFragment)
        {
            var lines = Lines(runId);
            var cut = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(typeFragment) && lines[i].Contains(stepFragment))
                {
                    cut = i;
                }
            }
            Assert.GreaterOrEqual(cut, 0,
                "the journal never recorded " + typeFragment + " " + stepFragment +
                ": " + string.Join(" | ", lines));
            WriteLines(runId, lines.Take(cut + 1));
        }

        // A torn final line the §F3 reader must drop (OW-STORE-002),
        // resumed from the last complete record.
        private static void AppendBrokenTail(string runId)
        {
            File.AppendAllText(
                RunnerHostFixture.JournalPath(StoreRoot, runId),
                "{\"type\":\"step_progress\",\"step\":0,\"incomple",
                s_utf8);
        }

        private static void AssertStartedOnlyTail(string runId, int step)
        {
            var tail = Lines(runId);
            Assert.IsTrue(
                tail.Length > 0 &&
                tail[tail.Length - 1].Contains("\"type\":\"step_started\"") &&
                tail[tail.Length - 1].Contains("\"step\":" + step),
                "the pre-reload journal must end at step " + step +
                "'s step_started with no later records; journal: " +
                string.Join(" | ", tail));
        }

        private static int JournalCountAll(string runId, params string[] fragments)
        {
            var count = 0;
            foreach (var line in Lines(runId))
            {
                var all = true;
                foreach (var fragment in fragments)
                {
                    all &= line.Contains(fragment);
                }
                if (all)
                {
                    count++;
                }
            }
            return count;
        }

        private static void WriteState(params string[] lines)
        {
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(StateFile, string.Join("\n", lines) + "\n", s_utf8);
        }

        private static string[] ReadState()
        {
            Assert.IsTrue(File.Exists(StateFile), "the phase-1 state file is missing");
            return File.ReadAllLines(StateFile);
        }

        private static JObject ParseObject(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, "fixture JSON did not parse");
            return (JObject)parsed.Value!.ToToken();
        }

        // The canonical text of the resumeToken the journal's LAST
        // step_waiting record for a step persisted — what the restored
        // Poll must be handed back verbatim (SPEC §F4 item 5).
        private static string WaitingTokenText(string runId, int step)
        {
            string? token = null;
            foreach (var line in Lines(runId))
            {
                if (!line.Contains("\"type\":\"step_waiting\"") ||
                    !line.Contains("\"step\":" + step))
                {
                    continue;
                }
                var env = ParseObject(line);
                var persisted = (env["data"] as JObject)?["resumeToken"];
                Assert.IsNotNull(
                    persisted,
                    "the step_waiting record carries no resumeToken: " + line);
                token = JsonValue.From(persisted!).CanonicalText;
            }
            Assert.IsNotNull(token,
                "the journal has no step_waiting record for step " + step);
            return token!;
        }

        // The quiet-settle window: the install's import and compile may
        // finish while the editor is still resolving, so wait until the
        // flags go quiet for a settle window before judging state.
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

        // Crosses the boundary's domain reload — the install's own
        // reload for the natural-interruption boundary, or the armed
        // marker compile's for the explicit one. The lock on this
        // domain is held by Unity Test Framework's EditModeRunner, not
        // by the test and not by the Run — Run calls
        // EditorApplication.LockReloadAssemblies
        // (com.unity.test-framework/UnityEditor.TestRunner/TestRunner/
        // EditModeRunner.cs:129, re-armed after every reload through
        // Resume → Run at line 95) and no OpWeave code ever locks or
        // unlocks assembly reloads. The only sanctioned release is
        // WaitForDomainReload.Perform — it calls
        // EditorApplication.UnlockReloadAssemblies and waits while a
        // script reload stays requested, throwing "Expected domain
        // reload, but it did not occur" when none comes
        // (com.unity.test-framework/UnityEditor.TestRunner/TestRunner/
        // Messages/WaitForDomainReload.cs:46-60). A bare
        // UnlockReloadAssemblies from the test would bypass the
        // runner's context save and PC restore, and is never used.
        //
        // Perform is driven only for an instruction the [UnityTest]
        // method's OWN enumerator yields: the work item's
        // IEditModeTestYieldInstruction branch performs a top-level
        // workItemStep only (UnityEditor.TestRunner/TestRunner/
        // EditorEnumeratorTestWorkItem.cs:130-154). A nested
        // IEnumerator is never unwrapped for that check —
        // TestEnumerator.Execute and ExecuteEnumerableAndRecordExceptions
        // each yield the nested enumerator itself AS an IEnumerator
        // (UnityEngine.TestRunner/NUnitExtensions/Attributes/
        // TestEnumerator.cs:55-58, UnityEngine.TestRunner/
        // NUnitExtensions/Commands/EnumerableTestMethodCommand.cs:
        // 102-105), so an instruction inside it surfaces only through
        // EditModeRunner's StepStack descent
        // (UnityEditor.TestRunner/TestRunner/EditModeRunner.cs:172-186),
        // where InvokeDelegator calls PrepareForDomainReload — context
        // and PC saved (EditModeRunner.cs:263-270, 295-302) — but
        // Perform is never called. The lock then holds the requested
        // reload pending forever: no reload, no exception, the armed
        // sentinel outlives every frame — exactly the earlier
        // corrections' "did not produce a domain reload" failures.
        // Every IEditModeTestYieldInstruction is therefore yielded
        // DIRECTLY from the [UnityTest] method body; this helper keeps
        // only the bookkeeping that must not be duplicated there.
        //
        // Returns true exactly once per boundary while the armed
        // sentinel still lives: the request mark is persisted BEFORE
        // the caller's RequestScriptReload + WaitForDomainReload
        // yield, so a domain dying inside the yield resumes past it
        // with the mark already down — a replayed segment that still
        // finds a live sentinel confirms only (RequestOnce's crumb)
        // and never issues the request a second time, and a dead
        // sentinel needs no release at all: the crossing already
        // proved itself, so false falls through to the caller's
        // sentinel assert — alive means the boundary's reload never
        // came, dead is the proof.
        private static bool ReloadRequestNeeded(string boundaryId)
        {
            if (!DomainSentinelAlive)
            {
                return false;
            }
            return MayRequest(boundaryId);
        }

        // The expected-resolve-error window of the manifest-tamper
        // cases. UTF 1.7.0 saves the console error count when the
        // test's execution context is serialized for a domain reload
        // and ends the test with "Errors occurred during domain
        // reload." when the count grows across the crossing — unless
        // the body's own LogScope had ignoreFailingMessages armed when
        // the context was saved. The tamper tests make UPM's
        // post-reload resolve log exactly that expected error (the
        // missing source never resolves; the alias entry mismatches
        // the recorded package name), so the test body arms the flag
        // as a statement of its own — never from setup or teardown and
        // never fixture-wide, where the saved context would not carry
        // it — immediately before the "mid" boundary's
        // refresh/request/yield, and drops it as soon as the reload is
        // noted so a later product error still fails the test.
        private static void ArmExpectedResolveErrors() =>
            LogAssert.ignoreFailingMessages = true;

        private static void DisarmExpectedResolveErrors() =>
            LogAssert.ignoreFailingMessages = false;

        private static IEnumerator WaitForInstallRegistered()
        {
            var guard = 0;
            while (!PackageInstalled(FixturePackage) && guard++ < 12000)
            {
                yield return null;
            }
            Assert.IsTrue(PackageInstalled(FixturePackage),
                "the fixture install never registered the package");
        }

        // ---------- the production Operation, wired for the fixture ----------

        // The settings feed of the Operation's loadSettings seam: the
        // SG-19 allowlist admits both fixture origins — the below-the-
        // Operation substitute for a project settings file.
        private static OpWeaveSettings FixtureSettings()
        {
            var parsed = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"allowedPackageSources\":[" +
                "\"registry:com.unity.*\"," +
                "\"git:" + AdmissibleOrigin + "\"," +
                "\"git:" + MissingOrigin + "\"]}");
            Assert.That(parsed.Ok, Is.True, () => string.Join(
                "; ", parsed.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return parsed.Value!;
        }

        // The production Execute side effect, verbatim from
        // UnityPackageAddOperation's own default wiring: the real
        // Client.Add request plus the request-tracking resume token.
        private static JsonValue IssueClientAdd(string id) =>
            PackageResolvedCondition.InitialToken(
                id, expectPresent: true, Client.Add(id));

        // The wait a step interrupted after StepStarted but before the
        // UPM request would have armed — the requestless token the
        // production InitialToken writes for a request-free wait. This
        // is the journal cut point, never a replacement of Execute.
        private static JsonValue IssueNoRequest(string id) =>
            PackageResolvedCondition.InitialToken(id, expectPresent: true);

        // A recovery pass must never drive the Execute seam — the
        // counter proves the recovering Run issued no Client.Add, and
        // the production Operation exposes no remove seam at all, so
        // a zero count plus an untouched manifest is the whole
        // mutation surface §F5/§G8.1 forbid. Statics reset with the
        // domain; the counting seam is armed before the recovering
        // phase registers it.
        private static int s_issueCalls;

        private static JsonValue CountingIssueClientAdd(string id)
        {
            s_issueCalls++;
            return IssueClientAdd(id);
        }

        // The real <see cref="PackageResolvedCondition"/> behind a
        // file-mirroring Check: the token the restored Poll hands the
        // condition is the verbatim resumeToken — written once so the
        // tests compare it to the journaled step_waiting payload
        // (SPEC §F4 item 5).
        private sealed class RecordingResolvedCondition : IWaitCondition
        {
            private readonly PackageResolvedCondition _inner =
                new PackageResolvedCondition();

            public WaitStatus Check(JsonValue token)
            {
                if (!File.Exists(TokenSeenFile))
                {
                    File.WriteAllText(TokenSeenFile, token.CanonicalText, s_utf8);
                }
                return _inner.Check(token);
            }
        }

        // The StaticContext the compiler's static phase would have built
        // for the add node — the real ValidateStatic reads Parameters.
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
                throw new NotSupportedException("ValidateStatic reads Parameters only.");
        }

        // The production static-validation admission on the dep the plan
        // carries — the SG-19 check the Operation itself performs. The
        // same check must reject the file:// form of the same fixture:
        // proof the admission is not vacuous.
        private static void AssertAdmissible(UnityPackageAddOperation op, string dep)
        {
            var rejected = op.ValidateStatic(new StubStaticContext(
                "add",
                new Dictionary<string, InputValue>
                {
                    [UnityPackageAddOperation.IdPort] =
                        InputValue.Present(JsonValue.From(JValue.CreateString(dep))),
                }));
            Assert.That(rejected, Is.Empty,
                () => "the fixture Git URL failed the production source " +
                    "contract: " + Describe(rejected));
            var rejectedLocal = op.ValidateStatic(new StubStaticContext(
                "add",
                new Dictionary<string, InputValue>
                {
                    [UnityPackageAddOperation.IdPort] =
                        InputValue.Present(JsonValue.From(JValue.CreateString(
                            BareRepoUrl + "?path=/pkgs/pkgadd#HEAD"))),
                }));
            Assert.That(
                rejectedLocal.Count == 1 &&
                    rejectedLocal[0].Code == "OW-PERM-001",
                "the SG-19 grammar must reject the file:// fixture form — " +
                    "the admissible https URL is what Client.Add is handed");
        }

        /// <summary>A Waitable gated by a flag file under Library/ — the
        /// Run's parked second step while the git step is judged.</summary>
        private sealed class HoldFlagOperation : IOperation
        {
            internal HoldFlagOperation()
            {
                Descriptor = Op.Define(HoldOp, 1)
                    .Summary("Waits for the release flag file.")
                    .Input("flag", "core.String", required: true)
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(
                    RunnerHostFixture.Json("\"held\""), "held", 1);

            public StepResult Poll(StepContext ctx)
            {
                if (!File.Exists(ctx.Get<string>("flag")))
                {
                    return StepResult.Waiting(
                        RunnerHostFixture.Json("\"held\""), "held", 1);
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // ---------- plan / phases ----------

        // [unity.package.add(id=dep) -> hold]: the step under test plus
        // a parked second node that proves whether the Run continued —
        // the Transitions edge the resumed Run follows.
        private static (CompiledPlan plan, WorkflowDocument doc) GitAddPlan(string dep)
        {
            var steps = new[]
            {
                Step(0, "add", UnityPackageAddOperation.OperationId, GitSeed, next: 1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: new Dictionary<string, InputSource>
                    {
                        [UnityPackageAddOperation.IdPort] =
                            new Constant(JsonValue.From(new JValue(dep))),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        [UnityPackageAddOperation.PackagePort] = "core.Json",
                    }),
                Step(1, "h", HoldOp, HoldSeed, next: -1,
                    idempotency: Idempotency.Idempotent,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["flag"] = new Constant(
                            JsonValue.From(new JValue(ReleaseFlag))),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["done"] = "core.Bool",
                    }),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(
                new[] { ("add", UnityPackageAddOperation.OperationId), ("h", HoldOp) },
                ("add", "h"));
            return (plan, doc);
        }

        // The production unity.package.add Operation over its own
        // delegate seams — the fixture settings feed, the UPM request
        // issue and, when asked, the token-recording real wait
        // condition — plus the parked hold step's probe op.
        private static UnityPackageAddOperation RegisterGitAddOps(
            RunnerHostFixture fixture,
            Func<string, JsonValue> issueAdd,
            bool recordToken = false)
        {
            IWaitCondition condition = recordToken
                ? new RecordingResolvedCondition()
                : new PackageResolvedCondition();
            var operation = new UnityPackageAddOperation(
                static () => Result<OpWeaveSettings>.Success(FixtureSettings()),
                issueAdd,
                condition,
                PackageInstallView.Capture);
            RegisterAs(fixture, operation, GitSeed, OperationOrigin.Standard);
            fixture.Register(new HoldFlagOperation(), HoldSeed);
            return operation;
        }

        // Phase 1: the Run's step 0 Executes — the real UPM request for
        // an installing scenario, the requestless wait for the
        // interrupted-before-request cases — and parks at step_waiting,
        // the durable mid-wait record. State goes to disk before any
        // yield, because the install's own compile may end this domain
        // at any later frame. The gate keeps the whole body — the
        // accept, the Execute and the state write — once-only when a
        // reload resumes the segment.
        private static PhaseResult GitAddPhase(
            string dep, Func<string, JsonValue> issueAdd) =>
            PhaseGate("phase-1", () => GitAddPhaseBody(dep, issueAdd));

        private static PhaseResult GitAddPhaseBody(
            string dep, Func<string, JsonValue> issueAdd)
        {
            ArmBoundary("phase-1");
            RunnerHostFixture? fixture = null;
            try
            {
                fixture = new RunnerHostFixture(StoreRoot);
                s_phase1Fixture = fixture;
                var operation = RegisterGitAddOps(fixture, issueAdd);
                AssertAdmissible(operation, dep);
                fixture.Install();
                var built = GitAddPlan(dep);
                var runId = Accept(fixture, built.plan, built.doc);
                var parked = false;
                for (var i = 0; i < 8 && !parked; i++)
                {
                    Tick(fixture);
                    var state = fixture.Host.Coordinator.StateOf(runId);
                    if (state != RunState.Running)
                    {
                        fixture.Dispose();
                        return PhaseResult.Fail(
                            "the git-add Run did not park mid-flight: " + state +
                            "; journal: " + string.Join(" | ", Lines(runId)));
                    }
                    parked = RunnerHostFixture.JournalHasAll(
                        StoreRoot, runId,
                        "\"type\":\"step_waiting\"", "\"step\":0");
                }
                if (!parked)
                {
                    var journal = string.Join(" | ", Lines(runId));
                    fixture.Dispose();
                    return PhaseResult.Fail(
                        "the git-add step left no step_waiting record: " + journal);
                }
                WriteState(runId, fixture.SessionId, ArmProbeNonce());
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // The mid-flight cut, in whichever domain the yields landed:
        // stop a still-installed host, apply the scenario's state
        // tamper, then cut the journal at the boundary under test and
        // arm the marker-script reload that carries the Run into the
        // recovering domain. The marker's Refresh can end the domain
        // before the body returns, so the phase marks itself complete
        // BEFORE the arm: the resumed segment sees the done mark and
        // never re-runs the tamper, the truncation or a second write.
        private static PhaseResult GitAddMidPhase(
            Cut cut, Action? tamper, bool expectInstalled)
        {
            try
            {
                return PhaseGate(
                    "mid", () => GitAddMidPhaseBody(cut, tamper, expectInstalled));
            }
            catch (Exception e)
            {
                // An escape past the body catch is otherwise a bare
                // exception name in the result XML — the full ToString()
                // here preserves its real origin in the log.
                Crumb("mid phase escaped PhaseGate: " + e);
                throw;
            }
        }

        private static PhaseResult GitAddMidPhaseBody(
            Cut cut, Action? tamper, bool expectInstalled)
        {
            try
            {
                var persisted = ReadState();
                var runId = persisted[0];
                // A no-install phase-1 parked in this same domain — the
                // live host must stop ticking before the cut, or later
                // records land past it (the T-W4.3-03 guard).
                s_phase1Fixture?.Host.Uninstall();
                s_phase1Fixture = null;
                if (expectInstalled)
                {
                    Assert.IsTrue(PackageInstalled(FixturePackage),
                        "the fixture install did not register before the cut");
                }
                tamper?.Invoke();
                switch (cut)
                {
                    case Cut.StartedOnly:
                        TruncateThrough(
                            runId, "\"type\":\"step_started\"", "\"step\":0");
                        AssertStartedOnlyTail(runId, 0);
                        break;
                    case Cut.WaitingTail:
                        TruncateThroughLast(
                            runId, "\"type\":\"step_waiting\"", "\"step\":0");
                        break;
                    default:
                        TruncateThroughLast(
                            runId, "\"type\":\"step_waiting\"", "\"step\":0");
                        AppendBrokenTail(runId);
                        break;
                }
                MarkPhaseDone("mid");
                ArmReload(TestContext.CurrentContext.Test.Name + "/" + cut);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("mid phase threw: " + e);
                return PhaseResult.Fail("mid phase threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // The recovering domain: a fresh fixture with the same
        // registrations recovers, and the scenario verdict is checked.
        // <paramref name="issueAdd"/> re-arms the production Execute
        // for the ResumePolicy-driven re-execution — the real
        // Client.Add for every retry in this suite. The gate keeps the
        // recovery and the verdict asserts once-only; the sentinel arm
        // at entry also proves a reload crossing inside the phase —
        // for the re-execution case the second install's compile may
        // end the domain mid-verify.
        private static PhaseResult GitAddPhaseAfter(
            string phaseId,
            Func<string, JsonValue> issueAdd, bool recordToken,
            Func<RunnerHostFixture, string, RunRecoveryReport, PhaseResult> verify) =>
            PhaseGate(phaseId, () => GitAddPhaseAfterBody(phaseId, issueAdd, recordToken, verify));

        private static PhaseResult GitAddPhaseAfterBody(
            string phaseId,
            Func<string, JsonValue> issueAdd, bool recordToken,
            Func<RunnerHostFixture, string, RunRecoveryReport, PhaseResult> verify)
        {
            ArmBoundary(phaseId);
            RunnerHostFixture? fixture = null;
            try
            {
                var persisted = ReadState();
                var runId = persisted[0];
                fixture = new RunnerHostFixture(StoreRoot);
                RegisterGitAddOps(fixture, issueAdd, recordToken);
                fixture.Editor.IsCompiling = EditorApplication.isCompiling;
                var report = Recover(fixture);
                Assert.IsTrue(report.ExclusionHeld);
                var result = verify(fixture, runId, report);
                fixture.Dispose();
                return result;
            }
            catch (Exception e)
            {
                fixture?.Dispose();
                return PhaseResult.Fail("phase-after threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static PhaseResult ExpectResumedAdopted(
            RunnerHostFixture fixture, string runId)
        {
            Assert.AreEqual(
                RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                "the adopted step must let the Run resume; journal: " +
                string.Join(" | ", Lines(runId)));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"",
                    "\"step\":0", "\"reconciled\":true"),
                "the full §G8.1 evidence row is Completed across the " +
                "reload — journaled reconciled:true (§F5)");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"run_resumed\""),
                "the adoption follows the run_resumed write-ahead");
            return PhaseResult.Pass();
        }

        private static PhaseResult ExpectNeedsReview(
            RunnerHostFixture fixture, string runId)
        {
            Assert.AreEqual(
                RunState.NeedsReview, fixture.Host.Coordinator.StateOf(runId),
                "incomplete §G8.1 evidence must end NeedsReview, never an " +
                "automatic outcome; journal: " + string.Join(" | ", Lines(runId)));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_needs_review\"",
                    "\"step\":0"),
                "the NeedsReview verdict is journaled on the git step");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(
                    StoreRoot, runId, "\"type\":\"run_resumed\""),
                "the NeedsReview verdict precedes run_resumed (§F4 item 6)");
            Assert.AreEqual(
                1, JournalCountAll(
                    runId, "\"type\":\"step_started\"", "\"step\":0"),
                "NeedsReview never re-executes — a second attempt would " +
                "be a duplicate add");
            return PhaseResult.Pass();
        }

        private static void AssertPhase(PhaseResult result, string phase) =>
            Assert.That(result.Ok, Is.True, phase + " failed: " + result.Failure);

        // ---------- SPEC §G8.1 test 9 across real domain reloads ----------

        // Completed: the interrupted step's persisted snapshot plus the
        // real install state satisfy every §G8.1 row — the recovery
        // pass adopts the outputs and the Run continues to Succeeded;
        // the package is never added a second time.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator StartedOnlyFullEvidence_ResumesCompleted_WithoutReAdd()
        {
            BeginTest();
            var (tagged, _) = EnsureFixtures();
            var dep = DepFor(tagged);
            AssertPhase(GitAddPhase(dep, IssueClientAdd), "phase-1");

            // The install's import compiles — the natural reload the
            // interruption crosses. Then the marker reload carries the
            // cut journal into the recovering domain.
            yield return WaitForIdleQuiet();
            yield return WaitForInstallRegistered();
            // The install's compile deferred its reload while the run
            // held the lock — release it only while the armed sentinel
            // lives: on a segment resumed after the crossing there is
            // no reload left to wait for and the release would only
            // force a redundant extra reload. The dead sentinel still
            // proves the boundary.
            if (ReloadRequestNeeded("install"))
            {
                Crumb("boundary \"install\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"install\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the package install did not produce a domain reload");
            NoteDomainReload("install");
            AssertPhase(GitAddMidPhase(Cut.StartedOnly, null, expectInstalled: true), "mid");
            if (ReloadRequestNeeded("mid"))
            {
                Crumb("boundary \"mid\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"mid\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the marker compile did not produce a domain reload");
            NoteDomainReload("mid");

            var after = GitAddPhaseAfter(
                "phase-2", IssueClientAdd, recordToken: false,
                (fixture, runId, _) =>
                {
                    var adopted = ExpectResumedAdopted(fixture, runId);
                    if (!adopted.Ok)
                    {
                        return adopted;
                    }
                    File.WriteAllText(ReleaseFlag, "released", s_utf8);
                    RunUntilTerminal(fixture, runId);
                    Assert.AreEqual(
                        1, JournalCountAll(
                            runId, "\"type\":\"step_started\"", "\"step\":0"),
                        "a second attempt would re-run Execute — a duplicate add");
                    Assert.AreEqual(
                        1, ManifestDepCount(FixturePackage),
                        "the manifest holds exactly the one dependency the " +
                        "first Execute wrote — no duplicate add");
                    Assert.IsTrue(
                        PackageInstalled(FixturePackage),
                        "the adopted package was never removed");
                    return PhaseResult.Pass();
                });
            AssertPhase(after, "phase-2");
        }

        // NotApplied: the interruption lands between StepStarted and
        // the UPM request — the requestless wait stands in for the
        // side effect that never ran — and the snapshot recorded
        // provable absence, so the verdict is NotApplied and
        // ResumePolicy re-executes exactly once; the re-executed
        // production Execute drives the real Client.Add install across
        // its own reload and the Run reaches Succeeded.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator StartedOnlyNotApplied_ReexecutesOnce_ToSucceeded()
        {
            BeginTest();
            var (tagged, _) = EnsureFixtures();
            var dep = DepFor(tagged);
            AssertPhase(GitAddPhase(dep, IssueNoRequest), "phase-1");

            // No install means no natural reload — let a few frames
            // pass, then cut in the same domain.
            for (var i = 0; i < 10; i++)
            {
                yield return null;
            }
            AssertPhase(GitAddMidPhase(Cut.StartedOnly, null, expectInstalled: false), "mid");
            if (ReloadRequestNeeded("mid"))
            {
                Crumb("boundary \"mid\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"mid\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the marker compile did not produce a domain reload");
            NoteDomainReload("mid");

            var after = GitAddPhaseAfter(
                "phase-2", IssueClientAdd, recordToken: false,
                (fixture, runId, _) =>
                {
                    Assert.AreEqual(
                        RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                        "proven non-application re-executes, it does not park; " +
                        "journal: " + string.Join(" | ", Lines(runId)));
                    PumpUntil(
                        fixture, runId,
                        () => JournalCountAll(
                            runId, "\"type\":\"step_started\"", "\"step\":0",
                            "\"attempt\":2") > 0,
                        "the NotApplied verdict never re-executed the step");
                    return PhaseResult.Pass();
                });
            AssertPhase(after, "phase-2");

            // The re-executed Execute installs: its import compiles and
            // reloads the domain mid-wait — the next §F4 pass resumes it.
            yield return WaitForIdleQuiet();
            yield return WaitForInstallRegistered();
            // The install's compile deferred its reload while the run
            // held the lock — release it only while the armed sentinel
            // lives: on a segment resumed after the crossing there is
            // no reload left to wait for and the release would only
            // force a redundant extra reload. The dead sentinel still
            // proves the boundary.
            if (ReloadRequestNeeded("install"))
            {
                Crumb("boundary \"install\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"install\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the package install did not produce a domain reload");
            NoteDomainReload("install");

            var resumed = GitAddPhaseAfter(
                "phase-3", IssueClientAdd, recordToken: false,
                (fixture, runId, _) =>
                {
                    Assert.AreEqual(
                        RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                        "the re-executed wait resumes after its own reload; " +
                        "journal: " + string.Join(" | ", Lines(runId)));
                    PumpUntil(
                        fixture, runId,
                        () => JournalCountAll(
                            runId, "\"type\":\"step_succeeded\"", "\"step\":0") > 0 ||
                            fixture.Host.Coordinator.StateOf(runId) != RunState.Running,
                        "the resumed poll never completed the re-executed add");
                    File.WriteAllText(ReleaseFlag, "released", s_utf8);
                    RunUntilTerminal(fixture, runId);
                    Assert.AreEqual(
                        2, JournalCountAll(
                            runId, "\"type\":\"step_started\"", "\"step\":0"),
                        "exactly one re-execution under ResumePolicy");
                    Assert.AreEqual(
                        1, ManifestDepCount(FixturePackage),
                        "one re-execution wrote exactly one manifest dependency");
                    Assert.IsTrue(PackageInstalled(FixturePackage));
                    return PhaseResult.Pass();
                });
            AssertPhase(resumed, "phase-3");
        }

        // Tag without a determinable commit: the persisted snapshot
        // carries no expected commit for a tag request, so the
        // installed package can never prove the §G8.1 row — Unknown →
        // NeedsReview, nothing re-added and nothing removed.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator StartedOnlyTagUndeterminable_NeedsReview()
        {
            BeginTest();
            EnsureFixtures();
            var dep = DepFor(TagUndeterminable);
            AssertPhase(GitAddPhase(dep, IssueClientAdd), "phase-1");

            yield return WaitForIdleQuiet();
            yield return WaitForInstallRegistered();
            // The install's compile deferred its reload while the run
            // held the lock — release it only while the armed sentinel
            // lives: on a segment resumed after the crossing there is
            // no reload left to wait for and the release would only
            // force a redundant extra reload. The dead sentinel still
            // proves the boundary.
            if (ReloadRequestNeeded("install"))
            {
                Crumb("boundary \"install\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"install\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the package install did not produce a domain reload");
            NoteDomainReload("install");
            AssertPhase(GitAddMidPhase(Cut.StartedOnly, null, expectInstalled: true), "mid");
            if (ReloadRequestNeeded("mid"))
            {
                Crumb("boundary \"mid\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"mid\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the marker compile did not produce a domain reload");
            NoteDomainReload("mid");

            var after = GitAddPhaseAfter(
                "phase-2", IssueClientAdd, recordToken: false,
                (fixture, runId, _) =>
                {
                    var verdict = ExpectNeedsReview(fixture, runId);
                    if (!verdict.Ok)
                    {
                        return verdict;
                    }
                    Assert.IsTrue(
                        PackageInstalled(FixturePackage),
                        "NeedsReview never removes what the first Execute added");
                    Assert.AreEqual(
                        1, ManifestDepCount(FixturePackage));
                    return PhaseResult.Pass();
                });
            AssertPhase(after, "phase-2");
        }

        // Moved tag: the tag moves between the journaled preflight
        // evidence and the resolve the real Client.Add performs — the
        // installation lands on a different commit than the tag named.
        // Production Preflight persists no expectedCommit for a tag
        // (SPEC §G8.1 item 3's change-free determination cannot pin
        // one), so the evidence row is Unknown → NeedsReview — never a
        // Completed the moved resolve would contradict.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator StartedOnlyMovedTag_NeedsReview()
        {
            BeginTest();
            var (tagged, _) = EnsureFixtures();
            var dep = DepFor(TagMoved);
            AssertPhase(
                GitAddPhase(
                    dep,
                    id =>
                    {
                        var moved = MoveTagToHead();
                        Assert.That(moved, Is.Not.EqualTo(tagged),
                            "the tag must move to a different commit");
                        return IssueClientAdd(id);
                    }),
                "phase-1");

            yield return WaitForIdleQuiet();
            yield return WaitForInstallRegistered();
            // The install's compile deferred its reload while the run
            // held the lock — release it only while the armed sentinel
            // lives: on a segment resumed after the crossing there is
            // no reload left to wait for and the release would only
            // force a redundant extra reload. The dead sentinel still
            // proves the boundary.
            if (ReloadRequestNeeded("install"))
            {
                Crumb("boundary \"install\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"install\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the package install did not produce a domain reload");
            NoteDomainReload("install");
            AssertPhase(GitAddMidPhase(Cut.StartedOnly, null, expectInstalled: true), "mid");
            if (ReloadRequestNeeded("mid"))
            {
                Crumb("boundary \"mid\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"mid\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the marker compile did not produce a domain reload");
            NoteDomainReload("mid");

            var after = GitAddPhaseAfter(
                "phase-2", IssueClientAdd, recordToken: false,
                (fixture, runId, _) =>
                {
                    var verdict = ExpectNeedsReview(fixture, runId);
                    if (!verdict.Ok)
                    {
                        return verdict;
                    }
                    Assert.IsTrue(
                        PackageInstalled(FixturePackage),
                        "NeedsReview never removes what the first Execute added");
                    Assert.AreEqual(
                        1, ManifestDepCount(FixturePackage));
                    return PhaseResult.Pass();
                });
            AssertPhase(after, "phase-2");
        }

        // Manifest-only partial: a manifest entry exists for the
        // expected source while the lockfile and the registered list
        // hold nothing — channel disagreement → Unknown → NeedsReview;
        // nothing is added and nothing removed.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator StartedOnlyManifestOnlyPartial_NeedsReview()
        {
            BeginTest();
            var (tagged, _) = EnsureFixtures();
            var dep = MissingDepFor(tagged);
            AssertPhase(GitAddPhase(dep, IssueNoRequest), "phase-1");

            for (var i = 0; i < 10; i++)
            {
                yield return null;
            }
            // The tamper lands inside the same domain — a manifest
            // entry with no lockfile or registered counterpart, the
            // partial state §G8.1 can never call applied.
            AssertPhase(
                GitAddMidPhase(
                    Cut.StartedOnly,
                    () => WriteManifestDependency(FixturePackage, dep),
                    expectInstalled: false),
                "mid");
            if (ReloadRequestNeeded("mid"))
            {
                Crumb("boundary \"mid\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                ArmExpectedResolveErrors();
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"mid\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the marker compile did not produce a domain reload");
            NoteDomainReload("mid");
            DisarmExpectedResolveErrors();

            var after = GitAddPhaseAfter(
                "phase-2", IssueClientAdd, recordToken: false,
                (fixture, runId, _) =>
                {
                    var verdict = ExpectNeedsReview(fixture, runId);
                    if (!verdict.Ok)
                    {
                        return verdict;
                    }
                    Assert.IsFalse(
                        PackageInstalled(FixturePackage),
                        "NeedsReview never installs the missing half of a " +
                        "partial state");
                    Assert.AreEqual(
                        1, ManifestDepCount(FixturePackage),
                        "the manifest still holds the lone partial entry — " +
                        "never a duplicate add");
                    return PhaseResult.Pass();
                });
            AssertPhase(after, "phase-2");
        }

        // Source mismatch: the package is installed and locked from
        // the expected source, but the manifest now names a different
        // origin — the three channels disagree about the source →
        // Unknown → NeedsReview; the installed package is left alone.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator StartedOnlySourceMismatch_NeedsReview()
        {
            BeginTest();
            var (tagged, _) = EnsureFixtures();
            var dep = DepFor(tagged);
            AssertPhase(GitAddPhase(dep, IssueClientAdd), "phase-1");

            yield return WaitForIdleQuiet();
            yield return WaitForInstallRegistered();
            // The install's compile deferred its reload while the run
            // held the lock — release it only while the armed sentinel
            // lives: on a segment resumed after the crossing there is
            // no reload left to wait for and the release would only
            // force a redundant extra reload. The dead sentinel still
            // proves the boundary.
            if (ReloadRequestNeeded("install"))
            {
                Crumb("boundary \"install\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"install\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the package install did not produce a domain reload");
            NoteDomainReload("install");
            Crumb("building \"mid\" tamper from reload-safe state");
            AssertPhase(
                GitAddMidPhase(
                    Cut.StartedOnly,
                    MissingSourceTamper(ReloadTaggedSha()),
                    expectInstalled: true),
                "mid");
            if (ReloadRequestNeeded("mid"))
            {
                Crumb("boundary \"mid\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                ArmExpectedResolveErrors();
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"mid\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the marker compile did not produce a domain reload");
            NoteDomainReload("mid");
            DisarmExpectedResolveErrors();

            // The tamper points the manifest dependency at a source
            // that never resolves, so UPM's own post-reload resolve —
            // not the recovering Run — can drop the fixture package
            // (editmode.log: the missing-repo.git resolve error, then
            // "Registered 54 packages" where 55 held the fixture).
            // §F5/§G8.1 bind OpWeave, not UPM: assert the product
            // guarantees — no re-issued request, the tamper's manifest
            // entry read back verbatim, nothing completed.
            s_issueCalls = 0;
            var after = GitAddPhaseAfter(
                "phase-2", CountingIssueClientAdd, recordToken: false,
                (fixture, runId, _) =>
                {
                    var verdict = ExpectNeedsReview(fixture, runId);
                    if (!verdict.Ok)
                    {
                        return verdict;
                    }
                    Assert.AreEqual(
                        0, s_issueCalls,
                        "NeedsReview never re-issues the Execute seam — " +
                        "no Client.Add (the Operation has no remove seam)");
                    Assert.AreEqual(
                        MissingDepFor(ReloadTaggedSha()),
                        ManifestDep(FixturePackage),
                        "the manifest still holds exactly the tamper's " +
                        "dependency — recovery never rewrote or removed it");
                    Assert.AreEqual(
                        0, JournalCountAll(
                            runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                        "NeedsReview completes nothing");
                    return PhaseResult.Pass();
                });
            AssertPhase(after, "phase-2");
        }

        // Ambiguous match: the manifest carries two entries for the
        // expected source — the identity the resolved package would be
        // attributed to is ambiguous → Unknown → NeedsReview; the
        // install stays untouched.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator StartedOnlyAmbiguousMatch_NeedsReview()
        {
            BeginTest();
            var (tagged, _) = EnsureFixtures();
            var dep = DepFor(tagged);
            AssertPhase(GitAddPhase(dep, IssueClientAdd), "phase-1");

            yield return WaitForIdleQuiet();
            yield return WaitForInstallRegistered();
            // The install's compile deferred its reload while the run
            // held the lock — release it only while the armed sentinel
            // lives: on a segment resumed after the crossing there is
            // no reload left to wait for and the release would only
            // force a redundant extra reload. The dead sentinel still
            // proves the boundary.
            if (ReloadRequestNeeded("install"))
            {
                Crumb("boundary \"install\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"install\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the package install did not produce a domain reload");
            NoteDomainReload("install");
            Crumb("building \"mid\" tamper from reload-safe state");
            AssertPhase(
                GitAddMidPhase(
                    Cut.StartedOnly,
                    AliasManifestTamper(DepFor(ReloadTaggedSha())),
                    expectInstalled: true),
                "mid");
            if (ReloadRequestNeeded("mid"))
            {
                Crumb("boundary \"mid\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                ArmExpectedResolveErrors();
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"mid\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the marker compile did not produce a domain reload");
            NoteDomainReload("mid");
            DisarmExpectedResolveErrors();

            var after = GitAddPhaseAfter(
                "phase-2", IssueClientAdd, recordToken: false,
                (fixture, runId, _) =>
                {
                    var verdict = ExpectNeedsReview(fixture, runId);
                    if (!verdict.Ok)
                    {
                        return verdict;
                    }
                    Assert.IsTrue(
                        PackageInstalled(FixturePackage),
                        "NeedsReview never removes the installed package");
                    return PhaseResult.Pass();
                });
            AssertPhase(after, "phase-2");
        }

        // StepWaiting: the journal ends on the last step_waiting record
        // — the recovery pass restores the wait and hands the persisted
        // resumeToken back to the real Poll verbatim; the resolved
        // package completes the step by polling, never by reconciliation.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator StepWaiting_PersistedToken_ResumesPollAcrossReload()
        {
            BeginTest();
            var (tagged, _) = EnsureFixtures();
            var dep = DepFor(tagged);
            AssertPhase(
                GitAddPhase(dep, IssueClientAdd),
                "phase-1");

            yield return WaitForIdleQuiet();
            yield return WaitForInstallRegistered();
            // The install's compile deferred its reload while the run
            // held the lock — release it only while the armed sentinel
            // lives: on a segment resumed after the crossing there is
            // no reload left to wait for and the release would only
            // force a redundant extra reload. The dead sentinel still
            // proves the boundary.
            if (ReloadRequestNeeded("install"))
            {
                Crumb("boundary \"install\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"install\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the package install did not produce a domain reload");
            NoteDomainReload("install");
            AssertPhase(GitAddMidPhase(Cut.WaitingTail, null, expectInstalled: true), "mid");
            if (ReloadRequestNeeded("mid"))
            {
                Crumb("boundary \"mid\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"mid\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the marker compile did not produce a domain reload");
            NoteDomainReload("mid");
            // The real PackageResolvedCondition reads the Editor's
            // busy flags — settle before the recovered Run polls.
            yield return WaitForIdleQuiet();

            var after = GitAddPhaseAfter(
                "phase-2", IssueClientAdd, recordToken: true,
                (fixture, runId, _) =>
                {
                    Assert.AreEqual(
                        RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                        "the waiting step must come back Running; journal: " +
                        string.Join(" | ", Lines(runId)));
                    var persistedToken = WaitingTokenText(runId, 0);
                    PumpUntil(
                        fixture, runId,
                        () => JournalCountAll(
                            runId, "\"type\":\"step_succeeded\"", "\"step\":0") > 0 ||
                            fixture.Host.Coordinator.StateOf(runId) != RunState.Running,
                        "the restored wait never completed the git step");
                    Assert.IsFalse(
                        RunnerHostFixture.JournalHas(
                            StoreRoot, runId, "\"reconciled\":true"),
                        "fresh polling — nothing was adopted without evidence");
                    Assert.IsTrue(
                        File.Exists(TokenSeenFile) &&
                        File.ReadAllText(TokenSeenFile) == persistedToken,
                        "the persisted resumeToken did not reach the " +
                        "post-reload Poll verbatim (SPEC §F4 item 5)");
                    File.WriteAllText(ReleaseFlag, "released", s_utf8);
                    RunUntilTerminal(fixture, runId);
                    Assert.IsTrue(PackageInstalled(FixturePackage));
                    return PhaseResult.Pass();
                });
            AssertPhase(after, "phase-2");
        }

        // Truncated tail: the last journal line is a torn fragment the
        // §F3 reader drops (OW-STORE-002) — the resume continues from
        // the last complete step_waiting record, exactly like the
        // undamaged case.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MidWaiting_TruncatedTail_ResumesAcrossReload()
        {
            BeginTest();
            var (tagged, _) = EnsureFixtures();
            var dep = DepFor(tagged);
            AssertPhase(
                GitAddPhase(dep, IssueClientAdd),
                "phase-1");

            yield return WaitForIdleQuiet();
            yield return WaitForInstallRegistered();
            // The install's compile deferred its reload while the run
            // held the lock — release it only while the armed sentinel
            // lives: on a segment resumed after the crossing there is
            // no reload left to wait for and the release would only
            // force a redundant extra reload. The dead sentinel still
            // proves the boundary.
            if (ReloadRequestNeeded("install"))
            {
                Crumb("boundary \"install\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"install\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the package install did not produce a domain reload");
            NoteDomainReload("install");
            AssertPhase(GitAddMidPhase(Cut.BrokenTail, null, expectInstalled: true), "mid");
            if (ReloadRequestNeeded("mid"))
            {
                Crumb("boundary \"mid\" issuing RequestScriptReload " +
                    "(sentinelAlive=" + DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                yield return new WaitForDomainReload();
                Crumb("boundary \"mid\" resumed after " +
                    "WaitForDomainReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
            }
            Assert.IsFalse(
                DomainSentinelAlive,
                "the marker compile did not produce a domain reload");
            NoteDomainReload("mid");
            // The real PackageResolvedCondition reads the Editor's
            // busy flags — settle before the recovered Run polls.
            yield return WaitForIdleQuiet();

            var after = GitAddPhaseAfter(
                "phase-2", IssueClientAdd, recordToken: true,
                (fixture, runId, report) =>
                {
                    Assert.IsTrue(
                        report.Diagnostics.Any(
                            d => d.Code == "OW-STORE-002" &&
                                d.Severity == Severity.Warning),
                        "the dropped tail is reported as a warning: " +
                        Describe(report.Diagnostics));
                    Assert.AreEqual(
                        RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                        "the resumed Run must come back Running; journal: " +
                        string.Join(" | ", Lines(runId)));
                    var persistedToken = WaitingTokenText(runId, 0);
                    PumpUntil(
                        fixture, runId,
                        () => JournalCountAll(
                            runId, "\"type\":\"step_succeeded\"", "\"step\":0") > 0 ||
                            fixture.Host.Coordinator.StateOf(runId) != RunState.Running,
                        "the restored wait never completed the git step");
                    Assert.IsTrue(
                        File.Exists(TokenSeenFile) &&
                        File.ReadAllText(TokenSeenFile) == persistedToken,
                        "the persisted resumeToken did not reach the " +
                        "post-reload Poll verbatim (SPEC §F4 item 5)");
                    File.WriteAllText(ReleaseFlag, "released", s_utf8);
                    RunUntilTerminal(fixture, runId);
                    Assert.IsTrue(PackageInstalled(FixturePackage));
                    return PhaseResult.Pass();
                });
            AssertPhase(after, "phase-2");
        }
    }

    // The reload-observer half of the loop guard: [InitializeOnLoad]
    // runs this static constructor on EVERY freshly loaded domain —
    // before the test runner resumes — so a domain reload is counted
    // unconditionally, even when no fixture test is in progress and
    // neither the test body nor its replayed setup/teardown got a
    // statement in. The counts live in SessionState — a class-wide
    // total and a per-test map keyed by full name — so no ResetTest
    // or replayed body can clear them: a request/reload cycle trips
    // the abort flag even if every other guard was bypassed.
    [InitializeOnLoad]
    internal static class GitPackageResumeReloadGuardHook
    {
        static GitPackageResumeReloadGuardHook() =>
            GitPackageResumeReloadTests.OnDomainLoaded();
    }
}
