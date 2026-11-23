#nullable enable

// T-W4.6-01 [Edit] child-side scenarios for the AT-F07 interruption suite
// (SPEC §T.2 AT-F07): the -executeMethod entry a child Editor launched by
// EditorProcessHarness runs inside the cloned project. The child builds a
// RunnerHostFixture over the job's own Run Store root — the fixture wires
// the real FileRunStore, the real coordinator.lock OS exclusion, the real
// RunLockHandoff and the real §F4 recovery engine; only the clock, the
// editor-state probe and the registry contents are test-controlled, which
// is exactly the seam the same-domain fixtures of T-W4.3-03 use.
//
// Scenarios (OWP_EPH_SCENARIO):
//   arrange-mark-gate    [test.atf07.filemark -> test.atf07.gatewait]:
//                        step 0 writes one execution line to a probe file,
//                        the Run parks on step 1's durable step_waiting —
//                        the journal a mid-flight kill or Editor restart
//                        leaves behind.
//   arrange-prefab-save  [open_contents -> save_contents -> gatewait]:
//                        a real embedded-Prefab edit session on the real
//                        Operations, but the save step's implementation is
//                        the test-only InSaveBarrierOperation: it edits the
//                        opened contents for real, then the armed
//                        InSaveImportBarrier writes <job>/in-save.signal
//                        and holds the import inside the synchronous
//                        import SaveAsPrefabAsset itself performs — the
//                        point inside the real save operation's execution
//                        window where the parent's Process.Kill lands for
//                        real (SPEC §G6.1 test 4). The durable journal
//                        genuinely ends at the bare step_started{step:1}
//                        the §E4 write-ahead left.
//   verify               the successor Editor: folds the journal BEFORE
//                        recovery (capturing the §F3 item 3 OW-STORE-002
//                        tail-drop warning / the §F2 NeedsReview verdict),
//                        runs the real §F4 recovery pass, ticks the Run to
//                        its decided state and writes verdict.json.
//
// The arrange phase then either exits the Editor cleanly (OWP_EPH_EXIT=exit —
// the "Editor restart" case) or blocks until the parent kills it
// (OWP_EPH_EXIT=block — the forced-termination cases).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
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
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>The batchmode <c>-executeMethod</c> entry point of the child
    /// Editor processes spawned by the AT-F07 suite.</summary>
    public static class InterruptionScenarioEntry
    {
        private const string WorkflowId = "test.atf07.interruption";
        private const string MarkOpId = "test.atf07.filemark";
        private const string GateOpId = "test.atf07.gatewait";
        private const string OpenOpId = "unity.prefab.open_contents";
        private const string SaveOpId = "unity.prefab.save_contents";

        private const string ProbeFolder = "Assets/OpWeaveAtf07";
        private const string PrefabPath = ProbeFolder + "/Target.prefab";

        private const int MaxParkTicks = 40;
        private const int MaxSettleTicks = 200;
        private const int MaxBlockMinutes = 15;

        /// <summary>The -executeMethod entry: dispatches on OWP_EPH_SCENARIO,
        /// writes fatal.txt and exits nonzero on any failure so a hung or
        /// crashing child is a test failure, never a silent pass.</summary>
        public static void Run()
        {
            var scenario = Environment.GetEnvironmentVariable(
                EditorProcessHarness.ScenarioEnvVar) ?? "";
            var jobDir = Environment.GetEnvironmentVariable(
                EditorProcessHarness.JobDirEnvVar) ?? "";
            var storeRoot = Environment.GetEnvironmentVariable(
                EditorProcessHarness.StoreEnvVar) ?? "";
            var exitMode = Environment.GetEnvironmentVariable(
                EditorProcessHarness.ExitModeEnvVar) ?? "block";
            try
            {
                switch (scenario)
                {
                    case "arrange-mark-gate":
                        ArrangeMarkGate(jobDir, storeRoot);
                        FinishArrange(exitMode, jobDir);
                        break;
                    case "arrange-prefab-save":
                        ArrangePrefabSave(jobDir, storeRoot);
                        FinishArrange(exitMode, jobDir);
                        break;
                    case "verify":
                        Verify(jobDir, storeRoot);
                        EditorApplication.Exit(0);
                        break;
                    case "arrange-matrix":
                        ArrangeMatrix(jobDir);
                        FinishArrange(exitMode, jobDir);
                        break;
                    case "verify-matrix":
                        VerifyMatrix(jobDir);
                        EditorApplication.Exit(0);
                        break;
                    default:
                        throw new InvalidOperationException(
                            "unknown scenario '" + scenario + "'");
                }
            }
            catch (Exception exception)
            {
                // The -logFile keeps every fatal: a child whose Run died
                // without parking explains itself in its own log even when
                // the parent never reads fatal.txt.
                Debug.LogError("AT-F07 child fatal: " + exception);
                try
                {
                    EditorProcessHarness.AtomicWriteFile(
                        Path.Combine(jobDir, "fatal.txt"), exception.ToString());
                }
                catch (Exception)
                {
                    // Best effort — a full disk must not mask the real failure.
                }
                EditorApplication.Exit(3);
            }
        }

        // ── arrange phases ──────────────────────────────────────────────

        // [mark -> gate]: the two-step Run parked at step_waiting{step:1} —
        // every durable record a real mid-flight journal holds.
        private static void ArrangeMarkGate(string jobDir, string storeRoot)
        {
            var fixture = new RunnerHostFixture(storeRoot);
            RegisterAll(fixture, jobDir);
            var steps = new[]
            {
                RunnerHostFixture.Step(
                    0, "mark", MarkOpId, 'a', next: 1,
                    outputTypes: new Dictionary<string, string>
                    {
                        ["mark"] = "core.String",
                    },
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(
                    1, "hold", GateOpId, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(
                new[] { ("mark", MarkOpId), ("hold", GateOpId) },
                ("mark", "hold"));
            var runId = AcceptAndPark(fixture, storeRoot, plan, doc);
            WriteCheckpoint(jobDir, runId, prefabPath: null, guid: null);
        }

        // [open -> save -> gate]: the real embedded-Prefab edit of SPEC §G6 —
        // open_contents loads Target.prefab's contents as the Run's editing
        // session (resource_acquired), save_contents writes it back through
        // PrefabUtility.SaveAsPrefabAsset and releases the resource, and the
        // gate parks the Run. The save is registered through the
        // InSaveBarrierOperation wrapper so the child blocks inside the save
        // step's Execute until the parent kills it — the §E4 write-ahead
        // state a kill during SaveAsPrefabAsset leaves is then real, never
        // reconstructed: step_started{step:1} durable, nothing after it.
        private static void ArrangePrefabSave(string jobDir, string storeRoot)
        {
            var fixture = new RunnerHostFixture(storeRoot);
            RegisterAll(fixture, jobDir, barrierOnSave: true);
            var (_, path, guid) = NewProbePrefab();
            var steps = new[]
            {
                RunnerHostFixture.Step(
                    0, "open", OpenOpId, '2', next: 1,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["prefab"] = new Constant(
                            PrefabContentsFixture.PrefabRef(guid, path)),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["root"] = "unity.GameObjectRef",
                    },
                    idempotency: Idempotency.Reconcilable),
                RunnerHostFixture.Step(
                    1, "save", SaveOpId, '6', next: 2,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["root"] = new FromStep(0, "root"),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["prefab"] = "unity.PrefabAssetRef",
                    },
                    idempotency: Idempotency.Reconcilable),
                RunnerHostFixture.Step(
                    2, "hold", GateOpId, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(
                new[] { ("open", OpenOpId), ("save", SaveOpId), ("hold", GateOpId) },
                ("open", "save"), ("save", "hold"));
            var runId = Accept(fixture, plan, doc);
            // The checkpoint goes out before the Run is ticked at all: the
            // child is expected to die inside the save barrier mid-arrange,
            // so nothing past the barrier can carry the parent's protocol.
            WriteCheckpoint(jobDir, runId, path, guid);
            Park(fixture, storeRoot, plan, runId);
        }

        // A probe Prefab with a marker component so its saved content is
        // distinguishable from any disturbance — the PrefabContentFingerprint
        // v1 the save records can never match foreign content (§G6.1).
        private static (GameObject Asset, string Path, string Guid) NewProbePrefab()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveAtf07");
            }
            var source = new GameObject("Atf07Target");
            try
            {
                source.AddComponent<BoxCollider>();
                var saved = PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
                if (saved == null)
                {
                    throw new InvalidOperationException(
                        "the probe Prefab did not save: " + PrefabPath);
                }
                var guid = AssetDatabase.AssetPathToGUID(PrefabPath);
                if (string.IsNullOrEmpty(guid))
                {
                    throw new InvalidOperationException(
                        "the probe Prefab has no guid: " + PrefabPath);
                }
                return (saved, PrefabPath, guid);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        // Accepts the Plan and ticks until the Run is parked on a durable
        // step_waiting — the journal's last record — or fails: the checkpoint
        // only counts once the mid-flight state is really on disk.
        private static string AcceptAndPark(
            RunnerHostFixture fixture,
            string storeRoot,
            CompiledPlan plan,
            WorkflowDocument doc)
        {
            var runId = Accept(fixture, plan, doc);
            Park(fixture, storeRoot, plan, runId);
            return runId;
        }

        private static string Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, new AcceptOptions(null, false));
            if (!accepted.Ok || accepted.Value is not { } handle)
            {
                throw new InvalidOperationException(
                    "accept failed: " + Describe(accepted.Diagnostics));
            }
            return handle.RunId;
        }

        // Ticks until the Run is parked on a durable step_waiting — the
        // journal's last record — or fails: a checkpoint only counts once
        // the mid-flight state is really on disk. A Run that dies inside a
        // step's Execute (the in-save barrier's forced termination) never
        // returns from here — the process is gone before the park completes.
        private static void Park(
            RunnerHostFixture fixture, string storeRoot, CompiledPlan plan, string runId)
        {
            var journal = RunnerHostFixture.JournalPath(storeRoot, runId);
            for (var i = 0; i < MaxParkTicks; i++)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                var tick = fixture.Host.Tick();
                if (!tick.Ok)
                {
                    throw new InvalidOperationException(
                        "tick failed: " + Describe(tick.Diagnostics));
                }
                var lines = File.Exists(journal)
                    ? File.ReadAllLines(journal)
                    : Array.Empty<string>();
                if (lines.Length != 0 &&
                    lines[lines.Length - 1].Contains("\"type\":\"step_waiting\""))
                {
                    // SPEC §F3 item 1 durability for the records the
                    // interruption must find: a parked Run's step_waiting is
                    // already Flushed — this drains anything Buffered the
                    // same tick wrote.
                    var flushed = fixture.Host.FlushJournal();
                    if (!flushed.Ok)
                    {
                        throw new InvalidOperationException(
                            "journal flush failed: " + Describe(flushed.Diagnostics));
                    }
                    return;
                }
                var state = fixture.Host.Coordinator.StateOf(runId);
                if (state != RunState.Running)
                {
                    throw new InvalidOperationException(ParkFailure(
                        fixture, storeRoot, plan, runId,
                        "the Run left Running without parking: state=" + state));
                }
            }
            throw new InvalidOperationException(ParkFailure(
                fixture, storeRoot, plan, runId,
                "the Run never reached step_waiting"));
        }

        // A park that cannot reach the durable step_waiting always explains
        // the Run it lost: the evidence goes to the child log through
        // Debug.LogError (a batchmode -logFile line) and returns into the
        // InvalidOperationException fatal.txt records — a failed arrange is
        // never a bare "state=Failed".
        private static string ParkFailure(
            RunnerHostFixture fixture, string storeRoot, CompiledPlan plan,
            string runId, string reason)
        {
            var evidence = reason + "; " +
                FailureEvidence(fixture, storeRoot, runId, plan);
            Debug.LogError("AT-F07 arrange park failed: " + evidence);
            return evidence;
        }

        // The failed Run's own explanation: the journal's failure record —
        // step_failed / step_needs_review / step_cancelled or a failed
        // run_finished carrying the summary's code and message — resolved
        // to the Plan step it names, the whole journal for context, and the
        // Run store's own load diagnostics (the fold verdict OW-STORE-002
        // and friends the in-memory state alone never shows).
        private static string FailureEvidence(
            RunnerHostFixture fixture, string storeRoot, string runId, CompiledPlan plan)
        {
            var journal = RunnerHostFixture.JournalPath(storeRoot, runId);
            string[] lines;
            try
            {
                lines = File.Exists(journal)
                    ? File.ReadAllLines(journal)
                    : Array.Empty<string>();
            }
            catch (Exception e)
            {
                return "the journal could not be read: " + e.GetType().Name +
                    " " + e.Message;
            }
            var parts = new List<string>();
            var failure = "";
            foreach (var line in lines)
            {
                if (line.Contains("\"type\":\"step_failed\"") ||
                    line.Contains("\"type\":\"step_needs_review\"") ||
                    line.Contains("\"type\":\"step_cancelled\"") ||
                    line.Contains("\"state\":\"failed\""))
                {
                    failure = line;
                }
            }
            parts.Add(failure.Length == 0
                ? "no failure record found"
                : "failure record" + StepName(failure, plan) + ": " + failure);
            parts.Add("journal[" + lines.Length + "]: " +
                (lines.Length == 0 ? "<empty>" : string.Join(" | ", lines)));
            try
            {
                var load = fixture.Host.Store.Load(runId);
                parts.Add("store.load " + (load.Ok ? "ok" : "failed") +
                    "; store diagnostics: " + DescribeCodes(load.Diagnostics));
            }
            catch (Exception e)
            {
                parts.Add("store.load threw " + e.GetType().Name +
                    ": " + e.Message);
            }
            return string.Join("; ", parts);
        }

        // The "step":N of a journal record resolved to the Plan step it
        // names — the NodeId/OperationId behind the bare index.
        private static string StepName(string record, CompiledPlan plan)
        {
            const string key = "\"step\":";
            var at = record.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return "";
            }
            at += key.Length;
            var end = at;
            while (end < record.Length && char.IsDigit(record[end]))
            {
                end++;
            }
            if (end == at ||
                !int.TryParse(record.Substring(at, end - at), out var step))
            {
                return "";
            }
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                if (plan.Steps[i].Index == step)
                {
                    return " [step " + step + " " + plan.Steps[i].NodeId +
                        " " + plan.Steps[i].OperationId + "]";
                }
            }
            return " [step " + step + "]";
        }

        // The "Editor restart" exit of OWP_EPH_EXIT=exit, or the blocked
        // park of =block whose only exits are the parent's Process.Kill, an
        // orderly stop file (test teardown), or the leak guard.
        private static void FinishArrange(string exitMode, string jobDir)
        {
            if (exitMode == "exit")
            {
                EditorApplication.Exit(0);
                return;
            }
            var stop = Path.Combine(jobDir, "child.stop");
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(MaxBlockMinutes);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(stop))
                {
                    EditorApplication.Exit(0);
                    return;
                }
                Thread.Sleep(200);
            }
            // The leak guard: a forgotten child must never outlive its test.
            EditorApplication.Exit(4);
        }

        // ── T-W4.6-04 matrix scenarios: the §F5 case-6 real Editor ──────
        // ── restart for all 20 standard Reconcilable Operations. ────────

        // The arrange-matrix child parks one [op -> hold] Run per standard
        // Reconcilable Operation, each on its own Run Store under the job
        // directory — the SPEC §E2 one-mutating-Run-per-store bound. Every
        // journal is left at the durable tail the Operation's Execute
        // wrote; the parent then cuts each back to step_started{step:0}
        // before the successor Editor launches — the mid-Execute restart
        // SPEC §F5 case 6 prescribes. The local Git fixture's dep URL and
        // commits arrive in matrix.json the parent wrote.
        private static void ArrangeMatrix(string jobDir)
        {
            var spec = ReadMatrixSpec(jobDir);
            var runs = new JObject();
            // The parked fixtures are NEVER disposed: each host's OS-held
            // coordinator exclusion must die with the process — the killed
            // child's dead-owner lock hand-off the successor Editor proves.
            var fixtures = new List<RunnerHostFixture>();
            foreach (var key in AtF07Matrix.OpKeys)
            {
                var store = Path.Combine(jobDir, "store-" + key);
                var fixture = new RunnerHostFixture(store);
                fixtures.Add(fixture);
                RegisterMatrixAll(fixture, spec, arrange: true);
                // The same saved, empty base scene the in-domain suite
                // runs every case from — an additive scene.create cannot
                // land while an untitled scene is open (OW-UNITY-011), and
                // the previous Operation's leftovers never leak into the
                // next pre-state.
                AtF07Matrix.EnsureBaseScene();
                var (steps, bind) = AtF07Matrix.ArrangeSteps(key, spec.Dep);
                var all = steps.Concat(new[]
                {
                    AtF07Matrix.Step(1, "h", AtF07Matrix.HoldOp, '4',
                        next: -1, idempotency: Idempotency.Idempotent),
                }).ToArray();
                var plan = RunnerHostFixture.Plan(
                    AtF07Matrix.WorkflowId, all);
                var doc = AtF07Matrix.Doc(
                    new[] { ("s", steps[0].OperationId), ("h", AtF07Matrix.HoldOp) },
                    ("s", "h"));
                var runId = Accept(fixture, plan, doc);
                bind?.Invoke(runId);
                Park(fixture, store, plan, runId);
                runs[key] = runId;
            }
            var checkpoint = new JObject { ["runs"] = runs };
            EditorProcessHarness.AtomicWriteFile(
                Path.Combine(jobDir, "checkpoint.json"),
                checkpoint.ToString(Newtonsoft.Json.Formatting.None));
        }

        // The verify-matrix successor: per store it folds the truncated
        // journal before recovery (the OW-STORE-002 / NeedsReview evidence
        // of §F3/§F2), runs the real §F4 pass, settles the resumed Run and
        // records the journal facts the parent's 20 case-6 assertions read.
        private static void VerifyMatrix(string jobDir)
        {
            var spec = ReadMatrixSpec(jobDir);
            var checkpoint = JObject.Parse(
                File.ReadAllText(Path.Combine(jobDir, "checkpoint.json")));
            var runs = (JObject)checkpoint["runs"]!;
            var verdicts = new JObject();
            foreach (var key in AtF07Matrix.OpKeys)
            {
                var runId = (string)runs[key]!;
                var store = Path.Combine(jobDir, "store-" + key);
                var verdict = new JObject { ["runId"] = runId };
                try
                {
                    var fixture = new RunnerHostFixture(store);
                    try
                    {
                        RegisterMatrixAll(fixture, spec, arrange: false);
                        var host = fixture.Host;
                        var fold = host.Store.Load(runId);
                        verdict["foldState"] =
                            fold.Ok && fold.Value is { } snap
                                ? snap.State.ToString() : "";
                        verdict["loadDiagnostics"] =
                            DescribeCodes(fold.Diagnostics);
                        var recovered = host.RecoverRuns();
                        verdict["recoveryOk"] = recovered.Ok;
                        verdict["exclusionHeld"] =
                            recovered.Ok && recovered.Value!.ExclusionHeld;
                        var state = "";
                        for (var i = 0; i < 10; i++)
                        {
                            var s = host.Coordinator.StateOf(runId);
                            if (s is RunState.Succeeded or RunState.Failed or
                                RunState.Cancelled or RunState.NeedsReview)
                            {
                                state = s.ToString()!;
                                break;
                            }
                            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                            if (!host.Tick().Ok)
                            {
                                break;
                            }
                        }
                        if (state.Length == 0)
                        {
                            state = host.Coordinator.StateOf(runId).ToString();
                        }
                        verdict["state"] = state;
                        var lines = RunnerHostFixture.JournalLines(store, runId);
                        verdict["runResumed"] = lines.Any(
                            l => l.Contains("\"type\":\"run_resumed\""));
                        verdict["stepNeedsReview"] = lines.Any(
                            l => l.Contains("\"type\":\"step_needs_review\"") &&
                                l.Contains("\"step\":0"));
                        verdict["succeededReconciled"] = lines.Any(
                            l => l.Contains("\"type\":\"step_succeeded\"") &&
                                l.Contains("\"step\":0") &&
                                l.Contains("\"reconciled\":true"));
                        verdict["stepSucceeded"] = lines.Count(
                            l => l.Contains("\"type\":\"step_succeeded\"") &&
                                l.Contains("\"step\":0"));
                    }
                    finally
                    {
                        fixture.Dispose();
                    }
                }
                catch (Exception e)
                {
                    verdict["error"] = e.ToString();
                }
                verdicts[key] = verdict;
            }
            EditorProcessHarness.AtomicWriteFile(
                Path.Combine(jobDir, "verdicts.json"),
                verdicts.ToString(Newtonsoft.Json.Formatting.None));
        }

        private sealed class MatrixSpec
        {
            internal string Dep = "";
            internal string Tagged = "";
            internal string Head = "";
        }

        private static MatrixSpec ReadMatrixSpec(string jobDir)
        {
            var parsed = JObject.Parse(
                File.ReadAllText(Path.Combine(jobDir, "matrix.json")));
            return new MatrixSpec
            {
                Dep = (string)parsed["dep"]!,
                Tagged = (string)parsed["tagged"]!,
                Head = (string)parsed["head"]!,
            };
        }

        // The matrix registration: every standard Reconcilable Operation at
        // its fingerprint seed plus the parked hold — package.add reads the
        // absent pre-state, package.remove the installed one (the verify
        // child's own views mirror what the successor Editor's UPM reads
        // after the restart: added stays added, removed stays removed).
        // The arrange child's package pre-states (add: absent — the request
        // has not run; remove: installed — the target exists) and the verify
        // child's post-restart reads (add: installed — the request landed;
        // remove: absent — the removal landed). Persisted package evidence
        // survives the process boundary: both end Completed-adopted.
        private static void RegisterMatrixAll(
            RunnerHostFixture fixture, MatrixSpec spec, bool arrange)
        {
            AtF07Matrix.RegisterAll(
                fixture,
                arrange
                    ? (Func<PackageInstallView>)AtF07Matrix.AbsentView
                    : () => AtF07Matrix.GitInstalledView(
                        spec.Dep, spec.Tagged, spec.Tagged),
                arrange
                    ? () => AtF07Matrix.GitInstalledView(
                        spec.Dep, spec.Tagged, spec.Tagged)
                    : (Func<PackageInstallView>)AtF07Matrix.AbsentView);
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind, PrefabContentsResources.Release);
        }

        // ── verify phase ────────────────────────────────────────────────

        private static void Verify(string jobDir, string storeRoot)
        {
            var checkpoint = ReadCheckpoint(jobDir);
            var runId = checkpoint.RunId;
            var fixture = new RunnerHostFixture(storeRoot);
            try
            {
                RegisterAll(fixture, jobDir);
                var host = fixture.Host;

                // The fold's own verdict, BEFORE the recovery pass touches
                // anything: §F3 item 3's OW-STORE-002 drop of a torn tail
                // and §F2's NeedsReview classification both surface here —
                // after the pass appends, the torn tail is already gone.
                var fold = host.Store.Load(runId);
                var foldState = fold.Ok && fold.Value is { } snapshot
                    ? snapshot.State.ToString()
                    : "";
                var loadDiagnostics = DescribeCodes(fold.Diagnostics);

                // A resumed waiting step polls against the file gate —
                // releasing it makes the wait completable, deterministically,
                // with no Editor-state dependency.
                File.WriteAllText(GateFilePath(jobDir), "go");

                var recovered = host.RecoverRuns();
                var recoveryOk = recovered.Ok;
                var report = recovered.Ok ? recovered.Value : null;
                var outcome = "";
                var outcomeState = "";
                if (report is not null)
                {
                    foreach (var result in report.Runs)
                    {
                        if (result.RunId == runId)
                        {
                            outcome = result.Outcome.ToString();
                            outcomeState = result.State?.ToString() ?? "";
                        }
                    }
                }

                // Drive the resumed Run to the state its evidence decides —
                // a resumed waiting step completes here, a NeedsReview Run
                // is already settled by the pass itself.
                var state = "";
                var ticks = 0;
                for (; ticks < MaxSettleTicks; ticks++)
                {
                    var current = host.Coordinator.StateOf(runId);
                    if (current is RunState.Succeeded or RunState.Failed or
                        RunState.Cancelled or RunState.NeedsReview)
                    {
                        state = current.ToString()!;
                        break;
                    }
                    fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                    var tick = host.Tick();
                    if (!tick.Ok)
                    {
                        break;
                    }
                }

                var verdict = new JObject
                {
                    ["runId"] = runId,
                    ["foldState"] = foldState,
                    ["loadDiagnostics"] = loadDiagnostics,
                    ["recoveryOk"] = recoveryOk,
                    ["exclusionHeld"] = report is { ExclusionHeld: true },
                    ["recoveryDiagnostics"] = DescribeCodes(recovered.Diagnostics),
                    ["outcome"] = outcome,
                    ["outcomeState"] = outcomeState,
                    ["state"] = state,
                    ["ticks"] = ticks,
                };
                EditorProcessHarness.AtomicWriteFile(
                    Path.Combine(jobDir, "verdict.json"),
                    verdict.ToString(Newtonsoft.Json.Formatting.None));
            }
            finally
            {
                fixture.Dispose();
            }
        }

        // ── registration, plans, files ──────────────────────────────────

        // The probe ops plus the real prefab pair at the SAME seeds every
        // phase registers them with — the resumed Run resolves its steps
        // against this identical synthetic snapshot, so the fingerprint pair
        // of each PlanStep must be stable across processes (and the seeds
        // must be lowercase hex: plan.json persists them as the 64-digit
        // descriptorHash/implementationHash). barrierOnSave wraps the real
        // save_contents in the in-save kill barrier of SPEC §G6.1 test 4 —
        // only the arrange-prefab-save child installs it.
        private static void RegisterAll(
            RunnerHostFixture fixture, string jobDir, bool barrierOnSave = false)
        {
            fixture.Register(
                new FileMarkOperation(MarkOpId, MarkFilePath(jobDir)), 'a');
            fixture.Register(
                new FileGateWaitableOperation(GateOpId, GateFilePath(jobDir)), '4');
            RegisterAs(fixture, new PrefabOpenContentsOperation(), '2');
            IOperation save = new PrefabSaveContentsOperation();
            if (barrierOnSave)
            {
                save = new InSaveBarrierOperation(save, jobDir);
            }
            RegisterAs(fixture, save, '6');
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind, PrefabContentsResources.Release);
        }

        private static void RegisterAs(
            RunnerHostFixture fixture, IOperation op, char hashSeed) =>
            fixture.Operations.Add(new RegisteredOperation(
                op.Descriptor,
                OperationOrigin.Standard,
                "Example.Operations",
                null,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64))
            {
                Implementation = op,
            });

        private static WorkflowDocument Doc(
            IReadOnlyList<(string id, string operation)> nodes,
            params (string from, string to)[] edges)
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = WorkflowId,
                Nodes = nodes
                    .Select(n => new NodeData
                    {
                        Id = n.id,
                        Operation = n.operation,
                        Version = 1,
                    })
                    .ToList(),
                ControlEdges = edges
                    .Select(e => new ControlEdge(e.from, e.to))
                    .ToList(),
            });
            if (!created.Ok || created.Value is not { } doc)
            {
                throw new InvalidOperationException(
                    "the scenario workflow document was rejected");
            }
            return doc;
        }

        private static string MarkFilePath(string jobDir) =>
            Path.Combine(jobDir, "step0.mark");

        private static string GateFilePath(string jobDir) =>
            Path.Combine(jobDir, "gate.release");

        private static void WriteCheckpoint(
            string jobDir, string runId, string? prefabPath, string? guid)
        {
            var checkpoint = new JObject { ["runId"] = runId };
            if (prefabPath is not null)
            {
                checkpoint["prefabPath"] = prefabPath;
            }
            if (guid is not null)
            {
                checkpoint["guid"] = guid;
            }
            EditorProcessHarness.AtomicWriteFile(
                Path.Combine(jobDir, "checkpoint.json"),
                checkpoint.ToString(Newtonsoft.Json.Formatting.None));
        }

        private sealed class Checkpoint
        {
            internal string RunId = "";
        }

        private static Checkpoint ReadCheckpoint(string jobDir)
        {
            var text = File.ReadAllText(Path.Combine(jobDir, "checkpoint.json"));
            var parsed = JObject.Parse(text);
            if (parsed["runId"] is not JValue { Type: JTokenType.String } id)
            {
                throw new InvalidOperationException(
                    "the checkpoint is missing runId: " + text);
            }
            return new Checkpoint { RunId = (string)id.Value! };
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return parts.Count == 0 ? "<none>" : string.Join("; ", parts);
        }

        private static string DescribeCodes(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(
                    diagnostic.Code + "|" + diagnostic.Severity + "|" +
                    diagnostic.Message);
            }
            return string.Join(";", parts);
        }

        // ── probe Operations ────────────────────────────────────────────

        /// <summary>
        /// An Immediate whose side effect is one appended line in a file:
        /// the "artifact" a re-executed step would produce a second of, which
        /// is how the suite counts duplicates across an interruption.
        /// </summary>
        private sealed class FileMarkOperation : IOperation
        {
            private readonly string _markPath;

            internal FileMarkOperation(string id, string markPath)
            {
                _markPath = markPath;
                Descriptor = Op.Define(id, 1)
                    .Summary("Appends one execution line to a mark file.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.AssetModify)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                File.AppendAllText(_markPath, "executed\n");
                ctx.SetOutput("mark", "written");
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException(
                    "Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// A Waitable gated on a file: Pending until the verify phase writes
        /// the release file, then done — a park boundary with zero
        /// Editor-state dependency, so the resumed poll completes
        /// deterministically in a fresh process.
        /// </summary>
        private sealed class FileGateWaitableOperation : IOperation
        {
            private readonly string _gatePath;

            internal FileGateWaitableOperation(string id, string gatePath)
            {
                _gatePath = gatePath;
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits until the release file exists.")
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
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(Json("\"held\""), "held", 1);

            public StepResult Poll(StepContext ctx) =>
                File.Exists(_gatePath)
                    ? StepResult.Succeeded()
                    : StepResult.Waiting(Json("\"held\""), "held", 1);

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// The deterministic in-save injection point of SPEC §G6.1 test 4:
        /// a pass-through over the real <c>unity.prefab.save_contents</c>
        /// (the Descriptor — and so the step's registered fingerprint — is
        /// the inner Operation's own) whose Execute first edits the opened
        /// contents for real — a save of unmodified contents can satisfy
        /// the import nothing-changed check and report no import the
        /// barrier could hold — then arms <see cref="InSaveImportBarrier"/>
        /// for this one execution window and runs the real
        /// <c>PrefabSaveContentsOperation.Execute</c>. The postprocessor
        /// parks the Editor inside the synchronous import
        /// <c>PrefabUtility.SaveAsPrefabAsset</c> performs — genuinely
        /// inside the save operation's execution — where the parent's
        /// Process.Kill lands: the write-ahead step_started{step:1} is
        /// already durable, no later record exists, exactly the durable
        /// state a forced termination during SaveAsPrefabAsset leaves.
        /// <c>&lt;job&gt;/child.stop</c> (test teardown) or the leak-guard
        /// deadline releases the barrier so a forgotten child can never
        /// hang forever.
        /// </summary>
        private sealed class InSaveBarrierOperation : IOperation
        {
            private readonly IOperation _inner;
            private readonly string _jobDir;

            internal InSaveBarrierOperation(IOperation inner, string jobDir)
            {
                _inner = inner;
                _jobDir = jobDir;
            }

            public OperationDescriptor Descriptor => _inner.Descriptor;

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                _inner.ValidateStatic(ctx);

            public PreflightResult Preflight(StepContext ctx) =>
                _inner.Preflight(ctx);

            public StepResult Execute(StepContext ctx)
            {
                var root = ctx.Get<GameObject>("root");
                if (root != null && root.GetComponent<SphereCollider>() == null)
                {
                    root.AddComponent<SphereCollider>();
                }
                InSaveImportBarrier.Arm(_jobDir, PrefabPath);
                try
                {
                    return _inner.Execute(ctx);
                }
                finally
                {
                    InSaveImportBarrier.Disarm();
                }
            }

            public StepResult Poll(StepContext ctx) => _inner.Poll(ctx);

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                _inner.Reconcile(ctx, snapshot);

            public void Cancel(StepContext ctx) => _inner.Cancel(ctx);
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok)
            {
                throw new InvalidOperationException("scenario JSON must parse");
            }
            return parsed.Value!;
        }
    }

    /// <summary>
    /// The kill point the in-save barrier of SPEC §G6.1 test 4 parks
    /// inside: a test-only <see cref="AssetPostprocessor"/> that fires
    /// inside the synchronous import
    /// <c>PrefabUtility.SaveAsPrefabAsset</c> performs — the genuine
    /// middle of <c>unity.prefab.save_contents</c>' Execute, where the
    /// parent's Process.Kill lands for real. It is armed only for the
    /// save step's own execution window and only for one matching import:
    /// on the save's import of the target Prefab it writes
    /// &lt;job&gt;/in-save.signal and holds the import until
    /// &lt;job&gt;/child.stop (test teardown) or the leak deadline. Every
    /// other import of the Editor passes through untouched, and a
    /// released barrier can never park a second import behind the
    /// teardown.
    /// </summary>
    internal sealed class InSaveImportBarrier : AssetPostprocessor
    {
        // The same leak-guard bound the arrange block uses: a forgotten
        // child must never outlive its test.
        private const int MaxBlockMinutes = 15;

        private static string? s_signalPath;
        private static string? s_stopPath;
        private static string? s_targetPath;

        // Arms the one-shot barrier for one Execute window: only an
        // import report listing the armed asset parks, and the arm is
        // cleared on that first match.
        internal static void Arm(string jobDir, string targetAssetPath)
        {
            s_signalPath = Path.Combine(jobDir, "in-save.signal");
            s_stopPath = Path.Combine(jobDir, "child.stop");
            s_targetPath = targetAssetPath;
        }

        internal static void Disarm()
        {
            s_signalPath = null;
            s_stopPath = null;
            s_targetPath = null;
        }

        // The earlier of the two points the synchronous import reports
        // the armed Prefab: inside the import pipeline, before the asset
        // is imported — the .prefab the save wrote is already on disk.
        private void OnPreprocessAsset()
        {
            var target = s_targetPath;
            if (target is not null &&
                string.Equals(assetPath, target, StringComparison.Ordinal))
            {
                Park();
            }
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            var target = s_targetPath;
            if (target is null)
            {
                return;
            }
            foreach (var imported in importedAssets)
            {
                if (string.Equals(imported, target, StringComparison.Ordinal))
                {
                    Park();
                    return;
                }
            }
        }

        // The import-side park: the signal reaches the parent first — the
        // in-save proof the whole case keys on — then the import is held
        // until the orderly stop or the leak deadline. The parent's
        // Process.Kill never returns from here, and that non-return is
        // the injection.
        private static void Park()
        {
            var signal = s_signalPath!;
            var stop = s_stopPath!;
            var target = s_targetPath!;
            Disarm();
            EditorProcessHarness.AtomicWriteFile(signal, "in-save");
            // The -logFile keeps the park's arrival even when the parent's
            // read of the signal races the kill.
            Debug.Log("AT-F07 in-save barrier parked on " + target);
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(MaxBlockMinutes);
            while (!File.Exists(stop) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }
}
