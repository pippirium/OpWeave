#nullable enable
// T-W1.7-06 [Core] tests — local:T-W1.7-06:os-lock-smoke-macos.
//
// The macOS leg of the SPEC §T.4 L-14 method check for the
// coordinator.lock inter-process exclusion (SPEC §E2 item 3): the
// CoordinatorRunLock of T-W1.7-04 is exercised from real second OS
// processes — `dotnet OpWeave.LockSmoke.dll`, a minimal console host over
// the product Core assembly built under dotnet/ — never a reimplemented
// or simulated lock. On .NET Unix runtimes the exclusion is a FileStream
// opened with FileShare.None, mapped to an exclusive non-blocking
// flock(2) on the open file description — a lock the process holds and
// the kernel releases when the process dies (see CoordinatorRunLock's
// L-14 remarks).
//
// Two scenarios, ten iterations each (the ticket's Verification method):
//   * contention — two child processes released on a start gate race
//     TryAcquire on the same lock root: exactly one may hold ("never both
//     holding"), the loser must be denied with OW-RUN-002;
//   * kill-holder — a child acquires, records lock.json and signals
//     "holding"; a third process is denied while it lives; the holder is
//     then killed and the kernel must release the flock so a second
//     process acquires — with the dead holder's recorded state surviving
//     in lock.json for recovery (SPEC §E2/§F4).
//
// A simultaneous hold, or a lock the OS never releases after abnormal
// termination, is an L-14 failure the ticket requires to be reported as a
// SPEC change proposal — these tests fail on it; nothing works around it.
//
// The shared second-process plumbing (helper build, spawn, signal-file
// polling, teardown) lives in LockSmokeHarness; the Windows leg is
// CoordinatorRunLockTwoProcessSmokeTests. Run this fixture on a macOS
// host with a dotnet test filter such as
//   dotnet test dotnet/OpWeave.Core.Tests/OpWeave.Core.Tests.csproj \
//     --filter "FullyQualifiedName~CoordinatorRunLockTwoProcessSmokeMacOS"
// On every other host the fixture ignores itself with a reason naming
// this verification — it never silently passes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// local:T-W1.7-06:os-lock-smoke-macos — two dotnet processes on a
    /// macOS host; contention and kill-holder scenarios, 10 times each;
    /// pass: 0 simultaneous holds and acquisition after every holder kill.
    /// The same sources run under Unity Mono: the fixture builds the
    /// helper through the dotnet SDK on PATH and reports itself not
    /// applicable — an explicit named skip, never a silent pass — on
    /// every non-macOS host.
    /// </summary>
    internal sealed class CoordinatorRunLockTwoProcessSmokeMacOSTests
    {
        private const int Iterations = 10;

        private static string? s_helperDll;

        private string _root = null!;
        private readonly List<LockSmokeHarness.ChildProcess> _children =
            new List<LockSmokeHarness.ChildProcess>();

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Assert.Ignore(
                    "local:T-W1.7-06:os-lock-smoke-macos is the macOS run of " +
                    "L-14 and is not applicable on this host (" +
                    RuntimeInformation.OSDescription + "); the Windows run " +
                    "is local:T-W1.7-05:os-lock-smoke-windows.");
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
        // process is denied; after the holder is killed the kernel drops
        // the exclusive flock on the dead process's open file description
        // and a second process acquires — and reads the dead holder's
        // still-recorded lock.json state for recovery. 10 iterations.
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
                // release the process-held lock without any cleanup.
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

        // ── second-process plumbing ───────────────────────────────────────

        private LockSmokeHarness.ChildProcess SpawnHelper(
            string arguments, string? stopFile) =>
            LockSmokeHarness.SpawnHelper(s_helperDll!, arguments, stopFile, _children);
    }
}
