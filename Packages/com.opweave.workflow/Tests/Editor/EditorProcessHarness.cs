#nullable enable

// T-W4.6-01 [Edit] test-only process-control harness (SPEC §T.2 AT-F07):
// launches, kills and restarts real Unity Editor processes on a cloned
// copy of the test project so the interruption suite crosses the true OS
// process boundary — the path a live-owner coordinator.lock, an abandoned
// lock.json and a journaled Run take across a genuine Editor death
// (SPEC §E2 item 3, §F4). The clone keeps the package under test through
// an absolute file: reference, so the child Editor runs exactly the code
// of this checkout.
//
// Portable to Windows and macOS — the shared harness T-W4.6-02/-04/-05
// drive: the OS-side primitives (exclusive file handle, process kill,
// handle release on death) are never simulated, and every path/API used
// here is .NET Standard 2.1 only. Not shipped in any production assembly.
//
// The parent/child protocol is a pair of tiny files this harness only
// transports: the arrange phase writes <job>/checkpoint.json once the Run
// is parked mid-flight, the verify phase writes <job>/verdict.json with
// the recovery outcome it observed. What the child arranges and verifies
// lives in InterruptionScenarioEntry.cs; the per-case journal surgery
// and the assertions live in the test fixtures.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Real-Editor process control for the AT-F07 interruption suite and the
    /// sibling tickets that share it (T-W4.6-02 lock contract, T-W4.6-04 the
    /// Runner recovery matrix, T-W4.6-05 macOS lock tests): clone a project,
    /// spawn a batchmode Editor on it, watch its signal files, kill or wait
    /// it, and relaunch — every durable effect the Run Store sees is then
    /// produced by a genuinely separate process.
    /// </summary>
    internal sealed class EditorProcessHarness
    {
        // ── Environment contract ─────────────────────────────────────────
        // The single source of the OWP_EPH_* names both ends of the process
        // boundary use: the parent test writes them through Launch's
        // environment, the child's -executeMethod entry reads them.
        internal const string ScenarioEnvVar = "OWP_EPH_SCENARIO";
        internal const string JobDirEnvVar = "OWP_EPH_JOB";
        internal const string StoreEnvVar = "OWP_EPH_STORE";
        internal const string ExitModeEnvVar = "OWP_EPH_EXIT";

        /// <summary>The Editor executable of the running Editor instance — the
        /// same binary every child launches (Unity.exe on Windows,
        /// Unity inside the .app on macOS).</summary>
        internal static string EditorExecutable =>
            ResolveEditorExecutable(EditorApplication.applicationPath);

        // EditorApplication.applicationPath is the executable itself on
        // Windows (…\Editor\Unity.exe) but the application BUNDLE directory
        // on macOS (…/Unity/Hub/Editor/<version>/Unity.app): Process.Start
        // cannot exec a bundle directory, so a .app path — or any directory
        // — resolves through it to Contents/MacOS/Unity. A missing binary
        // fails loudly here with the resolved path, never as an opaque
        // exec-of-a-directory error inside the first child launch.
        private static string ResolveEditorExecutable(string applicationPath)
        {
            if (!applicationPath.EndsWith(".app", StringComparison.Ordinal) &&
                !Directory.Exists(applicationPath))
            {
                return applicationPath;
            }
            var binary = Path.Combine(
                applicationPath, "Contents", "MacOS", "Unity");
            if (!File.Exists(binary))
            {
                throw new FileNotFoundException(
                    "the Unity executable inside the .app bundle is missing: " +
                    binary);
            }
            return binary;
        }

        internal static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        /// <summary>A spawned child Editor plus the plumbing its watcher needs.</summary>
        internal sealed class EditorChild
        {
            internal Process Process = null!;
            internal string LogPath = "";
            internal string JobDir = "";
            internal readonly StringBuilder Output = new StringBuilder();

            internal bool Exited => Process.HasExited;
        }

        // ── Project cloning ─────────────────────────────────────────────

        /// <summary>
        /// Copies the runnable part of <paramref name="sourceProject"/> —
        /// Assets/, Packages/ and ProjectSettings/ — into
        /// <paramref name="cloneProject"/> and rewrites every relative
        /// <c>file:</c> dependency in Packages/manifest.json and
        /// Packages/packages-lock.json to the absolute path it resolved to
        /// in the source project, so the clone still runs this checkout's
        /// package no matter where the clone directory lives. Library/ is
        /// never copied: the child rebuilds it.
        /// </summary>
        internal static void CloneProject(string sourceProject, string cloneProject)
        {
            CopyDirectory(Path.Combine(sourceProject, "Assets"),
                Path.Combine(cloneProject, "Assets"));
            CopyDirectory(Path.Combine(sourceProject, "Packages"),
                Path.Combine(cloneProject, "Packages"));
            CopyDirectory(Path.Combine(sourceProject, "ProjectSettings"),
                Path.Combine(cloneProject, "ProjectSettings"));
            var sourcePackages = Path.Combine(sourceProject, "Packages");
            RewriteFileDependencies(
                Path.Combine(cloneProject, "Packages", "manifest.json"), sourcePackages);
            RewriteFileDependencies(
                Path.Combine(cloneProject, "Packages", "packages-lock.json"), sourcePackages);
        }

        // file:"../relative/path" -> file:"<absolute path>". Only file:
        // dependencies are rewritten; registry versions never match the
        // pattern. Unity accepts forward slashes in file: URIs on both
        // supported hosts.
        private static void RewriteFileDependencies(string manifestPath, string sourcePackages)
        {
            if (!File.Exists(manifestPath))
            {
                return;
            }
            var text = File.ReadAllText(manifestPath);
            var rewritten = Regex.Replace(
                text,
                "file:([^\"]+)",
                match =>
                {
                    var relative = match.Groups[1].Value;
                    if (Path.IsPathRooted(relative))
                    {
                        return match.Value;
                    }
                    var resolved = Path.GetFullPath(
                        Path.Combine(sourcePackages, relative));
                    return "file:" + resolved.Replace('\\', '/');
                });
            if (!string.Equals(rewritten, text, StringComparison.Ordinal))
            {
                File.WriteAllText(manifestPath, rewritten, s_utf8);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(
                    file,
                    Path.Combine(destination, Path.GetFileName(file)),
                    overwrite: true);
            }
            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(
                    directory,
                    Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        // ── Child launch ────────────────────────────────────────────────

        /// <summary>
        /// Spawns <c>Unity -batchmode -nographics -silent-crashes
        /// -projectPath &lt;project&gt; -executeMethod &lt;method&gt;
        /// -logFile &lt;logPath&gt;</c> with <paramref name="environment"/>
        /// added to the child environment. <c>-silent-crashes</c> keeps a
        /// killed child from blocking on the platform crash reporter —
        /// the whole point of the kill is that the OS alone must release
        /// everything the process held. The child's stdout/stderr are
        /// drained asynchronously so a chatty Editor cannot block on a
        /// full pipe; the authoritative log is the -logFile at
        /// <paramref name="logPath"/>.
        /// </summary>
        internal EditorChild Launch(
            string projectPath,
            string executeMethod,
            string logPath,
            IReadOnlyDictionary<string, string> environment)
        {
            var psi = new ProcessStartInfo(
                EditorExecutable,
                "-batchmode -nographics -silent-crashes " +
                "-projectPath \"" + projectPath + "\" " +
                "-executeMethod " + executeMethod + " " +
                "-logFile \"" + logPath + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var pair in environment)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
            var child = new EditorChild { LogPath = logPath };
            child.Process = Process.Start(psi)!;
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
            return child;
        }

        /// <summary>The drained stdout/stderr tail, for failure messages.</summary>
        internal static string ChildOutput(EditorChild child)
        {
            lock (child.Output)
            {
                return child.Output.ToString();
            }
        }

        /// <summary>
        /// Waits for <paramref name="child"/> to exit, bounded by
        /// <paramref name="timeoutMs"/>; returns false on timeout.
        /// </summary>
        internal static bool WaitForExit(EditorChild child, int timeoutMs) =>
            child.Process.WaitForExit(timeoutMs);

        /// <summary>
        /// Abnormal termination of <paramref name="child"/> — Process.Kill
        /// on either host, then WaitForExit. This is the injection the
        /// whole suite exists for: nothing about the Editor or the Run is
        /// asked to clean up, so what survives is exactly what the SPEC's
        /// durability and recovery rules must rebuild.
        /// </summary>
        internal static void KillAndWait(EditorChild child, int timeoutMs)
        {
            if (!child.Process.HasExited)
            {
                child.Process.Kill();
            }
            if (!child.Process.WaitForExit(timeoutMs))
            {
                throw new InvalidOperationException(
                    "the killed child Editor did not exit within " + timeoutMs + " ms");
            }
        }

        /// <summary>
        /// Polls <paramref name="path"/> until it exists and reads as
        /// non-empty text, or the deadline passes (returns null). A
        /// transient IOException or a still-empty file is "not ready yet",
        /// retried — the child may have just renamed the file into place.
        /// </summary>
        internal static string? WaitForFileContent(
            string path, int timeoutMs, int pollMs = 200)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var text = ReadAllTextShared(path);
                        if (text.Length != 0)
                        {
                            return text;
                        }
                    }
                }
                catch (IOException)
                {
                    // Not readable yet — the writer still holds it.
                }
                Thread.Sleep(pollMs);
            }
            return null;
        }

        // ── Observing reads across the process boundary ────────────────

        /// <summary>
        /// The share mode of every test-side read of a file a live child
        /// process may write. <see cref="FileShare.ReadWrite"/> keeps the
        /// held read from denying the child's next append or write open —
        /// the bare <c>File.ReadAll*</c> <see cref="FileShare.Read"/> did
        /// exactly that to the parked owner's journal appends, which the
        /// store correctly fails closed as OW-STORE-001 — and
        /// <see cref="FileShare.Delete"/> keeps it from blocking the
        /// File.Replace the store's atomic maintenance performs.
        /// </summary>
        internal const FileShare SharedReadShare =
            FileShare.ReadWrite | FileShare.Delete;

        /// <summary>Opens <paramref name="path"/> read-only under
        /// <see cref="SharedReadShare"/>.</summary>
        internal static FileStream OpenSharedRead(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, SharedReadShare);

        /// <summary>The whole text of <paramref name="path"/> read under
        /// <see cref="SharedReadShare"/> — for files the protocol writes
        /// atomically, where the content is always complete.</summary>
        internal static string ReadAllTextShared(string path)
        {
            using var stream = OpenSharedRead(path);
            using var reader = new StreamReader(stream, s_utf8);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// The newline-terminated records of <paramref name="path"/> read
        /// under <see cref="SharedReadShare"/>. A trailing segment without
        /// its '\n' is dropped: FileShare.ReadWrite lets this read observe
        /// an append still in flight, and a torn tail is never a committed
        /// record — the same SPEC §F3 item 3 rule the store's own Load
        /// applies.
        /// </summary>
        internal static string[] ReadAllLinesShared(string path)
        {
            var text = ReadAllTextShared(path);
            if (text.Length == 0)
            {
                return Array.Empty<string>();
            }
            var segments = text.Split('\n');
            // Split leaves one element after the final '\n': "" for a
            // terminated file, the unterminated (possibly torn) remainder
            // otherwise — never a committed record either way.
            var lines = new string[segments.Length - 1];
            Array.Copy(segments, lines, segments.Length - 1);
            return lines;
        }

        /// <summary>
        /// Appends one newline-terminated record to <paramref name="path"/>
        /// under the product's own journal-append shape (FileMode.Append /
        /// FileAccess.Write / FileShare.Read): a read held open under
        /// <see cref="SharedReadShare"/> is always granted, so a parent
        /// polling the file can never deny the child's next append — the
        /// lock-contract contender's attempt log is written while the
        /// parent reads it.
        /// </summary>
        internal static void AppendLineShared(string path, string line)
        {
            using var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = s_utf8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="path"/> as
        /// close to atomically as .NET Standard 2.1 allows: write to a
        /// sibling temp file, delete any previous target, rename into
        /// place — readers never see a half-written checkpoint or verdict.
        /// </summary>
        internal static void AtomicWriteFile(string path, string content)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, s_utf8);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temporary, path);
        }
    }
}
