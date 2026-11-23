#nullable enable

// T-W4.6-01 [Edit] tests — local:T-W4.6-01:interruption-injection — the
// SPEC §T.2 AT-F07 injection suite driven across a REAL Unity Editor
// process boundary (SPEC §F0, §F2, §F3, §G6.1, §E2 item 3):
//
//   * Prefab save forced termination — a real
//     open_contents -> save_contents Run whose arrange child blocks at a
//     test-only barrier inside the save step's Execute and is killed there,
//     inside the save operation's execution window (SPEC §G6.1 test 4).
//     What the interruption genuinely leaves is the §E4 write-ahead state
//     of a kill during SaveAsPrefabAsset: the bare step_started{step:1} as
//     the durable journal tail — then a foreign Prefab overwrites the
//     destination. The §F4 pass of a NEW process must answer the §G6.1
//     fingerprint row: Unknown -> NeedsReview, never Completed.
//   * Journal tail corruption — a torn, unparseable final line appended to
//     the parked journal: the §F3 item 3 droppable tail. The new process
//     drops it with the OW-STORE-002 warning and resumes from the last
//     durable record (SPEC §F3, §F2 item 2).
//   * Unknown required record — a well-formed envelope of a type no v1
//     reader knows, required:true, with a correct crc: the fold's §F2
//     verdict, NeedsReview — no automatic resume, no automatic success
//     (SPEC §F0, G-05).
//   * Editor restart between steps — the arrange Editor exits for real
//     mid-flight; a new Editor session takes the abandoned coordinator.lock
//     over the dead owner's lock.json residue and finishes the Run.
//     SPEC §E2 item 3 (b) makes the recorded-owner takeover conditional on
//     the OS proving the previous process ended — the handle-held lock can
//     prove that on Windows and not elsewhere — so the asserted outcome is
//     platform-shaped: resume + finish where the OS proves it, NeedsReview
//     (OW-RUN-002, nothing resumed, nothing re-executed) where it cannot.
//
// Every case ends in a correct resume or NeedsReview — the two legal AT-F07
// verdicts — and every case asserts the ticket's two zeros: no Completed
// without the SPEC's evidence and no duplicate artifacts (each step's
// durable records and its mark-file artifact appear exactly once).
//
// The child Editor is a real second Unity.exe on a clone of TestProject —
// process death, OS lock release and session loss are genuine, never
// simulated. Domain-reload companions are not needed here: the ticket's
// §T.3 reload cases for this same contract already live in
// At02PrefabRecoveryTests / ResumeBranchingReloadTests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Persistence;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.6-01:interruption-injection</c>: real-Editor-process
    /// interruption injection for SPEC §T.2 AT-F07.
    /// </summary>
    internal sealed class AtF07InterruptionTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveAtf07Probe");

        private static string CloneDir => Path.Combine(ProbeDir, "project");
        private static string JobsDir => Path.Combine(ProbeDir, "jobs");

        private const string Entry =
            "OpWeave.UnityAdapter.Tests.InterruptionScenarioEntry.Run";

        // The first child launch builds the clone's whole Library; later
        // launches reuse it. The bounds stay generous on purpose — a hang
        // is a failure, not a flake to retry.
        private const int CheckpointTimeoutMs = 20 * 60 * 1000;
        private const int VerifyTimeoutMs = 20 * 60 * 1000;
        private const int ChildStopMs = 30000;

        // The SPEC §E2 item 3 (b) predicate the production seam answers:
        // the handle-held coordinator.lock exclusion proves the previous
        // owner process ended on Windows (share-mode-0 denial, §T.4 L-14);
        // everywhere else the recorded-owner takeover fails closed.
        private static bool ProcessTerminationProven =>
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private readonly EditorProcessHarness _harness = new EditorProcessHarness();
        private readonly List<EditorProcessHarness.EditorChild> _children =
            new List<EditorProcessHarness.EditorChild>();

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* leftover handles stay for the next run */ }
            }
            Directory.CreateDirectory(CloneDir);
            EditorProcessHarness.CloneProject(ProjectRoot, CloneDir);
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
            string jobDir, string scenario, string storeRoot, string exitMode, string tag)
        {
            // A killed child leaves Temp/UnityLockfile behind; the OS knows
            // its writer is gone but a stale file can still stall a batch
            // open — remove it before relaunching the same clone.
            var lockFile = Path.Combine(CloneDir, "Temp", "UnityLockfile");
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
            var child = _harness.Launch(
                CloneDir, Entry, Path.Combine(jobDir, "child-" + tag + ".log"),
                environment);
            child.JobDir = jobDir;
            _children.Add(child);
            return child;
        }

        private static string NewJob(string name)
        {
            var jobDir = Path.Combine(JobsDir, name);
            Directory.CreateDirectory(jobDir);
            return jobDir;
        }

        private JObject WaitCheckpoint(string jobDir, EditorProcessHarness.EditorChild child)
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

        private JObject WaitVerdict(string jobDir, EditorProcessHarness.EditorChild child)
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

        private static string Fatal(string jobDir)
        {
            var path = Path.Combine(jobDir, "fatal.txt");
            return File.Exists(path) ? File.ReadAllText(path) : "<none>";
        }

        private static string StoreOf(string jobDir) => Path.Combine(jobDir, "store");

        // ── journal surgery ─────────────────────────────────────────────

        private static string JournalPath(string storeRoot, string runId) =>
            RunnerHostFixture.JournalPath(storeRoot, runId);

        private static string[] JournalLines(string storeRoot, string runId) =>
            RunnerHostFixture.JournalLines(storeRoot, runId);

        // The journal must end at the bare step_started{step} — the durable
        // interruption state; a later record would be a two-steps-in-flight
        // journal the fold correctly refuses, never a mid-save kill.
        private static void AssertStartedOnlyTail(string storeRoot, string runId, int step)
        {
            var tail = JournalLines(storeRoot, runId);
            Assert.IsTrue(
                tail.Length > 0 &&
                tail[tail.Length - 1].Contains("\"type\":\"step_started\"") &&
                tail[tail.Length - 1].Contains("\"step\":" + step),
                "the journal must end at step " + step +
                "'s step_started with no later records; journal: " +
                string.Join(" | ", tail));
        }

        // A half-written tail: the front bytes of a record whose newline —
        // and most of itself — never landed (§F3 item 3's interrupted write).
        private static void AppendBrokenTail(string storeRoot, string runId)
        {
            File.AppendAllText(
                JournalPath(storeRoot, runId),
                "{\"seq\":42,\"ts\":\"2026-11-13T00:00:00.0000000Z\",\"sess",
                s_utf8);
        }

        // An unknown record type with a correct crc at a real position —
        // required:true. The fold must answer by the §F2 table, never by
        // guessing: NeedsReview, no resume, no success.
        private static void AppendForgedRequiredRecord(string storeRoot, string runId)
        {
            var lines = JournalLines(storeRoot, runId);
            var last = (JObject)JsonValue.Parse(lines[lines.Length - 1])
                .Value!.ToToken();
            var seq = last["seq"] is JValue { Value: long s } ? s : -1L;
            var ts = last["ts"] is JValue { Value: string t }
                ? t : "2026-11-13T00:00:00.0000000Z";
            var session = last["session"] is JValue { Value: string e }
                ? e : "session-forged";
            Assert.GreaterOrEqual(seq, 0, "the journal tail carries no seq");
            var env = new JObject
            {
                ["seq"] = seq + 1,
                ["ts"] = ts,
                ["session"] = session,
                ["type"] = "test.atf07.unknown_required",
                ["required"] = true,
                ["data"] = new JObject(),
            };
            var clone = (JObject)env.DeepClone();
            clone.Remove("crc");
            env["crc"] = Crc32.ToLowerHex(
                Crc32.Compute(s_utf8.GetBytes(CanonicalJson.Serialize(clone))));
            File.AppendAllText(
                JournalPath(storeRoot, runId),
                CanonicalJson.Serialize(env) + "\n", s_utf8);
        }

        // ── assertions ──────────────────────────────────────────────────

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

        private static bool JournalHas(
            string storeRoot, string runId, string fragment) =>
            RunnerHostFixture.JournalHas(storeRoot, runId, fragment);

        private static int MarkExecutions(string jobDir)
        {
            var mark = Path.Combine(jobDir, "step0.mark");
            Assert.IsTrue(File.Exists(mark), "the step-0 mark file is missing");
            return File.ReadAllLines(mark).Length;
        }

        private static string VerdictField(JObject verdict, string name) =>
            verdict[name]?.ToString() ?? "";

        // The fail-closed shape every case shares on a host that cannot
        // prove the previous owner ended (SPEC §E2 item 3 (b)): the pass
        // reports NeedsReview with OW-RUN-002 and appends nothing — no
        // resume record, no step outcome, no run end.
        private static void AssertFailedClosed(
            JObject verdict, string storeRoot, string runId, string because)
        {
            Assert.AreEqual(
                "NeedsReview", VerdictField(verdict, "outcome"),
                because + ": the takeover must fail closed; verdict: " + verdict);
            Assert.IsTrue(
                VerdictField(verdict, "recoveryDiagnostics").Contains("OW-RUN-002"),
                because + ": the §E2 item 3 (b) refusal is OW-RUN-002; verdict: " + verdict);
            Assert.IsFalse(
                JournalHas(storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a review-needed recovery never journals run_resumed");
            Assert.IsFalse(
                JournalHas(storeRoot, runId, "\"type\":\"run_finished\""),
                because + ": nothing may finish without evidence");
            Assert.AreEqual(
                0, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":1"),
                because + ": the held step may never complete past the interruption");
        }

        // The resumed-and-finished shape: the decided resume is journaled
        // run_resumed (§F4 item 6 write-ahead), the parked step completes
        // through Poll once, and the Run ends with real evidence — every
        // step_started/step_succeeded exactly once, no re-execution, one
        // mark line.
        private static void AssertResumedAndFinished(
            string jobDir, JObject verdict, string storeRoot, string runId, string because)
        {
            Assert.AreEqual(
                "Succeeded", VerdictField(verdict, "state"),
                because + ": the resumed Run must finish; verdict: " + verdict +
                "; journal: " + string.Join(" | ", JournalLines(storeRoot, runId)));
            Assert.IsTrue(
                JournalHas(storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a decided resume journals run_resumed first (§F4 item 6)");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"step_started\"", "\"step\":0"),
                because + ": step 0 must have exactly one start — never re-executed");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                because + ": step 0 must have exactly one success");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":1"),
                because + ": the resumed step completes through Poll exactly once");
            Assert.AreEqual(
                1, CountLines(storeRoot, runId, "\"type\":\"run_finished\"", "\"succeeded\""),
                because + ": one terminal record, carrying the decided outcome");
            Assert.AreEqual(
                1, MarkExecutions(jobDir),
                because + ": the step-0 artifact is written exactly once — " +
                    "zero duplicate artifacts");
        }

        // ── AT-F07 injections ───────────────────────────────────────────

        // SPEC §G6.1 test 4 / §F0: a real forced termination inside the
        // embedded Prefab save's execution window — the arrange child
        // blocks at the in-save barrier inside save_contents' Execute and
        // the Editor is killed there, so the durable state is genuinely the
        // §E4 write-ahead boundary of a kill during SaveAsPrefabAsset: a
        // bare step_started{step:1} tail over a destination a foreign
        // Prefab then occupies — can never settle Completed: the
        // PrefabContentFingerprint v1 the step recorded does not match, so
        // the §F5 verdict is Unknown and the Run ends NeedsReview, the
        // foreign artifact untouched and alone.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void PrefabSaveForcedTermination_NeverCompletesWithoutFingerprint()
        {
            var jobDir = NewJob("prefab-save");
            var storeRoot = StoreOf(jobDir);
            var arrange = Launch(jobDir, "arrange-prefab-save", storeRoot, "block", "arrange");
            var checkpoint = WaitCheckpoint(jobDir, arrange);
            var runId = checkpoint["runId"]!.ToString();
            var prefabPath = checkpoint["prefabPath"]!.ToString();

            // The injection: the child's in-save signal proves the save
            // step's Execute is in flight — inside the operation's
            // execution window — and the Editor is killed there for real.
            // The journal must already end at the bare step_started{step:1}
            // the §E4 write-ahead made durable; nothing is cut or forged.
            Assert.IsNotNull(
                EditorProcessHarness.WaitForFileContent(
                    Path.Combine(jobDir, "in-save.signal"), CheckpointTimeoutMs),
                "the arrange child never reached the in-save barrier; log: " +
                arrange.LogPath + "; fatal: " + Fatal(jobDir) +
                "; output: " + EditorProcessHarness.ChildOutput(arrange));
            EditorProcessHarness.KillAndWait(arrange, ChildStopMs);
            AssertStartedOnlyTail(storeRoot, runId, 1);
            var damaged = DamageClonePrefab(
                Path.Combine(CloneDir, prefabPath.Replace('/', Path.DirectorySeparatorChar)));

            var verify = Launch(jobDir, "verify", storeRoot, "block", "verify");
            var verdict = WaitVerdict(jobDir, verify);

            if (ProcessTerminationProven)
            {
                Assert.AreEqual(
                    "NeedsReview", VerdictField(verdict, "state"),
                    "an interrupted Prefab save whose evidence cannot match " +
                    "must end NeedsReview; verdict: " + verdict + "; journal: " +
                    string.Join(" | ", JournalLines(storeRoot, runId)));
                Assert.IsTrue(
                    CountLines(storeRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":1") == 1 ||
                    VerdictField(verdict, "outcome") == "NeedsReview",
                    "the Unknown verdict is journaled step_needs_review (§E1) or " +
                    "reported NeedsReview by the pass; verdict: " + verdict);
                Assert.IsFalse(
                    JournalHas(storeRoot, runId, "\"type\":\"run_resumed\""),
                    "a review-needed resume never journals run_resumed");
                Assert.AreEqual(
                    0, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":1"),
                    "the interrupted save may never be adopted as succeeded — " +
                    "no Completed without the §G6.1 fingerprint evidence");
                Assert.IsFalse(
                    JournalHas(storeRoot, runId, "\"type\":\"run_finished\""),
                    "a NeedsReview Run is not finished");
            }
            else
            {
                AssertFailedClosed(
                    verdict, storeRoot, runId, "a recorded-owner takeover this OS cannot prove");
            }
            Assert.AreEqual(
                damaged, File.ReadAllBytes(
                    Path.Combine(CloneDir, prefabPath.Replace('/', Path.DirectorySeparatorChar))),
                "NeedsReview neither restores nor re-saves — the destination " +
                "content stays exactly the foreign Prefab the injection left");
            Assert.AreEqual(
                1, Directory.GetFiles(
                        Path.Combine(CloneDir, "Assets", "OpWeaveAtf07"), "*.prefab").Length,
                "no duplicate Prefab artifact may appear next to the target");
        }

        // SPEC §F3 item 3 / §F2 item 2: a torn, unparseable last journal
        // line is the tail of an interrupted write — dropped with the
        // OW-STORE-002 warning, never NeedsReview by itself; the Run then
        // resumes on the last durable record.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void JournalTailCorruption_ResumesFromLastDurableRecord()
        {
            var jobDir = NewJob("tail-corruption");
            var storeRoot = StoreOf(jobDir);
            var arrange = Launch(jobDir, "arrange-mark-gate", storeRoot, "block", "arrange");
            var checkpoint = WaitCheckpoint(jobDir, arrange);
            var runId = checkpoint["runId"]!.ToString();

            EditorProcessHarness.KillAndWait(arrange, ChildStopMs);
            AppendBrokenTail(storeRoot, runId);

            var verify = Launch(jobDir, "verify", storeRoot, "block", "verify");
            var verdict = WaitVerdict(jobDir, verify);

            Assert.IsTrue(
                VerdictField(verdict, "loadDiagnostics").Contains("OW-STORE-002"),
                "the §F3 item 3 drop of the torn tail must carry OW-STORE-002; " +
                "verdict: " + verdict);
            if (ProcessTerminationProven)
            {
                AssertResumedAndFinished(
                    jobDir, verdict, storeRoot, runId,
                    "a torn journal tail drops and the Run resumes");
            }
            else
            {
                AssertFailedClosed(
                    verdict, storeRoot, runId, "a recorded-owner takeover this OS cannot prove");
                Assert.AreEqual(
                    1, MarkExecutions(jobDir),
                    "even a refused recovery never re-executes a finished step");
            }
        }

        // SPEC §F2 / §F0: a well-formed journal record of an unknown type
        // with required:true — the fold's NeedsReview classification. No
        // automatic resume, no automatic success, and the record stays.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void UnknownRequiredRecord_EndsNeedsReview()
        {
            var jobDir = NewJob("unknown-required");
            var storeRoot = StoreOf(jobDir);
            var arrange = Launch(jobDir, "arrange-mark-gate", storeRoot, "block", "arrange");
            var checkpoint = WaitCheckpoint(jobDir, arrange);
            var runId = checkpoint["runId"]!.ToString();

            EditorProcessHarness.KillAndWait(arrange, ChildStopMs);
            AppendForgedRequiredRecord(storeRoot, runId);

            var verify = Launch(jobDir, "verify", storeRoot, "block", "verify");
            var verdict = WaitVerdict(jobDir, verify);

            Assert.IsTrue(
                VerdictField(verdict, "loadDiagnostics").Contains("OW-STORE-002"),
                "the §F2 NeedsReview verdict on the unknown required record " +
                "must carry OW-STORE-002; verdict: " + verdict);
            if (ProcessTerminationProven)
            {
                Assert.IsTrue(
                    VerdictField(verdict, "state") == "NeedsReview" ||
                    VerdictField(verdict, "outcomeState") == "NeedsReview" ||
                    VerdictField(verdict, "outcome") == "NeedsReview",
                    "an unknown required record must end NeedsReview — never a " +
                    "resume, never a success; verdict: " + verdict +
                    "; journal: " + string.Join(" | ", JournalLines(storeRoot, runId)));
                Assert.IsFalse(
                    JournalHas(storeRoot, runId, "\"type\":\"run_resumed\""),
                    "a NeedsReview journal never resumes");
                Assert.AreEqual(
                    0, CountLines(storeRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":1"),
                    "the held step never completes");
                Assert.IsFalse(
                    JournalHas(storeRoot, runId, "\"type\":\"run_finished\""),
                    "a NeedsReview Run is not finished");
                Assert.AreEqual(
                    1, MarkExecutions(jobDir),
                    "the finished step is never re-executed");
            }
            else
            {
                AssertFailedClosed(
                    verdict, storeRoot, runId, "a recorded-owner takeover this OS cannot prove");
                Assert.AreEqual(
                    1, MarkExecutions(jobDir),
                    "even a refused recovery never re-executes a finished step");
            }
        }

        // SPEC §E2 item 3 / §F4: a real Editor restart between steps — the
        // arrange Editor exits mid-flight; the successor takes the
        // abandoned coordinator.lock over the dead session's lock.json
        // residue and drives the parked Run to its finish. Where the OS
        // cannot prove the previous owner ended, the same residue fails
        // closed instead.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void EditorRestartBetweenSteps_ResumesAndFinishes()
        {
            var jobDir = NewJob("restart");
            var storeRoot = StoreOf(jobDir);
            var arrange = Launch(jobDir, "arrange-mark-gate", storeRoot, "exit", "arrange");
            var checkpoint = WaitCheckpoint(jobDir, arrange);
            var runId = checkpoint["runId"]!.ToString();
            Assert.IsTrue(
                EditorProcessHarness.WaitForExit(arrange, VerifyTimeoutMs),
                "the arrange child did not exit for the restart; log: " +
                arrange.LogPath);

            var verify = Launch(jobDir, "verify", storeRoot, "block", "verify");
            var verdict = WaitVerdict(jobDir, verify);

            if (ProcessTerminationProven)
            {
                AssertResumedAndFinished(
                    jobDir, verdict, storeRoot, runId,
                    "a real Editor restart between steps");
            }
            else
            {
                AssertFailedClosed(
                    verdict, storeRoot, runId, "a recorded-owner takeover this OS cannot prove");
                Assert.AreEqual(
                    1, MarkExecutions(jobDir),
                    "even a refused recovery never re-executes a finished step");
            }
        }

        // A foreign Prefab over the clone's target — different content
        // (a different marker component) under the same asset identity, so
        // the recorded PrefabContentFingerprint v1 can never match and the
        // cheaper existence/name/timestamp checks are never the evidence
        // (§G6.1 禁止). Returns the foreign bytes for the unchanged-check.
        private byte[] DamageClonePrefab(string cloneAssetPath)
        {
            const string foreignPath = "Assets/__atf07Foreign.prefab";
            var foreign = new GameObject("Atf07Foreign");
            try
            {
                foreign.AddComponent<SphereCollider>();
                var saved = PrefabUtility.SaveAsPrefabAsset(foreign, foreignPath);
                Assert.IsNotNull(saved, "the foreign Prefab did not save");
                var foreignDisk = Path.Combine(
                    ProjectRoot, foreignPath.Replace('/', Path.DirectorySeparatorChar));
                var bytes = File.ReadAllBytes(foreignDisk);
                File.WriteAllBytes(cloneAssetPath, bytes);
                AssetDatabase.DeleteAsset(foreignPath);
                return bytes;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(foreign);
            }
        }
    }
}
