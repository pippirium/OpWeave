#nullable enable
// T-W1.7-05 [Core] tests — local:T-W1.7-05:os-lock-smoke-windows.
//
// The Windows leg of the SPEC §T.4 L-14 method check for the
// coordinator.lock inter-process exclusion (SPEC §E2 item 3): the
// CoordinatorRunLock of T-W1.7-04 is exercised from real second OS
// processes — `dotnet OpWeave.LockSmoke.dll`, a minimal console host over
// the product Core assembly built under dotnet/ — never a reimplemented
// or simulated lock.
//
// Two scenarios, ten iterations each (the ticket's Verification method):
//   * contention — two child processes released on a start gate race
//     TryAcquire on the same lock root: exactly one may hold ("never both
//     holding"), the loser must be denied with OW-RUN-002;
//   * kill-holder — a child acquires, records lock.json and signals
//     "holding"; a third process is denied while it lives; the holder is
//     then killed and the OS must release the share-mode-0 handle so a
//     second process acquires — with the dead holder's recorded state
//     surviving in lock.json for recovery (SPEC §E2/§F4).
//
// A simultaneous hold, or a lock the OS never releases after abnormal
// termination, is an L-14 failure the ticket requires to be reported as a
// SPEC change proposal — these tests fail on it; nothing works around it.
//
// The shared second-process plumbing (helper build, spawn, signal-file
// polling, teardown) lives in LockSmokeHarness; the T-W1.7-08 signal-file
// read-retry harness tests below
// (local:T-W1.7-08:signal-file-read-retry) prove the wait and the fixed
// deadline failure. The macOS leg is
// CoordinatorRunLockTwoProcessSmokeMacOSTests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// local:T-W1.7-05:os-lock-smoke-windows — two dotnet processes on a
    /// Windows host; contention and kill-holder scenarios, 10 times each;
    /// pass: 0 simultaneous holds and acquisition after every holder kill.
    /// The same sources run under Unity Mono: the fixture builds the
    /// helper through the dotnet SDK on PATH and skips non-Windows hosts.
    /// </summary>
    internal sealed class CoordinatorRunLockTwoProcessSmokeTests
    {
        private const int Iterations = 10;

        private static string? s_helperDll;

        private string _root = null!;
        private readonly List<LockSmokeHarness.ChildProcess> _children =
            new List<LockSmokeHarness.ChildProcess>();

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Assert.Ignore(
                    "local:T-W1.7-05:os-lock-smoke-windows is the Windows run of L-14; " +
                    "the macOS run is T-W1.7-06.");
            }
            s_helperDll = LockSmokeHarness.BuildHelper();
        }

        [SetUp]
        public void SetUp()
        {
            _root = LockSmokeHarness.NewLockRoot();
        }

        [TearDown]
        public void TearDown()
        {
            LockSmokeHarness.StopAndDisposeAll(_children);
            LockSmokeHarness.DeleteLockRoot(_root);
        }

        // ── contention: never both holding ────────────────────────────────

        // SPEC §E2 item 3 / §T.4 L-14: two real processes race TryAcquire on
        // the same coordinator.lock from a common start gate; exactly one
        // may hold — never both — and the loser must see the "owner alive"
        // denial (OW-RUN-002). 10 iterations.
        [Test]
        [Timeout(300000)]
        public void TwoProcessesNeverHoldTheOsLockSimultaneously_Contention()
        {
            for (var i = 0; i < Iterations; i++)
            {
                var gate = Path.Combine(_root, "gate." + i + ".signal");
                var readyA = Path.Combine(_root, "a." + i + ".ready");
                var readyB = Path.Combine(_root, "b." + i + ".ready");
                var childA = SpawnHelper(
                    "hold \"" + _root + "\" \"" + readyA + "\" \"" + gate + "\"",
                    readyA + ".stop");
                var childB = SpawnHelper(
                    "hold \"" + _root + "\" \"" + readyB + "\" \"" + gate + "\"",
                    readyB + ".stop");
                File.WriteAllText(gate, "go");

                var signalA = LockSmokeHarness.WaitSignalFile(readyA, childA);
                var signalB = LockSmokeHarness.WaitSignalFile(readyB, childB);
                var aHolds = signalA.StartsWith("holding", StringComparison.Ordinal);
                var bHolds = signalB.StartsWith("holding", StringComparison.Ordinal);

                Assert.IsFalse(aHolds && bHolds,
                    "iteration " + i + ": BOTH processes hold coordinator.lock — " +
                    "a simultaneous hold is an L-14 failure to report as a SPEC " +
                    "change proposal, not to work around. A=" + signalA + " B=" + signalB);
                Assert.IsTrue(aHolds || bHolds,
                    "iteration " + i + ": neither racer acquired the lock. " +
                    "A=" + signalA + " B=" + signalB);

                var loser = aHolds ? childB : childA;
                var winner = aHolds ? childA : childB;
                var loserSignal = aHolds ? signalB : signalA;
                Assert.That(loserSignal, Does.StartWith("denied OW-RUN-002"),
                    "iteration " + i + ": the losing process must be denied with " +
                    "OW-RUN-002 (owner alive); got: " + loserSignal);
                Assert.IsTrue(loser.Process.WaitForExit(15000),
                    "iteration " + i + ": the denied process did not exit");
                Assert.AreEqual(LockSmokeHarness.ExitDenied, loser.Process.ExitCode);

                File.WriteAllText(winner.StopFile!, "stop");
                Assert.IsTrue(winner.Process.WaitForExit(
                        LockSmokeHarness.ReadyTimeoutMilliseconds),
                    "iteration " + i + ": the holder did not finish its orderly release");
                Assert.AreEqual(LockSmokeHarness.ExitAcquired, winner.Process.ExitCode,
                    "iteration " + i + ": holder output: " + LockSmokeHarness.ChildOutput(winner));
            }
        }

        // ── kill-holder: the OS releases on abnormal exit ─────────────────

        // SPEC §E2 item 3 / §T.4 L-14: while the holder lives a second
        // process is denied; after the holder is killed the OS drops the
        // share-mode-0 handle and a second process acquires — and reads
        // the dead holder's still-recorded lock.json state for recovery.
        // 10 iterations.
        [Test]
        [Timeout(300000)]
        public void AbnormalHolderDeathReleasesTheOsLock_KillHolder()
        {
            for (var i = 0; i < Iterations; i++)
            {
                var ready = Path.Combine(_root, "holder." + i + ".ready");
                var holder = SpawnHelper(
                    "hold \"" + _root + "\" \"" + ready + "\"", ready + ".stop");
                var signal = LockSmokeHarness.WaitSignalFile(ready, holder);
                Assert.That(signal, Does.StartWith("holding"),
                    "iteration " + i + ": the holder failed to acquire: " + signal);
                var lines = signal.Split('\n');
                var runId = lines[1].Trim();
                var sessionId = lines[2].Trim();

                // The owner is alive: another process must be denied (a live
                // owner's lock is never taken).
                var probeFile = Path.Combine(_root, "probe." + i + ".result");
                var probe = SpawnHelper(
                    "try \"" + _root + "\" \"" + probeFile + "\"", null);
                Assert.IsTrue(probe.Process.WaitForExit(
                        LockSmokeHarness.ReadyTimeoutMilliseconds),
                    "iteration " + i + ": the probe process did not exit");
                Assert.AreEqual(LockSmokeHarness.ExitDenied, probe.Process.ExitCode);
                Assert.That(LockSmokeHarness.WaitSignalFile(probeFile, probe),
                    Does.StartWith("denied OW-RUN-002"),
                    "iteration " + i + ": a second process reached the held lock");

                // Abnormal termination — the L-14 question: the OS must
                // release the process-held handle without any cleanup.
                if (!holder.Process.HasExited)
                {
                    holder.Process.Kill();
                }
                Assert.IsTrue(holder.Process.WaitForExit(15000),
                    "iteration " + i + ": the killed holder did not exit");

                // Re-acquisition from this second dotnet process. The bounded
                // wait only absorbs kernel teardown lag after WaitForExit; a
                // lock the OS never releases fails here — an L-14 failure to
                // report as a SPEC change proposal, not to work around.
                CoordinatorRunLock? reacquired = null;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (reacquired is null)
                {
                    var attempt = CoordinatorRunLock.TryAcquire(_root);
                    if (attempt.Ok)
                    {
                        reacquired = attempt.Value;
                    }
                    else
                    {
                        Assert.AreEqual("OW-RUN-002", attempt.Diagnostics[0].Code,
                            "iteration " + i + ": re-acquisition after the holder " +
                            "kill failed with an unexpected diagnostic");
                        Assert.Less(DateTime.UtcNow, deadline,
                            "iteration " + i + ": the OS did not release " +
                            "coordinator.lock after the holder's abnormal exit — " +
                            "an L-14 failure to report as a SPEC change proposal");
                        Thread.Sleep(100);
                    }
                }
                try
                {
                    var read = reacquired!.ReadState();
                    Assert.IsTrue(read.Ok);
                    Assert.AreEqual(runId, read.Value!.RunId,
                        "the dead holder's recorded state must survive for recovery");
                    Assert.AreEqual(sessionId, read.Value!.EditorSessionId,
                        "the dead holder's recorded state must survive for recovery");
                    Assert.IsTrue(reacquired!.Release().Ok);
                }
                finally
                {
                    reacquired!.Dispose();
                }
            }
        }

        // ── harness: signal-file read retry (T-W1.7-08) ───────────────────

        // local:T-W1.7-08:signal-file-read-retry — a signal file that
        // exists but is held open without read sharing is "not readable
        // yet": the reader keeps polling inside the ready deadline and
        // returns the content once the holder releases it.
        [Test]
        public void SignalFileReadRetriesATransientSharingViolation_Harness()
        {
            var path = Path.Combine(_root, "harness.retry.ready");
            const string expected = "holding\nrun_harness\nsession_harness";
            using var opened = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                using var stream = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.None);
                var bytes = Encoding.UTF8.GetBytes(expected);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                opened.Set();
                release.Wait(TimeSpan.FromMinutes(1));
            });
            holder.IsBackground = true;
            holder.Start();
            Assert.IsTrue(opened.Wait(TimeSpan.FromSeconds(10)),
                "the holder thread did not open the signal file");

            string? content = null;
            Exception? error = null;
            var reader = new Thread(() =>
            {
                try
                {
                    content = LockSmokeHarness.WaitSignalFile(
                        path, null, LockSmokeHarness.ReadyTimeoutMilliseconds);
                }
                catch (Exception e)
                {
                    error = e;
                }
            });
            reader.IsBackground = true;
            reader.Start();
            try
            {
                // Enough polls to have hit the sharing violation
                // repeatedly: a reader that did not retry would already
                // be dead.
                Thread.Sleep(500);
                Assert.IsTrue(reader.IsAlive,
                    "the reader gave up on a transient sharing violation: " +
                    (error is null ? "no exception captured" : error.ToString()));
                Assert.IsNull(error);
            }
            finally
            {
                release.Set();
            }
            Assert.IsTrue(reader.Join(LockSmokeHarness.ReadyTimeoutMilliseconds + 5000),
                "the reader did not finish after the signal file became readable");
            Assert.IsNull(error);
            Assert.AreEqual(expected, content);
            Assert.IsTrue(holder.Join(TimeSpan.FromSeconds(10)),
                "the holder thread did not exit");
        }

        // local:T-W1.7-08:signal-file-read-retry — a signal file that
        // stays unreadable until the ready deadline fails with a fixed
        // message naming the file; it is never reported as a lock
        // behaviour failure.
        [Test]
        public void SignalFileUnreadablePastTheDeadlineFailsNamingTheFile_Harness()
        {
            var path = Path.Combine(_root, "harness.blocked.ready");
            using var opened = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                using var stream = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.None);
                opened.Set();
                release.Wait(TimeSpan.FromMinutes(1));
            });
            holder.IsBackground = true;
            holder.Start();
            Assert.IsTrue(opened.Wait(TimeSpan.FromSeconds(10)),
                "the holder thread did not open the signal file");
            try
            {
                var failure = Assert.Throws<AssertionException>(
                    () => LockSmokeHarness.WaitSignalFile(path, null, 300));
                var message = failure!.Message;
                Assert.That(message, Does.Contain(Path.GetFileName(path)),
                    "the fixed failure must name the unreadable signal file");
                Assert.That(message, Does.Contain("not readable"),
                    "the fixed failure must report an unreadable signal " +
                    "file, not a lock failure");
            }
            finally
            {
                release.Set();
            }
            Assert.IsTrue(holder.Join(TimeSpan.FromSeconds(10)),
                "the holder thread did not exit");
        }

        // ── second-process plumbing ───────────────────────────────────────

        private LockSmokeHarness.ChildProcess SpawnHelper(
            string arguments, string? stopFile) =>
            LockSmokeHarness.SpawnHelper(s_helperDll!, arguments, stopFile, _children);
    }
}
