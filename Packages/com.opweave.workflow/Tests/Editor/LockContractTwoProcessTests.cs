#nullable enable

// T-W4.6-02 [Edit] tests — local:T-W4.6-02:lock-contract-six-windows — the
// SPEC §T.2 AT-F07 coordinator.lock / lock.json contract proven across REAL
// Unity Editor process boundaries on Windows (SPEC §E2 item 3, §T.4 L-14):
//
//   1. Two Editor processes on one Run Store: a live owner's mutating Run is
//      parked mid-flight while a second Editor's startup recovery, accepted
//      mutating Run and every direct acquisition probe are refused — zero
//      concurrent mutating executions, not just exclusion of a helper.
//   2. A live owner's lock is never taken (SPEC §E2 item 3): the contender
//      neither starts nor resumes a Run while the owner finishes its own Run
//      in its own process — the Run another Editor could not disturb.
//   3. Abnormal termination releases the OS lock: Process.Kill drops the
//      share-mode-0 handle, the next Editor's §F4 pass takes the exclusion
//      (ExclusionHeld), records itself in lock.json and resumes.
//   4. lock.json-only residue is recovered only after journal verification
//      (§E2 item 3, §F4): a residue naming a verifiable unfinished Run is
//      taken over and re-recorded for the new session; a residue naming a
//      Run with no journal is left in place and answered NeedsReview.
//   5. A crash during the lock.json update never double-executes: the
//      pre-update and post-update complete files both survive a real kill
//      inside CoordinatorRunLock.WriteState, and a residue that contradicts
//      the journals (terminal recorded Run + unfinished mutating Run) ends
//      NeedsReview with nothing re-executed.
//   6. A domain reload does not open a concurrency window (§E2, L-14): the
//      raw CreateFileW handle survives the Editor's real domain reload —
//      proven by denials from a second process spanning the reload — and
//      the abandoned Run resumes once under the next Editor.
//
// Every case asserts the ticket's zeros: no mutating Run ever ran
// concurrently, no step ever executed twice, and no acquisition the owner
// had not ended ever succeeded. The child Editors are real Unity.exe
// instances on clones of TestProject — process death, OS handle release,
// domain reload and lock.json atomicity are genuine, never simulated.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Persistence;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.6-02:lock-contract-six-windows</c>: the six
    /// coordinator.lock / lock.json contract cases of SPEC §E2 item 3 on
    /// Windows, driven by real Editor processes (SPEC §T.4 L-14).
    /// </summary>
    internal sealed class LockContractTwoProcessTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveLockContractProbe");

        private static string CloneADir => Path.Combine(ProbeDir, "clone-a");
        private static string CloneBDir => Path.Combine(ProbeDir, "clone-b");
        private static string JobsDir => Path.Combine(ProbeDir, "jobs");

        private const string Entry =
            "OpWeave.UnityAdapter.Tests.LockContractScenarioEntry.Run";

        // The first child launch builds the clone's whole Library; later
        // launches reuse it. The bounds stay generous on purpose — a hang
        // is a failure, not a flake to retry.
        private const int CheckpointTimeoutMs = 20 * 60 * 1000;
        private const int VerifyTimeoutMs = 20 * 60 * 1000;
        private const int ReloadTimeoutMs = 15 * 60 * 1000;
        private const int ChildStopMs = 30000;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private readonly EditorProcessHarness _harness = new EditorProcessHarness();
        private readonly List<EditorProcessHarness.EditorChild> _children =
            new List<EditorProcessHarness.EditorChild>();

        // SPEC §T.4 L-14 names this ticket the Windows leg: the exclusion it
        // proves is the share-mode-0 CreateFileW implementation. The macOS
        // flock leg is T-W4.6-05's scope, so on a non-Windows host the whole
        // fixture is skipped, not weakened.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Assert.Ignore(
                    "local:T-W4.6-02 is the Windows leg of L-14; " +
                    "the macOS lock contract is T-W4.6-05's scope.");
            }
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* leftover handles stay for the next run */ }
            }
            Directory.CreateDirectory(CloneADir);
            Directory.CreateDirectory(CloneBDir);
            EditorProcessHarness.CloneProject(ProjectRoot, CloneADir);
            EditorProcessHarness.CloneProject(ProjectRoot, CloneBDir);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var child in _children)
            {
                try
                {
                    if (child.Process.HasExited)
                    {
                        continue;
                    }
                    var stop = Path.Combine(child.JobDir, "child.stop");
                    try { File.WriteAllText(stop, "stop", s_utf8); }
                    catch (Exception) { /* best effort */ }
                    if (!child.Process.WaitForExit(ChildStopMs))
                    {
                        child.Process.Kill();
                        child.Process.WaitForExit(ChildStopMs);
                    }
                }
                catch (Exception)
                {
                    // Best effort: a leaked child dies with the test Editor.
                }
                finally
                {
                    child.Process.Dispose();
                }
            }
            _children.Clear();
        }

        // ── launch / protocol ───────────────────────────────────────────

        private EditorProcessHarness.EditorChild Launch(
            string projectDir, string jobDir, string scenario, string storeRoot,
            string exitMode, string tag,
            IReadOnlyDictionary<string, string>? extraEnvironment = null)
        {
            // A killed child leaves Temp/UnityLockfile behind; the OS knows
            // its writer is gone but a stale file can still stall a batch
            // open — remove it before relaunching the same clone.
            var lockFile = Path.Combine(projectDir, "Temp", "UnityLockfile");
            if (File.Exists(lockFile))
            {
                try { File.Delete(lockFile); }
                catch (Exception) { /* the child reports a lock it cannot clear */ }
            }
            var environment = new Dictionary<string, string>
            {
                [EditorProcessHarness.ScenarioEnvVar] = scenario,
                [EditorProcessHarness.JobDirEnvVar] = jobDir,
                [EditorProcessHarness.StoreEnvVar] = storeRoot,
                [EditorProcessHarness.ExitModeEnvVar] = exitMode,
            };
            if (extraEnvironment is not null)
            {
                foreach (var pair in extraEnvironment)
                {
                    environment[pair.Key] = pair.Value;
                }
            }
            var child = _harness.Launch(
                projectDir, Entry, Path.Combine(jobDir, "child-" + tag + ".log"),
                environment);
            child.JobDir = jobDir;
            _children.Add(child);
            return child;
        }

        private static string NewCaseJob(string caseName, string jobName)
        {
            var jobDir = Path.Combine(JobsDir, caseName + "-" + jobName);
            Directory.CreateDirectory(jobDir);
            return jobDir;
        }

        private static string CaseStore(string caseName) =>
            Path.Combine(JobsDir, caseName + "-store");

        private JObject WaitCheckpoint(
            string jobDir, EditorProcessHarness.EditorChild child)
        {
            var text = EditorProcessHarness.WaitForFileContent(
                Path.Combine(jobDir, "checkpoint.json"), CheckpointTimeoutMs);
            Assert.IsNotNull(
                text,
                "the arrange child never wrote checkpoint.json; log: " +
                child.LogPath + "; fatal: " + Fatal(jobDir) +
                "; output: " + EditorProcessHarness.ChildOutput(child));
            return JObject.Parse(text!);
        }

        private JObject WaitVerdict(
            string jobDir, EditorProcessHarness.EditorChild child)
        {
            Assert.IsTrue(
                EditorProcessHarness.WaitForExit(child, VerifyTimeoutMs),
                "the verify child did not exit; log: " + child.LogPath +
                "; output: " + EditorProcessHarness.ChildOutput(child));
            var text = EditorProcessHarness.WaitForFileContent(
                Path.Combine(jobDir, "verdict.json"), 30000);
            Assert.IsNotNull(
                text,
                "the verify child never wrote verdict.json; log: " +
                child.LogPath + "; fatal: " + Fatal(jobDir) +
                "; output: " + EditorProcessHarness.ChildOutput(child));
            return JObject.Parse(text!);
        }

        private void WaitSignal(
            string jobDir, string fileName,
            EditorProcessHarness.EditorChild child, int timeoutMs)
        {
            Assert.IsNotNull(
                EditorProcessHarness.WaitForFileContent(
                    Path.Combine(jobDir, fileName), timeoutMs),
                "the child never wrote " + fileName + "; log: " + child.LogPath +
                "; fatal: " + Fatal(jobDir) +
                "; output: " + EditorProcessHarness.ChildOutput(child));
        }

        private static string Fatal(string jobDir)
        {
            var path = Path.Combine(jobDir, "fatal.txt");
            return File.Exists(path)
                ? EditorProcessHarness.ReadAllTextShared(path)
                : "<none>";
        }

        private static string MarkPath(string jobDir) =>
            Path.Combine(jobDir, "step0.mark");

        private static int MarkExecutions(string jobDir)
        {
            Assert.IsTrue(File.Exists(MarkPath(jobDir)),
                "the step-0 mark file is missing");
            return EditorProcessHarness.ReadAllLinesShared(MarkPath(jobDir)).Length;
        }

        private static void StopChild(EditorProcessHarness.EditorChild child)
        {
            File.WriteAllText(
                Path.Combine(child.JobDir, "child.stop"), "stop", s_utf8);
            Assert.IsTrue(
                EditorProcessHarness.WaitForExit(child, ChildStopMs),
                "the child did not exit on child.stop; log: " + child.LogPath);
        }

        // ── contender attempt log ───────────────────────────────────────

        // One committed attempt-log record is "<startTicks> <endTicks>
        // accepted <runId>" or "<startTicks> <endTicks> denied <code>" —
        // the wall-clock UTC ticks the child captured immediately before
        // and after the whole AcceptRun call. ReadAllLinesShared has
        // already dropped any unterminated tail, so a record that does not
        // split cleanly is corruption, never a torn read.
        private static bool TryParseAttempt(
            string line, out long startTicks, out long endTicks,
            out string outcome, out string detail)
        {
            startTicks = 0;
            endTicks = 0;
            outcome = "";
            detail = "";
            var first = line.IndexOf(' ');
            var second = first < 0 ? -1 : line.IndexOf(' ', first + 1);
            if (first <= 0 || second <= first + 1 ||
                !long.TryParse(
                    line.Substring(0, first),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out startTicks) ||
                !long.TryParse(
                    line.Substring(first + 1, second - first - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out endTicks))
            {
                return false;
            }
            var rest = line.Substring(second + 1);
            var third = rest.IndexOf(' ');
            outcome = third < 0 ? rest : rest.Substring(0, third);
            detail = third < 0 ? "" : rest.Substring(third + 1);
            return outcome.Length != 0;
        }

        // The deterministic half of the case-6 reload window: polls the
        // contender's append-only attempt log until a committed record's
        // START ticks — captured in the child immediately before
        // AcceptRun runs — pass the owner's reload-begin mark, which the
        // owner's beforeAssemblyReload handler wrote inside the teardown;
        // only then is the reload released to finish. An AcceptRun that
        // began after the begin mark while the teardown still parks has
        // its whole call inside the domain reload itself — the record
        // alone can never be stamped after the mark for an attempt that
        // ran before it (SPEC §E2 test 3 case 6).
        private void WaitAcceptAttemptAfter(
            string contenderJob, long afterTicks,
            EditorProcessHarness.EditorChild child)
        {
            var path = Path.Combine(contenderJob, "accept-attempts.log");
            var deadline = DateTime.UtcNow.AddMilliseconds(CheckpointTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    foreach (var line in
                             EditorProcessHarness.ReadAllLinesShared(path))
                    {
                        if (TryParseAttempt(
                                line, out var startTicks, out _, out _,
                                out _) &&
                            startTicks > afterTicks)
                        {
                            return;
                        }
                    }
                }
                System.Threading.Thread.Sleep(150);
            }
            Assert.Fail(
                "the contender never recorded a mutating-accept attempt " +
                "whose AcceptRun began after the reload-begin mark; " +
                "log: " + child.LogPath +
                "; fatal: " + Fatal(contenderJob) + "; output: " +
                EditorProcessHarness.ChildOutput(child));
        }

        // ── lock / residue inspection ───────────────────────────────────

        // The parent's own OS acquisition probe — the same
        // CoordinatorRunLock.TryAcquire the second Editor's recovery and
        // TryStartNext perform. While the owner lives every acquisition must
        // answer OW-RUN-002 (SPEC §E2 item 3: an unobtainable lock means a
        // live owner).
        private static void AssertLockDenied(string storeRoot, string because)
        {
            var acquired = CoordinatorRunLock.TryAcquire(storeRoot);
            if (acquired.Ok && acquired.Value is { } held)
            {
                held.Dispose();
                Assert.Fail(
                    because + ": the exclusion was acquired while a live " +
                    "owner holds it — that acquisition is the concurrent " +
                    "execution the contract forbids.");
            }
            Assert.IsTrue(
                DiagnosticsContain(acquired.Diagnostics, "OW-RUN-002"),
                because + ": a denied acquisition answers OW-RUN-002; got " +
                DescribeCodes(acquired.Diagnostics));
        }

        // lock.json as raw file bytes — readable while another process holds
        // the exclusion (the share-mode-0 handle covers coordinator.lock,
        // not lock.json). Empty or missing reads as null.
        private static JObject? ResidueDocument(string storeRoot)
        {
            var path = Path.Combine(storeRoot, CoordinatorRunLock.StateFileName);
            if (!File.Exists(path))
            {
                return null;
            }
            var text = EditorProcessHarness.ReadAllTextShared(path);
            return text.Length == 0 ? null : JObject.Parse(text);
        }

        private static void AssertResidue(
            string storeRoot, string runId, string? sessionId, string because)
        {
            var residue = ResidueDocument(storeRoot);
            Assert.IsNotNull(
                residue,
                because + ": lock.json holds no recorded owner");
            Assert.AreEqual(
                runId, residue!["runId"]?.ToString(),
                because + ": the recorded runId");
            if (sessionId is not null)
            {
                Assert.AreEqual(
                    sessionId, residue["editorSessionId"]?.ToString(),
                    because + ": the recorded editorSessionId");
            }
        }

        private static bool DiagnosticsContain(
            IReadOnlyList<Diagnostic> diagnostics, string code)
        {
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Code == code)
                {
                    return true;
                }
            }
            return false;
        }

        private static string DescribeCodes(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return parts.Count == 0 ? "<none>" : string.Join("; ", parts);
        }

        // ── journal / verdict helpers ───────────────────────────────────

        private static string[] JournalLines(string storeRoot, string runId) =>
            RunnerHostFixture.JournalLines(storeRoot, runId);

        private static bool JournalHas(
            string storeRoot, string runId, string fragment) =>
            RunnerHostFixture.JournalHas(storeRoot, runId, fragment);

        private static int CountLines(
            string storeRoot, string runId, params string[] fragments)
        {
            var count = 0;
            foreach (var line in JournalLines(storeRoot, runId))
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

        private static string VerdictField(JObject verdict, string name) =>
            verdict[name]?.ToString() ?? "";

        // The contract shape the contender must show for the whole window it
        // contended: recovery refused (no exclusion, OW-RUN-002), its mutating
        // Run accept refused OW-RUN-002, every direct acquisition probe denied
        // with OW-RUN-002, and its mark artifact never materialized — zero
        // concurrent mutating executions.
        private static void AssertContenderDenied(
            JObject verdict, string jobDir, string storeRoot, string because)
        {
            // SPEC §E2 item 3 (a): "取得できなければ所有者が生存しているとみな
            // し、新しい Run は OW-RUN-002 で拒否し、lock.json が指す Run を
            // 再開・変更しない" — the pass runs without the exclusion and the
            // refusal is carried by the report's diagnostics.
            Assert.AreEqual(
                "False", VerdictField(verdict, "exclusionHeld"),
                because + ": the contender's startup recovery must run " +
                "without the exclusion; verdict: " + verdict);
            Assert.IsTrue(
                VerdictField(verdict, "recoveryDiagnostics").Contains("OW-RUN-002"),
                because + ": the startup refusal is OW-RUN-002; verdict: " + verdict);
            // SPEC §E2 item 3 (a)'s answer to a NEW mutating Run while the
            // live owner's exclusion cannot be obtained is the OW-RUN-002
            // refusal itself — no Queued Run is created for the start gate
            // to pick up later.
            Assert.AreEqual(
                "False", VerdictField(verdict, "acceptOk"),
                because + ": a mutating Run accept is refused while the " +
                "live owner holds; verdict: " + verdict);
            Assert.IsTrue(
                VerdictField(verdict, "acceptDiagnostics").Contains("OW-RUN-002"),
                because + ": the accept refusal is OW-RUN-002; verdict: " + verdict);
            Assert.AreEqual(
                "", VerdictField(verdict, "runId"),
                because + ": no Run was accepted — nothing exists to start; " +
                "verdict: " + verdict);
            Assert.AreEqual(
                "", VerdictField(verdict, "runState"),
                because + ": no accepted Run means no Run state; verdict: " +
                verdict);
            Assert.AreEqual(
                "False", VerdictField(verdict, "acquiredAny"),
                because + ": no acquisition probe may succeed against a " +
                "live owner; verdict: " + verdict);
            var attempts = int.Parse(VerdictField(verdict, "attempts"));
            var denied = int.Parse(VerdictField(verdict, "denied"));
            Assert.GreaterOrEqual(
                attempts, 3,
                because + ": the contender must probe more than once; " +
                "verdict: " + verdict);
            Assert.AreEqual(
                attempts, denied,
                because + ": every probe against the live owner is denied; " +
                "verdict: " + verdict);
            Assert.AreEqual(
                "OW-RUN-002", VerdictField(verdict, "firstDenialCode"),
                because + ": the denial code is OW-RUN-002; verdict: " + verdict);
            Assert.AreEqual(
                "False", VerdictField(verdict, "markExists"),
                because + ": the contender's step never executed; verdict: " +
                verdict);
            Assert.IsFalse(
                File.Exists(MarkPath(jobDir)),
                because + ": the contender's mark artifact must not exist");
            var contenderRun = VerdictField(verdict, "runId");
            if (contenderRun.Length != 0)
            {
                Assert.IsFalse(
                    JournalHas(storeRoot, contenderRun, "\"type\":\"run_started\""),
                    because + ": the contender's journal must hold no " +
                    "run_started — its Run never became mutating");
            }
        }

        // The resumed-and-finished shape (shared with the AT-F07 suite):
        // the decided resume is journaled run_resumed (§F4 item 6), the
        // parked step completes through Poll once, and the Run ends with
        // real evidence — every step record exactly once, one mark line.
        private static void AssertResumedAndFinished(
            string jobDir, JObject verdict, string storeRoot, string runId,
            string because)
        {
            Assert.AreEqual(
                "Succeeded", VerdictField(verdict, "state"),
                because + ": the resumed Run must finish; verdict: " + verdict +
                "; journal: " + string.Join(" | ", JournalLines(storeRoot, runId)));
            Assert.IsTrue(
                JournalHas(storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a decided resume journals run_resumed first " +
                "(§F4 item 6)");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"step_started\"", "\"step\":0"),
                because + ": step 0 must have exactly one start — never " +
                "re-executed");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                because + ": step 0 must have exactly one success");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":1"),
                because + ": the resumed step completes through Poll exactly " +
                "once");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"run_finished\"", "\"succeeded\""),
                because + ": one terminal record, carrying the decided outcome");
            Assert.AreEqual(
                1, MarkExecutions(jobDir),
                because + ": the step-0 artifact is written exactly once — " +
                "zero duplicate artifacts");
        }

        // The parked-and-untouched shape: the owner's journal still ends at
        // the durable step_waiting, its one completed step recorded exactly
        // once, no resume record anywhere — the Run a contender was never
        // allowed to disturb.
        private static void AssertOwnerParkedUntouched(
            string storeRoot, string runId, string because)
        {
            var lines = JournalLines(storeRoot, runId);
            Assert.IsTrue(
                lines.Length > 0 &&
                lines[lines.Length - 1].Contains("\"type\":\"step_waiting\""),
                because + ": the owner's journal must still end at the " +
                "durable step_waiting; journal: " + string.Join(" | ", lines));
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                because + ": the owner's step 0 executed exactly once");
            Assert.AreEqual(
                0, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":1"),
                because + ": the parked step must not have completed");
            Assert.IsFalse(
                JournalHas(storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a live owner's Run is never resumed elsewhere");
        }

        // ── SPEC §E2 item 3 contract cases ──────────────────────────────

        // Case 1 — two Editor processes operating on one project never run
        // mutating Runs concurrently (SPEC §T.2 AT-F07 lock case 1, §E2
        // items 2 and 3). A live owner holds a mutating Run mid-flight; a
        // real second Editor's recovery, accepted Run and repeated OS probes
        // are all refused while the owner is undisturbed.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void TwoEditors_MutatingRunsNeverRunConcurrently()
        {
            var ownerJob = NewCaseJob("case1-concurrent", "owner");
            var contenderJob = NewCaseJob("case1-concurrent", "contender");
            var storeRoot = CaseStore("case1-concurrent");

            var owner = Launch(
                CloneADir, ownerJob, "hold-park", storeRoot, "block", "owner");
            var checkpoint = WaitCheckpoint(ownerJob, owner);
            var runId = checkpoint["runId"]!.ToString();
            var sessionId = checkpoint["sessionId"]!.ToString();

            // This Editor — a third process — cannot acquire either.
            AssertLockDenied(storeRoot, "the owner holds a mutating Run");

            var contender = Launch(
                CloneBDir, contenderJob, "contend", storeRoot, "block",
                "contender");
            WaitSignal(
                contenderJob, "contender-ready.signal", contender,
                CheckpointTimeoutMs);

            // A bounded contention window: the contender probes and Ticks
            // while the owner is parked; this process's probe stays denied
            // for the whole window too.
            var windowEnd = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < windowEnd)
            {
                AssertLockDenied(storeRoot, "inside the contention window");
                System.Threading.Thread.Sleep(500);
            }
            File.WriteAllText(
                Path.Combine(contenderJob, "contend.stop"), "stop", s_utf8);
            var verdict = WaitVerdict(contenderJob, contender);
            AssertContenderDenied(
                verdict, contenderJob, storeRoot, "case 1");
            AssertOwnerParkedUntouched(storeRoot, runId, "case 1");
            Assert.AreEqual(
                1, MarkExecutions(ownerJob),
                "case 1: the owner's step-0 artifact is written exactly once");
            Assert.IsTrue(
                File.Exists(Path.Combine(storeRoot, CoordinatorRunLock.LockFileName)),
                "case 1: coordinator.lock is never replaced or deleted while " +
                "held — the file stays for the next owner");

            StopChild(owner);
            // With the owner process ended the exclusion is free and the
            // recorded owner residue it left is exactly what recovery reads.
            AssertResidue(storeRoot, runId, sessionId,
                "case 1: the dead owner's residue survives for recovery");
        }

        // Case 2 — a live owner's lock is never taken, and its Run is
        // neither started nor resumed elsewhere (SPEC §T.2 AT-F07 lock
        // case 2). The contender fails on every front while the owner, in
        // its own live process, drives the same Run to a journaled finish —
        // then lock.json is emptied before the OS handle drops.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void LiveOwner_LockNeverTaken_RunNeverStartedOrResumedElsewhere()
        {
            var ownerJob = NewCaseJob("case2-liveowner", "owner");
            var contenderJob = NewCaseJob("case2-liveowner", "contender");
            var storeRoot = CaseStore("case2-liveowner");

            var owner = Launch(
                CloneADir, ownerJob, "hold-park", storeRoot, "block-tick",
                "owner");
            var checkpoint = WaitCheckpoint(ownerJob, owner);
            var runId = checkpoint["runId"]!.ToString();

            var contender = Launch(
                CloneBDir, contenderJob, "contend", storeRoot, "block",
                "contender");
            WaitSignal(
                contenderJob, "contender-ready.signal", contender,
                CheckpointTimeoutMs);

            // The owner is parked and live: the contender's attempts and
            // this process's probe are all denied.
            AssertLockDenied(storeRoot, "the live owner holds the exclusion");
            System.Threading.Thread.Sleep(4000);
            AssertLockDenied(storeRoot, "the live owner still holds it");

            // SPEC §E2 test 3 (2): "所有者のプロセスが生存している間は、別プロ
            // セスが排他を取れず、Run を開始・再開しない" — the denials bind
            // only while the owner lives, so the contender must be collected
            // inside that window: once the owner's Run ends the freed
            // exclusion is legitimately acquirable and any later probe tells
            // the test nothing about the live-owner contract.
            File.WriteAllText(
                Path.Combine(contenderJob, "contend.stop"), "stop", s_utf8);
            var verdict = WaitVerdict(contenderJob, contender);
            AssertContenderDenied(verdict, contenderJob, storeRoot, "case 2");
            AssertOwnerParkedUntouched(storeRoot, runId, "case 2");

            // Release the owner's own gate afterwards: the live owner
            // finishes its own Run in its own process — the Run the
            // contender could not take.
            File.WriteAllText(
                Path.Combine(ownerJob, "gate.release"), "go", s_utf8);
            WaitSignal(ownerJob, "finished.signal", owner, CheckpointTimeoutMs);

            // The owner's own journal carries the whole execution: one
            // start and one success per step, one terminal record — the Run
            // no other Editor touched.
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"run_started\""),
                "case 2: the owner journaled exactly one run_started");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                "case 2: step 0 succeeded exactly once, in the owner");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":1"),
                "case 2: the parked step completed in the owner exactly once");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"run_finished\"", "\"succeeded\""),
                "case 2: the owner journaled the terminal record");
            Assert.IsFalse(
                JournalHas(storeRoot, runId, "\"type\":\"run_resumed\""),
                "case 2: a live owner's Run is never resumed elsewhere");
            Assert.AreEqual(
                1, MarkExecutions(ownerJob),
                "case 2: the mark artifact is written exactly once");

            // SPEC §E2 item 3's orderly end: lock.json was emptied before
            // the OS handle dropped — the exclusion is free and the state
            // file reads empty.
            var acquired = CoordinatorRunLock.TryAcquire(storeRoot);
            Assert.IsTrue(
                acquired.Ok && acquired.Value is { },
                "case 2: the exclusion is free once the owner's Run ended");
            var recorded = acquired.Value!.ReadState();
            Assert.IsTrue(
                recorded.Ok && recorded.Value is null,
                "case 2: lock.json was emptied before the handle dropped");
            acquired.Value!.Dispose();

            StopChild(owner);
            Assert.IsTrue(
                File.Exists(Path.Combine(storeRoot, CoordinatorRunLock.LockFileName)),
                "case 2: coordinator.lock is left in place after release");
        }

        // Case 3 — abnormal Editor termination releases the OS lock and the
        // next Editor acquires it (SPEC §T.2 AT-F07 lock case 3, §E2 item
        // 3's restart rule). Process.Kill gives no cleanup: the share-mode-0
        // handle dies with the process, the residue survives for the §F4
        // pass, and the successor's takeover re-records lock.json for its
        // own session.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void AbnormalTermination_OsReleasesLock_NextEditorAcquires()
        {
            var ownerJob = NewCaseJob("case3-termination", "owner");
            var storeRoot = CaseStore("case3-termination");

            var owner = Launch(
                CloneADir, ownerJob, "hold-park", storeRoot, "block", "owner");
            var checkpoint = WaitCheckpoint(ownerJob, owner);
            var runId = checkpoint["runId"]!.ToString();
            var sessionId = checkpoint["sessionId"]!.ToString();

            EditorProcessHarness.KillAndWait(owner, ChildStopMs);

            // The OS released the exclusion with the dead process — this
            // Editor can already take it, and it finds the dead owner's
            // complete residue inside.
            var acquired = CoordinatorRunLock.TryAcquire(storeRoot);
            Assert.IsTrue(
                acquired.Ok && acquired.Value is { },
                "case 3: the OS must release the dead owner's handle — " +
                "acquisition after Process.Kill is the contract");
            var recorded = acquired.Value!.ReadState();
            Assert.IsTrue(
                recorded.Ok && recorded.Value is { } residue &&
                residue.RunId == runId && residue.EditorSessionId == sessionId,
                "case 3: the abandoned lock.json names the dead owner; " +
                "state: " + (recorded.Value?.ToString() ?? "<none>"));
            acquired.Value!.Dispose();
            Assert.IsTrue(
                File.Exists(Path.Combine(storeRoot, CoordinatorRunLock.LockFileName)),
                "case 3: coordinator.lock itself is never deleted");

            // The next Editor's real §F4 pass: it takes the exclusion, the
            // journal verification decides the resume, lock.json is
            // re-recorded for the new session, and the Run finishes.
            var verify = Launch(
                CloneADir, ownerJob, "verify", storeRoot, "block", "verify");
            var verdict = WaitVerdict(ownerJob, verify);
            Assert.AreEqual(
                "True", VerdictField(verdict, "exclusionHeld"),
                "case 3: the successor's recovery ran inside the exclusion " +
                "the OS freed; verdict: " + verdict);
            Assert.AreEqual(
                "Resumed", VerdictField(verdict, "outcome"),
                "case 3: the journal-verified takeover resumes the Run; " +
                "verdict: " + verdict);
            AssertResumedAndFinished(ownerJob, verdict, storeRoot, runId, "case 3");

            var postRecovery = VerdictField(verdict, "lockJsonPostRecovery");
            Assert.IsTrue(
                postRecovery.Contains(runId),
                "case 3: the takeover re-recorded lock.json for the resumed " +
                "Run; lock.json: " + postRecovery);
            Assert.IsTrue(
                postRecovery.Contains(VerdictField(verdict, "sessionId")),
                "case 3: the re-recording names THIS Editor's session; " +
                "lock.json: " + postRecovery);
        }

        // Case 4 — lock.json-only residue is recovered only after journal
        // verification (SPEC §T.2 AT-F07 lock case 4, §E2 item 3, §F4's
        // "lock.json だけが残っている" rule). A residue whose journal verifies
        // is taken over; a residue naming a Run with no verifiable journal
        // is left in place, answered NeedsReview, and never resumed.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void LockJsonResidue_JournalVerificationDecides_ResumeOrNeedsReview()
        {
            // ── 4a: residue + verifiable journal → takeover resume ──
            var ownerJob = NewCaseJob("case4-residue", "owner");
            var verifiedStore = CaseStore("case4-residue");
            var owner = Launch(
                CloneADir, ownerJob, "hold-park", verifiedStore, "block",
                "owner");
            var checkpoint = WaitCheckpoint(ownerJob, owner);
            var runId = checkpoint["runId"]!.ToString();
            EditorProcessHarness.KillAndWait(owner, ChildStopMs);

            var verify = Launch(
                CloneADir, ownerJob, "verify", verifiedStore, "block", "verify");
            var verdict = WaitVerdict(ownerJob, verify);
            Assert.AreEqual(
                "Resumed", VerdictField(verdict, "outcome"),
                "case 4a: a residue whose journal verifies is taken over; " +
                "verdict: " + verdict);
            AssertResumedAndFinished(
                ownerJob, verdict, verifiedStore, runId, "case 4a");
            var postRecovery = VerdictField(verdict, "lockJsonPostRecovery");
            Assert.IsTrue(
                postRecovery.Contains(VerdictField(verdict, "sessionId")),
                "case 4a: the takeover re-recorded lock.json for the new " +
                "session; lock.json: " + postRecovery);

            // ── 4b: residue naming a Run with no journal → NeedsReview ──
            var missingJob = NewCaseJob("case4-missing", "owner");
            var missingStore = CaseStore("case4-missing");
            var owner2 = Launch(
                CloneADir, missingJob, "hold-park", missingStore, "block",
                "owner2");
            var checkpoint2 = WaitCheckpoint(missingJob, owner2);
            var missingRun = checkpoint2["runId"]!.ToString();
            var missingSession = checkpoint2["sessionId"]!.ToString();
            EditorProcessHarness.KillAndWait(owner2, ChildStopMs);

            // The lock.json-only residue: the recorded Run's whole journal
            // directory is gone — nothing can verify it.
            Directory.Delete(
                Path.Combine(missingStore, "Runs", missingRun), recursive: true);
            Assert.IsFalse(
                Directory.Exists(Path.Combine(missingStore, "Runs", missingRun)),
                "case 4b: the recorded Run has no journal to verify");

            var verify2 = Launch(
                CloneADir, missingJob, "verify", missingStore, "block",
                "verify2");
            var verdict2 = WaitVerdict(missingJob, verify2);
            Assert.AreEqual(
                "True", VerdictField(verdict2, "exclusionHeld"),
                "case 4b: the pass ran inside the exclusion — the residue " +
                "fails journal verification, not acquisition; verdict: " +
                verdict2);
            // SPEC §E2 Editor再起動後 test (4): "lock.json だけが残った場合も、
            // ジャーナルを検証してから復旧し、検証できなければ NeedsReview" —
            // NeedsReview and no re-execution are the contract; the SPEC does
            // not prescribe which diagnostics channel must carry which code.
            Assert.AreEqual(
                "NeedsReview", VerdictField(verdict2, "outcome"),
                "case 4b: a residue with no verifiable journal is answered " +
                "NeedsReview, never resumed; verdict: " + verdict2);
            Assert.AreEqual(
                missingRun, VerdictField(verdict2, "residueRunId"),
                "case 4b: the residue the pass arbitrated on is the Run the " +
                "case created — the store no longer holds it; verdict: " +
                verdict2);
            Assert.IsTrue(
                VerdictField(verdict2, "loadDiagnostics").Contains("OW-RUN-010"),
                "case 4b: the created Run's whole record is gone — the " +
                "lock.json-only shape this case is defined by; verdict: " +
                verdict2);
            AssertResidue(
                missingStore, missingRun, missingSession,
                "case 4b: the unverifiable residue is left exactly as found — " +
                "it is what keeps new mutating Runs failing closed");
            Assert.AreEqual(
                1, MarkExecutions(missingJob),
                "case 4b: the step-0 artifact is written exactly once — " +
                "nothing was re-executed");
        }

        // Case 5 — a crash during the lock.json update never double-executes
        // (SPEC §T.2 AT-F07 lock case 5, §E2 item 3's "更新前か更新後" rule).
        // A residue whose recorded Run is terminal while another Run is
        // unfinished mutating is the contradiction the pass must fail closed
        // on; a kill inside the real WriteState leaves either the old or the
        // new complete file, and either way recovery resumes once.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void CrashDuringLockJsonUpdate_NeverDoubleExecutes()
        {
            // ── 5a: contradictory residue → NeedsReview, untouched ──
            var caseA = "case5a-contradiction";
            var finishedJob = NewCaseJob(caseA, "finished");
            var parkedJob = NewCaseJob(caseA, "parked");
            var storeA = CaseStore(caseA);

            // A Run that reaches a journaled Succeeded in a live process.
            var finisher = Launch(
                CloneADir, finishedJob, "finish-run", storeA, "block",
                "finisher");
            var finishedCheckpoint = WaitCheckpoint(finishedJob, finisher);
            var finishedRun = finishedCheckpoint["runId"]!.ToString();
            Assert.IsTrue(
                EditorProcessHarness.WaitForExit(finisher, VerifyTimeoutMs),
                "case 5a: the finish-run child did not exit; log: " +
                finisher.LogPath + "; fatal: " + Fatal(finishedJob));

            // A second Run parked mid-flight over the same store — then its
            // owner dies and the lock.json it would have re-recorded is left
            // naming the OLD run: the pre-update complete content a crash
            // during the update leaves.
            var parked = Launch(
                CloneADir, parkedJob, "hold-park", storeA, "block", "parked");
            var parkedCheckpoint = WaitCheckpoint(parkedJob, parked);
            var parkedRun = parkedCheckpoint["runId"]!.ToString();
            EditorProcessHarness.KillAndWait(parked, ChildStopMs);
            EditorProcessHarness.AtomicWriteFile(
                Path.Combine(storeA, CoordinatorRunLock.StateFileName),
                CanonicalJson.Serialize(new JObject
                {
                    ["runId"] = finishedRun,
                    ["editorSessionId"] =
                        finishedCheckpoint["sessionId"]!.ToString(),
                    ["acquiredAt"] = UtcTimestamp.Now(),
                }));

            var verifyA = Launch(
                CloneADir, parkedJob, "verify", storeA, "block", "verifya");
            var verdictA = WaitVerdict(parkedJob, verifyA);
            // SPEC §E2 Editor再起動後: "ジャーナルの整合性を確認できなければ
            // NeedsReview にし、自動再開しない（F0）" — NeedsReview plus no
            // re-execution is the contract; the SPEC does not prescribe that
            // this scenario surfaces OW-STORE-002 in any particular channel.
            Assert.AreEqual(
                "NeedsReview", VerdictField(verdictA, "outcome"),
                "case 5a: a residue whose recorded Run is terminal while " +
                "another Run is unfinished mutating contradicts the " +
                "journals — NeedsReview, never a resume; verdict: " + verdictA);
            Assert.AreEqual(
                finishedRun, VerdictField(verdictA, "residueRunId"),
                "case 5a: the residue names the TERMINAL Run — the " +
                "contradiction the pass must fail closed on; verdict: " +
                verdictA);
            Assert.IsFalse(
                JournalHas(storeA, parkedRun, "\"type\":\"run_resumed\""),
                "case 5a: the contradicted Run is never resumed");
            Assert.AreEqual(
                0, CountLines(storeA, parkedRun, "\"type\":\"step_succeeded\"", "\"step\":1"),
                "case 5a: the parked Run's held step never executed again — " +
                "no double execution");
            Assert.AreEqual(
                1, MarkExecutions(parkedJob),
                "case 5a: the mark artifact is written exactly once");
            AssertResidue(
                storeA, finishedRun, null,
                "case 5a: the contradictory residue is never overwritten — " +
                "it stays for the human decision");

            // ── 5b: kills inside and after the real WriteState ──
            var caseB = "case5b-updatekill";
            var updateJob = NewCaseJob(caseB, "owner");
            var storeB = CaseStore(caseB);

            var owner = Launch(
                CloneADir, updateJob, "hold-park", storeB, "block", "owner");
            var checkpointB = WaitCheckpoint(updateJob, owner);
            var runB = checkpointB["runId"]!.ToString();
            var sessionB = checkpointB["sessionId"]!.ToString();
            EditorProcessHarness.KillAndWait(owner, ChildStopMs);

            // Killed inside the real WriteState critical section, before the
            // atomic replace lands: the PRE-update complete lock.json is
            // what the crash leaves — exactly the old file.
            var pre = Launch(
                CloneADir, updateJob, "update-lock", storeB, "block", "upre",
                new Dictionary<string, string>
                {
                    [LockContractScenarioEntry.UpdateModeEnvVar] = "pre",
                });
            WaitSignal(updateJob, "in-update.signal", pre, CheckpointTimeoutMs);
            AssertResidue(
                storeB, runB, sessionB,
                "case 5b: the pre-update complete lock.json is still there " +
                "while the update is in flight");
            EditorProcessHarness.KillAndWait(pre, ChildStopMs);
            AssertResidue(
                storeB, runB, sessionB,
                "case 5b: a kill inside the update leaves the OLD complete " +
                "file — never a torn lock.json");

            // Killed after the write returned while the exclusion is still
            // held: the POST-update complete lock.json is what survives.
            var post = Launch(
                CloneADir, updateJob, "update-lock", storeB, "block", "upost",
                new Dictionary<string, string>
                {
                    [LockContractScenarioEntry.UpdateModeEnvVar] = "post",
                });
            var newSession = EditorProcessHarness.WaitForFileContent(
                Path.Combine(updateJob, "updated.signal"), CheckpointTimeoutMs);
            Assert.IsNotNull(
                newSession,
                "case 5b: the post-update child never completed WriteState; " +
                "log: " + post.LogPath + "; fatal: " + Fatal(updateJob));
            AssertResidue(
                storeB, runB, newSession!.Trim(),
                "case 5b: the post-update complete lock.json is what the " +
                "live writer recorded");
            AssertLockDenied(
                storeB,
                "case 5b: the update writer still holds the exclusion");
            EditorProcessHarness.KillAndWait(post, ChildStopMs);
            AssertResidue(
                storeB, runB, newSession.Trim(),
                "case 5b: a kill after the update leaves the NEW complete " +
                "file — never a torn lock.json");

            // Recovery over the post-update residue: the journal names the
            // same unfinished mutating Run, the takeover resumes it, and
            // every step still executes exactly once.
            var verifyB = Launch(
                CloneADir, updateJob, "verify", storeB, "block", "verifyb");
            var verdictB = WaitVerdict(updateJob, verifyB);
            Assert.AreEqual(
                "Resumed", VerdictField(verdictB, "outcome"),
                "case 5b: recovery over the post-update residue resumes " +
                "once; verdict: " + verdictB);
            AssertResumedAndFinished(
                updateJob, verdictB, storeB, runB, "case 5b");
        }

        // Case 6 — another process starting a Run during the owner's domain
        // reload creates no concurrency (SPEC §T.2 AT-F07 lock case 6, §E2
        // item 3, §T.4 L-14's "リロードでは解放しない"). The owner's child
        // holds the OS exclusion through a REAL domain reload: every
        // acquisition probe from the second process — before and after the
        // new domain reported itself — is denied, the contender attempts to
        // ACCEPT a mutating Run on every poll with at least one attempt's
        // whole AcceptRun call durably bracketed inside the reload —
        // between the owner's beforeAssemblyReload begin mark and the new
        // domain's first durable act — every attempt refused OW-RUN-002,
        // and the abandoned Run resumes once under the next Editor.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void RunStartDuringDomainReload_NeverConcurrent()
        {
            var ownerJob = NewCaseJob("case6-reload", "owner");
            var contenderJob = NewCaseJob("case6-reload", "contender");
            var storeRoot = CaseStore("case6-reload");

            var owner = Launch(
                CloneADir, ownerJob, "hold-park", storeRoot, "reload",
                "owner");
            var checkpoint = WaitCheckpoint(ownerJob, owner);
            var runId = checkpoint["runId"]!.ToString();
            var sessionId = checkpoint["sessionId"]!.ToString();

            var contender = Launch(
                CloneBDir, contenderJob, "contend", storeRoot, "block",
                "contender",
                new Dictionary<string, string>
                {
                    [LockContractScenarioEntry.PeerJobEnvVar] = ownerJob,
                });
            WaitSignal(
                contenderJob, "contender-ready.signal", contender,
                CheckpointTimeoutMs);

            // Arm the reload: the owner compiles a marker script, its whole
            // managed domain is destroyed and rebuilt — while the raw OS
            // handle stays open. post-reload.signal is the new domain
            // reporting itself; the process never exited.
            WaitSignal(ownerJob, "reload.armed", owner, CheckpointTimeoutMs);
            File.WriteAllText(
                Path.Combine(ownerJob, "reload.go"), "go", s_utf8);

            // The measured window is the domain reload itself: the owner's
            // beforeAssemblyReload handler — already inside the teardown —
            // records the wall-clock begin mark and parks on a test-side
            // gate. The gate is released only once the contender's attempt
            // log holds a record whose AcceptRun STARTED strictly after the
            // mark — and while the handler still parks the reload cannot
            // complete, so that attempt's end lands before the end mark
            // too: SPEC §E2 test 3 case 6's "attempt to start a Run during
            // the domain reload" is then guaranteed to have run inside the
            // reload, never in the gap before it.
            var beginText = EditorProcessHarness.WaitForFileContent(
                Path.Combine(ownerJob, "reload-begin.signal"),
                CheckpointTimeoutMs);
            Assert.IsNotNull(
                beginText,
                "case 6: the owner never wrote reload-begin.signal; log: " +
                owner.LogPath + "; fatal: " + Fatal(ownerJob) +
                "; output: " + EditorProcessHarness.ChildOutput(owner));
            var reloadBegin = long.Parse(
                beginText!.Trim(), CultureInfo.InvariantCulture);
            WaitAcceptAttemptAfter(contenderJob, reloadBegin, contender);
            File.WriteAllText(
                Path.Combine(ownerJob, "reload-window.release"), "go", s_utf8);

            WaitSignal(
                ownerJob, "post-reload.signal", owner, ReloadTimeoutMs);

            // The L-14 point: the exclusion did NOT lapse with the domain —
            // this process's probe is still denied while the owner lives.
            AssertLockDenied(
                storeRoot,
                "the OS exclusion survived the owner's domain reload");

            // The window's end mark — the new domain's first durable act:
            // an attempt whose whole AcceptRun call lies strictly between
            // the two marks is inside the reload window by the protocol's
            // own clock.
            var endText = EditorProcessHarness.WaitForFileContent(
                Path.Combine(ownerJob, "reload-end.signal"),
                CheckpointTimeoutMs);
            Assert.IsNotNull(
                endText,
                "case 6: the new domain never wrote reload-end.signal; " +
                "log: " + owner.LogPath + "; fatal: " + Fatal(ownerJob) +
                "; output: " + EditorProcessHarness.ChildOutput(owner));
            var reloadEnd = long.Parse(
                endText!.Trim(), CultureInfo.InvariantCulture);

            // The window itself must be ordered — a reload-end mark that
            // precedes the reload-begin mark was never written by the
            // owner's own post-reload domain. On 6000.5 an asset import
            // worker running the scenario hook produced exactly this
            // inversion, and the contract failure showed up two asserts
            // later with no hint of the real cause (T-W4.6-06).
            Assert.Less(
                reloadBegin, reloadEnd,
                "case 6: the reload window is ordered — the reload-begin " +
                "mark must precede the reload-end mark");

            // SPEC §T.4 L-14's "リロードでは解放しない" is the CONTENDER's
            // measurement: it polls asynchronously, so the parent's own
            // observation of post-reload.signal cannot prove the contender
            // probed inside the post-reload window. Wait for the
            // acknowledgement it writes after its first strictly post-reload
            // probe, then close the window.
            WaitSignal(
                contenderJob, "probed-after-reload.signal", contender,
                CheckpointTimeoutMs);
            File.WriteAllText(
                Path.Combine(contenderJob, "contend.stop"), "stop", s_utf8);
            var verdict = WaitVerdict(contenderJob, contender);
            AssertContenderDenied(verdict, contenderJob, storeRoot, "case 6");
            var before = int.Parse(VerdictField(verdict, "attemptsBeforePeerReload"));
            var after = int.Parse(VerdictField(verdict, "attemptsAfterPeerReload"));
            Assert.GreaterOrEqual(
                before, 1,
                "case 6: the contender must have probed before the reload " +
                "completed; verdict: " + verdict);
            Assert.GreaterOrEqual(
                after, 1,
                "case 6: the contender must have probed after the reload " +
                "completed — the window the contract cares about; " +
                "verdict: " + verdict);

            // SPEC §E2 test 3 case 6's mandatory evidence: the contender's
            // append-only attempt log — "<startTicks> <endTicks> accepted
            // <runId>" or "<startTicks> <endTicks> denied <code>" per
            // committed record. Every attempt at the SAME mutating
            // workflow must have been refused OW-RUN-002 — none may be
            // accepted — and at least one must have its WHOLE AcceptRun
            // call inside the reload: reload-begin < startTicks and
            // endTicks < reload-end.
            var attemptsPath = Path.Combine(contenderJob, "accept-attempts.log");
            var attemptLines = File.Exists(attemptsPath)
                ? EditorProcessHarness.ReadAllLinesShared(attemptsPath)
                : Array.Empty<string>();
            Assert.GreaterOrEqual(
                attemptLines.Length, 1,
                "case 6: the contender recorded no mutating-accept " +
                "attempts; log: " + contender.LogPath + "; fatal: " +
                Fatal(contenderJob));
            var insideWindow = 0;
            foreach (var line in attemptLines)
            {
                Assert.IsTrue(
                    TryParseAttempt(
                        line, out var startTicks, out var endTicks,
                        out var outcome, out var detail),
                    "case 6: malformed attempt record: " + line);
                Assert.AreEqual(
                    "denied", outcome,
                    "case 6: a mutating accept was granted while a live " +
                    "owner held the exclusion — the concurrent start the " +
                    "contract forbids; record: " + line);
                Assert.AreEqual(
                    "OW-RUN-002", detail,
                    "case 6: a denied mutating accept answers OW-RUN-002; " +
                    "record: " + line);
                // The whole call inside the window: an attempt whose
                // AcceptRun began after the begin mark AND returned before
                // the end mark ran during the domain reload itself — a
                // stamp alone cannot prove the call was inside it.
                if (startTicks > reloadBegin && endTicks < reloadEnd)
                {
                    insideWindow++;
                }
            }
            Assert.GreaterOrEqual(
                insideWindow, 1,
                "case 6: at least one mutating-accept attempt must lie " +
                "wholly inside the reload window (begin=" + reloadBegin +
                ", end=" + reloadEnd + "); attempts: " +
                string.Join(" | ", attemptLines));

            // The store-level half: no Run the contender accepted, started
            // or resumed exists — Runs/ holds exactly the owner's parked
            // Run, nothing more.
            var runDirs = Directory.GetDirectories(
                Path.Combine(storeRoot, "Runs"));
            Assert.AreEqual(
                1, runDirs.Length,
                "case 6: the store holds exactly the owner's Run — nothing " +
                "the contender attempted was ever persisted; runs: " +
                string.Join(", ", runDirs));
            Assert.AreEqual(
                runId, Path.GetFileName(runDirs[0]),
                "case 6: the single stored Run is the owner's parked Run");

            // The owner's Run was never disturbed: the journal still ends
            // at the durable step_waiting — no run_resumed, no re-executed
            // step — and the step-0 artifact exists exactly once: zero
            // concurrent executions across the reload window.
            AssertOwnerParkedUntouched(storeRoot, runId, "case 6");
            Assert.AreEqual(
                1, MarkExecutions(ownerJob),
                "case 6: the owner's step-0 artifact is written exactly " +
                "once — no execution overlapped the reload");

            // The abandoned Run: the owner process is killed (now the OS
            // releases the handle), the residue is the parked owner's, and
            // the successor's journal-verified takeover finishes it once.
            EditorProcessHarness.KillAndWait(owner, ChildStopMs);
            AssertResidue(
                storeRoot, runId, sessionId,
                "case 6: the residue names the reloaded owner's Run");
            var verify = Launch(
                CloneADir, ownerJob, "verify", storeRoot, "block", "verify");
            var verifyVerdict = WaitVerdict(ownerJob, verify);
            Assert.AreEqual(
                "Resumed", VerdictField(verifyVerdict, "outcome"),
                "case 6: the Run survives the reload as journal state and " +
                "resumes once; verdict: " + verifyVerdict);
            AssertResumedAndFinished(
                ownerJob, verifyVerdict, storeRoot, runId, "case 6");
        }

        // ── observing-read self-checks ────────────────────────────────

        // The race that failed case 2 on the first head: the parent read
        // the parked owner's journal through File.ReadAllLines — a held
        // FileShare.Read handle — while the live owner appended
        // step_waiting records. The denied FileMode.Append open is the
        // OW-STORE-001 the journal then recorded, correctly failing the
        // Run closed. This helper-level guard pins both halves without
        // children or timing: a FileShare.Read hold must deny the
        // product's append open, and the harness's observing read held
        // open across the same append must not — in either direction.
        [Test]
        public void SharedStoreRead_HeldOpen_NeverDeniesOwnerAppend()
        {
            var dir = Path.Combine(
                JobsDir, "shared-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var journal = Path.Combine(dir, "journal.ndjson");
            File.WriteAllText(
                journal, "{\"type\":\"run_started\"}\n", s_utf8);

            // The exact open FileRunStore.OpenJournalStream performs for
            // every journal record (FileMode.Append / FileAccess.Write /
            // FileShare.Read).
            void AppendRecord(string record)
            {
                using var stream = new FileStream(
                    journal, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = s_utf8.GetBytes(record + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // The pre-fix observation shape: a FileShare.Read hold denies
            // the owner's append open — the sharing violation behind the
            // OW-STORE-001 that ended the case-2 Run.
            using (new FileStream(
                       journal, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(
                    () => AppendRecord("{\"type\":\"step_waiting\"}"),
                    "a FileShare.Read hold must deny the product's " +
                    "append open — that denial is the case-2 defect");
            }
            Assert.AreEqual(
                1,
                EditorProcessHarness.ReadAllLinesShared(journal).Length,
                "the denied append left no record");

            // The observing read now in use: held open across the same
            // append, the record lands — a reading parent can never fail
            // the owner's Run again.
            using (EditorProcessHarness.OpenSharedRead(journal))
            {
                Assert.DoesNotThrow(
                    () => AppendRecord("{\"type\":\"step_waiting\"}"));
            }
            var lines = EditorProcessHarness.ReadAllLinesShared(journal);
            Assert.AreEqual(2, lines.Length);
            Assert.IsTrue(
                lines[1].Contains("\"type\":\"step_waiting\""),
                "the append committed while the observing read was held");

            // The other direction: the owner's held append stream must not
            // deny the observing read either (FileShare.Read grants it).
            using (new FileStream(
                       journal, FileMode.Append, FileAccess.Write,
                       FileShare.Read))
            {
                Assert.AreEqual(
                    2,
                    EditorProcessHarness.ReadAllLinesShared(journal).Length,
                    "a live append hold must not deny the observing read");
            }
        }
    }
}
