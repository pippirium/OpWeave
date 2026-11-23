#nullable enable

// T-W4.6-02 [Edit] child-side scenarios for the coordinator.lock / lock.json
// contract suite (SPEC §E2 item 3, §T.2 AT-F07 lock cases, §T.4 L-14): the
// -executeMethod entry a child Editor launched by EditorProcessHarness runs
// inside the cloned project. Like T-W4.6-01's InterruptionScenarioEntry the
// child builds a RunnerHostFixture over a Run Store root — the real
// FileRunStore, the real OS exclusion (share-mode-0 CreateFileW on Windows,
// exclusive flock(2) via FileShare.None on macOS), the real lock.json
// critical section and the real §F4 recovery engine; only the
// clock, the editor-state probe and the registry contents are test
// controlled.
//
// Scenarios (OWP_EPH_SCENARIO):
//   hold-park    [mark -> gate]: a mutating Run parked on step 1's durable
//                step_waiting while the live process owns coordinator.lock.
//                OWP_EPH_EXIT decides how the child ends its hold:
//                  block      waits for child.stop — the parent either stops
//                             it or kills it, the two owner endings the
//                             contract cases need (live owner / abnormal
//                             termination).
//                  block-tick keeps ticking so the parent's gate.release
//                             finishes the Run inside the SAME live process —
//                             the live-owner Run the contender must never
//                             disturb, ended by the real Release() path.
//                  reload     waits for <job>/reload.go, then compiles a
//                             marker script in Assets so the Editor performs
//                             a REAL domain reload while the OS exclusion is
//                             held. The managed fixture dies with the old
//                             domain; the OS handle (CreateFileW on Windows,
//                             the finalization-suppressed FileStream's flock
//                             descriptor on macOS) is what must survive
//                             (SPEC §E2, §T.4 L-14). The window
//                             IS the domain reload, bracketed by wall-clock
//                             marks: a beforeAssemblyReload handler — run
//                             inside the teardown itself — writes
//                             <job>/reload-begin.signal and parks on
//                             <job>/reload-window.release, holding the
//                             reload open until the contender has recorded
//                             a mutating-accept attempt strictly inside
//                             it; the new domain's [InitializeOnLoadMethod]
//                             then writes <job>/post-reload.signal and
//                             <job>/reload-end.signal and waits for
//                             child.stop.
//   finish-run   the same plan with the gate released: the Run runs to a
//                journaled Succeeded inside one live process — the terminal
//                Run a contradictory lock.json residue may name.
//   contend      the second Editor: its startup recovery must be refused
//                wholesale (WithoutExclusion / OW-RUN-002), its mutating Run
//                accept refused OW-RUN-002 — attempted once before
//                contender-ready.signal and again on every poll, each
//                attempt appended to accept-attempts.log with the wall-clock
//                UTC ticks bracketing the whole AcceptRun call and the
//                outcome — and every direct CoordinatorRunLock.TryAcquire
//                probe denied while the owner lives — counted across the
//                parent's window (and across the peer's domain reload when
//                OWP_EPH_PEERJOB names the owner's job dir). Writes
//                verdict.json.
//   update-lock  performs the REAL lock.json update on the store the
//                checkpoint names — CoordinatorRunLock.WriteState inside the
//                held exclusion, the same replace the coordinator's
//                TryStartNext and the recovery takeover perform.
//                OWP_EPH_UPDATEMODE decides where the parent kills it:
//                  pre   the CriticalSectionProbe parks INSIDE the critical
//                        section before the atomic replace lands — the kill
//                        leaves the pre-update complete lock.json.
//                  post  the write completes, updated.signal carries the new
//                        editorSessionId, and the child holds the exclusion
//                        until killed — the kill leaves the post-update
//                        complete lock.json.
//   verify       the successor Editor: folds the journal BEFORE recovery,
//                captures lock.json before and after the real §F4 pass
//                (pre-recovery residue / the takeover re-recording / the
//                post-release emptying), ticks the decided Run to its end
//                and writes verdict.json.
//
// Exit protocol: children write fatal.txt and EditorApplication.Exit(3) on
// any failure — a hung or crashing child is a test failure, never a silent
// pass.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Process = System.Diagnostics.Process;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>The batchmode <c>-executeMethod</c> entry point of the child
    /// Editor processes spawned by the lock-contract suite.</summary>
    public static class LockContractScenarioEntry
    {
        private const string WorkflowId = "test.lock.contract";
        private const string MarkOpId = "test.lock.filemark";
        private const string GateOpId = "test.lock.gatewait";

        // The two OWP_EPH_* names this suite adds to the shared harness
        // contract: the peer job dir a contender watches for the owner's
        // domain-reload signal, and the update-lock kill placement.
        internal const string PeerJobEnvVar = "OWP_EPH_PEERJOB";
        internal const string UpdateModeEnvVar = "OWP_EPH_UPDATEMODE";

        private const int MaxParkTicks = 40;
        private const int MaxSettleTicks = 200;
        private const int MaxBlockMinutes = 15;
        // The parked gate step's fake-clock timeout: BlockTicking advances the
        // fixture clock 1 s per ~150 ms Tick, so the builder default (30 s)
        // fails the owner's Run ~4 real seconds after it parks — long before a
        // second Editor finishes booting — and the contender then takes the
        // freed exclusion legally. The step must outlive the WHOLE bounded
        // contention window in fake seconds: the file gate, never the clock,
        // is what releases it (SPEC §E2 test 3 (2): the owner must still be
        // live and holding when the contender's denials are measured).
        private const int GateTimeoutSeconds = 7200;
        private const int ContendPollMs = 120;

        /// <summary>The -executeMethod entry: dispatches on OWP_EPH_SCENARIO.</summary>
        public static void Run()
        {
            // SPEC §G1 item 3 (T-W4.6-06): an asset import worker
            // inherits the launched child Editor's whole environment —
            // OWP_EPH_* included — but it is never the process the
            // parent spawned for the scenario, so no scenario runs in it.
            if (AssetDatabase.IsAssetImportWorkerProcess())
            {
                return;
            }
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
                    case "hold-park":
                        HoldPark(jobDir, storeRoot, exitMode);
                        break;
                    case "finish-run":
                        FinishRun(jobDir, storeRoot);
                        EditorApplication.Exit(0);
                        break;
                    case "contend":
                        Contend(jobDir, storeRoot);
                        EditorApplication.Exit(0);
                        break;
                    case "update-lock":
                        UpdateLock(jobDir, storeRoot);
                        break;
                    case "verify":
                        Verify(jobDir, storeRoot);
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
                Debug.LogError("L-14 child fatal: " + exception);
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

        /// <summary>
        /// The post-reload half of hold-park/reload — an
        /// [InitializeOnLoadMethod] runs in every domain the test assembly
        /// loads into, so it is gated four ways: never in an asset import
        /// worker process (SPEC §G1 item 3 — on Unity 6000.5 the owner
        /// Editor spawns AssetImportWorker* processes that inherit the
        /// scenario environment and would otherwise write the owner's
        /// signals before the real reload ever began, T-W4.6-06), only the
        /// reload-mode holder child (env vars survive a domain reload —
        /// the OS process lives on, exactly like the lock handle under
        /// test), only in the process whose own id the old domain recorded
        /// when it armed the reload, and only once. It writes
        /// post-reload.signal — the parent's proof the domain was destroyed
        /// and rebuilt while coordinator.lock stayed held — and
        /// reload-end.signal, the window's closing wall-clock mark paired
        /// with the reload-begin.signal the old domain's
        /// beforeAssemblyReload handler wrote inside the teardown, then
        /// waits for child.stop or the leak deadline.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void AfterDomainReload()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
            {
                return;
            }
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EditorProcessHarness.ScenarioEnvVar),
                    "hold-park", StringComparison.Ordinal) ||
                !string.Equals(
                    Environment.GetEnvironmentVariable(EditorProcessHarness.ExitModeEnvVar),
                    "reload", StringComparison.Ordinal))
            {
                return;
            }
            var jobDir = Environment.GetEnvironmentVariable(
                EditorProcessHarness.JobDirEnvVar) ?? "";
            if (jobDir.Length == 0 ||
                !File.Exists(Path.Combine(jobDir, "reload.armed")))
            {
                return;
            }
            // The arming owner recorded its own process id inside
            // reload.armed; the hook acts only in the post-reload domain
            // of that SAME process — any other process that inherited the
            // scenario environment (an import worker whose API guard did
            // not run, or any second Editor pointed at this job dir)
            // writes no signal.
            if (!ArmedInThisProcess(jobDir))
            {
                return;
            }
            var signal = Path.Combine(jobDir, "post-reload.signal");
            if (File.Exists(signal))
            {
                return;
            }
            EditorProcessHarness.AtomicWriteFile(signal, "reloaded");
            // The window's end mark — the new domain's first durable act:
            // a contender attempt whose wall-clock record falls strictly
            // between the beforeAssemblyReload begin mark and this mark
            // landed inside the domain reload itself (SPEC §E2 test 3
            // case 6).
            EditorProcessHarness.AtomicWriteFile(
                Path.Combine(jobDir, "reload-end.signal"),
                DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            Debug.Log("L-14 domain reload completed while coordinator.lock was held");
            var stop = Path.Combine(jobDir, "child.stop");
            var deadline = EditorApplication.timeSinceStartup +
                TimeSpan.FromMinutes(MaxBlockMinutes).TotalSeconds;
            EditorApplication.update += Watch;
            void Watch()
            {
                if (!File.Exists(stop) &&
                    EditorApplication.timeSinceStartup < deadline)
                {
                    return;
                }
                EditorApplication.update -= Watch;
                EditorApplication.Exit(0);
            }
        }

        // ── hold-park ───────────────────────────────────────────────────

        // [mark -> gate]: step 0 appends one execution line to the job's mark
        // file, step 1 parks on a durable step_waiting — the mid-flight Run
        // whose coordinator.lock ownership the whole contract probes.
        private static void HoldPark(string jobDir, string storeRoot, string exitMode)
        {
            var fixture = new RunnerHostFixture(storeRoot);
            RegisterAll(fixture, jobDir);
            var plan = MarkGatePlan();
            var doc = Doc(
                new[] { ("mark", MarkOpId), ("hold", GateOpId) },
                ("mark", "hold"));
            var runId = Accept(fixture, plan, doc);
            Park(fixture, storeRoot, runId);
            WriteCheckpoint(jobDir, runId, fixture.SessionId);
            switch (exitMode)
            {
                case "block-tick":
                    BlockTicking(fixture, jobDir, runId);
                    return;
                case "reload":
                    ReloadWhileHeld(jobDir);
                    return;
                default:
                    BlockForKill(jobDir);
                    return;
            }
        }

        // The parked hold: the Run stays mid-flight, the exclusion stays
        // held, and the only exits are the parent's Process.Kill, the
        // orderly child.stop (test teardown), or the leak guard.
        private static void BlockForKill(string jobDir)
        {
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
            EditorApplication.Exit(4);
        }

        // The ticking hold: the live owner keeps driving its parked Run so
        // the parent's gate.release finishes it here — the live-owner Run a
        // contender's denied attempts must never disturb. finished.signal
        // proves the terminal state was reached in THIS process.
        private static void BlockTicking(
            RunnerHostFixture fixture, string jobDir, string runId)
        {
            var stop = Path.Combine(jobDir, "child.stop");
            var finished = Path.Combine(jobDir, "finished.signal");
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(MaxBlockMinutes);
            var signaled = false;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(stop))
                {
                    EditorApplication.Exit(0);
                    return;
                }
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                var tick = fixture.Host.Tick();
                if (!tick.Ok)
                {
                    throw new InvalidOperationException(
                        "tick failed inside the live owner: " +
                        Describe(tick.Diagnostics));
                }
                var state = fixture.Host.Coordinator.StateOf(runId);
                if (!signaled && state is RunState.Succeeded or RunState.Failed or
                    RunState.Cancelled or RunState.NeedsReview)
                {
                    EditorProcessHarness.AtomicWriteFile(
                        finished, state.ToString()!);
                    signaled = true;
                }
                Thread.Sleep(150);
            }
            EditorApplication.Exit(4);
        }

        // The reload hold: the domain is destroyed and rebuilt while the OS
        // exclusion is held. The parent's reload.go is the arming point —
        // the marker script write + synchronous refresh then makes the
        // Editor compile and reload for real. Nothing here disposes the
        // fixture: the whole point is that the managed objects die with the
        // old domain while the OS handle lives on — the raw CreateFileW
        // handle on Windows, the finalization-suppressed FileStream's flock
        // descriptor on macOS.
        private static void ReloadWhileHeld(string jobDir)
        {
            // reload.armed doubles as the arming record: its content is
            // THIS process's own id. The [InitializeOnLoadMethod] hook
            // fires in every domain the assembly loads into — on 6000.5
            // including the owner's asset import workers, which inherit
            // this whole environment — and only the post-reload domain of
            // the arming process itself may write the signals (T-W4.6-06).
            EditorProcessHarness.AtomicWriteFile(
                Path.Combine(jobDir, "reload.armed"),
                Process.GetCurrentProcess().Id.ToString(
                    CultureInfo.InvariantCulture));
            var go = Path.Combine(jobDir, "reload.go");
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(MaxBlockMinutes);
            while (!File.Exists(go) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            if (!File.Exists(go))
            {
                EditorApplication.Exit(4);
                return;
            }
            // The measured window is the domain reload itself (SPEC §E2
            // test 3 case 6): beforeAssemblyReload runs on the main thread
            // after the reload has STARTED — the old domain is already
            // being torn down — so the handler's begin mark opens the
            // window inside the reload, and its park on the test-side gate
            // holds the teardown open until the parent releases
            // reload-window.release — which it does only once a contender
            // attempt strictly after the mark is durably recorded. The
            // contract's "attempt to start a Run during the domain reload"
            // then lands inside the reload deterministically, never in the
            // gap before it. The subscription is never removed: the domain
            // it belongs to is the one being unloaded.
            var windowRelease = Path.Combine(jobDir, "reload-window.release");
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            void OnBeforeReload()
            {
                EditorProcessHarness.AtomicWriteFile(
                    Path.Combine(jobDir, "reload-begin.signal"),
                    DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                while (!File.Exists(windowRelease) &&
                       DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(200);
                }
                if (File.Exists(windowRelease))
                {
                    return;
                }
                // Bounded park, never a hang: the parent's release wait has
                // already failed on its own assertion — record the fatal
                // and let the teardown finish so teardown cleanup can stop
                // the process.
                EditorProcessHarness.AtomicWriteFile(
                    Path.Combine(jobDir, "fatal.txt"),
                    "reload-window.release never arrived inside " +
                    "beforeAssemblyReload");
            }
            var markerDir = Path.Combine(Application.dataPath, "OpWeaveLockReload");
            Directory.CreateDirectory(markerDir);
            File.WriteAllText(
                Path.Combine(markerDir, "LockReloadMarker.cs"),
                "namespace OpWeaveLockReloadProbe\n{\n" +
                "    internal static class Marker\n    {\n    }\n}\n");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            CompilationPipeline.RequestScriptCompilation();
            // Return without Exit: the pending compile drives the real domain
            // reload; the new domain's AfterDomainReload reports it.
        }

        // ── finish-run ──────────────────────────────────────────────────

        // The same [mark -> gate] plan run to its journaled Succeeded inside
        // one live process — the terminal Run the crash-during-update
        // contradiction residue may name.
        private static void FinishRun(string jobDir, string storeRoot)
        {
            var fixture = new RunnerHostFixture(storeRoot);
            try
            {
                RegisterAll(fixture, jobDir);
                File.WriteAllText(GateFilePath(jobDir), "go");
                var plan = MarkGatePlan();
                var doc = Doc(
                    new[] { ("mark", MarkOpId), ("hold", GateOpId) },
                    ("mark", "hold"));
                var runId = Accept(fixture, plan, doc);
                WriteCheckpoint(jobDir, runId, fixture.SessionId);
                var state = RunState.Queued;
                for (var i = 0; i < MaxSettleTicks; i++)
                {
                    var current = fixture.Host.Coordinator.StateOf(runId);
                    if (current is RunState.Succeeded or RunState.Failed or
                        RunState.Cancelled or RunState.NeedsReview)
                    {
                        state = current!.Value;
                        break;
                    }
                    fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                    var tick = fixture.Host.Tick();
                    if (!tick.Ok)
                    {
                        throw new InvalidOperationException(
                            "tick failed inside finish-run: " +
                            Describe(tick.Diagnostics));
                    }
                }
                if (state != RunState.Succeeded)
                {
                    throw new InvalidOperationException(
                        "the finish-run Run did not succeed: state=" + state);
                }
                var flushed = fixture.Host.FlushJournal();
                if (!flushed.Ok)
                {
                    throw new InvalidOperationException(
                        "journal flush failed: " + Describe(flushed.Diagnostics));
                }
            }
            finally
            {
                fixture.Dispose();
            }
        }

        // ── contend ─────────────────────────────────────────────────────

        // The second Editor on the same Run Store root: startup recovery,
        // an accepted mutating Run, and direct OS acquisition probes — all
        // of which the live owner's exclusion must refuse for the whole
        // contention window the parent leaves open (and across the peer's
        // domain reload when OWP_EPH_PEERJOB points at the owner job).
        private static void Contend(string jobDir, string storeRoot)
        {
            var fixture = new RunnerHostFixture(storeRoot);
            try
            {
                RegisterAll(fixture, jobDir);
                var host = fixture.Host;

                // SPEC §E2 item 3 (a): recovery without the exclusion resumes
                // nothing — the report must carry the OW-RUN-002 refusal. The
                // pass reports its decisions in RunRecoveryReport.Diagnostics;
                // the outer Result's diagnostics are the transport's, not the
                // pass's.
                var recovered = host.RecoverRuns();
                var report = recovered.Ok ? recovered.Value : null;
                var exclusionHeld = report is { ExclusionHeld: true };

                // SPEC §E2 item 3 (a): "新しい Run は OW-RUN-002 で拒否し" —
                // while the live owner's exclusion cannot be obtained the
                // mutating accept itself is refused; nothing is left for the
                // start gate to pick up either.
                var plan = MarkGatePlan();
                var doc = Doc(
                    new[] { ("mark", MarkOpId), ("hold", GateOpId) },
                    ("mark", "hold"));
                var attemptsLog = Path.Combine(jobDir, "accept-attempts.log");
                var firstStart = DateTime.UtcNow.Ticks;
                var accepted = host.AcceptRun(
                    plan, doc, ActorKind.Agent, new AcceptOptions(null, false));
                var firstEnd = DateTime.UtcNow.Ticks;
                var runId = accepted.Ok && accepted.Value is { } handle
                    ? handle.RunId
                    : "";
                // The append-only attempt log: every mutating-accept attempt
                // — this first one and one per poll below — is a committed
                // record of the wall-clock UTC ticks bracketing the WHOLE
                // AcceptRun call, so the parent can place the call itself
                // inside the owner's reload window, not just the record's
                // stamp (SPEC §E2 test 3 case 6).
                RecordAcceptAttempt(attemptsLog, firstStart, firstEnd, accepted);

                // Ready means "the probe loop starts now": the parent's
                // contention window opens only once every attempt the loop
                // makes is inside it.
                EditorProcessHarness.AtomicWriteFile(
                    Path.Combine(jobDir, "contender-ready.signal"), "ready");

                var peerDir = Environment.GetEnvironmentVariable(PeerJobEnvVar) ?? "";
                var peerReloadSignal = peerDir.Length == 0
                    ? null
                    : Path.Combine(peerDir, "post-reload.signal");
                var peerReloaded = false;
                var peerProbeSignaled = false;
                var stop = Path.Combine(jobDir, "contend.stop");
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(MaxBlockMinutes);
                var attempts = 0;
                var denied = 0;
                var beforeReload = 0;
                var afterReload = 0;
                var tickFailures = 0;
                var acquiredAny = false;
                var firstDenialCode = "";
                while (!File.Exists(stop) && DateTime.UtcNow < deadline)
                {
                    if (!peerReloaded && peerReloadSignal is { } signal &&
                        File.Exists(signal))
                    {
                        peerReloaded = true;
                    }
                    var acquired = CoordinatorRunLock.TryAcquire(storeRoot);
                    attempts++;
                    if (peerReloaded)
                    {
                        afterReload++;
                        // The L-14 post-reload acknowledgement: the parent's
                        // own read of post-reload.signal cannot order this
                        // process's asynchronous poll, so the first probe
                        // made strictly after the peer's new domain reported
                        // itself is acknowledged by file — the parent's
                        // contention window closes on it, never on its own
                        // observation (SPEC §T.4 L-14's "リロードでは解放しない").
                        if (!peerProbeSignaled && peerReloadSignal is not null)
                        {
                            peerProbeSignaled = true;
                            EditorProcessHarness.AtomicWriteFile(
                                Path.Combine(jobDir, "probed-after-reload.signal"),
                                "probed");
                        }
                    }
                    else
                    {
                        beforeReload++;
                    }
                    if (acquired.Ok && acquired.Value is { } held)
                    {
                        acquiredAny = true;
                        held.Dispose();
                    }
                    else
                    {
                        denied++;
                        if (firstDenialCode.Length == 0)
                        {
                            firstDenialCode = FirstCode(acquired.Diagnostics);
                        }
                    }
                    // SPEC §E2 test 3 case 6: the second process ATTEMPTS TO
                    // START a mutating Run on every poll — across the peer's
                    // domain-reload window too — never merely probing the
                    // raw exclusion. A refused accept persists nothing (the
                    // §E2 item 3 probe is pre-persistence), so the log is
                    // pure observation. The recorded ticks bracket the call
                    // itself: the parent's window test proves the whole
                    // AcceptRun ran inside the reload, never only that the
                    // record was stamped inside it.
                    var attemptStart = DateTime.UtcNow.Ticks;
                    var attempt = host.AcceptRun(
                        plan, doc, ActorKind.Agent,
                        new AcceptOptions(null, false));
                    var attemptEnd = DateTime.UtcNow.Ticks;
                    RecordAcceptAttempt(
                        attemptsLog, attemptStart, attemptEnd, attempt);
                    var tick = host.Tick();
                    if (!tick.Ok)
                    {
                        tickFailures++;
                    }
                    Thread.Sleep(ContendPollMs);
                }

                var runState = runId.Length == 0
                    ? ""
                    : host.Coordinator.StateOf(runId)?.ToString() ?? "";
                var verdict = new JObject
                {
                    ["recoveryOk"] = recovered.Ok,
                    ["exclusionHeld"] = exclusionHeld,
                    ["recoveryDiagnostics"] = DescribeCodes(
                        report?.Diagnostics ?? recovered.Diagnostics),
                    ["acceptOk"] = accepted.Ok,
                    ["acceptDiagnostics"] = DescribeCodes(accepted.Diagnostics),
                    ["runId"] = runId,
                    ["runState"] = runState,
                    ["attempts"] = attempts,
                    ["denied"] = denied,
                    ["acquiredAny"] = acquiredAny,
                    ["firstDenialCode"] = firstDenialCode,
                    ["attemptsBeforePeerReload"] = beforeReload,
                    ["attemptsAfterPeerReload"] = afterReload,
                    ["markExists"] = File.Exists(MarkFilePath(jobDir)),
                    ["tickFailures"] = tickFailures,
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

        // ── update-lock ─────────────────────────────────────────────────

        // The real lock.json update on the store the checkpoint names, killed
        // by the parent at the two placements SPEC §E2 item 3 calls out:
        // inside the critical section before the atomic replace lands ("pre"
        // — the old complete file survives) and after it returned ("post" —
        // the new complete file survives). Both halves of "a crash during
        // the update leaves either the old or the new complete content".
        private static void UpdateLock(string jobDir, string storeRoot)
        {
            var checkpoint = ReadCheckpoint(jobDir);
            var mode = Environment.GetEnvironmentVariable(UpdateModeEnvVar) ?? "pre";
            var acquired = CoordinatorRunLock.TryAcquire(storeRoot);
            if (!acquired.Ok || acquired.Value is not { } held)
            {
                throw new InvalidOperationException(
                    "update-lock could not acquire the exclusion: " +
                    Describe(acquired.Diagnostics));
            }
            var newSession = "session-" + Guid.NewGuid().ToString("N");
            if (mode == "post")
            {
                var written = held.WriteState(
                    RunLockState.AcquiredNow(checkpoint.RunId, newSession));
                if (!written.Ok)
                {
                    held.Dispose();
                    throw new InvalidOperationException(
                        "update-lock WriteState failed: " +
                        Describe(written.Diagnostics));
                }
                EditorProcessHarness.AtomicWriteFile(
                    Path.Combine(jobDir, "updated.signal"), newSession);
                BlockForKill(jobDir);
                return;
            }
            // "pre": the probe parks inside the real WriteState critical
            // section — before ReplaceAtomically — so the parent's kill lands
            // genuinely inside the lock.json update.
            held.CriticalSectionProbe = stage =>
            {
                if (!string.Equals(stage, "write", StringComparison.Ordinal))
                {
                    return;
                }
                EditorProcessHarness.AtomicWriteFile(
                    Path.Combine(jobDir, "in-update.signal"), "in-update");
                BlockForKill(jobDir);
            };
            var write = held.WriteState(
                RunLockState.AcquiredNow(checkpoint.RunId, newSession));
            // Reached only when the teardown stop file released the park —
            // a killed child never returns here.
            held.Dispose();
            if (!write.Ok)
            {
                throw new InvalidOperationException(
                    "update-lock WriteState failed: " + Describe(write.Diagnostics));
            }
            EditorApplication.Exit(0);
        }

        // ── verify ──────────────────────────────────────────────────────

        // The successor Editor: the same fold-then-recover-then-settle shape
        // as the AT-F07 verify child, plus the lock.json bytes around the §F4
        // pass — the takeover re-recording (§F4 item 6) and the release
        // emptying (§E2 item 3) are lock.json evidence the verdict carries.
        private static void Verify(string jobDir, string storeRoot)
        {
            var checkpoint = ReadCheckpoint(jobDir);
            var runId = checkpoint.RunId;
            var fixture = new RunnerHostFixture(storeRoot);
            try
            {
                RegisterAll(fixture, jobDir);
                var host = fixture.Host;

                // The fold's own verdict BEFORE the pass touches anything.
                var fold = host.Store.Load(runId);
                var foldState = fold.Ok && fold.Value is { } snapshot
                    ? snapshot.State.ToString()
                    : "";
                var loadDiagnostics = DescribeCodes(fold.Diagnostics);
                var lockJsonBefore = ReadLockJson(storeRoot);

                // A resumed waiting step polls against the file gate —
                // releasing it makes the wait completable, deterministically.
                File.WriteAllText(GateFilePath(jobDir), "go");

                var recovered = host.RecoverRuns();
                var recoveryOk = recovered.Ok;
                var report = recovered.Ok ? recovered.Value : null;
                var lockJsonPostRecovery = ReadLockJson(storeRoot);
                // The recorded owner the pass arbitrated on: for the
                // "lock.json だけが残っている" case (SPEC §E2 test 4) this is
                // the checkpoint's run — its whole record is gone by design,
                // so the fold above answers OW-RUN-010 and the report is the
                // pass's inspection of what remains.
                var residueRunId = RecordedOwnerRunId(lockJsonBefore);
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

                // Drive the resumed Run to the state its evidence decides.
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
                    ["sessionId"] = fixture.SessionId,
                    ["foldState"] = foldState,
                    ["loadDiagnostics"] = loadDiagnostics,
                    ["lockJsonBefore"] = lockJsonBefore,
                    ["lockJsonPostRecovery"] = lockJsonPostRecovery,
                    ["lockJsonFinal"] = ReadLockJson(storeRoot),
                    ["recoveryOk"] = recoveryOk,
                    ["exclusionHeld"] = report is { ExclusionHeld: true },
                    ["recoveryDiagnostics"] = DescribeCodes(
                        report?.Diagnostics ?? recovered.Diagnostics),
                    ["residueRunId"] = residueRunId,
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

        // ── plan, registration, protocol ────────────────────────────────

        // The [mark -> gate] plan: one mutating Immediate whose artifact is
        // a countable file line (duplicates are the suite's re-execution
        // detector) and one Waitable gated on a file the parent controls.
        private static CompiledPlan MarkGatePlan()
        {
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
                    idempotency: Idempotency.Idempotent)
                    with { TimeoutSeconds = GateTimeoutSeconds },
            };
            return RunnerHostFixture.Plan(WorkflowId, steps);
        }

        // The probe ops at the SAME seeds every phase registers them with —
        // the resumed Run resolves its steps against this identical snapshot,
        // so each PlanStep's fingerprint pair is stable across processes.
        private static void RegisterAll(RunnerHostFixture fixture, string jobDir)
        {
            fixture.Register(
                new FileMarkOperation(MarkOpId, MarkFilePath(jobDir)), 'a');
            fixture.Register(
                new FileGateWaitableOperation(GateOpId, GateFilePath(jobDir)), '4');
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
        // journal's last record — or fails: the checkpoint only counts once
        // the mid-flight state is really on disk.
        private static void Park(
            RunnerHostFixture fixture, string storeRoot, string runId)
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
                    ? EditorProcessHarness.ReadAllLinesShared(journal)
                    : Array.Empty<string>();
                if (lines.Length != 0 &&
                    lines[lines.Length - 1].Contains("\"type\":\"step_waiting\""))
                {
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
                    throw new InvalidOperationException(
                        "the Run left Running without parking: state=" + state +
                        "; journal: " +
                        (File.Exists(journal)
                            ? string.Join(
                                " | ",
                                EditorProcessHarness.ReadAllLinesShared(journal))
                            : "<none>"));
                }
            }
            throw new InvalidOperationException(
                "the Run never reached step_waiting; journal: " +
                (File.Exists(journal)
                    ? string.Join(
                        " | ",
                        EditorProcessHarness.ReadAllLinesShared(journal))
                    : "<none>"));
        }

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

        // lock.json as raw bytes — the protocol's own observation channel,
        // never the exclusion's read path (production reads it only inside
        // the held critical section; the verdict records what the FILE held
        // before and after the pass).
        private static string ReadLockJson(string storeRoot)
        {
            var path = Path.Combine(storeRoot, CoordinatorRunLock.StateFileName);
            return File.Exists(path)
                ? EditorProcessHarness.ReadAllTextShared(path)
                : "";
        }

        // The recorded owner's runId out of a raw lock.json payload, or "" —
        // read by JSON so the verdict states WHICH run the residue names
        // without trusting the checkpoint when they could differ.
        private static string RecordedOwnerRunId(string lockJson)
        {
            if (lockJson.Length == 0)
            {
                return "";
            }
            try
            {
                return JObject.Parse(lockJson)["runId"]?.ToString() ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static void WriteCheckpoint(
            string jobDir, string runId, string sessionId)
        {
            var checkpoint = new JObject
            {
                ["runId"] = runId,
                ["sessionId"] = sessionId,
            };
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
            var text = EditorProcessHarness.ReadAllTextShared(
                Path.Combine(jobDir, "checkpoint.json"));
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

        // The process binding of the arming record (T-W4.6-06): the owner
        // wrote its own process id into reload.armed when it armed the
        // reload, so the post-reload hook acts only in the domain of that
        // same OS process. An unreadable or unparsable record is not an
        // arming of this process — fail closed, write no signal.
        private static bool ArmedInThisProcess(string jobDir)
        {
            try
            {
                var text = EditorProcessHarness.ReadAllTextShared(
                    Path.Combine(jobDir, "reload.armed"));
                return int.TryParse(
                           text.Trim(), NumberStyles.None,
                           CultureInfo.InvariantCulture, out var pid) &&
                       pid == Process.GetCurrentProcess().Id;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string FirstCode(IReadOnlyList<Diagnostic> diagnostics) =>
            diagnostics.Count == 0 ? "" : diagnostics[0].Code;

        // One committed attempt record: "<startTicks> <endTicks> accepted
        // <runId>" or "<startTicks> <endTicks> denied <first-code>" — start
        // is taken immediately before AcceptRun and end immediately after
        // it returns, so the pair brackets the whole call the parent's
        // [reload-begin, reload-end) window must contain — appended under
        // the same share discipline as the store's own journal appends, so
        // the parent's observing reads never deny the next append.
        private static void RecordAcceptAttempt(
            string attemptsLog, long startTicks, long endTicks,
            Result<RunHandle> attempt)
        {
            var outcome = attempt.Ok && attempt.Value is { } handle
                ? "accepted " + handle.RunId
                : "denied " + FirstCode(attempt.Diagnostics);
            EditorProcessHarness.AppendLineShared(
                attemptsLog,
                startTicks.ToString(CultureInfo.InvariantCulture) + " " +
                endTicks.ToString(CultureInfo.InvariantCulture) + " " +
                outcome);
        }

        // ── probe Operations ────────────────────────────────────────────

        /// <summary>
        /// An Immediate whose side effect is one appended line in a file:
        /// the "artifact" a re-executed step would produce a second of, which
        /// is how the suite counts duplicate executions across processes.
        /// Its AssetModify effect is what makes the Run mutating — the
        /// SPEC §E2 exclusion class.
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
        /// A Waitable gated on a file: Pending until the parent or the verify
        /// phase writes the release file — the park boundary with zero
        /// Editor-state dependency this suite's holds and resumes need.
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
}
