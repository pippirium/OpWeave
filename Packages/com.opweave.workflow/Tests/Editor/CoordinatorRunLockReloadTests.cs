#nullable enable
// T-W1.7-04 [Edit] tests: the coordinator.lock inter-process exclusion of
// SPEC §E2 item 3 across a Unity domain reload — the local:T-W1.7-04:
// lock-reload-observation fixture and the L-14 observation record.
//
// The [UnityTest] holds the OS lock in this Editor process, forces a real
// domain reload (a transient marker script under Assets/Editor/ +
// RecompileScripts + the domain sentinel — the SPEC §T.3 pattern; like
// WeakFingerprintTests, `yield return new WaitForDomainReload()` is not
// used because the reload can complete inside RecompileScripts' own wait),
// re-adopts the surviving OS handle through CoordinatorRunLock.TryAdopt and
// proves with a second process (a host-python child attempting the same
// OS-level exclusion) that the lock blocks acquisition both before and
// after the reload.
//
// Everything that must cross the reload is persisted under
// <project>/Library/OpWeaveLockProbe/ — UTF locals do not survive — and the
// probe root itself sits under Library/, never Assets/; the marker script
// folder is removed by [UnityTearDown]. Nothing tracked is modified.
//
// Iterator hygiene (L-14): UTF resumes a [UnityTest] across the reload by
// serializing the compiler-generated iterator object, and fields of
// non-serializable types — the held CoordinatorRunLock, Result<T> structs,
// tuples — come back null/default. The iterator body therefore captures
// NOTHING before `yield return new RecompileScripts()` that is read
// afterwards: each phase is a plain static helper returning a
// ProbePhaseResult, and every post-reload input is recomputed inside the
// helper (paths from static string properties, held.txt re-read from disk,
// helper objects re-created). Every step logs a "[L14] " breadcrumb so
// editmode.log/editmode.xml shows exactly where the run stopped; helper
// failures — including an unexpected exception such as the NRE this
// restructure replaces — surface as the ProbePhaseResult failure text
// instead of a bare stackless fault.
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Persistence;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W1.7-04:lock-reload-observation — coordinator.lock held by
    /// the Editor process keeps excluding a second process across a real
    /// domain reload (SPEC §T.4 L-14).
    /// </summary>
    internal sealed class CoordinatorRunLockReloadTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir => Path.Combine(ProjectRoot, "Library", "OpWeaveLockProbe");
        private static string LockRoot => Path.Combine(ProbeDir, "run-root");
        private static string HeldFile => Path.Combine(ProbeDir, "held.txt");
        private static string HelperPath => Path.Combine(ProbeDir, "lock_probe.py");
        private static string LockPath => Path.Combine(LockRoot, CoordinatorRunLock.LockFileName);
        private static string StatePath => Path.Combine(LockRoot, CoordinatorRunLock.StateFileName);

        // A failure message that can never itself throw: every diagnostic
        // code and message joined so editmode.xml carries the full reason;
        // "<none>" marks a diagnostic-free failure and "<null>" a missing
        // entry instead of indexing past the list.
        private static string DescribeDiagnostics<T>(Result<T> result)
        {
            if (result.Diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            var parts = new string[result.Diagnostics.Count];
            for (var i = 0; i < parts.Length; i++)
            {
                var diagnostic = result.Diagnostics[i];
                parts[i] = diagnostic is null
                    ? "<null diagnostic>"
                    : diagnostic.Code + " " + diagnostic.Message;
            }
            return string.Join("; ", parts);
        }

        // The plain result a phase helper hands back to the iterator: no
        // captured state, no managed lock, only a pass flag plus the full
        // failure text the iterator asserts on. [Serializable] so UTF's
        // iterator preservation across the domain reload can carry it too.
        [Serializable]
        private sealed class ProbePhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static ProbePhaseResult Pass() => new ProbePhaseResult { Ok = true };

            public static ProbePhaseResult Fail(string message) =>
                new ProbePhaseResult { Ok = false, Failure = message };
        }

        // A "[L14] " breadcrumb so editmode.log/editmode.xml shows exactly
        // which step the run reached — the previous head died with an
        // unexplained NullReferenceException and an empty output section.
        private static void Crumb(string message) => UnityEngine.Debug.Log("[L14] " + message);

        // A failure return that first drops any OS exclusion this phase
        // still owns — a bailed run must not leak the held handle into the
        // next test (the persisted held.txt record stays for
        // CleanupProbeArtifacts to re-attach and dispose).
        private static ProbePhaseResult FailAndRelease(CoordinatorRunLock? held, string message)
        {
            try
            {
                held?.Dispose();
            }
            catch (Exception)
            {
                // Best effort: report the original failure regardless.
            }
            return ProbePhaseResult.Fail(message);
        }

        private const string MarkerFolder = "Assets/Editor/OpWeaveLockProbe";
        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveLockProbe\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        // The second-process acquisition attempt: opens coordinator.lock and
        // takes the OS-level exclusive hold the product method maps to —
        // Windows: the open itself is denied under FileShare.None; Unix: the
        // exclusive non-blocking flock is denied. Exit 0 = acquired.
        private const string HelperSource =
            "import os, sys\n" +
            "path = sys.argv[1]\n" +
            "try:\n" +
            "    fd = os.open(path, os.O_RDWR)\n" +
            "except OSError as e:\n" +
            "    print('open-denied: ' + str(e))\n" +
            "    sys.exit(3)\n" +
            "try:\n" +
            "    os.lseek(fd, 0, os.SEEK_SET)\n" +
            "    if sys.platform == 'win32':\n" +
            "        import msvcrt\n" +
            "        msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)\n" +
            "        os.lseek(fd, 0, os.SEEK_SET)\n" +
            "        msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)\n" +
            "    else:\n" +
            "        import fcntl\n" +
            "        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)\n" +
            "        fcntl.flock(fd, fcntl.LOCK_UN)\n" +
            "    print('acquired')\n" +
            "    sys.exit(0)\n" +
            "except OSError as e:\n" +
            "    print('lock-denied: ' + str(e))\n" +
            "    sys.exit(4)\n" +
            "finally:\n" +
            "    os.close(fd)\n";

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        // The second-process host: python3 first, then python, then the
        // Windows py launcher. Returns null when no interpreter is on PATH —
        // the test then reports the observation environment as missing
        // rather than silently skipping a required check.
        private static string? FindPython()
        {
            foreach (var name in new[] { "python3", "python", "py" })
            {
                try
                {
                    var psi = new ProcessStartInfo(name, "--version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using (var process = Process.Start(psi))
                    {
                        if (process is not null && process.WaitForExit(10000) && process.ExitCode == 0)
                        {
                            return name;
                        }
                    }
                }
                catch (Exception)
                {
                    // Candidate not installed; try the next one.
                }
            }
            return null;
        }

        // Runs the second-process acquisition helper; exit code 0 means it
        // actually took the OS-level hold, a positive code means it was
        // denied, and -1 reports a hung helper — kept as data so the
        // post-reload phase helper can fold it into a ProbePhaseResult
        // instead of throwing inside UTF's resumed iterator machinery.
        private static (int Exit, string Output) TryChildAcquire(string python, string lockPath)
        {
            var psi = new ProcessStartInfo(
                python, "\"" + HelperPath + "\" \"" + lockPath + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var process = Process.Start(psi)!)
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(); } catch { /* best effort */ }
                    return (-1, "the second-process acquisition helper timed out");
                }
                return (process.ExitCode, stdout.Result + stderr.Result);
            }
        }

        private static void WriteHelper()
        {
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(HelperPath, HelperSource);
        }

        private static void WriteMarker()
        {
            Directory.CreateDirectory(MarkerDisk);
            File.WriteAllText(Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
        }

        // Releases a leftover probe lock from a previous (failed) run and
        // removes every artifact this fixture creates.
        private static bool CleanupProbeArtifacts()
        {
            if (File.Exists(HeldFile))
            {
                try
                {
                    var lines = File.ReadAllLines(HeldFile);
                    if (lines.Length >= 1 && long.TryParse(lines[0], out var handleValue))
                    {
                        var adopted = CoordinatorRunLock.TryAdopt(
                            lines.Length > 1 ? lines[1] : LockRoot, new IntPtr(handleValue));
                        if (adopted.Ok)
                        {
                            adopted.Value!.Dispose();
                        }
                    }
                }
                catch (Exception)
                {
                    // Best-effort cleanup: the persisted handle may already be dead.
                }
            }
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file cannot be deleted — left for the next run */ }
            }
            var dirty = false;
            if (AssetDatabase.IsValidFolder(MarkerFolder))
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
                dirty = true;
            }
            if (Directory.Exists(MarkerDisk))
            {
                Directory.Delete(MarkerDisk, recursive: true);
                dirty = true;
            }
            return dirty;
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            if (CleanupProbeArtifacts())
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (CleanupProbeArtifacts())
            {
                yield return new RecompileScripts(false);
            }
        }

        // SPEC §E2 item 3 + §T.4 L-14: the Editor process holds the lock, a
        // second process is denied before the reload, the reload is crossed,
        // and the same second process is still denied afterwards — the
        // process-held OS handle outlives the managed domain.
        //
        // The iterator body is deliberately minimal: phase helper call,
        // yield RecompileScripts, sentinel wait, phase helper call, asserts
        // on the returned plain results — with plain string messages only.
        // A `() => ...` assertion message is a compiler-generated display
        // class capturing iterator locals, and UTF's post-reload
        // resurrection of the iterator cannot rebuild it: evaluating it
        // after the reload threw a NullReferenceException in the test
        // plumbing. Nothing else the compiler hoists into an iterator
        // field before the reload (a Result<T>, the held
        // CoordinatorRunLock, a process tuple) is touched afterwards
        // either — UTF's serialization of the iterator does not preserve
        // them — and the post-reload result lives in a local created
        // after the reload.
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator CoordinatorLockSurvivesDomainReload_AndExcludesSecondProcess()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 returned ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True,
                "pre-reload phase failed: " + phase1.Failure);
            Crumb("phase-1 passed; yielding RecompileScripts");

            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Crumb("iterator resumed; domain sentinel alive=" + DomainSentinelAlive);
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

            var phase2 = RunPhaseAfterReload();
            Crumb("phase-2 returned ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True,
                "post-reload phase failed: " + phase2.Failure);
        }

        // Phase 1 (pre-reload): take the exclusion, record lock.json, deny
        // the second process and persist everything phase 2 needs. The held
        // CoordinatorRunLock is intentionally abandoned on success — on
        // Windows the exclusion is a raw OS handle with no managed owner
        // (and on Unix finalization is suppressed), so dropping the
        // reference is what lets it outlive the managed domain.
        private static ProbePhaseResult RunPhaseBeforeReload()
        {
            CoordinatorRunLock? held = null;
            try
            {
                Crumb("phase-1 begin: resolving python");
                var python = FindPython();
                Crumb("phase-1 python=" + (python ?? "<null>"));
                if (python is null)
                {
                    return ProbePhaseResult.Fail(
                        "the L-14 observation needs a host python (python3/python/py) for the second process");
                }

                // Control: the lock is free now, so a second process CAN
                // acquire it — proves the helper really exercises the OS
                // exclusion and the denial results below are meaningful.
                Directory.CreateDirectory(LockRoot);
                File.WriteAllBytes(LockPath, Array.Empty<byte>());
                WriteHelper();
                var free = TryChildAcquire(python, LockPath);
                Crumb("phase-1 control acquire exit=" + free.Exit + " output=" + free.Output);
                if (free.Exit != 0)
                {
                    return ProbePhaseResult.Fail(
                        "control acquisition on the free lock failed: " + free.Output);
                }

                var runId = Identifiers.NewRunId();
                var sessionId = Guid.NewGuid().ToString("N");

                var acquired = CoordinatorRunLock.TryAcquire(LockRoot);
                Crumb("phase-1 TryAcquire ok=" + acquired.Ok +
                    " diagnostics=" + DescribeDiagnostics(acquired));
                if (!acquired.Ok)
                {
                    return ProbePhaseResult.Fail(
                        "TryAcquire(root=" + LockRoot + ") failed: " +
                        DescribeDiagnostics(acquired));
                }
                held = acquired.Value!;

                var written = held.WriteState(RunLockState.AcquiredNow(runId, sessionId));
                Crumb("phase-1 WriteState ok=" + written.Ok +
                    " diagnostics=" + DescribeDiagnostics(written));
                if (!written.Ok)
                {
                    return FailAndRelease(held, "WriteState failed: " + DescribeDiagnostics(written));
                }

                var deniedBefore = TryChildAcquire(python, LockPath);
                Crumb("phase-1 second-process denial exit=" + deniedBefore.Exit +
                    " output=" + deniedBefore.Output);
                if (deniedBefore.Exit == -1)
                {
                    return FailAndRelease(held, deniedBefore.Output);
                }
                if (deniedBefore.Exit == 0)
                {
                    return FailAndRelease(held,
                        "a second process acquired the held lock before the reload: " +
                        deniedBefore.Output);
                }

                // Persist across the reload (UTF locals die): the OS handle
                // value for adoption, the probe root and the recorded owner
                // fields — written as early as possible so teardown can
                // always re-attach and release the exclusion. Only then is
                // the marker script written: a watcher-triggered reload can
                // no longer lose persisted state.
                File.WriteAllText(HeldFile,
                    held.LockHandleValue.ToInt64() + "\n" + LockRoot + "\n" +
                    runId + "\n" + sessionId);
                Crumb("phase-1 persisted " + HeldFile);

                // The marker script makes the pending recompile genuine and
                // the sentinel proves the domain changed.
                WriteMarker();
                AssetDatabase.Refresh();
                ArmDomainSentinel();
                Crumb("phase-1 complete: marker written, sentinel armed");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-1 threw " + e.GetType().Name + ": " + e.Message);
                return FailAndRelease(held, "phase-1 threw an unexpected exception: " + e);
            }
        }

        // Phase 2 (post-reload): re-derive every input — the managed object
        // is dead, the OS handle is the only surviving state. Nothing from
        // before the reload may be assumed: the python path is re-resolved
        // and the persisted record (held.txt — static-free storage under
        // Library/) is re-read and validated before it is used. A TryAdopt
        // failure is reported verbatim (SPEC §E2 "could not be kept" — the
        // L-14 finding), never relaxed.
        private static ProbePhaseResult RunPhaseAfterReload()
        {
            CoordinatorRunLock? reattached = null;
            try
            {
                Crumb("phase-2 begin: resolving python");
                var python = FindPython();
                Crumb("phase-2 python=" + (python ?? "<null>"));
                if (python is null)
                {
                    return ProbePhaseResult.Fail(
                        "the L-14 observation needs a host python (python3/python/py) after the domain reload");
                }

                Crumb("phase-2 held.txt exists=" + File.Exists(HeldFile) + " path=" + HeldFile);
                if (!File.Exists(HeldFile))
                {
                    return ProbePhaseResult.Fail(
                        "the persisted lock record did not survive the domain reload");
                }
                var persisted = File.ReadAllLines(HeldFile);
                Crumb("phase-2 held.txt lines=" + persisted.Length);
                if (persisted.Length < 4)
                {
                    return ProbePhaseResult.Fail(
                        "the persisted lock record is incomplete after the reload: " +
                        persisted.Length + " line(s)");
                }
                if (!long.TryParse(persisted[0], out var handleValue))
                {
                    return ProbePhaseResult.Fail(
                        "the persisted lock handle value is unreadable after the reload: " +
                        persisted[0]);
                }
                Crumb("phase-2 parsed handle=0x" + handleValue.ToString("x") +
                    " root=" + persisted[1] + " runId=" + persisted[2] +
                    " sessionId=" + persisted[3]);

                var adopted = CoordinatorRunLock.TryAdopt(
                    persisted[1], new IntPtr(handleValue));
                Crumb("phase-2 TryAdopt ok=" + adopted.Ok +
                    " diagnostics=" + DescribeDiagnostics(adopted));
                if (!adopted.Ok)
                {
                    return ProbePhaseResult.Fail(
                        "TryAdopt(root=" + persisted[1] + ", handle=0x" +
                        handleValue.ToString("x") + ") failed: " +
                        DescribeDiagnostics(adopted));
                }
                reattached = adopted.Value!;
                Crumb("phase-2 IsHeld=" + reattached.IsHeld);
                if (!reattached.IsHeld)
                {
                    return FailAndRelease(reattached,
                        "the adopted Run lock reports IsHeld=false");
                }

                var read = reattached.ReadState();
                Crumb("phase-2 ReadState ok=" + read.Ok +
                    " diagnostics=" + DescribeDiagnostics(read));
                if (!read.Ok)
                {
                    return FailAndRelease(reattached,
                        "ReadState failed: " + DescribeDiagnostics(read));
                }
                if (read.Value is null)
                {
                    return FailAndRelease(reattached,
                        "ReadState returned no recorded lock state after adoption");
                }
                if (!string.Equals(read.Value.RunId, persisted[2], StringComparison.Ordinal))
                {
                    return FailAndRelease(reattached,
                        "recorded runId drifted across the reload: expected " +
                        persisted[2] + " but read " + read.Value.RunId);
                }
                if (!string.Equals(
                    read.Value.EditorSessionId, persisted[3], StringComparison.Ordinal))
                {
                    return FailAndRelease(reattached,
                        "recorded editorSessionId drifted across the reload: expected " +
                        persisted[3] + " but read " + read.Value.EditorSessionId);
                }

                // The L-14 observation: still denying a second process after
                // the reload — the exclusion was never released.
                var deniedAfter = TryChildAcquire(python, LockPath);
                Crumb("phase-2 second-process denial exit=" + deniedAfter.Exit +
                    " output=" + deniedAfter.Output);
                if (deniedAfter.Exit == -1)
                {
                    return FailAndRelease(reattached, deniedAfter.Output);
                }
                if (deniedAfter.Exit == 0)
                {
                    return FailAndRelease(reattached,
                        "a second process acquired the lock after the domain reload — the exclusion did not survive: " +
                        deniedAfter.Output);
                }

                // Orderly release: lock.json emptied, then the OS handle
                // drops — afterwards a second process can acquire again.
                var released = reattached.Release();
                Crumb("phase-2 Release ok=" + released.Ok +
                    " diagnostics=" + DescribeDiagnostics(released));
                if (!released.Ok)
                {
                    return FailAndRelease(reattached,
                        "Release failed: " + DescribeDiagnostics(released));
                }
                var stateLength = new FileInfo(StatePath).Length;
                Crumb("phase-2 lock.json length after release=" + stateLength);
                if (stateLength != 0)
                {
                    return FailAndRelease(reattached,
                        "lock.json must be emptied before the exclusion is released; length=" +
                        stateLength);
                }
                var reacquired = TryChildAcquire(python, LockPath);
                Crumb("phase-2 second-process reacquire exit=" + reacquired.Exit +
                    " output=" + reacquired.Output);
                if (reacquired.Exit != 0)
                {
                    return FailAndRelease(reattached,
                        "the released lock is not acquirable by a second process: " +
                        reacquired.Output);
                }
                Crumb("phase-2 complete");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-2 threw " + e.GetType().Name + ": " + e.Message);
                return FailAndRelease(reattached, "phase-2 threw an unexpected exception: " + e);
            }
        }
    }
}
