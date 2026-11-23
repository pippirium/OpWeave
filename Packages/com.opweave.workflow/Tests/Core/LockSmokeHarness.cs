#nullable enable
// Shared second-process plumbing for the coordinator.lock two-process
// smoke fixtures (SPEC §E2 item 3, §T.4 L-14): the Windows fixture
// (local:T-W1.7-05:os-lock-smoke-windows) and the macOS fixture
// (local:T-W1.7-06:os-lock-smoke-macos) drive the same
// OpWeave.LockSmoke console helper — a minimal dotnet host over the
// product Core assembly — as real second OS processes. File-based
// signalling is identical on both OSes; only the platform gate and the
// OS lock mechanism the fixture verifies differ (a share-mode-0
// CreateFileW handle on Windows, an exclusive non-blocking flock(2) on
// the open file description on .NET Unix runtimes).
//
// T-W1.7-08: WaitSignalFile tolerates a transient sharing violation on
// the just-moved signal file — an IOException before the ready deadline
// is "not readable yet", retried, never a lock failure.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// The file-signalled second-process protocol shared by the L-14
    /// smoke fixtures: spawn `dotnet OpWeave.LockSmoke.dll`, poll its
    /// atomic signal files, stop or kill children, and build the helper
    /// through the dotnet SDK on PATH.
    /// </summary>
    internal static class LockSmokeHarness
    {
        internal const int ReadyTimeoutMilliseconds = 30000;
        internal const int ExitAcquired = 0;
        internal const int ExitDenied = 3;

        internal sealed class ChildProcess
        {
            internal Process Process = null!;
            internal string? StopFile;
            internal readonly StringBuilder Output = new StringBuilder();
        }

        private sealed class DotnetResult
        {
            internal int Exit;
            internal string Output = string.Empty;
        }

        // The unique per-test lock root under the system temp directory.
        internal static string NewLockRoot()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "OpWeave.LockSmoke." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        // Locates dotnet/OpWeave.LockSmoke/OpWeave.LockSmoke.csproj,
        // builds it through the dotnet SDK on PATH and returns the built
        // helper assembly path.
        internal static string BuildHelper()
        {
            var project = FindHelperProject();
            Assert.IsNotNull(project,
                "the OpWeave.LockSmoke helper project was not found by walking up " +
                "from this test's source path or the test assembly directory");
            var build = RunDotnet(
                "build \"" + project + "\" -c Debug --nologo -v q", 180000);
            Assert.AreEqual(0, build.Exit,
                "the OpWeave.LockSmoke helper failed to build: " + build.Output);
            var helperDll = Path.Combine(
                Path.GetDirectoryName(project)!,
                "bin", "Debug", "net8.0", "OpWeave.LockSmoke.dll");
            Assert.IsTrue(File.Exists(helperDll),
                "the built helper assembly is missing: " + helperDll);
            return helperDll;
        }

        // Every child the fixture spawned: orderly stop-file release
        // first, then a kill, then dispose. Best effort — a leaked child
        // dies with the test host.
        internal static void StopAndDisposeAll(List<ChildProcess> children)
        {
            foreach (var child in children)
            {
                try
                {
                    if (!child.Process.HasExited)
                    {
                        if (child.StopFile is not null)
                        {
                            try { File.WriteAllText(child.StopFile, "stop"); }
                            catch (Exception) { /* best effort */ }
                            child.Process.WaitForExit(5000);
                        }
                        if (!child.Process.HasExited)
                        {
                            child.Process.Kill();
                            child.Process.WaitForExit(5000);
                        }
                    }
                    child.Process.Dispose();
                }
                catch (Exception)
                {
                    // Best effort: a leaked child dies with the test host.
                }
            }
            children.Clear();
        }

        // Removes the per-test lock root; a brief retry absorbs a dying
        // child's last handle release.
        internal static void DeleteLockRoot(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                    return;
                }
                catch (Exception e) when (
                    e is IOException || e is UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        // A second dotnet process running the OpWeave.LockSmoke helper on
        // the product Core assembly; its stdout/stderr are drained so a
        // chatty failure cannot block on a full pipe.
        internal static ChildProcess SpawnHelper(
            string helperDll, string arguments, string? stopFile,
            List<ChildProcess> children)
        {
            var psi = new ProcessStartInfo(
                "dotnet", "\"" + helperDll + "\" " + arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var child = new ChildProcess { StopFile = stopFile };
            try
            {
                child.Process = Process.Start(psi)!;
            }
            catch (Exception e)
            {
                Assert.Fail("the dotnet helper host could not be started " +
                    "(the smoke needs the dotnet SDK on PATH): " + e.Message);
                throw;
            }
            child.Process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (child.Output)
                    {
                        child.Output.AppendLine(e.Data);
                    }
                }
            };
            child.Process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (child.Output)
                    {
                        child.Output.AppendLine(e.Data);
                    }
                }
            };
            child.Process.BeginOutputReadLine();
            child.Process.BeginErrorReadLine();
            children.Add(child);
            return child;
        }

        internal static string ChildOutput(ChildProcess child)
        {
            lock (child.Output)
            {
                return child.Output.ToString();
            }
        }

        // The helper's atomic signal file is the child's only reporting
        // channel; poll for it, failing fast on a child that died without
        // signalling instead of waiting out the full timeout. Between the
        // helper's atomic move and this read, another process can briefly
        // hold the file without read sharing (T-W1.7-08, Windows): a
        // transient IOException is "not readable yet", not a lock
        // failure — it is retried inside the same ready deadline, and
        // only a file still unreadable at the deadline fails, with a
        // fixed message naming the file.
        internal static string WaitSignalFile(string path, ChildProcess? child) =>
            WaitSignalFile(path, child, ReadyTimeoutMilliseconds);

        internal static string WaitSignalFile(
            string path, ChildProcess? child, int timeoutMilliseconds)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (true)
            {
                var content = TryReadSignalFile(path);
                if (content is not null)
                {
                    return content;
                }
                if (!File.Exists(path) &&
                    child is not null && child.Process.HasExited)
                {
                    // A dead child may have signalled just before exiting;
                    // give the atomic move a moment before declaring a
                    // missing signal.
                    Thread.Sleep(100);
                    content = TryReadSignalFile(path);
                    if (content is not null)
                    {
                        return content;
                    }
                    if (!File.Exists(path))
                    {
                        Assert.Fail("the helper exited without signalling " +
                            Path.GetFileName(path) + ": exit=" +
                            child.Process.ExitCode + " output=" +
                            ChildOutput(child));
                    }
                    // The file exists but cannot be opened yet: the child
                    // did signal, so the fast-fail no longer applies —
                    // keep polling the read until the deadline.
                }
                if (DateTime.UtcNow >= deadline)
                {
                    Assert.Fail(File.Exists(path)
                        ? "the helper signal file " + Path.GetFileName(path) +
                            " was not readable within " + timeoutMilliseconds +
                            " ms; output=" +
                            (child is null ? "" : ChildOutput(child))
                        : "the helper did not signal " +
                            Path.GetFileName(path) + " within " +
                            timeoutMilliseconds + " ms; output=" +
                            (child is null ? "" : ChildOutput(child)));
                }
                Thread.Sleep(25);
            }
        }

        // The signal file's content once it exists and can be read; null
        // while it is absent or still held without read sharing. Only
        // IOException is transient here — any other exception fails the
        // test at once.
        private static string? TryReadSignalFile(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static DotnetResult RunDotnet(string arguments, int timeoutMilliseconds)
        {
            var psi = new ProcessStartInfo("dotnet", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var result = new DotnetResult();
            Process? process = null;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception e)
            {
                Assert.Fail("the dotnet SDK host could not be started " +
                    "(the smoke needs dotnet on PATH): " + e.Message);
                throw;
            }
            var stdout = process!.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(); } catch (Exception) { /* best effort */ }
                Assert.Fail("dotnet " + arguments + " did not finish within " +
                    timeoutMilliseconds + " ms");
            }
            result.Exit = process.ExitCode;
            result.Output = stdout.Result + stderr.Result;
            return result;
        }

        // The helper project under <repo>/dotnet/: under dotnet the test
        // assembly builds in dotnet/OpWeave.Core.Tests/bin/ and this file's
        // compile-time path is <repo>/Packages/...; under Unity the package
        // compiles from <project>/Packages/... and the current directory is
        // the project root — every start walks up until dotnet/ appears.
        private static string? FindHelperProject()
        {
            foreach (var start in CandidateStarts())
            {
                var dir = start;
                for (var depth = 0; depth < 10 && dir is not null; depth++)
                {
                    var candidate = Path.Combine(
                        dir, "dotnet", "OpWeave.LockSmoke", "OpWeave.LockSmoke.csproj");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return null;
        }

        private static IEnumerable<string> CandidateStarts()
        {
            var source = SourceDirectory();
            if (source is not null)
            {
                yield return source;
            }
            yield return AppContext.BaseDirectory;
            yield return Directory.GetCurrentDirectory();
        }

        private static string? SourceDirectory([CallerFilePath] string path = "") =>
            string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
    }
}
