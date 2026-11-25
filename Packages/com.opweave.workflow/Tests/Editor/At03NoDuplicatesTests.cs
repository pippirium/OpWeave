#nullable enable

// T-W4.5-05 [Edit] tests: local:T-W4.5-05:at03-no-duplicates — SPEC §T.3
// AT-03, §F5 and §1.1 for the four artifact kinds AT-03 names (assets,
// GameObjects, scripts and packages): a Run whose journal ends at the
// bare step_started{step:0} an abnormal termination leaves is decided by
// the §F4 recovery pass, and the recovered Run then finishes with
// exactly one instance of its artifact — never a duplicate creation,
// never a wrongful deletion, and NeedsReview whenever the recorded
// evidence cannot prove the outcome. The same-domain tests drive the
// real OpWeaveRunnerHost + FileRunStore path the T-W4.3-03 fixtures
// established through the park/truncate/recover mechanics of the AT-02
// suites; the [UnityTest] companions cross a real domain reload
// (SPEC §T.3), including two real-UPM package.add cases where the
// registry install's own compile produces the reload. SPEC §E2 admits
// at most one mutating Run per Run Store, so the reload companions park
// each Run in a store of its own and never dispose a phase-1 host: the
// OS exclusion each parked Run holds is a process-held handle that
// survives the domain reload, the hand-off seam of SPEC §E2 item 3 the
// post-reload pass re-adopts.
//
// UTF resumes a domain-reload-interrupted [UnityTest] by bumping the
// enumerator PC, so the segment between the last completed yield and
// the reload re-runs — and its before/after command state is a plain
// object that does NOT survive the reload, so UnitySetUp and
// UnityTearDown replay from their first statement every domain (the
// GitPackageResumeReloadTests finding this file adopts). Every phase
// therefore runs once behind a SessionState progress record, a "test
// in progress" mark armed at the head of the test body plus a probe
// nonce keeps a replayed setup from restoring the borrowed manifest
// or deleting the live probe state, every recompile boundary is
// marked in SessionState BEFORE it yields and never issued twice, the
// marker write is idempotent, and a boundary yield fires only while
// the armed sentinel lives. UnitySetUp may never cross a domain
// reload — a reload inside a before-command re-runs the test it
// guards — and UnityTearDown ends at the sanctioned bounded
// DrainedScriptReload drain whenever its cleanup scheduled a
// compile, so a deferred reload is never handed to the next fixture.
// An [InitializeOnLoad] hook counts EVERY domain load unconditionally
// into a per-test and a class-wide SessionState counter — the hook is
// the ONLY counter, so one crossing can never count twice. A count
// past a test's own cap arms an abort keyed to THAT test alone: its
// recompile points then fail closed and its own teardown reports and
// clears the record, so a request/reload cycle fails the looping
// test instead of leaking into the suite; only a class-wide total
// past the suite cap arms the shared abort that fails every remaining
// test at UnitySetUp, so a request/reload cycle can never hang the
// Editor.
//
// Artifact kinds and their rows:
//   - asset:    unity.asset.create_material    (adopt / re-execute / Unknown)
//   - objects:  unity.gameobject.create and unity.gameobject.delete
//   - script:   unity.script.create
//   - package:  unity.package.add — scripted PackageInstallView for the
//               deterministic same-domain verdicts, and the real
//               Client.Add/PackageInstallView.Capture path against the
//               default-allowlisted registry probe
//               (com.unity.editorcoroutines@1.0.0) for the UPM rows —
//               the "or the registry" source the ticket names.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.5-05:at03-no-duplicates</c> acceptance cases: every
    /// artifact kind's interrupted Run recovers without a duplicate
    /// creation or a wrongful deletion, and insufficient evidence ends
    /// NeedsReview — in the same domain and across a real domain reload.
    /// </summary>
    internal sealed class At03NoDuplicatesTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveAt03NoDuplicates");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string StateFile => Path.Combine(ProbeDir, "state.txt");

        // The reload companions' per-Run Store: started-only Runs
        // unfinished at once need a store each — SPEC §E2 admits at most
        // one mutating Run per store.
        private static string StoreRootFor(string key) =>
            Path.Combine(ProbeDir, "run-root-" + key);

        // The group stores in phase order — WriteState persists the run
        // ids in this order and the post-reload phase walks them the
        // same way: one Run per artifact kind.
        private static readonly string[] s_groupKeys =
            { "material", "gameobject", "script", "package" };

        // The real-UPM package cases' own stores.
        private const string AdoptedUpmKey = "pkgadopt";
        private const string ReexecuteUpmKey = "pkgreexec";

        private static IEnumerable<string> AllStoreRoots()
        {
            yield return StoreRoot;
            for (var i = 0; i < s_groupKeys.Length; i++)
            {
                yield return StoreRootFor(s_groupKeys[i]);
            }
            yield return StoreRootFor(AdoptedUpmKey);
            yield return StoreRootFor(ReexecuteUpmKey);
        }

        private const string ProbeFolder = "Assets/OpWeaveAt03";
        // The script probes' own folder below the default codePaths
        // entry — the T-W4.5-04 convention: unity.script.create's
        // configured code-path bound admits it with no settings
        // override, and the bound itself is never relaxed.
        private const string ScriptFolder = "Assets/Scripts/OpWeaveAt03";
        private const string BaseScenePath = ProbeFolder + "/Base.unity";
        private const string MaterialPath = ProbeFolder + "/At03Material.mat";
        private const string ScriptClassName = "At03CreatedScript";
        private const string ScriptResClassName = "At03ResumedScript";
        private const string DamagedClassName = "At03Damaged";

        private const string CreateTargetName = "At03Created";
        private const string CreateTamperedName = "At03Tampered";
        private const string DeleteTargetName = "At03Deleted";
        private const string NeighbourName = "At03Neighbour";

        // The default-allowlisted registry probe the real-UPM package
        // cases drive — its Editor assembly makes the install compile,
        // the compile ends in the domain reload the cases cross.
        private const string PackageName = "com.unity.editorcoroutines";
        private const string PackageVersion = "1.0.0";
        private const string PackageId = PackageName + "@" + PackageVersion;

        private static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");
        private static string LockPath =>
            Path.Combine(ProjectRoot, "Packages", "packages-lock.json");
        private static string ManifestBackupPath =>
            Path.Combine(ProbeDir, "orig-manifest.json");
        private static string LockBackupPath =>
            Path.Combine(ProbeDir, "orig-packages-lock.json");

        private static string MarkerFolder => "Assets/Editor/OpWeaveAt03Reload";
        private static string MarkerDisk =>
            Path.Combine(
                ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveAt03Reload\n" +
            "{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private const string ProbeShaderPath = ProbeFolder + "/Probe.shader";
        private const string ProbeShaderName = "OpWeave/At03Probe";
        private const string ProbeShaderSource =
            "Shader \"OpWeave/At03Probe\"\n" +
            "{\n" +
            "    Properties\n" +
            "    {\n" +
            "        _Color (\"Color\", Color) = (1, 1, 1, 1)\n" +
            "        _MainTex (\"Main Tex\", 2D) = \"white\" {}\n" +
            "    }\n" +
            "    SubShader\n" +
            "    {\n" +
            "        Pass\n" +
            "        {\n" +
            "            CGPROGRAM\n" +
            "            #pragma vertex vert\n" +
            "            #pragma fragment frag\n" +
            "            float4 vert(float4 v : POSITION) : SV_Position { return v; }\n" +
            "            fixed4 frag() : SV_Target { return fixed4(1, 1, 1, 1); }\n" +
            "            ENDCG\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        // The content a disturbed artifact carries — text no honest
        // evidence row can match, kept different from every fixture write
        // so the disturbance is never mistaken for the step's own output.
        private const string DisturbedContent = "disturbed by at-03\n";

        private const string WorkflowId = "example.at03.no.duplicates";
        private const string HoldOp = "test.at03.hold";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // The issue-call counter the real-UPM cases read inside their own
        // domain — a static resets on reload, so each phase asserts the
        // count its own domain produced.
        private static int s_issueCalls;

        private static void Crumb(string message) => Debug.Log("[W4505] " + message);

        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static PhaseResult Pass() => new PhaseResult { Ok = true };

            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        // ── Replay state (the GitPackageResumeReloadTests contract) ─────

        // The probe-state nonce: probe.nonce under ProbeDir carries it and
        // SessionState mirrors it across domain reloads. A resumed domain
        // may re-enter UnitySetUp/UnityTearDown (UTF replays the
        // before/after commands around the restored test), and a naive
        // cleanup would delete the live probe state — the run stores,
        // the journals, the state file and the manifest backups —
        // before the test body resumes. The nonce marks ProbeDir as
        // owned by the in-flight test; only a dir whose nonce is not the
        // armed one is stale residue. It arms inside WriteState, so the
        // earlier "test in progress" mark — set at the head of the body —
        // is the primary replay guard and this the second layer.
        private const string ProbeSessionKey = "OpWeave.At03.probe-state";

        private static string NonceFile => Path.Combine(ProbeDir, "probe.nonce");

        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(NonceFile, nonce + "\n", s_utf8);
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

        // The persisted replay records and the reload-count loop guard —
        // the GitPackageResumeReloadTests contract, keyed per test so a
        // tripped test never takes the suite with it: per-test and
        // class-wide reload caps counted unconditionally by the
        // [InitializeOnLoad] hook (the ONLY writer — a boundary note
        // counts nothing or one crossing would count twice), a per-test
        // abort record that fails the tripped test's recompile requests
        // closed and is reported then cleared by that test's own
        // teardown, plus a class-wide abort only the suite total can
        // arm — and a "never re-run" discipline: a test that starts over
        // from its first statement fails closed, never silently re-runs
        // a phase. Each reload companion crosses at most two reloads
        // (the marker compile and, for the real-UPM cases, the install)
        // plus one teardown drain — the per-test cap leaves headroom
        // above that and the class cap is per-test × tests + slack, so
        // a legitimate test never trips the guard while a
        // request/reload cycle always does.
        private const int MaxDomainReloadsPerTest = 4;
        private const int ReloadTestCount = 4;
        private const int MaxDomainReloadsTotal =
            MaxDomainReloadsPerTest * ReloadTestCount + 4;
        private const string PhasesDoneSessionKey =
            "OpWeave.At03.phases-done";
        private const string SentinelsArmedSessionKey =
            "OpWeave.At03.sentinels-armed";
        private const string ReloadCountSessionKey =
            "OpWeave.At03.reload-count";
        private const string ReloadTotalSessionKey =
            "OpWeave.At03.reload-total";
        private const string StartedSessionKey =
            "OpWeave.At03.started";
        private const string ReloadRequestsSessionKey =
            "OpWeave.At03.reload-requests";
        private const string TestInProgressSessionKey =
            "OpWeave.At03.test-in-progress";
        private const string ResumeOwnerSessionKey =
            "OpWeave.At03.resume-owner";
        private const string ReloadAbortSessionKey =
            "OpWeave.At03.reload-abort";
        // The per-test abort record — a ";name:detail;" map like the
        // reload-count map below: keyed to the test the guard tripped
        // on so its setup/body/teardown fails closed while a stale
        // record can never fail a different test, and clearable by the
        // owning test's teardown or the OneTime reset.
        private const string PerTestAbortSessionKey =
            "OpWeave.At03.reload-abort-test";
        private const string CompilePendingSessionKey =
            "OpWeave.At03.compile-pending";
        private const string ClassActiveSessionKey =
            "OpWeave.At03.class-active";

        private static void ResetTest()
        {
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

        // One record per test name (the full name contains ':'/'-'):
        // whether the body already ran its first segment. A reload-
        // restarted test body must not silently re-run its phases, and
        // a body re-started for any other reason is equally suspect.
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

        // The head-of-body guard a resumed/restarted test calls before
        // anything else: a re-entry of the SAME test — the in-progress
        // mark still armed — is a restart after a reload and fails
        // closed rather than silently re-running the phases that
        // already mutated state. A stale mark left by a DIFFERENT test
        // (that test's teardown did not run; a previous body died) is
        // not this test's replay: it is dropped, logged, and the
        // orphaned probe residue is reclaimed before this test arms its
        // own marks. The reload-request records reset together with the
        // marks so a re-run starts from a clean request slate.
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
            var takeover = inProgress.Length > 0;
            if (takeover)
            {
                Crumb("dropping a stale in-progress mark for \"" +
                    inProgress + "\" left by a previous test");
                // The dead test's guard records die with it — keyed to
                // its own name, they are cleared here rather than left
                // to mislead a later same-named start.
                RecordClear(ReloadCountSessionKey, inProgress);
                RecordClear(PerTestAbortSessionKey, inProgress);
            }
            ResetTest();
            SessionState.SetString(ReloadRequestsSessionKey, "");
            SessionState.SetString(TestInProgressSessionKey, name);
            SessionState.SetString(ResumeOwnerSessionKey, name);
            MarkStarted(name);
            if (AbortRequestedFor(name))
            {
                Assert.Fail(
                    "domain-reload loop guard tripped before \"" + name +
                    "\" could start: " + AbortMessageFor(name));
            }
            if (takeover)
            {
                // The takeover path: a previous test whose teardown
                // never ran left its mark armed, so a replayed
                // UnitySetUp early-outs and this is the only cleanup
                // that sees the residue — the dead test's nonce is
                // disarmed so its probe root is reclaimed and its
                // borrowed manifest restored.
                DisarmProbeNonce();
                NewUntitledScene();
                var backup = File.Exists(ManifestBackupPath);
                RestoreManifestIfCaptured();
                if (backup && PackageInstalled(PackageName))
                {
                    NoteCompilePending();
                }
                CleanupProbeArtifacts();
                CleanupCompileArtifacts();
            }
        }

        // The per-test guard records are single-key ";name:value;" maps:
        // one SessionState entry per record kind, so a suite-level reset
        // (OneTimeSetUp / OneTimeTearDown) or the owning test's teardown
        // really can clear every name's record — the r1 per-name keys
        // could never be enumerated or reset, and the sticky global
        // abort they fed leaked one test's trip into every later test.
        private static string RecordGet(string sessionKey, string name)
        {
            var map = SessionState.GetString(sessionKey, "");
            var marker = ";" + name + ":";
            var at = map.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return "";
            }
            var start = at + marker.Length;
            var end = map.IndexOf(';', start);
            return end < 0
                ? map.Substring(start)
                : map.Substring(start, end - start);
        }

        private static void RecordSet(
            string sessionKey, string name, string value)
        {
            var map = SessionState.GetString(sessionKey, "");
            var marker = ";" + name + ":";
            var at = map.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                SessionState.SetString(
                    sessionKey, map + marker + value + ";");
                return;
            }
            var start = at + marker.Length;
            var end = map.IndexOf(';', start);
            if (end < 0)
            {
                end = map.Length;
            }
            SessionState.SetString(
                sessionKey,
                map.Substring(0, start) + value + map.Substring(end));
        }

        private static void RecordClear(string sessionKey, string name)
        {
            var map = SessionState.GetString(sessionKey, "");
            var marker = ";" + name + ":";
            var at = map.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return;
            }
            var end = map.IndexOf(';', at + marker.Length);
            SessionState.SetString(
                sessionKey,
                end < 0
                    ? map.Substring(0, at)
                    : map.Substring(0, at) + map.Substring(end));
        }

        // The per-test reload count keyed by test name — survives the
        // reload and any re-entered setup/body. ONLY the
        // [InitializeOnLoad] hook bumps it: a boundary confirmation
        // merely reads what the hook recorded, or a single crossing
        // would count twice (the r1 double-count that pushed a
        // legitimate test past its cap).
        private static int GuardCount(string name) =>
            int.TryParse(RecordGet(ReloadCountSessionKey, name), out var n)
                ? n : 0;

        private static void BumpGuardCount(string name) =>
            RecordSet(
                ReloadCountSessionKey, name,
                (GuardCount(name) + 1).ToString());

        // The class-wide abort — armed only when the suite's total
        // domain loads pass the class cap, the last-resort stopper a
        // suite-wide runaway needs; cleared at the suite's OneTime
        // boundaries.
        private static bool AbortRequested() =>
            SessionState.GetString(ReloadAbortSessionKey, "").Length > 0;

        // The per-test abort — armed when the NAMED test's own reload
        // count passes its cap. It fails that test's setup, requests
        // and teardown while leaving every other test's slate clean,
        // and the owning test's teardown clears it.
        private static bool PerTestAbortArmed(string name) =>
            RecordGet(PerTestAbortSessionKey, name).Length > 0;

        private static bool AbortRequestedFor(string name) =>
            AbortRequested() || PerTestAbortArmed(name);

        private static string AbortMessageFor(string name) =>
            AbortRequested()
                ? SessionState.GetString(ReloadAbortSessionKey, "")
                : RecordGet(PerTestAbortSessionKey, name);

        // The persisted compile-pending mark: a cleanup pass that
        // schedules a compile arms it, the boundary that drains it
        // clears it before its own reload. A test that dies between the
        // arm and the drain leaves the next domain's teardown to drain.
        private static void NoteCompilePending() =>
            SessionState.SetString(CompilePendingSessionKey, "1");

        private static bool CompilePending =>
            SessionState.GetString(CompilePendingSessionKey, "").Length > 0;

        private static void ClearCompilePending() =>
            SessionState.EraseString(CompilePendingSessionKey);

        // The persisted once-per-boundary request record: the request
        // marks (recompile requests and reload-arm requests) each
        // boundary makes before it yields. A re-executed segment sees
        // the mark and must not make the request again — the reload it
        // already caused is the one it resumes after.
        private static bool RequestOnce(string requestId)
        {
            var requests =
                SessionState.GetString(ReloadRequestsSessionKey, "");
            if (requests.Contains(";" + requestId + ";"))
            {
                return false;
            }
            SessionState.SetString(
                ReloadRequestsSessionKey,
                requests + ";" + requestId + ";");
            return true;
        }

        // Every explicit recompile/refresh point goes through here: an
        // armed abort fails the OWNING test closed instead of silently
        // dropping its request — a suppressed request must never leave
        // a WaitForDomainReload yield behind it.
        private static bool MayRequest(string requestId)
        {
            var name = CurrentTestFullName;
            if (AbortRequestedFor(name))
            {
                Assert.Fail(
                    "domain-reload loop guard: \"" + name + "\" exceeded " +
                    MaxDomainReloadsPerTest +
                    " domain reloads — recompile request \"" + requestId +
                    "\" suppressed (" + AbortMessageFor(name) + ")");
            }
            return RequestOnce(requestId);
        }

        private static bool PhaseDone(string phaseId) =>
            SessionState.GetString(PhasesDoneSessionKey, "")
                .Contains(";" + phaseId + ";");

        private static void MarkPhaseDone(string phaseId)
        {
            var done = SessionState.GetString(PhasesDoneSessionKey, "");
            if (!done.Contains(";" + phaseId + ";"))
            {
                SessionState.SetString(
                    PhasesDoneSessionKey, done + ";" + phaseId + ";");
            }
        }

        // Runs a phase exactly once per test: the persisted
        // phases-done record is set when the phase body returns Pass,
        // so a re-executed segment skips a phase that already passed
        // instead of mutating again, and a phase that failed is
        // returned again as the same failure — the recorded outcome
        // stands. A phase whose body could end the domain before it
        // returns (a marker write plus its Refresh, a package install's
        // compile) calls MarkPhaseDone before that statement so the
        // mark lands in the domain that did the work.
        private static PhaseResult PhaseGate(
            string phaseId, Func<PhaseResult> body)
        {
            if (PhaseDone(phaseId))
            {
                Crumb("phase \"" + phaseId + "\" already done — skipped " +
                    "(re-entered body after a domain reload)");
                return PhaseResult.Pass();
            }
            var result = body();
            if (result.Ok)
            {
                MarkPhaseDone(phaseId);
            }
            return result;
        }

        // Arms the boundary's sentinel — the persisted per-boundary
        // arm record makes the arm once-only so a re-executed segment
        // that already crossed (sentinel now dead) cannot re-arm and
        // fake a new reload; a resumed segment that still holds a live
        // sentinel sees it live and re-arms nothing.
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

        // The boundary's persisted confirmation: the resumed domain
        // confirms the reload the [InitializeOnLoad] hook already
        // counted (the hook is the only counter — a second bump here is
        // what pushed a legitimate two-reload test over its cap), clears
        // the compile-pending mark and verifies the in-progress test is
        // still the one that armed the reload. A resume under a
        // different test would mean the reload landed inside a command
        // the suite did not expect — the test fails closed rather than
        // adopting another test's reload as its own.
        private static void NoteDomainReload(string boundary)
        {
            var name = CurrentTestFullName;
            var owner = SessionState.GetString(ResumeOwnerSessionKey, "");
            if (!string.Equals(owner, name, StringComparison.Ordinal))
            {
                Assert.Fail(
                    "boundary \"" + boundary + "\" resumed under \"" +
                    name + "\" but the armed test was \"" + owner +
                    "\" — refusing to adopt another test's reload");
            }
            ClearCompilePending();
            var count = GuardCount(name);
            Crumb("boundary \"" + boundary + "\" crossed: " + count +
                " domain reload(s) so far for \"" + name + "\"");
            if (AbortRequestedFor(name) || count > MaxDomainReloadsPerTest)
            {
                Assert.Fail(
                    "domain-reload loop guard: \"" + name + "\" crossed " +
                    count + " domain reloads at \"" + boundary +
                    "\" — the suite needs at most " +
                    MaxDomainReloadsPerTest +
                    (AbortMessageFor(name).Length > 0
                        ? " (" + AbortMessageFor(name) + ")"
                        : ""));
            }
        }

        // The [InitializeOnLoad] hook's entry point — counts EVERY
        // domain load unconditionally, into a class-wide total no
        // replayed body can clear, and into the per-test record of the
        // test whose in-progress mark is armed. A count past the
        // per-test cap arms an abort keyed to THAT test alone — its
        // recompile points fail closed and its own teardown reports and
        // clears the record — while only the class total passing the
        // suite cap arms the shared abort that fails every remaining
        // test at UnitySetUp, so a request/reload cycle can never hang
        // the Editor.
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
            if (name.Length > 0 &&
                count > MaxDomainReloadsPerTest && !PerTestAbortArmed(name))
            {
                RecordSet(
                    PerTestAbortSessionKey, name,
                    name + " testReloads=" + count + " totalReloads=" + total);
                Crumb("domain-reload loop guard tripped on \"" + name +
                    "\": " + count + " reloads for the in-flight test " +
                    "(cap " + MaxDomainReloadsPerTest + ") — its " +
                    "recompile points now fail closed and its own " +
                    "teardown reports and clears the record");
            }
            if (total > MaxDomainReloadsTotal && !AbortRequested())
            {
                SessionState.SetString(
                    ReloadAbortSessionKey,
                    (name.Length > 0 ? name : "<no test in progress>") +
                        " testReloads=" + count + " totalReloads=" + total);
                Crumb("domain-reload class guard tripped: " + total +
                    " reloads for the class (cap " +
                    MaxDomainReloadsTotal + ") — every recompile point " +
                    "is now a no-op and UnitySetUp fails every remaining " +
                    "test");
            }
        }

        private static void AssertPhase(PhaseResult result, string phase) =>
            Assert.That(result.Ok, Is.True, phase + " failed: " + result.Failure);

        // Whether the boundary still owes its reload: the armed
        // sentinel (its half already crossed when false — the reload
        // that resumed this domain killed it) and the request mark —
        // false when an earlier pass already issued the request —
        // decide together. The mark is set BEFORE the yield so a
        // re-executed segment cannot request a second time.
        private static bool ReloadRequestNeeded(string boundaryId)
        {
            if (!DomainSentinelAlive)
            {
                return false;
            }
            return MayRequest(boundaryId);
        }

        // ── Setup / cleanup ─────────────────────────────────────────────

        // The replay records reset exactly once per class. On a domain
        // reload UTF's current release re-runs [OneTimeSetUp] on the
        // resumed domain before the restored test — the class-active
        // mark makes the reset idempotent so the replay cannot clear
        // the suite's started/phases records mid-run.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // The pending probe the teardown drain trusts is only as
            // strong as its shape: prove it once up front (the
            // At01AgentStepsTests pattern, T-W2.4-07) so the fixture
            // fails here — before any test — instead of silently
            // degrading to an isCompiling-only check that re-opens the
            // missed-request race the probe exists to close.
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
            if (SessionState.GetString(ClassActiveSessionKey, "").Length > 0)
            {
                Crumb("OneTimeSetUp re-entered on a resumed domain — " +
                    "the in-flight suite's replay records are kept");
                return;
            }
            EndTest();
            DisarmProbeNonce();
            SessionState.SetString(ReloadCountSessionKey, "");
            SessionState.SetInt(ReloadTotalSessionKey, 0);
            SessionState.SetString(StartedSessionKey, "");
            SessionState.SetString(ReloadRequestsSessionKey, "");
            SessionState.SetString(ReloadAbortSessionKey, "");
            SessionState.SetString(PerTestAbortSessionKey, "");
            SessionState.EraseString(CompilePendingSessionKey);
            SessionState.EraseString(ResumeOwnerSessionKey);
            ResetTest();
            SessionState.SetString(ClassActiveSessionKey, "1");
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            EndTest();
            DisarmProbeNonce();
            SessionState.SetString(ReloadCountSessionKey, "");
            SessionState.SetInt(ReloadTotalSessionKey, 0);
            SessionState.SetString(StartedSessionKey, "");
            SessionState.SetString(ReloadRequestsSessionKey, "");
            SessionState.SetString(ReloadAbortSessionKey, "");
            SessionState.SetString(PerTestAbortSessionKey, "");
            SessionState.EraseString(CompilePendingSessionKey);
            SessionState.EraseString(ResumeOwnerSessionKey);
            SessionState.EraseString(ClassActiveSessionKey);
            ResetTest();
            LogAssert.ignoreFailingMessages = false;
        }

        // No plain [SetUp]/[TearDown]: NUnit's setup/teardown command keeps
        // no persisted state, so a [UnityTest] resumed across a domain
        // reload runs [SetUp] again before the iterator continues — here
        // that deleted ProbeDir and the phase-1 state file mid-test. The
        // cleanup lives in [UnitySetUp]/[UnityTearDown], the persisted
        // outer command — and UTF replays THOSE commands from their
        // first statement on every domain a [UnityTest] crosses, so the
        // "test in progress" mark plus the probe nonce gate the cleanup:
        // a replay around the in-flight test restores nothing and deletes
        // nothing (the body it resumes into owns the probe state). The
        // package manifest a real-UPM case borrowed is restored BEFORE
        // the probe directory holding its backup is cleaned. Setup may
        // never cross a domain reload — a reload inside a before-command
        // re-runs the test it guards — so it yields no reload instruction
        // at all: the compile-scheduling deletes only record the pending
        // compile in the persisted mark, and the deferred reload rides
        // the body's first armed boundary (a UnityTest) or the teardown's
        // own bounded drain (a same-domain test). UTF's
        // PrepareForDomainReload on a setup-yielded instruction would
        // record this command's program counter against the test
        // method's fresh enumerator — the stale-PC silent-PASS vector
        // the RecompileScripts(false) residual here was (T-W2.4-07).
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup entry: \"" + CurrentTestFullName +
                "\" in-progress=" + TestInProgressActive() +
                " reloads=" + GuardCount(CurrentTestFullName));
            // The loop guard's word is final for the test it tripped
            // on: a per-test abort fails only that test here — before
            // any fixture work — while the class-wide total abort still
            // ends the suite, so the run ends with a result XML instead
            // of more reloads.
            if (AbortRequestedFor(CurrentTestFullName))
            {
                Assert.Fail(
                    "domain-reload loop guard tripped during \"" +
                    CurrentTestFullName + "\": " +
                    AbortMessageFor(CurrentTestFullName));
            }
            // Installing/removing a package makes the Editor emit a
            // benign [Error] log about the immutable-folder meta file —
            // the expected byproduct of a real UPM install (the
            // GitPackageResumeReloadTests finding).
            LogAssert.ignoreFailingMessages = true;
            if (TestInProgressActive() || ProbeStateIsLive())
            {
                yield break;
            }
            RestoreManifestIfCaptured();
            NewUntitledScene();
            // A leftover install's restore schedules a removal compile —
            // mark it so the deferred reload is owned, never a surprise.
            if (File.Exists(ManifestBackupPath) &&
                PackageInstalled(PackageName))
            {
                NoteCompilePending();
            }
            CleanupProbeArtifacts();
            // The compile-scheduling deletes mark the pending compile;
            // the queued reload stays behind the runner's assembly lock
            // and drains at the body's first armed boundary or this
            // command's teardown drain — never here (T-W2.4-07).
            CleanupCompileArtifacts();
            NewBaseScene();
            yield break;
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
                var aborting = AbortRequestedFor(name);
                DisposeAll();
                // The teardown owns the deferred reload its cleanup
                // queues: the manifest restore that removes a leftover
                // install (probe installed AND a backup to restore —
                // the removal compile is then certain) and the
                // script/marker folder deletes each schedule a compile
                // whose reload the runner's assembly lock defers; left
                // pending, it rides into the NEXT fixture whose waits
                // do not expect one. Releasing it at this command's own
                // sanctioned bounded drain replays only the after-
                // command, never the test already recorded. A tampered
                // manifest with no install restores bytes but queues no
                // compile — the gate must not drain for it.
                var scriptPending = File.Exists(ManifestBackupPath) &&
                    PackageInstalled(PackageName);
                RestoreManifestIfCaptured();
                NewUntitledScene();
                scriptPending |= CleanupCompileArtifacts();
                // ReleaseLeakedState must see the SessionState handle
                // records a surviving exclusion is adopted through:
                // clearing them first would leave a live handle no
                // adoptable path back and deny every later accept on
                // that store.
                CleanupProbeArtifacts();
                if (scriptPending || CompilePending || ReloadPending())
                {
                    // Clear the mark BEFORE the yield: a domain that
                    // dies inside the drain replays this command and
                    // the rebuilt drain proves the pending state again
                    // rather than releasing a second reload on the
                    // stale flag alone. The sanctioned bounded
                    // conditional drain (the At01AgentStepsTests idiom,
                    // T-W2.4-07): only a proven pending reload is
                    // crossed — ReloadPending() counts a requested
                    // reload armed behind the runner's lock that an
                    // isCompiling-only check would miss — at the
                    // bounded DrainedScriptReload instruction relayed
                    // from this command's own top level, never UTF's
                    // unbounded WaitForDomainReload.
                    ClearCompilePending();
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
                ClearHandoffRecords();
                if (aborting)
                {
                    Assert.Fail(
                        "domain-reload loop guard tripped during \"" +
                        name + "\": " + AbortMessageFor(name));
                }
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                // The guard's per-test records belong to THIS test —
                // report the trip above, then clear so a tripped test
                // can never fail a later test's setup or teardown.
                RecordClear(ReloadCountSessionKey, name);
                RecordClear(PerTestAbortSessionKey, name);
                EndTest();
            }
        }

        private void DisposeAll()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
        }

        // A saved, clean base scene: the GameObject rows need a stable
        // loaded scene whose unsaved children survive the domain reload.
        private static void NewBaseScene()
        {
            EnsureFolder(ProbeFolder);
            EnsureFolder(ScriptFolder);
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(
                EditorSceneManager.SaveScene(scene, BaseScenePath), Is.True,
                "the AT-03 base scene must save.");
        }

        private static void NewUntitledScene() =>
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // The T-W4.3-01 cleanup contract, verbatim in shape, once per Run
        // Store the fixture touches: the exclusion this process still
        // holds on a probe root is taken through the hand-off seam and
        // ended the orderly way; then both hand-off records drop.
        private static void ReleaseLeakedState()
        {
            foreach (var storeRoot in AllStoreRoots())
            {
                ReleaseLeakedState(storeRoot);
            }
        }

        private static void ReleaseLeakedState(string storeRoot)
        {
            var obtained = RunLockHandoff.TryObtain(storeRoot);
            if (!obtained.Ok || obtained.Value is not { } held)
            {
                Crumb("the probe Run Store is excluded elsewhere: " +
                    Describe(obtained.Diagnostics));
                RunLockHandoff.Clear(storeRoot);
                return;
            }
            if (!held.Release().Ok)
            {
                held.Dispose();
            }
            RunLockHandoff.Clear(storeRoot);
        }

        private static void ClearHandoffRecords()
        {
            foreach (var storeRoot in AllStoreRoots())
            {
                RunLockHandoff.Clear(storeRoot);
            }
        }

        // The no-compile cleanup — the run stores, the journals, the
        // state file, the manifest backups and the non-code probe
        // assets — safe inside UnitySetUp and UnityTearDown, which must
        // never schedule a reload. A replayed command around a live
        // test leaves the probe root untouched: only a ProbeDir whose
        // nonce is not the armed one is stale residue, so the Run-lock
        // reclaim and the deletes run only while no in-flight test owns
        // the probe state.
        private static void CleanupProbeArtifacts()
        {
            if (ProbeStateIsLive())
            {
                return;
            }
            ReleaseLeakedState();
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            var probeDisk = PhysicalPath(ProbeFolder);
            if (Directory.Exists(probeDisk))
            {
                try { Directory.Delete(probeDisk, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            if (File.Exists(probeDisk + ".meta"))
            {
                try { File.Delete(probeDisk + ".meta"); } catch (Exception) { }
            }
        }

        // Whether deleting the folder leaves a script delta: only an
        // imported folder that still carries compiled inputs — a
        // MonoScript or an assembly definition — queues a compile whose
        // reload a drain must wait for. The imported-but-EMPTY folder
        // every base scene creates below Assets/Scripts deletes with an
        // import but no compile; flagging it made UnityTearDown wait
        // for a reload that never came — the r1 "Expected domain
        // reload, but it did not occur" failures.
        private static bool FolderHoldsScripts(string assetFolder)
        {
            if (!AssetDatabase.IsValidFolder(assetFolder))
            {
                return false;
            }
            return
                AssetDatabase.FindAssets(
                    "t:MonoScript", new[] { assetFolder }).Length > 0 ||
                AssetDatabase.FindAssets(
                    "t:AssemblyDefinitionAsset",
                    new[] { assetFolder }).Length > 0;
        }

        // The compile-scheduling cleanup: the .cs-carrying folders
        // under Assets/. Deleting an imported one queues an import;
        // only a folder that held compiled inputs leaves a script
        // delta whose compile ends in a deferred domain reload — the
        // teardown drains it at its own sanctioned boundary and a
        // test body's first boundary absorbs it; the returned flag
        // and the persisted compile-pending mark carry "a reload is
        // now queued" to whichever drain runs first. A folder deleted
        // only on disk without an import tracks no compile — it was
        // never imported.
        private static bool CleanupCompileArtifacts()
        {
            var compile = FolderHoldsScripts(MarkerFolder);
            if (AssetDatabase.IsValidFolder(MarkerFolder))
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
            }
            if (Directory.Exists(MarkerDisk))
            {
                Directory.Delete(MarkerDisk, recursive: true);
            }
            if (File.Exists(MarkerDisk + ".meta"))
            {
                File.Delete(MarkerDisk + ".meta");
            }
            // Assets/Scripts itself is a normal project folder — created
            // when absent but never deleted by a test; only the suite's
            // own probe folder below it is removed.
            compile |= FolderHoldsScripts(ScriptFolder);
            if (AssetDatabase.IsValidFolder(ScriptFolder))
            {
                AssetDatabase.DeleteAsset(ScriptFolder);
            }
            var scriptDisk = PhysicalPath(ScriptFolder);
            if (Directory.Exists(scriptDisk))
            {
                try { Directory.Delete(scriptDisk, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            if (File.Exists(scriptDisk + ".meta"))
            {
                try { File.Delete(scriptDisk + ".meta"); } catch (Exception) { }
            }
            if (compile)
            {
                NoteCompilePending();
            }
            return compile;
        }

        // The SPEC §D0.3 protected-Run set a package or script mutation
        // consults: the suite's Runs live only on probe stores, so the
        // seam answers an empty set — the judgement under test is the
        // §F5 verdict, never the production-store guard's.
        private static IDisposable EmptyProtectedRuns() =>
            RunDefinitionGuard.InstallForTests(
                runs: () => (IReadOnlyList<ProtectedRun>)
                    Array.Empty<ProtectedRun>());

        // ── Fixtures, plans, accept/tick/recover ────────────────────────

        private RunnerHostFixture NewFixture(
            PackageWiring? package = null,
            List<RunnerHostFixture.ControlledWaitableOperation>? holds = null)
        {
            var fixture = new RunnerHostFixture(StoreRoot);
            _fixtures.Add(fixture);
            RegisterAll(fixture, package, holds);
            return fixture;
        }

        private void DisposeFixture(RunnerHostFixture fixture)
        {
            fixture.Dispose();
            _fixtures.Remove(fixture);
        }

        // The unity.package.add seam set one fixture binds: the
        // deterministic same-domain rows script the package-state view
        // and count the issue calls; the real-UPM rows bind the real
        // capture, the real Client.Add and the real wait condition.
        private sealed class PackageWiring
        {
            internal Func<PackageInstallView> View;
            internal Func<string, JsonValue> Issue;
            internal IWaitCondition Condition;

            internal PackageWiring(
                Func<PackageInstallView> view,
                Func<string, JsonValue> issue,
                IWaitCondition condition)
            {
                View = view;
                Issue = issue;
                Condition = condition;
            }
        }

        private static PackageWiring AbsentWiring() =>
            new PackageWiring(
                () => AbsentView(), RequestlessIssue, new DoneCondition());

        private static PackageWiring RealWiring(bool issueRealAdd) =>
            new PackageWiring(
                PackageInstallView.Capture,
                issueRealAdd ? RealIssue : RequestlessIssue,
                new PackageResolvedCondition());

        private static JsonValue RequestlessIssue(string id) =>
            PackageResolvedCondition.InitialToken(id, expectPresent: true);

        private static JsonValue RealIssue(string id)
        {
            s_issueCalls++;
            return PackageResolvedCondition.InitialToken(
                id, expectPresent: true, Client.Add(id));
        }

        // Every Operation the suite's plans use, at the seeds the parked
        // plans' fingerprints were built with — a fresh host must resolve
        // each step's implementation after a host swap or a domain
        // reload. The allowed-paths seams bind the host's own
        // ActiveRunAllowedPaths, never the test's.
        private static void RegisterAll(
            RunnerHostFixture fixture,
            PackageWiring? package = null,
            List<RunnerHostFixture.ControlledWaitableOperation>? holds = null)
        {
            var host = fixture.Host;
            RegisterAs(
                fixture, new CreateMaterialOperation(), 'a',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new GameObjectCreateOperation(), 'c',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new GameObjectDeleteOperation(), 'e',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new CreateScriptOperation(), '0',
                OperationOrigin.Standard);
            var wiring = package ?? AbsentWiring();
            RegisterAs(
                fixture,
                new UnityPackageAddOperation(
                    static () => Result<OpWeaveSettings>.Success(
                        OpWeaveSettings.Default),
                    wiring.Issue,
                    wiring.Condition,
                    wiring.View),
                '2', OperationOrigin.Standard);
            var hold = new RunnerHostFixture.ControlledWaitableOperation(HoldOp);
            fixture.Register(hold, '4');
            holds?.Add(hold);
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

        private static RunRecoveryReport Recover(RunnerHostFixture fixture)
        {
            var recovered = fixture.Host.RecoverRuns();
            Assert.IsTrue(recovered.Ok, "the recovery pass failed: " +
                Describe(recovered.Diagnostics));
            Assert.IsNotNull(recovered.Value);
            return recovered.Value!;
        }

        // The recovered Run's tail: the hold step released and the Run
        // ticked to its run_finished — the "recovered Runs finish with
        // exactly one instance" half of AT-03.
        private static void FinishRun(
            RunnerHostFixture fixture,
            string runId,
            RunnerHostFixture.ControlledWaitableOperation hold,
            string because)
        {
            hold.Release = true;
            for (var i = 0;
                i < 8 && fixture.Host.Coordinator.StateOf(runId) != RunState.Succeeded;
                i++)
            {
                Tick(fixture);
            }
            Assert.AreEqual(
                RunState.Succeeded,
                fixture.Host.Coordinator.StateOf(runId),
                because + " did not finish; journal: " + string.Join(
                    " | ", Lines(fixture.Root, runId)));
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(
                    fixture.Root, runId, "\"type\":\"run_finished\""),
                because + ": the finished Run must journal run_finished");
        }

        private static void AssertNeedsReview(
            string storeRoot, RunnerHostFixture fixture, string runId, string because)
        {
            Assert.AreEqual(
                RunState.NeedsReview,
                fixture.Host.Coordinator.StateOf(runId),
                because + " must end in NeedsReview; journal: " + string.Join(
                    " | ", RunnerHostFixture.JournalLines(storeRoot, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(
                    storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a review-needed resume never journals run_resumed");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    storeRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                because + ": the Unknown verdict is journaled step_needs_review (§E1)");
            Assert.AreEqual(
                1,
                RunnerHostFixture.JournalCount(
                    storeRoot, runId, "\"type\":\"step_started\""),
                because + ": NeedsReview never re-executed the interrupted step");
        }

        // The decided half of an §F5 started-only judgement: the verdict
        // journaled run_resumed (§F4 item 6's write-ahead) and never
        // step_needs_review.
        private static void AssertResumed(
            string storeRoot, RunnerHostFixture fixture, string runId, string because)
        {
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(
                    storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a decided resume journals run_resumed before " +
                    "the outcome is applied (§F4 item 6)");
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    storeRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                because + " must not end the interrupted step in NeedsReview");
            Assert.AreNotEqual(
                RunState.NeedsReview,
                fixture.Host.Coordinator.StateOf(runId),
                because + " must not end in NeedsReview");
        }

        // The adopted half: the verdict was Completed — the step's
        // journaled success carries reconciled:true and the step was
        // never executed a second time.
        private static void AssertAdopted(
            string storeRoot, RunnerHostFixture fixture, string runId, string because)
        {
            AssertResumed(storeRoot, fixture, runId, because);
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                because + ": the Completed verdict adopts the step's outputs " +
                    "(step_succeeded{reconciled:true})");
            Assert.AreEqual(
                1,
                RunnerHostFixture.JournalCount(
                    storeRoot, runId, "\"type\":\"step_started\""),
                because + ": an adopted step never re-executed");
        }

        // The re-executed half: the verdict was NotApplied — the resumed
        // step ran once more, so the journal holds exactly two
        // step_started{step:0} attempts.
        private static void AssertReexecuted(
            string storeRoot, RunnerHostFixture fixture, string runId, string because)
        {
            AssertResumed(storeRoot, fixture, runId, because);
            Assert.AreEqual(
                2,
                JournalCountAll(
                    storeRoot, runId, "\"type\":\"step_started\"", "\"step\":0"),
                because + ": a proven NotApplied re-executes exactly once");
        }

        // The same all-fragments matching JournalHasAll applies, counted:
        // a step-scoped count sees only the step under test — the
        // workflows' hold step journals its own step_started{step:1}, which
        // a bare type count would mistake for a re-execution.
        private static int JournalCountAll(
            string storeRoot, string runId, params string[] fragments)
        {
            var count = 0;
            foreach (var line in RunnerHostFixture.JournalLines(storeRoot, runId))
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

        // ── Journal surgery ─────────────────────────────────────────────

        private static string[] Lines(string storeRoot, string runId) =>
            RunnerHostFixture.JournalLines(storeRoot, runId);

        private static void WriteLines(
            string storeRoot, string runId, IEnumerable<string> lines)
        {
            File.WriteAllText(
                RunnerHostFixture.JournalPath(storeRoot, runId),
                string.Join("\n", lines) + "\n", s_utf8);
        }

        // Keeps the records through the FIRST line carrying both fragments —
        // the crash window between that durable record and whatever the loop
        // was about to append next.
        private static void TruncateThrough(
            string storeRoot, string runId, string typeFragment, string stepFragment)
        {
            var lines = Lines(storeRoot, runId);
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
            WriteLines(storeRoot, runId, lines.Take(cut + 1));
        }

        // The regression guard of the started-only fixtures: the journal the
        // recovering host reads must END at the bare step_started{step}.
        private static void AssertStartedOnlyTail(string storeRoot, string runId, int step)
        {
            var tail = Lines(storeRoot, runId);
            Assert.IsTrue(
                tail.Length > 0 &&
                tail[tail.Length - 1].Contains("\"type\":\"step_started\"") &&
                tail[tail.Length - 1].Contains("\"step\":" + step),
                "the journal must end at step " + step +
                "'s step_started with no later records; journal: " +
                string.Join(" | ", tail));
        }

        private static void TruncateToStartedOnly(
            RunnerHostFixture fixture, string runId)
        {
            fixture.Host.FlushJournal();
            TruncateThrough(
                fixture.Root, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(fixture.Root, runId, 0);
        }

        // A phase-1 state write also arms the probe-state nonce: from
        // here on the probe root is live in-flight state a replayed
        // setup/teardown must not reclaim.
        private static void WriteState(params string[] lines)
        {
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(StateFile, string.Join("\n", lines) + "\n", s_utf8);
            ArmProbeNonce();
        }

        private static string[] ReadState()
        {
            Assert.IsTrue(File.Exists(StateFile), "the phase-1 state file is missing");
            // The session token proves the file this domain reads is the
            // state THIS session's phase-1 wrote — residue of an earlier
            // session (or a file a replayed cleanup failed to delete)
            // must fail closed here, never feed phase-2 a silent pass.
            Assert.IsTrue(
                ProbeStateIsLive(),
                "the phase-1 probe state is stale — the nonce on disk is " +
                "not this Editor session's, so what phase-2 would read " +
                "is residue, not the state this session's phase-1 wrote");
            return File.ReadAllLines(StateFile);
        }

        // The marker content varies per boundary id so each test's
        // arm always differs from any leftover on disk — the write is
        // never a no-op that would leave the boundary without a
        // compile to cross.
        private static string MarkerSourceFor(string markerId) =>
            MarkerSource + "// reload probe " + markerId + "\n";

        // The marker-script reload arm: the request mark BEFORE the
        // write (a resumed segment only confirms the reload the first
        // pass caused), then the sentinel and the idempotent write
        // whose compile ends this domain — deterministic content,
        // applied only when the bytes differ, so a resumed segment
        // schedules no second compile.
        private static void ArmReload(string boundaryId)
        {
            if (!MayRequest(boundaryId + "-arm"))
            {
                return;
            }
            ArmDomainSentinel();
            Directory.CreateDirectory(MarkerDisk);
            var markerPath = Path.Combine(MarkerDisk, "ReloadMarker.cs");
            var source = MarkerSourceFor(boundaryId);
            if (!File.Exists(markerPath) ||
                File.ReadAllText(markerPath) != source)
            {
                File.WriteAllText(markerPath, source, s_utf8);
            }
            AssetDatabase.Refresh();
        }

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the drain
        // reaches it through reflection (the At01AgentStepsTests /
        // PackageAddUpmTests probe, T-W2.4-07). A reload already armed
        // behind the runner's reload lock surfaces here while
        // isCompiling is false — the deferred state an isCompiling-only
        // check misses and hands to the next fixture.
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

        // The quiet window before the sanctioned WaitForDomainReload
        // crossing (the PackageResolvedReloadTests shape): a registered
        // install can still have its import and compile in flight, so
        // the wait holds until the Editor reports neither compiling nor
        // updating for a few steady seconds. It yields only plain
        // frames — the reload itself is crossed in the test body, where
        // WaitForDomainReload.Perform may run.
        private static IEnumerator WaitForEditorQuiet()
        {
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var quietAt = -1.0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    quietAt = EditorApplication.timeSinceStartup + QuietSettleSec;
                }
                else if (quietAt >= 0.0 &&
                         EditorApplication.timeSinceStartup >= quietAt)
                {
                    yield break;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup + QuietSettleSec;
                }
                yield return null;
            }
        }

        // The sanctioned teardown drain (the At01AgentStepsTests idiom,
        // T-W2.4-07): a queued compile surfaces as isCompiling a frame or
        // two after Refresh returns, so the wait only trusts a sustained
        // quiet spell (the QuietSettleSec convention) — quiet flags reset
        // on every busy frame and a periodic Refresh flushes the import
        // queue so a queued compile surfaces inside the window, never
        // behind it. A pending compile or requested reload is crossed
        // only at DrainedScriptReload — the bounded instruction that
        // unlocks once the pending state is proven; never UTF's
        // unbounded WaitForDomainReload inside a command and never an
        // unconditional RecompileScripts(false) unlock, which releases
        // the runner's reload lock even when nothing is queued. The
        // instruction the nested enumerator produces is yielded DIRECTLY
        // from the caller's own top level through its MoveNext relay —
        // a nested yield would never reach Perform — and the whole
        // sequence is idempotent: a crossed reload resumes the caller's
        // enumerator at its yield with every local reset (or replays the
        // command), so the caller rebuilds the drain when it arrives
        // null rather than relaying a dead reference.
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
                        "At03NoDuplicatesTests." + phase +
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
                "At03NoDuplicatesTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "At03NoDuplicatesTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "At03NoDuplicatesTests." + phase +
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
                            "At03NoDuplicatesTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "At03NoDuplicatesTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // ── Artifact fixtures ───────────────────────────────────────────

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(
                ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

        private static void EnsureFolder(string folderPath)
        {
            var segments = folderPath.Split('/');
            var parent = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var child = parent + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(child))
                {
                    Assert.That(AssetDatabase.CreateFolder(parent, segments[i]),
                        Is.Not.Empty, "the probe folder must be created: " + child);
                }
                parent = child;
            }
        }

        private static string ScriptPath(string className) =>
            ScriptFolder + "/" + className + ".cs";

        // Valid C# for every write the suite makes: an imported .cs sits
        // in a deferred compile the armed reload (or the next editor
        // tick) runs, so every byte on disk must compile cleanly.
        private static string ProbeScriptSource(string className) =>
            "public static class " + className + "\n" +
            "{\n" +
            "    public const int Value = 0;\n" +
            "}\n";

        // A real Shader asset the material row resolves by name.
        private static string CreateProbeShader()
        {
            EnsureFolder(ProbeFolder);
            File.WriteAllText(PhysicalPath(ProbeShaderPath), ProbeShaderSource);
            AssetDatabase.ImportAsset(
                ProbeShaderPath, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(Shader.Find(ProbeShaderName), Is.Not.Null,
                "the probe shader must resolve by name: " + ProbeShaderName);
            return ProbeShaderName;
        }

        // The disturbed-artifact write a .mat gets: a different valid
        // Material over the path Execute wrote, so the recorded
        // serialized content can never match and the file stays
        // importable.
        private static void DamageMaterial(string assetPath)
        {
            var material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, assetPath);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<Material>(assetPath),
                "the disturbance could not overwrite " + assetPath);
        }

        // The disturbed-artifact write a .cs gets: different valid C#
        // over the path Execute wrote — content the recorded expected
        // hash can never match.
        private static void DamageScript(string assetPath)
        {
            File.WriteAllText(
                PhysicalPath(assetPath), ProbeScriptSource(DamagedClassName));
            AssetDatabase.Refresh();
        }

        private static int CountRootObjects(string name)
        {
            var count = 0;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == name)
                {
                    count++;
                }
            }
            return count;
        }

        // ── Package state ───────────────────────────────────────────────

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

        // Byte-for-byte capture of the project's package state before a
        // real-UPM case borrows it — the restore writes the bytes back
        // and resolves only when the world actually changed.
        private static void CaptureManifestBackup()
        {
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllBytes(ManifestBackupPath, File.ReadAllBytes(ManifestPath));
            var lockBytes = ReadBytesOrNull(LockPath);
            if (lockBytes is not null)
            {
                File.WriteAllBytes(LockBackupPath, lockBytes);
            }
            else if (File.Exists(LockBackupPath))
            {
                File.Delete(LockBackupPath);
            }
        }

        private static byte[]? ReadBytesOrNull(string path) =>
            File.Exists(path) ? File.ReadAllBytes(path) : null;

        // Restores the manifest/lock the suite borrowed; returns whether
        // it resolved so the caller knows a removal compile — and the
        // deferred reload it ends in — is now queued.
        private static bool RestoreManifestIfCaptured()
        {
            if (!File.Exists(ManifestBackupPath))
            {
                return false;
            }
            var changed = false;
            var manifestBytes = File.ReadAllBytes(ManifestBackupPath);
            if (!File.ReadAllBytes(ManifestPath).SequenceEqual(manifestBytes))
            {
                File.WriteAllBytes(ManifestPath, manifestBytes);
                changed = true;
            }
            var lockBytes = ReadBytesOrNull(LockBackupPath);
            var lockNow = ReadBytesOrNull(LockPath);
            if (!BytesEqual(lockNow, lockBytes))
            {
                if (lockBytes is null)
                {
                    try { File.Delete(LockPath); }
                    catch (Exception) { /* a held lockfile settles later */ }
                }
                else
                {
                    File.WriteAllBytes(LockPath, lockBytes);
                }
                changed = true;
            }
            if (changed)
            {
                try { Client.Resolve(); }
                catch (Exception) { /* a partial restore still settles later */ }
            }
            return changed;
        }

        private static bool BytesEqual(byte[]? left, byte[]? right)
        {
            if (left is null || right is null)
            {
                return left is null && right is null;
            }
            return left.SequenceEqual(right);
        }

        // The real-manifest channel tamper the Unknown package row makes:
        // a manifest-only dependency the lockfile and the registered list
        // never carry — the partial state SPEC §G8.1 names Unknown.
        private static void TamperManifestAddDep()
        {
            var manifest = JObject.Parse(File.ReadAllText(ManifestPath));
            var deps = (JObject)manifest["dependencies"]!;
            deps[PackageName] = PackageVersion;
            File.WriteAllText(ManifestPath, manifest.ToString());
        }

        private static string? ManifestDependency(string name)
        {
            var manifest = JObject.Parse(File.ReadAllText(ManifestPath));
            return manifest["dependencies"] is JObject deps &&
                deps.Property(name) is { } property
                    ? (string)property.Value!
                    : null;
        }

        // The scripted package-state rows: all three channels read, the
        // target absent from every one (the parked snapshot's pre-state),
        // the fully-agreeing installed state, and the manifest-only
        // partial the Unknown row needs.
        private static PackageInstallView AbsentView() =>
            new PackageInstallView(
                new JObject(), new JObject(), Array.Empty<ResolvedPackageView>());

        private static PackageInstallView InstalledView() =>
            new PackageInstallView(
                new JObject { [PackageName] = PackageVersion },
                new JObject
                {
                    [PackageName] = new JObject
                    {
                        ["version"] = PackageVersion,
                        ["source"] = "registry",
                    },
                },
                new[]
                {
                    new ResolvedPackageView(
                        PackageName, PackageVersion, "registry",
                        PackageVersion, null, null),
                });

        private static PackageInstallView PartialAddView() =>
            new PackageInstallView(
                new JObject { [PackageName] = PackageVersion },
                new JObject(), Array.Empty<ResolvedPackageView>());

        // The always-settled wait condition — a resumed package wait
        // settles in one Poll.
        private sealed class DoneCondition : IWaitCondition
        {
            public WaitStatus Check(JsonValue token) => WaitStatus.Done();
        }

        // ── The [op -> hold] park ───────────────────────────────────────

        // Accepts [step -> hold] and ticks once: step 0 executes, step 1's
        // waitable parks the Run mid-flight — the interruption the journal
        // then fakes by truncation back to the bare step_started{step:0}.
        private static string ParkStartedOnly(
            RunnerHostFixture fixture,
            string opId,
            char hashSeed,
            IReadOnlyDictionary<string, InputSource> inputs,
            IReadOnlyDictionary<string, string>? outputTypes = null)
        {
            var steps = new[]
            {
                Step(0, "s", opId, hashSeed, next: 1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: inputs, outputTypes: outputTypes),
                Step(1, "h", HoldOp, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(new[] { ("s", opId), ("h", HoldOp) }, ("s", "h"));
            var runId = Accept(fixture, plan, doc);
            Tick(fixture);
            Assert.AreEqual(
                RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                "the " + opId + " Run did not park mid-flight; journal: " +
                string.Join(" | ", Lines(fixture.Root, runId)));
            return runId;
        }

        // ── Plan inputs ─────────────────────────────────────────────────

        private static readonly IReadOnlyDictionary<string, string> s_materialOut =
            new Dictionary<string, string>
            {
                ["material"] = "unity.AssetRef<unity.Material>",
            };

        private static readonly IReadOnlyDictionary<string, string> s_gameObjectOut =
            new Dictionary<string, string>
            {
                ["gameObject"] = "unity.GameObjectRef",
            };

        private static readonly IReadOnlyDictionary<string, string> s_scriptOut =
            new Dictionary<string, string>
            {
                ["script"] = "unity.AssetRef<unity.MonoScript>",
            };

        private static readonly IReadOnlyDictionary<string, string> s_packageOut =
            new Dictionary<string, string>
            {
                ["package"] = "core.Json",
            };

        private static readonly IReadOnlyDictionary<string, string> s_noOutputs =
            new Dictionary<string, string>();

        private static Dictionary<string, InputSource> MaterialInputs(string path) =>
            new Dictionary<string, InputSource>
            {
                ["path"] = new Constant(RunnerHostFixture.Json("\"" + path + "\"")),
                ["shader"] = new Constant(
                    RunnerHostFixture.Json("\"" + ProbeShaderName + "\"")),
            };

        private static Dictionary<string, InputSource> GameObjectCreateInputs() =>
            new Dictionary<string, InputSource>
            {
                ["name"] = new Constant(
                    RunnerHostFixture.Json("\"" + CreateTargetName + "\"")),
            };

        private static Dictionary<string, InputSource> GameObjectTargetInputs(
            string name) =>
            new Dictionary<string, InputSource>
            {
                ["target"] = new Constant(RunnerHostFixture.Json(
                    "{\"kind\":\"gameObject\",\"path\":\"/" + name + "\"}")),
            };

        private static Dictionary<string, InputSource> ScriptInputs(
            string path, string className) =>
            new Dictionary<string, InputSource>
            {
                ["path"] = new Constant(RunnerHostFixture.Json("\"" + path + "\"")),
                ["content"] = new Constant(
                    JsonValue.From(new JValue(ProbeScriptSource(className)))),
            };

        private static Dictionary<string, InputSource> PackageAddInputs() =>
            new Dictionary<string, InputSource>
            {
                ["id"] = new Constant(
                    RunnerHostFixture.Json("\"" + PackageId + "\"")),
            };

        // ── unity.asset.create_material ─────────────────────────────────

        // The crash window after a successful Execute: the artifact is
        // exactly what the step wrote, so the §F4 verdict is Completed —
        // the resumed Run adopts the outputs, finishes at its hold and
        // leaves exactly one .mat behind.
        [Test]
        public void CreateMaterial_AdoptedFinishesExactlyOneInstance()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                CreateProbeShader();
                var runId = ParkStartedOnly(
                    fixture1, CreateMaterialOperation.OperationId, 'a',
                    MaterialInputs(MaterialPath), s_materialOut);
                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<Material>(MaterialPath),
                    "the first Execute did not write the Material");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);

                var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
                var fixture2 = NewFixture(holds: holds);
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertAdopted(
                    StoreRoot, fixture2, runId,
                    "an intact asset.create_material evidence row");
                FinishRun(fixture2, runId, holds[0],
                    "the adopted create_material Run");
                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<Material>(MaterialPath),
                    "the finished Run keeps its single Material");
                Assert.AreEqual(
                    1, CountArtifacts(ProbeFolder, ".mat"),
                    "exactly one material artifact exists");
            }
        }

        // The crash window before Execute landed: the recorded path is
        // empty just as the snapshot recorded it, so the §F4 verdict is
        // NotApplied and the resumed step executes once more — one
        // material at the end, never two.
        [Test]
        public void CreateMaterial_NotAppliedReexecutesExactlyOnce()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                CreateProbeShader();
                var runId = ParkStartedOnly(
                    fixture1, CreateMaterialOperation.OperationId, 'a',
                    MaterialInputs(MaterialPath), s_materialOut);
                Assert.IsTrue(
                    AssetDatabase.DeleteAsset(MaterialPath),
                    "the crash-before-effect world removes the artifact");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);

                var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
                var fixture2 = NewFixture(holds: holds);
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                FinishRun(fixture2, runId, holds[0],
                    "the re-executed create_material Run");
                AssertReexecuted(
                    StoreRoot, fixture2, runId,
                    "a provably-unapplied asset.create_material");
                Assert.IsNotNull(
                    AssetDatabase.LoadAssetAtPath<Material>(MaterialPath),
                    "the single re-execution wrote the Material");
                Assert.AreEqual(
                    1, CountArtifacts(ProbeFolder, ".mat"),
                    "exactly one material artifact exists after re-execution");
            }
        }

        // The disturbed evidence row: foreign bytes stand at the recorded
        // path, the verdict is Unknown — NeedsReview, no resume, no
        // re-execution — and the foreign artifact is never deleted.
        [Test]
        public void CreateMaterial_UnknownKeepsForeignArtifact()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                CreateProbeShader();
                var runId = ParkStartedOnly(
                    fixture1, CreateMaterialOperation.OperationId, 'a',
                    MaterialInputs(MaterialPath), s_materialOut);
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);
                DamageMaterial(MaterialPath);

                var fixture2 = NewFixture();
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertNeedsReview(
                    StoreRoot, fixture2, runId,
                    "a disturbed asset.create_material evidence row");
                Assert.AreEqual(
                    "Standard",
                    AssetDatabase.LoadAssetAtPath<Material>(MaterialPath).shader.name,
                    "NeedsReview neither rewrites nor deletes — the foreign " +
                    "Material stays");
            }
        }

        // ── unity.gameobject.create / unity.gameobject.delete ───────────

        // GameObject evidence survives a host swap in-session: the
        // created child is provably the recorded one, so the verdict is
        // Completed and the finished Run holds exactly one new root.
        [Test]
        public void GameObjectCreate_AdoptedFinishesExactlyOneInstance()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                var runId = ParkStartedOnly(
                    fixture1, GameObjectCreateOperation.OperationId, 'c',
                    GameObjectCreateInputs(), s_gameObjectOut);
                Assert.AreEqual(
                    1, CountRootObjects(CreateTargetName),
                    "the first Execute did not create the object");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);

                var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
                var fixture2 = NewFixture(holds: holds);
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertAdopted(
                    StoreRoot, fixture2, runId,
                    "an intact gameobject.create evidence row");
                FinishRun(fixture2, runId, holds[0],
                    "the adopted gameobject.create Run");
                Assert.AreEqual(
                    1, CountRootObjects(CreateTargetName),
                    "exactly one GameObject carries the created name — no " +
                    "duplicate creation");
            }
        }

        // The created object is gone and the snapshot recorded it absent:
        // NotApplied re-executes once — one object, never two.
        [Test]
        public void GameObjectCreate_NotAppliedReexecutesExactlyOnce()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                var runId = ParkStartedOnly(
                    fixture1, GameObjectCreateOperation.OperationId, 'c',
                    GameObjectCreateInputs(), s_gameObjectOut);
                var created = GameObject.Find(CreateTargetName);
                Assert.IsNotNull(created, "the first Execute did not create the object");
                UnityEngine.Object.DestroyImmediate(created);
                Assert.AreEqual(
                    0, CountRootObjects(CreateTargetName),
                    "the crash-before-effect world holds no created object");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);

                var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
                var fixture2 = NewFixture(holds: holds);
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                FinishRun(fixture2, runId, holds[0],
                    "the re-executed gameobject.create Run");
                AssertReexecuted(
                    StoreRoot, fixture2, runId,
                    "a provably-unapplied gameobject.create");
                Assert.AreEqual(
                    1, CountRootObjects(CreateTargetName),
                    "exactly one GameObject exists after re-execution");
            }
        }

        // The created child renamed before recovery: the live children
        // list is neither the snapshot nor the snapshot plus the recorded
        // addition — Unknown, NeedsReview — and nothing is deleted or
        // re-created.
        [Test]
        public void GameObjectCreate_UnknownKeepsForeignObject()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                var runId = ParkStartedOnly(
                    fixture1, GameObjectCreateOperation.OperationId, 'c',
                    GameObjectCreateInputs(), s_gameObjectOut);
                var created = GameObject.Find(CreateTargetName);
                Assert.IsNotNull(created, "the first Execute did not create the object");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);
                created!.name = CreateTamperedName;

                var fixture2 = NewFixture();
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertNeedsReview(
                    StoreRoot, fixture2, runId,
                    "a disturbed gameobject.create evidence row");
                Assert.AreEqual(
                    0, CountRootObjects(CreateTargetName),
                    "a re-execution would have created a second object");
                Assert.AreEqual(
                    1, CountRootObjects(CreateTamperedName),
                    "NeedsReview removed nothing — the object stays");
            }
        }

        // The delete landed and the sibling evidence still matches:
        // Completed — the finished Run keeps the target deleted and never
        // touches the surviving children.
        [Test]
        public void GameObjectDelete_AdoptedFinishesWithoutExtraDeletion()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                var target = new GameObject(DeleteTargetName);
                var neighbour = new GameObject(NeighbourName);
                Assert.IsNotNull(target);
                Assert.IsNotNull(neighbour);
                var runId = ParkStartedOnly(
                    fixture1, GameObjectDeleteOperation.OperationId, 'e',
                    GameObjectTargetInputs(DeleteTargetName), s_noOutputs);
                Assert.IsNull(
                    GameObject.Find(DeleteTargetName),
                    "the first Execute did not delete the target");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);

                var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
                var fixture2 = NewFixture(holds: holds);
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertAdopted(
                    StoreRoot, fixture2, runId,
                    "an intact gameobject.delete evidence row");
                FinishRun(fixture2, runId, holds[0],
                    "the adopted gameobject.delete Run");
                Assert.AreEqual(
                    0, CountRootObjects(DeleteTargetName),
                    "the deleted object is never restored by recovery");
                Assert.AreEqual(
                    1, CountRootObjects(NeighbourName),
                    "the surviving sibling was never mis-deleted");
            }
        }

        // A foreign same-named root takes the deleted object's slot: the
        // recorded children list matches neither the live list nor the
        // snapshot-minus-target — Unknown, NeedsReview — and the foreign
        // object is never mis-deleted by a re-execution.
        [Test]
        public void GameObjectDelete_UnknownKeepsForeignObject()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                var target = new GameObject(DeleteTargetName);
                Assert.IsNotNull(target);
                var runId = ParkStartedOnly(
                    fixture1, GameObjectDeleteOperation.OperationId, 'e',
                    GameObjectTargetInputs(DeleteTargetName), s_noOutputs);
                Assert.IsNull(
                    GameObject.Find(DeleteTargetName),
                    "the first Execute did not delete the target");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);
                var foreign = new GameObject(DeleteTargetName);
                Assert.IsNotNull(foreign);

                var fixture2 = NewFixture();
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertNeedsReview(
                    StoreRoot, fixture2, runId,
                    "a disturbed gameobject.delete evidence row");
                Assert.AreEqual(
                    1, CountRootObjects(DeleteTargetName),
                    "NeedsReview neither deletes the foreign object nor " +
                    "restores the original — a re-execution would have " +
                    "deleted it");
            }
        }

        // ── unity.script.create ─────────────────────────────────────────

        // The .cs on disk is byte-identical to the recorded expected
        // content: Completed — the finished Run keeps exactly one script.
        [Test]
        public void ScriptCreate_AdoptedFinishesExactlyOneInstance()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                var path = ScriptPath(ScriptClassName);
                var runId = ParkStartedOnly(
                    fixture1, CreateScriptOperation.OperationId, '0',
                    ScriptInputs(path, ScriptClassName), s_scriptOut);
                Assert.IsTrue(
                    File.Exists(PhysicalPath(path)),
                    "the first Execute did not write the script");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);

                var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
                var fixture2 = NewFixture(holds: holds);
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertAdopted(
                    StoreRoot, fixture2, runId,
                    "an intact script.create evidence row");
                FinishRun(fixture2, runId, holds[0],
                    "the adopted script.create Run");
                Assert.AreEqual(
                    ProbeScriptSource(ScriptClassName),
                    File.ReadAllText(PhysicalPath(path)),
                    "exactly one script, byte-identical to the step's output");
            }
        }

        // The .cs is gone just as the snapshot recorded: NotApplied —
        // the resumed step writes it once more, never twice.
        [Test]
        public void ScriptCreate_NotAppliedReexecutesExactlyOnce()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                var path = ScriptPath(ScriptResClassName);
                var runId = ParkStartedOnly(
                    fixture1, CreateScriptOperation.OperationId, '0',
                    ScriptInputs(path, ScriptResClassName), s_scriptOut);
                // The crash-before-effect world is the recorded empty
                // pre-state: no file, no .meta and no imported asset
                // entry. A bare .cs delete leaves the .meta and the
                // entry behind — Reconcile reads either as a disturbed
                // artifact (Unknown), never NotApplied. The Refresh
                // retires the entry, and the delete's script delta
                // queues the compile the persisted compile-pending
                // mark hands to the teardown drain.
                var scriptDisk = PhysicalPath(path);
                File.Delete(scriptDisk);
                if (File.Exists(scriptDisk + ".meta"))
                {
                    File.Delete(scriptDisk + ".meta");
                }
                AssetDatabase.Refresh();
                NoteCompilePending();
                Assert.IsFalse(
                    File.Exists(scriptDisk),
                    "the crash-before-effect world holds no script");
                Assert.IsFalse(
                    File.Exists(scriptDisk + ".meta"),
                    "the crash-before-effect world holds no leftover .meta");
                Assert.AreEqual(
                    string.Empty,
                    AssetDatabase.AssetPathToGUID(
                        path, AssetPathToGUIDOptions.OnlyExistingAssets),
                    "the crash-before-effect world holds no imported asset entry");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);

                var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
                var fixture2 = NewFixture(holds: holds);
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                FinishRun(fixture2, runId, holds[0],
                    "the re-executed script.create Run");
                AssertReexecuted(
                    StoreRoot, fixture2, runId,
                    "a provably-unapplied script.create");
                Assert.AreEqual(
                    ProbeScriptSource(ScriptResClassName),
                    File.ReadAllText(PhysicalPath(path)),
                    "exactly one script exists after re-execution");
                Assert.AreEqual(
                    1, CountArtifacts(ScriptFolder, ".cs"),
                    "no second script file exists");
            }
        }

        // Foreign valid C# at the recorded path: the expected-content
        // evidence can never match — Unknown, NeedsReview — and the
        // foreign file is never rewritten or deleted.
        [Test]
        public void ScriptCreate_UnknownKeepsForeignScript()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture();
                var path = ScriptPath(ScriptClassName);
                var runId = ParkStartedOnly(
                    fixture1, CreateScriptOperation.OperationId, '0',
                    ScriptInputs(path, ScriptClassName), s_scriptOut);
                Assert.IsTrue(
                    File.Exists(PhysicalPath(path)),
                    "the first Execute did not write the script");
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);
                DamageScript(path);

                var fixture2 = NewFixture();
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertNeedsReview(
                    StoreRoot, fixture2, runId,
                    "a disturbed script.create evidence row");
                Assert.AreEqual(
                    ProbeScriptSource(DamagedClassName),
                    File.ReadAllText(PhysicalPath(path)),
                    "NeedsReview leaves the foreign script — a re-execution " +
                    "would have overwritten it or failed on the occupied path");
            }
        }

        // ── unity.package.add: the scripted-view verdicts ───────────────

        // The recorded snapshot proves the target was absent and the
        // three channels now agree on the one install: Completed — the
        // resumed Run adopts the resolved package and finishes having
        // issued no second add.
        [Test]
        public void PackageAdd_AdoptedFinishesWithoutSecondIssue()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture(package: AbsentWiring());
                var runId = ParkStartedOnly(
                    fixture1, UnityPackageAddOperation.OperationId, '2',
                    PackageAddInputs(), s_packageOut);
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);

                var issues = 0;
                var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
                var fixture2 = NewFixture(
                    package: new PackageWiring(
                        () => InstalledView(),
                        id =>
                        {
                            issues++;
                            return RequestlessIssue(id);
                        },
                        new DoneCondition()),
                    holds: holds);
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertAdopted(
                    StoreRoot, fixture2, runId,
                    "an intact package.add evidence row");
                FinishRun(fixture2, runId, holds[0],
                    "the adopted package.add Run");
                Assert.AreEqual(
                    0, issues,
                    "an adopted package.add never issues a second Client.Add");
            }
        }

        // Absent everywhere now just as the snapshot recorded: NotApplied
        // — the resumed step issues the add exactly once and the Run
        // finishes on the one install.
        [Test]
        public void PackageAdd_NotAppliedReexecutesExactlyOnce()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                var fixture1 = NewFixture(package: AbsentWiring());
                var runId = ParkStartedOnly(
                    fixture1, UnityPackageAddOperation.OperationId, '2',
                    PackageAddInputs(), s_packageOut);
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);

                // The issue lands the install: the scripted view flips to
                // the fully-agreeing state the moment the one add is
                // issued — the re-execution the verdict allows, once.
                var issues = 0;
                var view = AbsentView();
                var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
                var fixture2 = NewFixture(
                    package: new PackageWiring(
                        () => view,
                        id =>
                        {
                            issues++;
                            view = InstalledView();
                            return RequestlessIssue(id);
                        },
                        new DoneCondition()),
                    holds: holds);
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                FinishRun(fixture2, runId, holds[0],
                    "the re-executed package.add Run");
                AssertReexecuted(
                    StoreRoot, fixture2, runId,
                    "a provably-unapplied package.add");
                Assert.AreEqual(
                    1, issues,
                    "a proven NotApplied issued the add exactly once");
            }
        }

        // The REAL package-state channels: a manifest-only dependency the
        // lockfile and the registered list never carry — the partial
        // state SPEC §G8.1 names Unknown — NeedsReview on the real
        // PackageInstallView.Capture read, and nothing is added or
        // removed.
        [Test]
        public void PackageAdd_UnknownKeepsRealManifestEntry()
        {
            BeginTest();
            using (EmptyProtectedRuns())
            {
                Assert.IsFalse(
                    PackageInstalled(PackageName),
                    "the probe package must not already be installed");
                CaptureManifestBackup();
                var fixture1 = NewFixture(
                    package: RealWiring(issueRealAdd: false));
                var runId = ParkStartedOnly(
                    fixture1, UnityPackageAddOperation.OperationId, '2',
                    PackageAddInputs(), s_packageOut);
                TruncateToStartedOnly(fixture1, runId);
                DisposeFixture(fixture1);
                TamperManifestAddDep();

                var issues = 0;
                var fixture2 = NewFixture(
                    package: new PackageWiring(
                        PackageInstallView.Capture,
                        id =>
                        {
                            issues++;
                            return RequestlessIssue(id);
                        },
                        new PackageResolvedCondition()));
                var report = Recover(fixture2);
                Assert.IsTrue(report.ExclusionHeld);
                AssertNeedsReview(
                    StoreRoot, fixture2, runId,
                    "a partial real package-state evidence row");
                Assert.AreEqual(
                    0, issues,
                    "NeedsReview never issued a Client.Add");
                Assert.AreEqual(
                    PackageVersion, ManifestDependency(PackageName),
                    "NeedsReview removed nothing — the manifest entry stays");
                Assert.IsFalse(
                    PackageInstalled(PackageName),
                    "the partial state never resolved into an install");
            }
        }

        // ── SPEC §F5 across a real domain reload ────────────────────────

        // The SPEC §T.3 AT-03 reload case: one Run per artifact kind,
        // each parked started-only with its evidence intact, must be
        // decided Completed in the domain the compile creates — and each
        // recovered Run then finishes holding exactly one artifact, no
        // duplicates, no deletions.
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator StartedOnlyAdoptedFinishesExactlyOneAcrossTheRealReload()
        {
            BeginTest();
            var before = PhaseGate("adopt-group-p1", AdoptedGroupPhase);
            Crumb("adopted phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            AssertPhase(before, "adopt-group-p1");

            // The marker compile the arm queued crosses here, at the
            // body's sanctioned boundary: the request mark was set
            // inside ArmReload before the write, and the release below
            // is issued at most once — a re-executed segment finds both
            // marks and simply confirms the reload that resumed it.
            if (ReloadRequestNeeded("adopt-group-mid"))
            {
                Crumb("boundary \"adopt-group-mid\" issuing " +
                    "RequestScriptReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                // The bounded sanctioned crossing (T-W2.4-07): only a
                // reload proven pending is waited on, with a deadline —
                // never UTF's unbounded WaitForDomainReload.
                if (ReloadPending())
                {
                    yield return new DrainedScriptReload(
                        "adopt-group-mid", WaitTimeoutSec);
                }
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");
            NoteDomainReload("adopt-group-mid");

            var after = PhaseGate("adopt-group-p2", AdoptedGroupPhaseAfter);
            Crumb("adopted phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            AssertPhase(after, "adopt-group-p2");
        }

        // The disturbed half across the same boundary: one Run per
        // artifact kind, each evidence row damaged so no honest verdict
        // but Unknown remains — NeedsReview one domain later, no resume,
        // no re-execution, no deletion.
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator StartedOnlyDisturbedNeedsReviewAcrossTheRealReload()
        {
            BeginTest();
            var before = PhaseGate("disturbed-group-p1", DisturbedGroupPhase);
            Crumb("disturbed phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            AssertPhase(before, "disturbed-group-p1");

            if (ReloadRequestNeeded("disturbed-group-mid"))
            {
                Crumb("boundary \"disturbed-group-mid\" issuing " +
                    "RequestScriptReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                // The bounded sanctioned crossing (T-W2.4-07): only a
                // reload proven pending is waited on, with a deadline —
                // never UTF's unbounded WaitForDomainReload.
                if (ReloadPending())
                {
                    yield return new DrainedScriptReload(
                        "disturbed-group-mid", WaitTimeoutSec);
                }
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");
            NoteDomainReload("disturbed-group-mid");

            var after = PhaseGate("disturbed-group-p2", DisturbedGroupPhaseAfter);
            Crumb("disturbed phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            AssertPhase(after, "disturbed-group-p2");
        }

        // One phase host on a Run Store of its own — never installed, so
        // no editor-update tick can pump its parked hold past the
        // truncation point, and never disposed on the pass path: the OS
        // exclusion its parked Run holds is the process-held handle the
        // hand-off seam carries across the domain reload.
        private static RunnerHostFixture GroupFixture(
            List<RunnerHostFixture> fixtures, string key, PackageWiring? package = null)
        {
            var fixture = new RunnerHostFixture(StoreRootFor(key));
            fixtures.Add(fixture);
            RegisterAll(fixture, package);
            return fixture;
        }

        private static void DisposeFixtures(List<RunnerHostFixture> fixtures)
        {
            for (var i = 0; i < fixtures.Count; i++)
            {
                fixtures[i].Dispose();
            }
            fixtures.Clear();
        }

        // Phase 1: each [op -> hold] Run — one per Run Store — executes
        // step 0 and waits on the hold; every journal is cut back to the
        // bare step_started{step:0} a real interruption would have left.
        private static PhaseResult AdoptedGroupPhase()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                using (EmptyProtectedRuns())
                {
                    var runIds = new string[s_groupKeys.Length];

                    CreateProbeShader();
                    runIds[0] = ParkStartedOnly(
                        GroupFixture(fixtures, "material"),
                        CreateMaterialOperation.OperationId, 'a',
                        MaterialInputs(MaterialPath), s_materialOut);
                    if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) == null)
                    {
                        DisposeFixtures(fixtures);
                        return PhaseResult.Fail(
                            "the create_material Execute did not write the Material");
                    }

                    runIds[1] = ParkStartedOnly(
                        GroupFixture(fixtures, "gameobject"),
                        GameObjectCreateOperation.OperationId, 'c',
                        GameObjectCreateInputs(), s_gameObjectOut);
                    if (CountRootObjects(CreateTargetName) != 1)
                    {
                        DisposeFixtures(fixtures);
                        return PhaseResult.Fail(
                            "the gameobject.create Execute did not create the object");
                    }

                    runIds[2] = ParkStartedOnly(
                        GroupFixture(fixtures, "script"),
                        CreateScriptOperation.OperationId, '0',
                        ScriptInputs(
                            ScriptPath(ScriptClassName), ScriptClassName),
                        s_scriptOut);
                    if (!File.Exists(PhysicalPath(ScriptPath(ScriptClassName))))
                    {
                        DisposeFixtures(fixtures);
                        return PhaseResult.Fail(
                            "the script.create Execute did not write the script");
                    }

                    runIds[3] = ParkStartedOnly(
                        GroupFixture(fixtures, "package", AbsentWiring()),
                        UnityPackageAddOperation.OperationId, '2',
                        PackageAddInputs(), s_packageOut);

                    for (var i = 0; i < fixtures.Count; i++)
                    {
                        TruncateToStartedOnly(fixtures[i], runIds[i]);
                    }
                    WriteState(runIds);
                    // The marker write plus its Refresh can end this
                    // domain before the body returns — mark the phase
                    // complete first so a resumed segment never re-runs
                    // the park, the truncation or a second write.
                    MarkPhaseDone("adopt-group-p1");
                    ArmReload("adopt-group-mid");
                    Crumb("adopted phase-1 complete: four started-only runs");
                    return PhaseResult.Pass();
                }
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2: same Editor session (SessionState survives the
        // reload), same live objects — a fresh-domain §F4 pass per Run
        // Store reads one started-only journal and one intact world: the
        // surviving OS handle re-adopts, every verdict is Completed, and
        // each Run finishes holding exactly one artifact.
        private static PhaseResult AdoptedGroupPhaseAfter()
        {
            var fixtures = new List<RunnerHostFixture>();
            var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
            try
            {
                using (EmptyProtectedRuns())
                {
                    var persisted = ReadState();
                    Assert.AreEqual(
                        s_groupKeys.Length, persisted.Length,
                        "the phase-1 state file lost a run id");

                    for (var i = 0; i < s_groupKeys.Length; i++)
                    {
                        var fixture = new RunnerHostFixture(
                            StoreRootFor(s_groupKeys[i]));
                        fixtures.Add(fixture);
                        RegisterAll(
                            fixture,
                            s_groupKeys[i] == "package"
                                ? new PackageWiring(
                                    () => InstalledView(), RequestlessIssue,
                                    new DoneCondition())
                                : null,
                            holds);
                        fixture.Editor.IsCompiling = EditorApplication.isCompiling;
                        AssertStartedOnlyTail(fixture.Root, persisted[i], 0);
                    }
                    var names = new[]
                    {
                        "asset.create_material", "gameobject.create",
                        "script.create", "package.add",
                    };
                    for (var i = 0; i < fixtures.Count; i++)
                    {
                        var report = Recover(fixtures[i]);
                        Assert.IsTrue(
                            report.ExclusionHeld,
                            "the " + s_groupKeys[i] +
                                " store's recovery pass could not take its exclusion");
                        AssertAdopted(
                            fixtures[i].Root, fixtures[i], persisted[i],
                            "an intact " + names[i] + " evidence row across " +
                            "a reload");
                        FinishRun(fixtures[i], persisted[i], holds[i],
                            "the adopted " + names[i] + " Run");
                    }

                    Assert.IsNotNull(
                        AssetDatabase.LoadAssetAtPath<Material>(MaterialPath),
                        "create_material: exactly one Material across the reload");
                    Assert.AreEqual(
                        1, CountArtifacts(ProbeFolder, ".mat"),
                        "create_material: no duplicate asset file");
                    Assert.AreEqual(
                        1, CountRootObjects(CreateTargetName),
                        "gameobject.create: exactly one object across the reload");
                    Assert.AreEqual(
                        ProbeScriptSource(ScriptClassName),
                        File.ReadAllText(PhysicalPath(ScriptPath(ScriptClassName))),
                        "script.create: exactly one script, byte-identical");
                    DisposeFixtures(fixtures);
                    Crumb("adopted phase-2 complete: four finished runs");
                    return PhaseResult.Pass();
                }
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 1 of the disturbed companion: identical park, then every
        // evidence row damaged so no honest verdict but Unknown remains.
        private static PhaseResult DisturbedGroupPhase()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                using (EmptyProtectedRuns())
                {
                    var runIds = new string[s_groupKeys.Length];

                    CreateProbeShader();
                    runIds[0] = ParkStartedOnly(
                        GroupFixture(fixtures, "material"),
                        CreateMaterialOperation.OperationId, 'a',
                        MaterialInputs(MaterialPath), s_materialOut);
                    if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) == null)
                    {
                        DisposeFixtures(fixtures);
                        return PhaseResult.Fail(
                            "the create_material Execute did not write the Material");
                    }
                    DamageMaterial(MaterialPath);

                    runIds[1] = ParkStartedOnly(
                        GroupFixture(fixtures, "gameobject"),
                        GameObjectCreateOperation.OperationId, 'c',
                        GameObjectCreateInputs(), s_gameObjectOut);
                    var created = GameObject.Find(CreateTargetName);
                    if (created is null)
                    {
                        DisposeFixtures(fixtures);
                        return PhaseResult.Fail(
                            "the gameobject.create Execute did not create the object");
                    }
                    created.name = CreateTamperedName;

                    var scriptPath = ScriptPath(ScriptClassName);
                    runIds[2] = ParkStartedOnly(
                        GroupFixture(fixtures, "script"),
                        CreateScriptOperation.OperationId, '0',
                        ScriptInputs(scriptPath, ScriptClassName), s_scriptOut);
                    if (!File.Exists(PhysicalPath(scriptPath)))
                    {
                        DisposeFixtures(fixtures);
                        return PhaseResult.Fail(
                            "the script.create Execute did not write the script");
                    }
                    DamageScript(scriptPath);

                    runIds[3] = ParkStartedOnly(
                        GroupFixture(fixtures, "package", AbsentWiring()),
                        UnityPackageAddOperation.OperationId, '2',
                        PackageAddInputs(), s_packageOut);

                    for (var i = 0; i < fixtures.Count; i++)
                    {
                        TruncateToStartedOnly(fixtures[i], runIds[i]);
                    }
                    WriteState(runIds);
                    MarkPhaseDone("disturbed-group-p1");
                    ArmReload("disturbed-group-mid");
                    Crumb("disturbed phase-1 complete: four disturbed runs");
                    return PhaseResult.Pass();
                }
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2 of the disturbed companion: every started-only journal
        // against a disturbed world ends NeedsReview in the new domain,
        // and every disturbed artifact is exactly as the disturbance
        // left it.
        private static PhaseResult DisturbedGroupPhaseAfter()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                using (EmptyProtectedRuns())
                {
                    var persisted = ReadState();
                    Assert.AreEqual(
                        s_groupKeys.Length, persisted.Length,
                        "the phase-1 state file lost a run id");

                    for (var i = 0; i < s_groupKeys.Length; i++)
                    {
                        var fixture = new RunnerHostFixture(
                            StoreRootFor(s_groupKeys[i]));
                        fixtures.Add(fixture);
                        RegisterAll(
                            fixture,
                            s_groupKeys[i] == "package"
                                ? new PackageWiring(
                                    () => PartialAddView(), RequestlessIssue,
                                    new DoneCondition())
                                : null);
                        fixture.Editor.IsCompiling = EditorApplication.isCompiling;
                        AssertStartedOnlyTail(fixture.Root, persisted[i], 0);
                    }
                    var names = new[]
                    {
                        "asset.create_material", "gameobject.create",
                        "script.create", "package.add",
                    };
                    for (var i = 0; i < fixtures.Count; i++)
                    {
                        var report = Recover(fixtures[i]);
                        Assert.IsTrue(
                            report.ExclusionHeld,
                            "the " + s_groupKeys[i] +
                                " store's recovery pass could not take its exclusion");
                        AssertNeedsReview(
                            fixtures[i].Root, fixtures[i], persisted[i],
                            "a disturbed " + names[i] + " evidence row across " +
                            "a reload");
                    }

                    Assert.AreEqual(
                        "Standard",
                        AssetDatabase.LoadAssetAtPath<Material>(MaterialPath)
                            .shader.name,
                        "create_material: NeedsReview leaves the foreign artifact");
                    Assert.AreEqual(
                        0, CountRootObjects(CreateTargetName),
                        "gameobject.create: NeedsReview re-created nothing");
                    Assert.AreEqual(
                        1, CountRootObjects(CreateTamperedName),
                        "gameobject.create: NeedsReview deleted nothing");
                    Assert.AreEqual(
                        ProbeScriptSource(DamagedClassName),
                        File.ReadAllText(PhysicalPath(ScriptPath(ScriptClassName))),
                        "script.create: NeedsReview leaves the foreign script");
                    DisposeFixtures(fixtures);
                    Crumb("disturbed phase-2 complete: four NeedsReview verdicts");
                    return PhaseResult.Pass();
                }
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ── unity.package.add across the REAL registry ──────────────────

        // The real-UPM adoption case: the parked Run's own Execute issued
        // the real Client.Add, the crash window is truncated to the bare
        // step_started{step:0}, and the package's own compile produces
        // the domain reload the recovery crosses — the verdict is
        // Completed on the real three-channel agreement and the Run
        // finishes with exactly one install, no second add ever issued.
        [UnityTest]
        [Timeout(480000)]
        public IEnumerator PackageAdd_StartedOnlyAdoptedFinishesOnceAcrossRealUpmReload()
        {
            BeginTest();
            var before = PhaseGate("upm-adopt-p1", RealAdoptPhase1);
            Crumb("real-adopt phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            AssertPhase(before, "upm-adopt-p1");

            // The package registers while the runner still holds the
            // assembly-reload lock; the install's own compile can only
            // become a domain reload inside the sanctioned
            // WaitForDomainReload boundary, never at a plain frame — a
            // reload that interrupts a plain yield makes UTF restart the
            // test, and the re-entered phase-1 collides with the parked
            // Run's still-live lock (the PackageResolvedReloadTests
            // pattern). The release is marked in SessionState before it
            // yields, so a re-executed segment can never request it twice.
            var guard = 0;
            while (!PackageInstalled(PackageName) && guard++ < 120000)
            {
                yield return null;
            }
            Assert.IsTrue(PackageInstalled(PackageName),
                "the real Client.Add never resolved the probe package");
            if (ReloadRequestNeeded("upm-adopt-install"))
            {
                Crumb("boundary \"upm-adopt-install\" issuing " +
                    "RequestScriptReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                yield return WaitForEditorQuiet();
                EditorUtility.RequestScriptReload();
                // The bounded sanctioned crossing (T-W2.4-07): only a
                // reload proven pending is waited on, with a deadline —
                // never UTF's unbounded WaitForDomainReload.
                if (ReloadPending())
                {
                    yield return new DrainedScriptReload(
                        "upm-adopt-install", WaitTimeoutSec);
                }
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe package's compile did not produce a domain reload");
            NoteDomainReload("upm-adopt-install");

            var after = PhaseGate(
                "upm-adopt-fin",
                () => RealUpmFinishPhase(AdoptedUpmKey, expectAdopted: true));
            Crumb("real-adopt phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            AssertPhase(after, "upm-adopt-fin");
        }

        // The real-UPM re-execution case: the parked Run issued nothing
        // before the crash, so the recovering pass proves NotApplied,
        // re-executes the step once — the real Client.Add the second
        // attempt issues — and the finished Run leaves exactly one
        // install.
        [UnityTest]
        [Timeout(480000)]
        public IEnumerator PackageAdd_StartedOnlyNotAppliedReexecutesOnceAcrossRealUpmReload()
        {
            BeginTest();
            var before = PhaseGate("upm-reexec-p1", RealReexecutePhase1);
            Crumb("real-reexecute phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            AssertPhase(before, "upm-reexec-p1");

            // The marker-script compile the arm queued crosses at this
            // sanctioned boundary — its release is marked before it
            // yields, so a resumed segment never requests it twice.
            if (ReloadRequestNeeded("upm-reexec-mid"))
            {
                Crumb("boundary \"upm-reexec-mid\" issuing " +
                    "RequestScriptReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                EditorUtility.RequestScriptReload();
                // The bounded sanctioned crossing (T-W2.4-07): only a
                // reload proven pending is waited on, with a deadline —
                // never UTF's unbounded WaitForDomainReload.
                if (ReloadPending())
                {
                    yield return new DrainedScriptReload(
                        "upm-reexec-mid", WaitTimeoutSec);
                }
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");
            NoteDomainReload("upm-reexec-mid");

            // Recovery proves the add never landed and re-executes it:
            // the real Client.Add this phase issues resolves across the
            // reload boundary below.
            var resumed = PhaseGate("upm-reexec-p2", RealReexecutePhase2);
            Crumb("real-reexecute phase-2 ok=" + resumed.Ok + " failure=" + resumed.Failure);
            AssertPhase(resumed, "upm-reexec-p2");

            var guard = 0;
            while (!PackageInstalled(PackageName) && guard++ < 120000)
            {
                yield return null;
            }
            Assert.IsTrue(PackageInstalled(PackageName),
                "the re-executed real Client.Add never resolved the probe package");
            if (ReloadRequestNeeded("upm-reexec-install"))
            {
                Crumb("boundary \"upm-reexec-install\" issuing " +
                    "RequestScriptReload (sentinelAlive=" +
                    DomainSentinelAlive + ")");
                AssetDatabase.Refresh();
                yield return WaitForEditorQuiet();
                EditorUtility.RequestScriptReload();
                // The bounded sanctioned crossing (T-W2.4-07): only a
                // reload proven pending is waited on, with a deadline —
                // never UTF's unbounded WaitForDomainReload.
                if (ReloadPending())
                {
                    yield return new DrainedScriptReload(
                        "upm-reexec-install", WaitTimeoutSec);
                }
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe package's compile did not produce a domain reload");
            NoteDomainReload("upm-reexec-install");

            var after = PhaseGate(
                "upm-reexec-fin",
                () => RealUpmFinishPhase(ReexecuteUpmKey, expectAdopted: false));
            Crumb("real-reexecute phase-3 ok=" + after.Ok + " failure=" + after.Failure);
            AssertPhase(after, "upm-reexec-fin");
        }

        // Phase 1 of the real adoption case: a request-issuing Execute
        // parks the Run at step_waiting{step:0}; the journal is cut back
        // to step_started{step:0} while the real resolve is in flight —
        // the snapshot records the still-absent target, and the domain
        // sentinel marks the session the install's reload ends.
        private static PhaseResult RealAdoptPhase1()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                using (EmptyProtectedRuns())
                {
                    if (PackageInstalled(PackageName))
                    {
                        return PhaseResult.Fail(
                            "the probe package is already installed — the " +
                            "project manifest must not depend on it");
                    }
                    CaptureManifestBackup();
                    s_issueCalls = 0;
                    var fixture = GroupFixture(
                        fixtures, AdoptedUpmKey, RealWiring(issueRealAdd: true));
                    var runId = ParkStartedOnly(
                        fixture, UnityPackageAddOperation.OperationId, '2',
                        PackageAddInputs(), s_packageOut);
                    TruncateToStartedOnly(fixture, runId);
                    if (s_issueCalls != 1)
                    {
                        DisposeFixtures(fixtures);
                        return PhaseResult.Fail(
                            "the real Execute did not issue its Client.Add once");
                    }
                    WriteState(runId);
                    // The sentinel for the install's own reload boundary
                    // — armed once, through the persisted arm record.
                    ArmBoundary("upm-adopt-install");
                    return PhaseResult.Pass();
                }
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 1 of the real re-execution case: a requestless Execute —
        // the crash landed before the add was issued — the same
        // started-only journal, and a marker compile the reload rides on.
        private static PhaseResult RealReexecutePhase1()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                using (EmptyProtectedRuns())
                {
                    if (PackageInstalled(PackageName))
                    {
                        return PhaseResult.Fail(
                            "the probe package is already installed — the " +
                            "project manifest must not depend on it");
                    }
                    CaptureManifestBackup();
                    var fixture = GroupFixture(
                        fixtures, ReexecuteUpmKey, RealWiring(issueRealAdd: false));
                    var runId = ParkStartedOnly(
                        fixture, UnityPackageAddOperation.OperationId, '2',
                        PackageAddInputs(), s_packageOut);
                    TruncateToStartedOnly(fixture, runId);
                    WriteState(runId);
                    // The marker write plus its Refresh can end this
                    // domain before the body returns — mark the phase
                    // complete first.
                    MarkPhaseDone("upm-reexec-p1");
                    ArmReload("upm-reexec-mid");
                    return PhaseResult.Pass();
                }
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2 of the re-execution case: one domain later the real
        // three-channel absence is provable — NotApplied — and the
        // resumed step's one re-execution issues the real Client.Add.
        private static PhaseResult RealReexecutePhase2()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                using (EmptyProtectedRuns())
                {
                    var persisted = ReadState();
                    Assert.AreEqual(
                        1, persisted.Length, "the phase-1 state file lost the run id");
                    s_issueCalls = 0;
                    var fixture = new RunnerHostFixture(StoreRootFor(ReexecuteUpmKey));
                    fixtures.Add(fixture);
                    RegisterAll(fixture, RealWiring(issueRealAdd: true));
                    fixture.Editor.IsCompiling = EditorApplication.isCompiling;
                    AssertStartedOnlyTail(fixture.Root, persisted[0], 0);
                    var report = Recover(fixture);
                    Assert.IsTrue(
                        report.ExclusionHeld,
                        "the re-execution store's recovery pass could not " +
                        "take its exclusion");
                    // The re-execution's real Client.Add must have been
                    // issued by the recovery pass or the first tick —
                    // never twice.
                    Tick(fixture);
                    AssertReexecuted(
                        fixture.Root, fixture, persisted[0],
                        "a provably-unapplied package.add across a reload");
                    Assert.AreEqual(
                        1, s_issueCalls,
                        "the resumed step re-issued the real Client.Add " +
                        "exactly once");
                    ArmBoundary("upm-reexec-install");
                    return PhaseResult.Pass();
                }
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // The shared post-install phase of both real-UPM cases: a fresh
        // host on the same store re-adopts the parked Run — the adopted
        // case reads the fully-agreeing three-channel install as
        // Completed on its step_started tail; the re-executed case
        // re-attaches the resumed step's waiting record — then the hold
        // is released and the Run finishes. Exactly one manifest entry,
        // exactly one registered package, and the journal's step_started
        // count names the attempts.
        private static PhaseResult RealUpmFinishPhase(string key, bool expectAdopted)
        {
            var fixtures = new List<RunnerHostFixture>();
            var holds = new List<RunnerHostFixture.ControlledWaitableOperation>();
            try
            {
                using (EmptyProtectedRuns())
                {
                    var persisted = ReadState();
                    Assert.AreEqual(
                        1, persisted.Length, "the phase-1 state file lost the run id");
                    s_issueCalls = 0;
                    var fixture = new RunnerHostFixture(StoreRootFor(key));
                    fixtures.Add(fixture);
                    RegisterAll(fixture, RealWiring(issueRealAdd: true), holds);
                    fixture.Editor.IsCompiling = EditorApplication.isCompiling;
                    var runId = persisted[0];
                    if (expectAdopted)
                    {
                        // The parked step_started tail is the evidence the
                        // §F4 pass reads — assert it BEFORE Recover
                        // appends its own run_resumed/step records.
                        AssertStartedOnlyTail(fixture.Root, runId, 0);
                    }
                    var report = Recover(fixture);
                    Assert.IsTrue(
                        report.ExclusionHeld,
                        "the " + key +
                            " store's recovery pass could not take its exclusion");
                    if (expectAdopted)
                    {
                        AssertAdopted(
                            fixture.Root, fixture, runId,
                            "an intact real package.add evidence row across " +
                            "a reload");
                    }
                    else
                    {
                        // The re-executed step's waiting record survived
                        // the install's reload — recovery re-attaches it.
                        Assert.IsTrue(
                            RunnerHostFixture.JournalHasAll(
                                fixture.Root, runId, "\"type\":\"step_waiting\"",
                                "\"step\":0"),
                            "the re-executed step must park at its wait; " +
                            "journal: " + string.Join(
                                " | ", Lines(fixture.Root, runId)));
                        Assert.AreEqual(
                            2,
                            JournalCountAll(
                                fixture.Root, runId, "\"type\":\"step_started\"",
                                "\"step\":0"),
                            "the re-executed step ran exactly one second attempt");
                    }
                    FinishRun(fixture, runId, holds[0],
                        "the recovered real package.add Run");
                    Assert.AreEqual(
                        PackageVersion, ManifestDependency(PackageName),
                        "exactly one manifest dependency carries the resolved " +
                        "version — no duplicate add");
                    Assert.IsTrue(
                        PackageInstalled(PackageName),
                        "the resolved package is registered exactly once");
                    Assert.AreEqual(
                        0, s_issueCalls,
                        "recovery issued no further Client.Add");
                    return PhaseResult.Pass();
                }
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "finish phase threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Exactly-one for a file artifact: every on-disk file the named
        // probe folder carries with the artifact's own extension — a
        // duplicate creation under a second name shows up here.
        private static int CountArtifacts(string folder, string extension)
        {
            var disk = PhysicalPath(folder);
            return Directory.Exists(disk)
                ? Directory.GetFiles(disk, "*" + extension).Length
                : 0;
        }
    }

    // The loop guard's domain-load tap: [InitializeOnLoad] runs on
    // every domain the suite's reloads create, so the counters a
    // replayed body could clear are bumped from a point no test code
    // can skip (the GitPackageResumeReloadTests pattern).
    [InitializeOnLoad]
    internal static class At03ReloadGuardHook
    {
        static At03ReloadGuardHook() =>
            At03NoDuplicatesTests.OnDomainLoaded();
    }
}
