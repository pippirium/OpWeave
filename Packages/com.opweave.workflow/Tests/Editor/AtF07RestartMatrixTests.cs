#nullable enable

// T-W4.6-04 [Edit] tests: local:T-W4.6-04:atf07-reconcilable-20x6 case 6 —
// the SPEC §F5 real-Editor-restart row for every one of the 20 standard
// Reconcilable Operations (SPEC §I). One arrange-matrix child Editor parks
// a [op -> hold] Run per Operation on its own Run Store (SPEC §E2), the
// parent kills the process mid-flight and cuts every journal back to the
// bare step_started{step:0}, and one verify-matrix successor Editor folds
// each journal before recovery, runs the real §F4 pass on it and reports
// the journal facts. §F5's restart row then prescribes each verdict:
// Operations whose evidence is file content, a Prefab fingerprint or the
// persisted package state are re-identified and Completed-adopted; the
// session-bound GameObject/Component/instance evidence can never be
// re-proven in a new Editor session and ends NeedsReview; open_contents'
// dead editing session is provably absent and re-executes exactly once.
// No automatic success, no automatic re-execution, no duplicate artifact.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// SPEC §F5 case 6 across a real process boundary: the twenty
    /// Reconcilable Operations each parked mid-flight in a first Editor the
    /// parent kills, resumed in a successor Editor.
    /// </summary>
    [TestFixture]
    [Timeout(3600000)]
    internal sealed class AtF07RestartMatrixTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string CloneDir =>
            Path.Combine(AtF07Matrix.ProbeDir, "project");
        private static string JobDir =>
            Path.Combine(AtF07Matrix.ProbeDir, "job-matrix");

        // The clone's fixture generation stamp: a clone stamped by an
        // older generation — or carrying no stamp at all — cannot be
        // trusted, so it is deleted and rebuilt before reuse. Bump when
        // the matrix fixture the clone must carry changes — including
        // the clone-hygiene contract itself, since a stamped clone
        // built before the generated-folder exclusion is dirty by
        // definition (T-W2.4-11).
        private const string FixtureGeneration = "3";
        private static string GenerationFile =>
            Path.Combine(AtF07Matrix.ProbeDir, "project.generation");

        private const string Entry =
            "OpWeave.UnityAdapter.Tests.InterruptionScenarioEntry.Run";
        private const int CheckpointTimeoutMs = 30 * 60 * 1000;
        private const int VerifyTimeoutMs = 30 * 60 * 1000;
        private const int ChildStopMs = 30000;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private readonly EditorProcessHarness _harness = new EditorProcessHarness();
        private readonly List<EditorProcessHarness.EditorChild> _children =
            new List<EditorProcessHarness.EditorChild>();

        private static JObject? s_verdicts;
        private static string? s_bootstrapError;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var current = Directory.Exists(CloneDir) &&
                File.Exists(GenerationFile) &&
                File.ReadAllText(GenerationFile).Trim() == FixtureGeneration;
            if (!current)
            {
                if (Directory.Exists(CloneDir))
                {
                    try { Directory.Delete(CloneDir, recursive: true); }
                    catch (Exception)
                    {
                        // A still-held file stays; the re-clone below merges
                        // the fresh project over the leftovers.
                    }
                }
                Directory.CreateDirectory(CloneDir);
                // The exclusion keeps the parent's generated fixture
                // trees out of the raw copy — residue under the source's
                // Assets/ otherwise reaches the child as an occupied
                // never-overwrite target (OW-UNITY-005, T-W2.4-11).
                EditorProcessHarness.CloneProject(
                    ProjectRoot, CloneDir, AtF07Matrix.CloneExcludedFolders);
            }
            // The fail-closed residue guarantee on every path: a stamped
            // clone REUSED across suite runs still carries what its own
            // arrange/verify children generated — the same OW-UNITY-005
            // fatal for the next arrange child as parent residue is.
            EditorProcessHarness.ScrubProjectDirectories(
                CloneDir, AtF07Matrix.CloneExcludedFolders);
            if (!current)
            {
                // Stamped only after the clone is built and scrubbed, so
                // a later run never reads a dirty clone as current.
                File.WriteAllText(GenerationFile, FixtureGeneration, s_utf8);
            }
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

        private EditorProcessHarness.EditorChild Launch(
            string scenario, string exitMode, string tag)
        {
            var lockFile = Path.Combine(CloneDir, "Temp", "UnityLockfile");
            if (File.Exists(lockFile))
            {
                try { File.Delete(lockFile); }
                catch (Exception) { /* the child reports its own stale lock */ }
            }
            var environment = new Dictionary<string, string>
            {
                [EditorProcessHarness.ScenarioEnvVar] = scenario,
                [EditorProcessHarness.JobDirEnvVar] = JobDir,
                [EditorProcessHarness.StoreEnvVar] = JobDir,
                [EditorProcessHarness.ExitModeEnvVar] = exitMode,
            };
            var child = _harness.Launch(
                CloneDir, Entry, Path.Combine(JobDir, "child-" + tag + ".log"),
                environment);
            child.JobDir = JobDir;
            _children.Add(child);
            return child;
        }

        // The one batched restart: arrange child parks all twenty Runs,
        // the parent kills it, cuts every journal to step_started{step:0}
        // and the verify successor recovers them all. Runs once — every
        // test case reads the cached verdicts.json.
        private void EnsureRun()
        {
            if (s_verdicts is not null)
            {
                return;
            }
            // A failed bootstrap leaves the recorded error for every later
            // case — the real arrange/verify failure, never a missing
            // verdicts.json null-reference.
            if (s_bootstrapError is not null)
            {
                Assert.Fail(s_bootstrapError);
            }
            try
            {
                RunBootstrap();
            }
            catch (Exception e)
            {
                s_bootstrapError = e.ToString();
                throw;
            }
        }

        private void RunBootstrap()
        {
            // A previous run's job residue — the store-* roots carry
            // parked Runs and dead-owner lock records a new arrange
            // child would recover or queue behind — never survives into
            // this run; everything under JobDir is regenerated here.
            if (Directory.Exists(JobDir))
            {
                try { Directory.Delete(JobDir, recursive: true); }
                catch (Exception)
                {
                    // A still-held file stays; every output below is
                    // rewritten and stale stores are the only hazard —
                    // the children are dead before this point.
                }
            }
            Directory.CreateDirectory(JobDir);

            // The local Git fixture repository: created by the test, tags
            // created (and for case 5 moved) locally, no external remote —
            // the dep is the Git URL form per T-W2.12-05.
            var (tagged, head) = AtF07Matrix.EnsureGitFixture();
            File.WriteAllText(
                Path.Combine(JobDir, "matrix.json"),
                new JObject
                {
                    // The dep requests a full commit SHA: a tag request
                    // cannot carry the persisted expected commit §G8.1
                    // requires for Completed after a restart either.
                    ["dep"] = AtF07Matrix.GitDep(tagged),
                    ["tagged"] = tagged,
                    ["head"] = head,
                }.ToString(Newtonsoft.Json.Formatting.None), s_utf8);

            var arrange = Launch("arrange-matrix", "block", "arrange");
            var checkpointText = EditorProcessHarness.WaitForFileContent(
                Path.Combine(JobDir, "checkpoint.json"), CheckpointTimeoutMs,
                watchedProcess: arrange.Process);
            Assert.IsNotNull(
                checkpointText,
                "the arrange-matrix child never wrote checkpoint.json; " +
                "fatal: " + Fatal() + "; output: " +
                EditorProcessHarness.ChildOutput(arrange));
            var runs = (JObject)JObject.Parse(checkpointText!)["runs"]!;
            EditorProcessHarness.KillAndWait(arrange, ChildStopMs);

            // The real interruption: every parked Run's journal ends at the
            // bare step_started{step:0} — the mid-Execute restart of §F5
            // case 6 — and the killed child's owner locks die with it.
            foreach (var key in AtF07Matrix.OpKeys)
            {
                var runId = (string)runs[key]!;
                var journal = Path.Combine(
                    JobDir, "store-" + key, "Runs", runId, "journal.ndjson");
                Assert.IsTrue(File.Exists(journal),
                    key + ": the arrange child's journal is missing");
                var lines = File.ReadAllLines(journal);
                var cut = -1;
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("\"type\":\"step_started\"") &&
                        lines[i].Contains("\"step\":0"))
                    {
                        cut = i;
                        break;
                    }
                }
                Assert.GreaterOrEqual(cut, 0,
                    key + ": the arrange journal never recorded " +
                    "step_started{0}: " + string.Join(" | ", lines));
                File.WriteAllText(
                    journal,
                    string.Join("\n", lines.Take(cut + 1)) + "\n", s_utf8);
            }

            var verify = Launch("verify-matrix", "exit", "verify");
            Assert.IsTrue(
                EditorProcessHarness.WaitForExit(verify, VerifyTimeoutMs),
                "the verify-matrix child did not exit; output: " +
                EditorProcessHarness.ChildOutput(verify));
            var verdictText = EditorProcessHarness.WaitForFileContent(
                Path.Combine(JobDir, "verdicts.json"), 30000,
                watchedProcess: verify.Process);
            Assert.IsNotNull(
                verdictText,
                "the verify-matrix child never wrote verdicts.json; " +
                "fatal: " + Fatal() + "; output: " +
                EditorProcessHarness.ChildOutput(verify));
            s_verdicts = JObject.Parse(verdictText!);
        }

        private static string Fatal()
        {
            var path = Path.Combine(JobDir, "fatal.txt");
            return File.Exists(path) ? File.ReadAllText(path) : "<none>";
        }

        private static IEnumerable<string> Ops() => AtF07Matrix.OpKeys;

        // ══ SPEC §F5 case 6: a real Editor restart. File-identity and
        // ══ persisted evidence re-identify the product → Completed-adopted;
        // ══ session-bound evidence can never be re-proven → NeedsReview;
        // ══ open_contents' dead session is provably absent → one re-exec.
        [TestCaseSource(nameof(Ops))]
        public void Case6_RealEditorRestart(string key)
        {
            EnsureRun();
            var verdict = (JObject)s_verdicts![key]!;
            Assert.IsNotNull(verdict, key + ": the verify child wrote no verdict");
            Assert.IsNull(
                verdict["error"]?.ToString(),
                key + ": the verify child threw: " + verdict["error"]);
            Assert.IsTrue(
                verdict.Value<bool?>("recoveryOk") == true,
                key + ": the recovery pass failed: " + verdict);
            switch (AtF07Matrix.RestartVerdict(key))
            {
                case MatrixVerdict.Review:
                    Assert.AreEqual(
                        "NeedsReview", verdict.Value<string>("state"),
                        key + ": session-bound evidence can never be " +
                        "re-proven after a real restart");
                    Assert.IsTrue(
                        verdict.Value<bool>("stepNeedsReview"),
                        key + ": the Unknown verdict must journal " +
                        "step_needs_review{step:0}");
                    Assert.IsFalse(
                        verdict.Value<bool>("runResumed"),
                        key + ": a review-needed resume never journals " +
                        "run_resumed — no automatic re-execution");
                    break;
                case MatrixVerdict.Adopted:
                    Assert.IsTrue(
                        verdict.Value<bool>("succeededReconciled"),
                        key + ": the persisted evidence row held across the " +
                        "restart — step_succeeded{step:0} reconciled:true; " +
                        "verdict: " + verdict);
                    Assert.AreEqual(
                        "Running", verdict.Value<string>("state"),
                        key + ": the adopted Run must continue to the " +
                        "parked hold");
                    break;
                default:
                    Assert.IsTrue(
                        verdict.Value<bool>("runResumed"),
                        key + ": the provably-absent session resumes the Run");
                    Assert.IsTrue(
                        verdict.Value<int>("stepSucceeded") >= 1,
                        key + ": the re-executed open_contents reaches " +
                        "step_succeeded{step:0}");
                    Assert.AreEqual(
                        "Running", verdict.Value<string>("state"),
                        key + ": the re-executed Run re-parks at the hold");
                    break;
            }
        }
    }
}
