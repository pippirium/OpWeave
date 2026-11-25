#nullable enable
// T-W3.8-02 [Edit] tests — local:T-W3.8-02:cli-smoke-atf01 — the G-06
// release gate's CLI half (SPEC §T.1 G-06, §T.2 AT-F01, §G0): on the
// pinned com.unity.pipeline 0.9.1-exp.1, drive the real `unity` CLI
// (a separate product, owner-installed) against THIS running Editor's
// pipeline server — command listing, a minimal search/validate/execute
// smoke, and the §T.2 AT-F01 external-extension flow end to end:
// install the independent OpWeave-Sample-Operations Git package at an
// immutable full commit SHA through `unity command package add`,
// atomic registration, Agent search finds it, validate, execute,
// remove leaves no Registry entry, and no file under
// Packages/com.opweave.workflow/ changes.
//
// Fixture shape follows T-W2.13-02's SampleExtensionInstallRemoveTests:
// the project's manifest and lockfile are captured byte-exact and
// restored, every install/removal reload is crossed at the sanctioned
// `yield return new WaitForDomainReload()` boundary, and state that
// must cross a reload rides SessionState or the backup files. Unlike
// that suite nothing here calls the in-process AgentApi — every Agent
// assertion travels through a spawned `unity` process over the real
// `unity command opweave <name>` surface, exactly the transport a
// caller outside the Editor uses.
//
// CLI facts (L-01, docs.unity3d.com com.unity.pipeline@0.9): the
// server auto-starts in batchmode, publishes
// Library/Pipeline/.unity-pipeline-port, keeps port and evalToken
// across a domain reload, and the CLI exits EDITOR_NOT_RUNNING during
// the ~0.6 s outage — a retry after the crossing always answers. The
// `package` group ships add/remove/list/resolve/search/status;
// mutating calls take a bare `--confirm` flag (boolean grammar). The
// CLI's own `--query`/`--limit`/`--tags` listing options collide with
// same-named command arguments, so searches here bind through
// producesType / prefix / origin, never the colliding names.
//
// This file lives in OpWeave.Pipeline.Tests, which compiles only
// while com.unity.pipeline 0.9.1-exp.1 is installed — the §G0 support
// pin itself: an unverified version compiles out both adapter and
// suite instead of ever presenting itself as supported.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.TestTools;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace OpWeave.Pipeline.Tests
{
    /// <summary>
    /// <c>local:T-W3.8-02:cli-smoke-atf01</c> — pinned-version CLI smoke
    /// and the AT-F01 external-extension flow driven through the real
    /// <c>unity command</c> CLI.
    /// </summary>
    internal sealed class CliSmokeAtf01Tests
    {
        // ---------- fixture identities ----------

        private const string PipelinePackage = "com.unity.pipeline";
        private const string PipelinePinnedVersion = "0.9.1-exp.1";
        private const string SamplePackage = "com.opweave.sample.operations";
        private const string SampleRepository =
            "https://github.com/pippirium/OpWeave-Sample-Operations.git";
        // The immutable revision both T-W2.13-02 and T-W3.8-01 verify —
        // a full 40-hex commit SHA, never a moving tag or local clone.
        private const string SamplePinnedSha =
            "92865779755002c4b928a2d788d55dceddc99c86";
        private const string SampleEditorAssembly =
            "OpWeave.Sample.Operations.Editor";
        private const string SampleOperationId = "org.opweave.sample.make_pose";
        private const string SampleTypeId = "org.opweave.sample.BonePose";

        private const string OpWeavePackage = "com.opweave.workflow";

        // The 14 SPEC §H1 commands the adapter declares under group
        // opweave — the same list PipelineCommandSurfaceTests pins
        // statically; here it must appear on the real CLI listing.
        private static readonly string[] s_expectedCommands =
        {
            "search_operations", "describe_operation",
            "search_compatible_operations", "list_types",
            "get_workflow_schema", "list_workflows", "validate_workflow",
            "execute_workflow", "get_run_status", "get_run_logs",
            "list_runs", "cancel_workflow", "migrate_workflow",
            "get_registry_status",
        };

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;
        private const double RunPollDeadlineSec = 180.0;
        private const double CliReadyDeadlineSec = 120.0;
        private const int CliCallTimeoutMs = 90000;

        // SessionState slots that carry values across the reloads the
        // package changes force: the lazily captured com.opweave.workflow
        // tree digest and the installed sample's own workflow body (the
        // package's files are gone in the post-removal domain).
        private const string TreeDigestKey = "OpWeave.CliSmoke.TreeDigest";
        private const string SampleWorkflowKey = "OpWeave.CliSmoke.SampleWorkflow";

        // Static, not SessionState: a domain reload drops the whole App
        // Domain, so a plain field IS the reload proof — it reads false
        // again only because the domain really reloaded.
        private static bool s_domainAlive;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");

        private static string LockPath =>
            Path.Combine(ProjectRoot, "Packages", "packages-lock.json");

        private static string BackupDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveCliSmokeBackup");

        private static string DescriptorPath =>
            Path.Combine(
                ProjectRoot, "Library", "Pipeline", ".unity-pipeline-port");

        private static string SampleDependency =>
            SampleRepository + "#" + SamplePinnedSha;

        private static readonly Encoding s_utf8 = new UTF8Encoding(false);

        // ---------- the spawned `unity` CLI ----------

        private sealed class CliResult
        {
            internal int ExitCode = -1;
            internal string Stdout = "";
            internal string Stderr = "";
            internal bool TimedOut;

            internal string Describe() =>
                "exit " + ExitCode + (TimedOut ? " (timed out)" : "") +
                "\nstdout: " + Stdout + "\nstderr: " + Stderr;
        }

        // The `unity` CLI executable: an owner-installed Windows app
        // alias on PATH (WindowsApps) — resolved explicitly so the
        // failure names what was searched rather than surfacing as an
        // opaque Process.Start miss.
        private static string FindUnityCli()
        {
            var candidates = new List<string>();
            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var entry in path!.Split(Path.PathSeparator))
                {
                    if (entry.Length != 0)
                    {
                        candidates.Add(Path.Combine(entry, "unity.exe"));
                        candidates.Add(Path.Combine(entry, "unity"));
                    }
                }
            }
            var localAppData =
                Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            var windowsApps = Path.Combine(
                localAppData, "Microsoft", "WindowsApps");
            if (localAppData.Length != 0)
            {
                candidates.Add(Path.Combine(windowsApps, "unity.exe"));
            }
            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // An unreadable PATH entry is skipped, not fatal.
                }
            }
            Assert.Fail(
                "the `unity` CLI executable was not found on PATH or " +
                "under " + windowsApps + " — it is a required part of " +
                "the local:T-W3.8-02 acceptance environment " +
                "(SPEC §T.1 G-06)");
            throw new InvalidOperationException("unreachable");
        }

        // Spawns `unity` with the global non-interactive/JSON flags plus
        // <paramref name="arguments"/>, pumps Editor frames while it runs
        // (a blocking WaitForExit would starve the main-thread commands
        // the call itself invokes), and returns the captured streams.
        private IEnumerator RunCli(
            CliResult result, int timeoutMs, params string[] arguments)
        {
            var cli = FindUnityCli();
            var psi = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.StandardOutputEncoding = s_utf8;
            psi.StandardErrorEncoding = s_utf8;
            psi.Environment["UNITY_NON_INTERACTIVE"] = "1";
            psi.Environment["UNITY_FORMAT"] = "json";
            psi.ArgumentList.Add("command");
            psi.ArgumentList.Add("--json");
            psi.ArgumentList.Add("--no-banner");
            psi.ArgumentList.Add("--non-interactive");
            psi.ArgumentList.Add("--quiet");
            psi.ArgumentList.Add("--color");
            psi.ArgumentList.Add("never");
            psi.ArgumentList.Add("--project-path");
            psi.ArgumentList.Add(ProjectRoot);
            // A null element passes the Add unremarked and detonates
            // inside Process.Start's PasteArguments.AppendArgument —
            // the signature of a pre-reload local consumed after a
            // WaitForDomainReload crossing. Assert first so a future
            // miss names the index instead of a framework NRE.
            for (var index = 0; index < arguments.Length; index++)
            {
                Assert.That(
                    arguments[index], Is.Not.Null,
                    "the `unity` CLI argument at index " + index +
                    " is null" +
                    (index > 0
                        ? " (the value after " + arguments[index - 1] + ")"
                        : ""));
                psi.ArgumentList.Add(arguments[index]);
            }
            var process = Process.Start(psi)!;
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var deadline =
                    EditorApplication.timeSinceStartup + timeoutMs / 1000.0;
                while (!process.HasExited &&
                       EditorApplication.timeSinceStartup < deadline)
                {
                    yield return null;
                }
                if (!process.HasExited)
                {
                    result.TimedOut = true;
                    try { process.Kill(); }
                    catch (Exception) { /* already gone */ }
                    process.WaitForExit(15000);
                }
                if (process.HasExited)
                {
                    result.ExitCode = process.ExitCode;
                }
                // A closed pipe completes the read promptly even when
                // the task has not yet been observed — bound the drain
                // so a wedged child can never hang the fixture.
                result.Stdout =
                    stdout.Wait(10000) ? stdout.Result : "";
                result.Stderr =
                    stderr.Wait(10000) ? stderr.Result : "";
            }
            finally
            {
                process.Dispose();
            }
        }

        // The CLI's --json envelope {success, command, data, errors,
        // warnings}: the first '{' onward parses as one object — the
        // human-format banner and telemetry line precede it only when
        // --json is absent. A body that does not parse is a corrupted
        // transport (a foreign write interleaved inside the spawned
        // process's stdout stream — the emitted wire object is a pure
        // JToken tree and can never produce an unquoted token), so the
        // raw capture is persisted to an artifact file before the
        // failure is raised: the next reader needs the bytes, not just
        // the parse error.
        private static JObject CliJson(CliResult result, string what)
        {
            var brace = result.Stdout.IndexOf('{');
            Assert.That(brace, Is.GreaterThanOrEqualTo(0),
                what + ": the CLI printed no JSON body — " +
                result.Describe());
            try
            {
                return JObject.Parse(result.Stdout.Substring(brace));
            }
            catch (Newtonsoft.Json.JsonException parse)
            {
                var artifact = PersistCliArtifact(result, what);
                Assert.Fail(
                    what + ": the CLI stdout is not parseable JSON (" +
                    parse.Message + ") — raw capture persisted at " +
                    artifact + " — " + result.Describe());
                throw new InvalidOperationException("unreachable");
            }
        }

        // How many process invocations a read-only CLI call may make
        // before an unparseable body fails the test. The corruption this
        // absorbs lands at a different byte each time — a transient
        // transport defect, never a deterministic emission — so a fresh
        // capture parses clean, while a hypothetical deterministic
        // defect fails all attempts and every capture is persisted as
        // evidence. Mutating calls (package add/remove,
        // execute_workflow, cancel_workflow) never take this path: a
        // retry whose corrupted reply still applied the change would
        // apply it twice.
        private const int CliReadOnlyAttempts = 3;

        private static string ArtifactDir =>
            Path.Combine(
                ProjectRoot, "Library", "OpWeaveCliSmokeArtifacts");

        private static int s_cliArtifactSeq;

        // Writes one corrupted capture — exit code, full stdout and
        // stderr — under Library/OpWeaveCliSmokeArtifacts/ (the
        // project's own scratch area, Git-ignored like the sibling
        // backup directory) and returns the path for the failure
        // message. A pass cleans nothing: captures that preceded a
        // retry that then succeeded stay as transport-defect evidence.
        private static string PersistCliArtifact(CliResult result, string what)
        {
            try
            {
                Directory.CreateDirectory(ArtifactDir);
                var path = Path.Combine(
                    ArtifactDir,
                    "cli-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") +
                    "-" + (++s_cliArtifactSeq).ToString("D2") +
                    ".stdout.txt");
                File.WriteAllText(
                    path,
                    "unity command " + what + "\n" + result.Describe(),
                    s_utf8);
                return path;
            }
            catch (Exception)
            {
                return "<artifact write failed>";
            }
        }

        // The read-only variant of <see cref="RunCli"/>: when the body
        // the CLI printed does not parse, the call is re-issued — a
        // transient interleave corrupts a fresh capture at a different
        // byte, so the retry returns clean output. Each unparseable
        // capture is persisted before the next attempt; after
        // <see cref="CliReadOnlyAttempts"/> attempts the last result is
        // handed back for <see cref="CliJson"/> to report. A timed-out
        // or JSON-less reply is not retried — only the corrupt-body
        // signature is a transport artifact.
        private IEnumerator RunCliReadOnly(
            CliResult result, int timeoutMs, params string[] arguments)
        {
            for (var attempt = 1;; attempt++)
            {
                yield return RunCli(result, timeoutMs, arguments);
                var brace = result.Stdout.IndexOf('{');
                var parseable = false;
                if (brace >= 0)
                {
                    try
                    {
                        JObject.Parse(result.Stdout.Substring(brace));
                        parseable = true;
                    }
                    catch (Newtonsoft.Json.JsonException)
                    {
                        parseable = false;
                    }
                }
                if (parseable || result.TimedOut || brace < 0 ||
                    attempt >= CliReadOnlyAttempts)
                {
                    yield break;
                }
                PersistCliArtifact(
                    result, string.Join(" ", arguments));
                yield return null;
            }
        }

        private static void AssertCliSucceeded(
            CliResult result, JObject envelope, string what)
        {
            Assert.That(result.TimedOut, Is.False,
                what + ": the `unity` CLI call timed out — " +
                result.Describe());
            Assert.That(envelope["success"]?.Value<bool>(), Is.True,
                what + ": the CLI call failed — " + result.Describe());
        }

        // The AgentApiResponse {ok, data, errors, warnings} the command
        // handler returned — wherever the exec result nests it inside
        // the CLI envelope (as data.result or directly), the object
        // carrying a boolean "ok" IS the response envelope.
        private static JObject AgentEnvelope(JObject root)
        {
            if (root["ok"] is JValue { Type: JTokenType.Boolean })
            {
                return root;
            }
            foreach (var token in root.Descendants().OfType<JObject>())
            {
                if (token["ok"] is JValue { Type: JTokenType.Boolean })
                {
                    return token;
                }
            }
            throw new InvalidOperationException(
                "no AgentApi envelope ({ok:...}) inside the CLI reply: " +
                root.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static JObject AgentData(JObject root)
        {
            var envelope = AgentEnvelope(root);
            var data = envelope["data"] as JObject;
            Assert.That(data, Is.Not.Null,
                "the AgentApi envelope carries no data object: " +
                envelope.ToString(Newtonsoft.Json.Formatting.None));
            return data!;
        }

        private static string DescribeEnvelopeErrors(JObject envelope) =>
            string.Join(
                "; ",
                ((envelope["errors"] as JArray) ?? new JArray())
                    .Select(e => (string?)e["code"] + " " + (string?)e["message"]));

        // ---------- Editor-side environment helpers ----------

        private static bool PackageInstalled(string name)
        {
            try
            {
                return PackageInfo.FindForPackageName(name) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool AssemblyLoaded(string name) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == name);

        private static JObject? LockEntry()
        {
            if (!File.Exists(LockPath))
            {
                return null;
            }
            try
            {
                var root = JObject.Parse(File.ReadAllText(LockPath));
                return root["dependencies"]?[SamplePackage] as JObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // One SHA-256 over the sorted relative paths and content hashes
        // of every file under the com.opweave.workflow package root —
        // the §T.2 "Core unchanged" evidence, held in SessionState so it
        // survives the reloads this suite forces.
        private static string OpWeaveTreeDigest()
        {
            var info = PackageInfo.FindForPackageName(OpWeavePackage);
            Assert.That(info, Is.Not.Null,
                "the com.opweave.workflow package must resolve for the " +
                "tree digest");
            var root = Path.GetFullPath(info!.resolvedPath);
            var lines = new StringBuilder();
            var files = Directory.GetFiles(
                root, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var relative = file.Substring(root.Length)
                    .TrimStart(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                lines.Append(relative)
                    .Append(' ')
                    .Append(Sha256Hex(File.ReadAllBytes(file)))
                    .Append('\n');
            }
            return Sha256Hex(s_utf8.GetBytes(lines.ToString()));
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var text = new StringBuilder(64);
                foreach (var b in sha.ComputeHash(bytes))
                {
                    text.Append(b.ToString("x2"));
                }
                return text.ToString();
            }
        }

        // ---------- manifest / lock byte capture and restore ----------

        private static byte[]? ReadBytesOrNull(string path) =>
            File.Exists(path) ? File.ReadAllBytes(path) : null;

        private static void CaptureOriginal()
        {
            Directory.CreateDirectory(BackupDir);
            var manifest = File.ReadAllBytes(ManifestPath);
            var lockBytes = ReadBytesOrNull(LockPath);
            File.WriteAllBytes(
                Path.Combine(BackupDir, "orig-manifest.json"), manifest);
            var lockBackup = Path.Combine(BackupDir, "orig-packages-lock.json");
            if (lockBytes is not null)
            {
                File.WriteAllBytes(lockBackup, lockBytes);
            }
            else if (File.Exists(lockBackup))
            {
                File.Delete(lockBackup);
            }
        }

        private static bool ProjectMatchesOriginal()
        {
            var manifestBackup =
                Path.Combine(BackupDir, "orig-manifest.json");
            if (!File.Exists(manifestBackup))
            {
                return false;
            }
            if (!File.ReadAllBytes(manifestBackup)
                    .SequenceEqual(ReadBytesOrNull(ManifestPath) ??
                        Array.Empty<byte>()))
            {
                return false;
            }
            var lockBackup =
                Path.Combine(BackupDir, "orig-packages-lock.json");
            var capturedLock = File.Exists(lockBackup)
                ? File.ReadAllBytes(lockBackup)
                : null;
            var currentLock = ReadBytesOrNull(LockPath);
            return capturedLock is null
                ? currentLock is null
                : currentLock is not null &&
                    capturedLock.SequenceEqual(currentLock);
        }

        // Byte-for-byte restore of the project's package state, then a
        // resolve when the bytes actually moved — the pending reload is
        // crossed by the caller at the sanctioned boundary.
        private static void RestoreOriginalSync()
        {
            var manifestBackup =
                Path.Combine(BackupDir, "orig-manifest.json");
            if (!File.Exists(manifestBackup))
            {
                return;
            }
            var changed = false;
            var manifest = File.ReadAllBytes(manifestBackup);
            if (!manifest.SequenceEqual(
                    ReadBytesOrNull(ManifestPath) ?? Array.Empty<byte>()))
            {
                File.WriteAllBytes(ManifestPath, manifest);
                changed = true;
            }
            var lockBackup =
                Path.Combine(BackupDir, "orig-packages-lock.json");
            var capturedLock = File.Exists(lockBackup)
                ? File.ReadAllBytes(lockBackup)
                : null;
            var currentLock = ReadBytesOrNull(LockPath);
            if (capturedLock is null && currentLock is not null)
            {
                try { File.Delete(LockPath); }
                catch (Exception) { /* a held file stays for settle */ }
                changed = true;
            }
            else if (capturedLock is not null &&
                     (currentLock is null ||
                      !capturedLock.SequenceEqual(currentLock)))
            {
                File.WriteAllBytes(LockPath, capturedLock);
                changed = true;
            }
            if (changed)
            {
                try { Client.Resolve(); }
                catch (Exception) { /* a partial restore still settles */ }
            }
        }

        // The fixture is settled only once the bytes match AND the
        // sample's Editor assembly is unloaded — the removal's pending
        // reload must be crossed, not just resolved (T-W2.13-02's
        // settled-restore rule).
        private static bool SampleFullyRemoved() =>
            !AssemblyLoaded(SampleEditorAssembly);

        private static void DeleteBackupDir()
        {
            if (Directory.Exists(BackupDir))
            {
                try { Directory.Delete(BackupDir, recursive: true); }
                catch (Exception) { /* a held file is left */ }
            }
        }

        // ---------- bounded waits ----------

        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            var deadline =
                EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!condition() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
        }

        // The compile/import quiet window — an install's compile may
        // still be running when its request completes, so let any
        // in-flight work settle first. A quiet spell alone does NOT
        // prove a reload is armed: the package can register before its
        // import and compile are queued (isCompiling false, nothing
        // requested), and in that deferred state this heuristic ends
        // on a domain where WaitForDomainReload.Perform would throw.
        // The pending proof below is what arms the crossing.
        private static IEnumerator QuietSettle()
        {
            var deadline =
                EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var quietAt = -1.0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (EditorApplication.isCompiling ||
                    EditorApplication.isUpdating)
                {
                    quietAt = EditorApplication.timeSinceStartup +
                        QuietSettleSec;
                }
                else if (quietAt >= 0.0 &&
                         EditorApplication.timeSinceStartup >= quietAt)
                {
                    break;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup +
                        QuietSettleSec;
                }
                yield return null;
            }
        }

        // The pending-request flag WaitForDomainReload.Perform polls —
        // the same proof PackageAddUpmTests arms its crossings on.
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the wait
        // reaches it through reflection. A reload already armed behind
        // the runner's reload lock surfaces here while isCompiling is
        // false, which is exactly the deferred state the quiet window
        // misses. The probe's shape is proven once in OneTimeSetUp: a
        // Unity version without it fails the fixture outright — there
        // is no isCompiling-only fallback.
        private const string ScriptReloadProbeType =
            "UnityEditorInternal.InternalEditorUtility";
        private const string ScriptReloadProbeMethod =
            "IsScriptReloadRequested";

        private static readonly MethodInfo? s_scriptReloadRequested =
            typeof(EditorApplication).Assembly
                .GetType(ScriptReloadProbeType)
                ?.GetMethod(
                    ScriptReloadProbeMethod,
                    BindingFlags.Static |
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

        // Non-null and shaped as OneTimeSetUp proved — the fixture does
        // not run at all on a Unity version whose probe is absent.
        private static bool ScriptReloadRequested() =>
            (bool)s_scriptReloadRequested!.Invoke(null, null)!;

        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        // The pending proof WaitForDomainReload needs before it is
        // yielded: its Perform throws the moment neither flag is set,
        // so the instruction may only be armed once a reload is
        // provably queued — a requested script reload, or an
        // in-flight compile that ends in one. Queued imports are
        // flushed about once a second (a synchronous Refresh is also
        // how the package's own compile surfaces). The wait only
        // waits — it never throws and never yields an edit-mode yield
        // instruction, which a nested enumerator is not allowed to
        // yield; the caller checks ReloadPending() afterwards.
        private static IEnumerator WaitForReloadPending()
        {
            var deadline =
                EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var nextRefreshAt = double.NegativeInfinity;
            while (!ReloadPending() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                if (EditorApplication.timeSinceStartup >= nextRefreshAt)
                {
                    AssetDatabase.Refresh();
                    nextRefreshAt =
                        EditorApplication.timeSinceStartup + 1.0;
                }
                yield return null;
            }
        }

        // The timeout report the WaitForReloadPending callers raise —
        // a timed-out wait means the package change produced no
        // compile, and the message names the one state in which no
        // change could ever have caused a reload.
        private static string ReloadTimeoutMessage(string what) =>
            "no domain reload became pending within " + WaitTimeoutSec +
            " s of " + what + " (sample Editor assembly " +
            SampleEditorAssembly + " loaded: " +
            AssemblyLoaded(SampleEditorAssembly) + ")";

        // The per-Editor-session resume-owner record behind each body
        // boundary: armed only by this test's own pre-boundary statement
        // and SessionState-mirrored across the reload, so a continuation
        // UTF resumes on a stale enumerator position — one that never
        // ran the arming segment — finds the previous test's mark or
        // none and fails the boundary check closed instead of silently
        // continuing into the post-reload phase. It complements the
        // s_domainAlive sentinel: the sentinel proves a reload
        // happened, the owner mark proves this test armed it.
        private const string BoundaryOwnerSessionKey =
            "OpWeave.CliSmokeAtf01.boundary-owner";

        private static string CurrentTestFullName =>
            TestContext.CurrentContext.Test.FullName ??
            TestContext.CurrentContext.Test.Name ?? "unknown";

        private static void ArmBoundary(string boundaryId) =>
            SessionState.SetString(
                BoundaryOwnerSessionKey,
                CurrentTestFullName + "|" + boundaryId);

        private static void AssertBoundaryOwned(string boundaryId)
        {
            var owner = SessionState.GetString(BoundaryOwnerSessionKey, "");
            Assert.That(
                owner,
                Is.EqualTo(CurrentTestFullName + "|" + boundaryId),
                "the \"" + boundaryId + "\" reload boundary was not armed by " +
                "this test in this Editor session (armed: \"" + owner +
                "\") — the continuation resumed on a stale position");
        }

        // ---------- pipeline server / Agent service readiness ----------

        // The loopback descriptor the CLI itself reads to find this
        // Editor — present and carrying a port+token means the server
        // published itself; the /api/status readiness check rides the
        // same descriptor (the L-01 wire the CLI consumes).
        private static bool ServerDescriptorPresent()
        {
            try
            {
                if (!File.Exists(DescriptorPath))
                {
                    return false;
                }
                var descriptor = JObject.Parse(
                    File.ReadAllText(DescriptorPath));
                return descriptor["port"]?.Value<int>() > 0 &&
                    !string.IsNullOrEmpty(
                        descriptor["evalToken"]?.ToString());
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The Agent service readiness check through the real CLI —
        // the wire mirror of the sibling suite's LiveReady()
        // (RegistrySettled() && AgentApiReady()). Answering alone is
        // not enough: get_registry_status returns ok:true even while
        // catalog.Snapshot() is still null — generationId,
        // registryHash and a fresh stale flag ride the data only once
        // a domain-startup build has published, and a Registry-scoped
        // command issued in that gap still hits the
        // MissingRegistrySnapshot refusal. Ready therefore means
        // ok:true AND a published, non-stale snapshot (generationId
        // present, stale false). The only refusal the loop tolerates
        // is OW-API-003 while the domain install is still landing;
        // spawned calls can also hit the ~0.6 s EDITOR_NOT_RUNNING
        // window a reload leaves, so the loop retries inside a
        // bounded window rather than asserting on a single attempt.
        private IEnumerator WaitAgentServiceReady()
        {
            var deadline =
                EditorApplication.timeSinceStartup + CliReadyDeadlineSec;
            var last = "";
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (!ServerDescriptorPresent())
                {
                    last = "no pipeline descriptor yet";
                    yield return null;
                    continue;
                }
                var result = new CliResult();
                yield return RunCli(
                    result, CliCallTimeoutMs,
                    "opweave", "get_registry_status");
                var body = "";
                var brace = result.Stdout.IndexOf('{');
                if (brace >= 0)
                {
                    body = result.Stdout.Substring(brace);
                }
                if (!result.TimedOut && body.Contains("\"OW-API-003\""))
                {
                    last = "OW-API-003 — service install not landed";
                    yield return null;
                    continue;
                }
                JObject? reply = null;
                if (brace >= 0)
                {
                    try { reply = JObject.Parse(body); }
                    catch (Exception) { reply = null; }
                }
                JObject? envelope = null;
                if (reply is not null)
                {
                    try { envelope = AgentEnvelope(reply); }
                    catch (InvalidOperationException) { envelope = null; }
                }
                if (reply is not null &&
                    reply["success"]?.Value<bool>() == true &&
                    envelope is not null &&
                    envelope["ok"]?.Value<bool>() == true)
                {
                    var data = envelope["data"] as JObject;
                    if (data is not null &&
                        !string.IsNullOrEmpty(
                            (string?)data["generationId"]) &&
                        data["stale"]?.Value<bool>() == false)
                    {
                        yield break;
                    }
                    last = "get_registry_status answered ok:true " +
                        "before a snapshot published (no " +
                        "generationId yet, or still stale)";
                }
                else if (envelope is not null &&
                         envelope["ok"]?.Value<bool>() == false)
                {
                    last = DescribeEnvelopeErrors(envelope);
                }
                else
                {
                    last = result.Describe();
                }
                yield return null;
            }
            Assert.Fail(
                "the Agent service never came up behind the CLI: " +
                last);
        }

        // ---------- sample fixture helpers ----------

        // The workflow document the installed sample itself ships under
        // Samples~/Workflows/ — the canonical AT-F01 artifact, found
        // through the installed package's resolved path, never a local
        // substitute. Its body rides SessionState so the post-removal
        // domain can replay the same document against the unregistered
        // Operation.
        private static string SampleWorkflowBody()
        {
            var cached = SessionState.GetString(SampleWorkflowKey, "");
            if (cached.Length != 0)
            {
                return cached;
            }
            var info = PackageInfo.FindForPackageName(SamplePackage);
            Assert.That(info, Is.Not.Null,
                "the sample package must be installed to read its " +
                "shipped workflow");
            var directory = Path.Combine(
                info!.resolvedPath, "Samples~", "Workflows");
            Assert.That(Directory.Exists(directory), Is.True,
                "the installed sample package ships no " +
                "Samples~/Workflows/: " + directory);
            var files = Directory.GetFiles(directory, "*.opweave.json");
            Array.Sort(files, StringComparer.Ordinal);
            Assert.That(files.Length, Is.GreaterThan(0),
                "the installed sample package ships no *.opweave.json");
            var body = File.ReadAllText(files[0]);
            SessionState.SetString(SampleWorkflowKey, body);
            return body;
        }

        // ---------- suite setup / teardown ----------

        // The pending-reload proof every domain crossing below arms on
        // is only as strong as the probe it polls: if this Unity
        // version does not carry it, the fixture fails here — before
        // any package change — instead of silently degrading to an
        // isCompiling-only check that re-opens the missed-request
        // race the probe exists to close.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var probe = s_scriptReloadRequested;
            if (probe is null ||
                !probe.IsStatic ||
                probe.GetParameters().Length != 0 ||
                probe.ReturnType != typeof(bool))
            {
                Assert.Fail(
                    "the domain-reload pending probe is unavailable on " +
                    "Unity " + Application.unityVersion + ": " +
                    ScriptReloadProbeType + "." + ScriptReloadProbeMethod +
                    " in the UnityEditor assembly must be a static, " +
                    "parameterless method returning bool");
            }
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            LogAssert.ignoreFailingMessages = true;

            // A run that died before its teardown may have left the
            // sample installed with the originals still backed up —
            // restore from the backup first so the capture records the
            // project's own baseline, never the sample's leftovers.
            RestoreOriginalSync();
            CaptureOriginal();

            if (SessionState.GetString(TreeDigestKey, "").Length == 0)
            {
                SessionState.SetString(
                    TreeDigestKey, OpWeaveTreeDigest());
            }

            var deadline =
                EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while ((!ProjectMatchesOriginal() || !SampleFullyRemoved()) &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            if (!ProjectMatchesOriginal() || !SampleFullyRemoved())
            {
                RestoreOriginalSync();
                AssetDatabase.Refresh();
                yield return WaitForReloadPending();
                Assert.That(ReloadPending(), Is.True,
                    ReloadTimeoutMessage("the manifest/lock restore"));
                yield return new WaitForDomainReload();
            }
            // A reload the restore left queued still fires on the next
            // reload-lock release — drain it from this method's own
            // enumerator (a nested enumerator may not yield
            // WaitForDomainReload) so the test body starts on a settled
            // domain and the reload it crosses is provably the
            // install's own compile.
            // `deadline` died with every other local at the crossing
            // above — the drain runs on a fresh budget armed on this
            // domain, re-armed after each further crossing inside the
            // loop so queued reloads all drain instead of the loop
            // silently ending on a zeroed field.
            var drainDeadline =
                EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (ReloadPending() &&
                   EditorApplication.timeSinceStartup < drainDeadline)
            {
                if (ScriptReloadRequested())
                {
                    yield return new WaitForDomainReload();
                    drainDeadline =
                        EditorApplication.timeSinceStartup +
                        WaitTimeoutSec;
                }
                yield return null;
            }
            Assert.That(ProjectMatchesOriginal(), Is.True,
                "the project did not settle on its captured " +
                "manifest/lock state");
            Assert.That(SampleFullyRemoved(), Is.True,
                "the sample's Editor assembly stayed loaded — a " +
                "previous teardown never crossed the removal reload");

            // Borrowing the project's manifest is non-destructive only
            // while the project does not already depend on the sample:
            // installing then removing a real dependency would rewrite
            // state the fixture may only restore. A declared dependency
            // is an unmet environment precondition, never a silent pass.
            var manifestText = File.ReadAllText(ManifestPath);
            if (manifestText.Contains(SamplePackage))
            {
                Assert.Inconclusive(
                    "precondition unmet: the project already depends " +
                    "on " + SamplePackage);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            RestoreOriginalSync();

            var deadline =
                EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (!ProjectMatchesOriginal() &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            if (!ProjectMatchesOriginal() || !SampleFullyRemoved())
            {
                RestoreOriginalSync();
                AssetDatabase.Refresh();
                yield return WaitForReloadPending();
                Assert.That(ReloadPending(), Is.True,
                    ReloadTimeoutMessage("the manifest/lock restore"));
                yield return new WaitForDomainReload();
            }
            // A reload the removal or the restore left queued still
            // fires on the next reload-lock release — cross each one
            // here so the next test's setup meets a settled domain.
            // The WaitForDomainReload sits in this method's own
            // enumerator: the Unity Test Framework rejects it from a
            // nested enumerator.
            // `deadline` died with every other local at the crossing
            // above — the drain runs on a fresh budget armed on this
            // domain, re-armed after each further crossing inside the
            // loop so queued reloads all drain instead of the loop
            // silently ending on a zeroed field.
            var drainDeadline =
                EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (ReloadPending() &&
                   EditorApplication.timeSinceStartup < drainDeadline)
            {
                if (ScriptReloadRequested())
                {
                    yield return new WaitForDomainReload();
                    drainDeadline =
                        EditorApplication.timeSinceStartup +
                        WaitTimeoutSec;
                }
                yield return null;
            }
            Assert.That(ProjectMatchesOriginal(), Is.True,
                "the project was not restored to its captured " +
                "manifest/lock state after the CLI probe");
            Assert.That(SampleFullyRemoved(), Is.True,
                "the sample's Editor assembly stayed loaded after " +
                "the restore — the removal reload was never crossed");

            // §T.2 pass condition: no file under
            // Packages/com.opweave.workflow/ changed — the tree digest
            // captured at session start must still hold.
            var baseline = SessionState.GetString(TreeDigestKey, "");
            Assert.That(baseline, Is.Not.Empty,
                "no com.opweave.workflow tree digest was captured");
            Assert.That(OpWeaveTreeDigest(), Is.EqualTo(baseline),
                "a file under Packages/com.opweave.workflow/ changed " +
                "during the CLI smoke/AT-F01 tests");

            DeleteBackupDir();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            SessionState.EraseString(TreeDigestKey);
            SessionState.EraseString(SampleWorkflowKey);
        }

        // ---------- §G0 / §T.1 G-06 pinned version ----------

        // The support pin, checked without launching a process: the
        // project manifest declares exactly com.unity.pipeline
        // 0.9.1-exp.1, the installed package reports that version, the
        // adapter assembly is compiled — and both gated asmdefs compile
        // ONLY under that exact version (versionDefines), so a
        // different Pipeline version can never present itself as
        // supported: the adapter and its command surface simply do not
        // exist (SPEC §G0, the "unverified is not supported" rule the
        // ticket's errors-and-boundaries criterion records).
        [Test]
        public void PinnedPipelineVersion_IsDeclaredAndGated()
        {
            var manifest = File.ReadAllText(ManifestPath);
            Assert.That(
                manifest.Contains(
                    "\"" + PipelinePackage + "\": \"" +
                    PipelinePinnedVersion + "\""),
                Is.True,
                "the project manifest must pin " + PipelinePackage +
                " to " + PipelinePinnedVersion);

            var info = PackageInfo.FindForPackageName(PipelinePackage);
            Assert.That(info, Is.Not.Null,
                PipelinePackage + " is not installed — the gated " +
                "test assembly cannot be running");
            Assert.That(info!.version, Is.EqualTo(PipelinePinnedVersion),
                "the installed Pipeline version must be the pinned " +
                PipelinePinnedVersion);

            var adapter = FindAssembly("OpWeave.Pipeline");
            Assert.That(adapter, Is.Not.Null,
                "the OpWeave.Pipeline adapter assembly is not compiled");

            // The compile gate itself is the "not reported as
            // supported" mechanism: both asmdefs must gate on exactly
            // the pinned version.
            var packageRoot = Path.GetFullPath(
                PackageInfo.FindForPackageName(OpWeavePackage)!
                    .resolvedPath);
            foreach (var relative in new[]
            {
                Path.Combine(
                    "Editor", "Pipeline", "OpWeave.Pipeline.asmdef"),
                Path.Combine(
                    "Tests", "Editor", "Pipeline",
                    "OpWeave.Pipeline.Tests.asmdef"),
            })
            {
                var asmdef = File.ReadAllText(
                    Path.Combine(packageRoot, relative));
                Assert.That(
                    asmdef.Contains(
                        "\"expression\": \"" + PipelinePinnedVersion + "\""),
                    Is.True,
                    relative + " must versionDefines-gate on exactly " +
                    PipelinePinnedVersion);
            }
        }

        private static Assembly? FindAssembly(string assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(
                        assembly.GetName().Name, assemblyName,
                        StringComparison.Ordinal))
                {
                    return assembly;
                }
            }
            return null;
        }

        // ---------- §T.1 G-06: command listing + minimal smoke ----------

        // `unity command opweave` on the pinned version lists exactly the
        // 14 §H1 commands, and a minimal search → validate → execute →
        // status/logs round trip works through the same real CLI surface
        // (the release gate's second half; the Git-UPM half is
        // local:T-W3.8-01).
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator CommandListing_AndMinimalFlow_ViaUnityCli()
        {
            LogAssert.ignoreFailingMessages = true;
            _ = FindUnityCli();

            var descriptorDeadline =
                EditorApplication.timeSinceStartup + CliReadyDeadlineSec;
            while (!ServerDescriptorPresent() &&
                   EditorApplication.timeSinceStartup < descriptorDeadline)
            {
                yield return null;
            }
            Assert.That(ServerDescriptorPresent(), Is.True,
                "the pipeline server never published its descriptor at " +
                DescriptorPath);
            yield return WaitAgentServiceReady();

            // ── command listing: the group's own listing must name all
            // 14 SPEC §H1 commands.
            var listing = new CliResult();
            yield return RunCliReadOnly(
                listing, CliCallTimeoutMs, "opweave");
            var listingJson = CliJson(listing, "opweave group listing");
            AssertCliSucceeded(
                listing, listingJson, "opweave group listing");
            foreach (var name in s_expectedCommands)
            {
                Assert.That(
                    listing.Stdout.Contains("opweave " + name) ||
                    listing.Stdout.Contains("opweave." + name) ||
                    listing.Stdout.Contains("\"" + name + "\""),
                    Is.True,
                    "the CLI listing does not name opweave " + name +
                    " — " + listing.Describe());
            }

            // ── minimal search: a catalog query over the CLI returns
            // the standard Operation.
            var search = new CliResult();
            yield return RunCliReadOnly(
                search, CliCallTimeoutMs,
                "opweave", "search_operations",
                "--producesType", "unity.GameObjectRef");
            AssertCliSucceeded(
                search, CliJson(search, "search_operations"),
                "search_operations");
            var searchData = AgentData(CliJson(search, "search_operations"));
            var items = searchData["items"] as JArray;
            Assert.That(items, Is.Not.Null.And.Not.Empty,
                "search_operations must return items");
            Assert.That(
                items!.Any(
                    i => (string?)i["id"] == "unity.gameobject.find"),
                Is.True,
                "the search must find unity.gameobject.find: " +
                (items?.ToString(Newtonsoft.Json.Formatting.None) ??
                    "<none>"));

            // ── describe the same Operation over the CLI.
            var describe = new CliResult();
            yield return RunCliReadOnly(
                describe, CliCallTimeoutMs,
                "opweave", "describe_operation",
                "--id", "unity.gameobject.find");
            var describeJson = CliJson(describe, "describe_operation");
            AssertCliSucceeded(describe, describeJson, "describe_operation");
            var described = AgentData(describeJson);
            Assert.That((string?)described["id"],
                Is.EqualTo("unity.gameobject.find"));

            // ── minimal validate + execute: start →
            // unity.gameobject.find over a GameObject this test creates
            // — read-only, idempotent, leaves no project change.
            var probeName = "OpWeaveCliSmoke" + Guid.NewGuid().ToString("N");
            var probe = new GameObject(probeName);
            try
            {
                var workflow =
                    "{\"schemaVersion\":\"1.0\",\"id\":\"example.cli-smoke-find\"," +
                    "\"nodes\":[" +
                    "{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}," +
                    "{\"id\":\"find\",\"operation\":\"unity.gameobject.find\",\"version\":1," +
                    "\"parameters\":{\"name\":\"" + probeName + "\"}}]," +
                    "\"controlEdges\":[" +
                    "{\"from\":\"start\",\"fromPort\":\"next\",\"to\":\"find\"}]}";

                var validate = new CliResult();
                yield return RunCliReadOnly(
                    validate, CliCallTimeoutMs,
                    "opweave", "validate_workflow",
                    "--workflow", workflow);
                var validateJson = CliJson(validate, "validate_workflow");
                AssertCliSucceeded(
                    validate, validateJson, "validate_workflow");
                var validateEnvelope = AgentEnvelope(validateJson);
                Assert.That(
                    validateEnvelope["ok"]?.Value<bool>(), Is.True,
                    "validate_workflow: " +
                    DescribeEnvelopeErrors(validateEnvelope));
                var planHash =
                    (string?)AgentData(validateJson)["planHash"];
                Assert.That(planHash, Is.Not.Null.And.Not.Empty,
                    "validate_workflow must report the §D0.1 planHash");

                var execute = new CliResult();
                yield return RunCli(
                    execute, CliCallTimeoutMs,
                    "opweave", "execute_workflow",
                    "--workflow", workflow,
                    "--expectedPlanHash", planHash!);
                var executeJson = CliJson(execute, "execute_workflow");
                AssertCliSucceeded(
                    execute, executeJson, "execute_workflow");
                var executeEnvelope = AgentEnvelope(executeJson);
                Assert.That(
                    executeEnvelope["ok"]?.Value<bool>(), Is.True,
                    "execute_workflow: " +
                    DescribeEnvelopeErrors(executeEnvelope));
                var runId =
                    (string?)AgentData(executeJson)["runId"];
                Assert.That(runId, Is.Not.Null.And.Not.Empty,
                    "a successful execute_workflow names the Run");

                var runBox = new RunStateBox();
                yield return PollRunToDecided(runBox, runId!);
                Assert.That(runBox.State, Is.EqualTo("succeeded"),
                    "the minimal find workflow must succeed — final " +
                    "state " + runBox.State);

                var logs = new CliResult();
                yield return RunCliReadOnly(
                    logs, CliCallTimeoutMs,
                    "opweave", "get_run_logs",
                    "--runId", runId!);
                var logsJson = CliJson(logs, "get_run_logs");
                AssertCliSucceeded(logs, logsJson, "get_run_logs");
                var changes =
                    AgentData(logsJson)["changes"] as JArray;
                Assert.That(changes, Is.Not.Null.And.Empty,
                    "the read-only find Run journals no change");
            }
            finally
            {
                if (probe is not null)
                {
                    UnityEngine.Object.DestroyImmediate(probe);
                }
            }
        }

        // Polls get_run_status over the CLI until the Run reaches a
        // decided state. The result rides a box because UnityTest
        // helpers return IEnumerator, not a value — `yield return` the
        // call so Editor frames (and with them the §E8 feed) pump.
        private sealed class RunStateBox
        {
            internal string State = "";
        }

        private IEnumerator PollRunToDecided(RunStateBox box, string runId)
        {
            var deadline =
                EditorApplication.timeSinceStartup + RunPollDeadlineSec;
            var decided = new HashSet<string>(StringComparer.Ordinal)
            {
                "succeeded", "failed", "cancelled",
                "needs_review", "awaiting_approval",
            };
            while (EditorApplication.timeSinceStartup < deadline)
            {
                var poll = new CliResult();
                yield return RunCli(
                    poll, CliCallTimeoutMs,
                    "opweave", "get_run_status",
                    "--runId", runId);
                if (poll.TimedOut)
                {
                    yield return null;
                    continue;
                }
                JObject? body = null;
                var brace = poll.Stdout.IndexOf('{');
                if (brace >= 0)
                {
                    try { body = JObject.Parse(poll.Stdout.Substring(brace)); }
                    catch (Exception) { body = null; }
                }
                if (body is null)
                {
                    yield return null;
                    continue;
                }
                JObject? envelope = null;
                try { envelope = AgentEnvelope(body); }
                catch (InvalidOperationException) { envelope = null; }
                if (envelope is null)
                {
                    yield return null;
                    continue;
                }
                // A just-admitted Run arrives through the §E8 feed the
                // next frames deliver; OW-RUN-010 until then.
                if (envelope["ok"]?.Value<bool>() != true)
                {
                    var errors =
                        (envelope["errors"] as JArray) ?? new JArray();
                    if (errors.Count == 1 &&
                        (string?)errors[0]!["code"] == "OW-RUN-010")
                    {
                        yield return null;
                        continue;
                    }
                    Assert.Fail(
                        "get_run_status(" + runId + "): " +
                        DescribeEnvelopeErrors(envelope));
                }
                box.State =
                    (string?)(envelope["data"] as JObject)?["state"] ?? "";
                if (decided.Contains(box.State))
                {
                    yield break;
                }
                yield return null;
            }
        }

        // ---------- §T.2 AT-F01 through the CLI ----------

        // The full external-extension flow on the real `unity` CLI:
        // `package add` installs the independent sample Git UPM at the
        // immutable full SHA, the domain-startup Registry build
        // registers Operation + TypeProvider + Codec atomically
        // (§X.4 / §C3), Agent search finds the Operation, validate and
        // execute succeed, `package remove` leaves no Registry entry —
        // and nothing under Packages/com.opweave.workflow/ changes
        // (§T.2, verified by the teardown tree digest).
        [UnityTest]
        [Timeout(1200000)]
        public IEnumerator Atf01_InstallSearchValidateExecuteRemove_ViaUnityCli()
        {
            LogAssert.ignoreFailingMessages = true;
            _ = FindUnityCli();

            yield return WaitAgentServiceReady();

            // ── install through the CLI: the pipeline package group's
            // `package add` at the immutable full-SHA Git URL — never a
            // local clone, file: substitute or moving tag. Async: the
            // answer is the in_progress receipt; the reload it arms is
            // crossed below at the sanctioned boundary.
            var add = new CliResult();
            yield return RunCli(
                add, 300000,
                "package", "add",
                "--identifier", SampleDependency,
                "--confirm");
            var addJson = CliJson(add, "package add");
            AssertCliSucceeded(add, addJson, "package add");

            yield return WaitUntil(() => PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.True,
                "the sample package never registered with UPM after " +
                "`unity command package add`");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the sample install"));
            ArmBoundary("install");
            s_domainAlive = true;
            yield return new WaitForDomainReload();
            AssertBoundaryOwned("install");
            Assert.That(s_domainAlive, Is.False,
                "the sample install did not cross a domain reload");

            // ── post-reload: the server is back on the same port and
            // token (L-01); the Agent service installs again.
            yield return WaitAgentServiceReady();

            // §X.4 attestation: the lock records the resolved commit —
            // exactly the pinned full SHA — and the provenance the
            // Agent surface reports agrees.
            var lockEntry = LockEntry();
            Assert.That(lockEntry, Is.Not.Null,
                "packages-lock.json must carry a " + SamplePackage +
                " entry after the install");
            Assert.That(
                (string?)lockEntry!["source"], Is.EqualTo("git"),
                "the lockfile entry must record a git dependency");
            Assert.That(
                (string?)lockEntry["hash"], Is.EqualTo(SamplePinnedSha),
                "the resolved commit must be the pinned SHA");

            // atomic registration: ONE published build admitted the
            // Operation, its TypeProvider and the BonePose type — the
            // §C3 rule-8 snapshot the Agent catalog reads.
            var search = new CliResult();
            yield return RunCliReadOnly(
                search, CliCallTimeoutMs,
                "opweave", "search_operations",
                "--producesType", SampleTypeId);
            var searchJson = CliJson(search, "search_operations");
            AssertCliSucceeded(search, searchJson, "search_operations");
            var items = AgentData(searchJson)["items"] as JArray;
            Assert.That(
                items is not null &&
                items.Any(i => (string?)i["id"] == SampleOperationId),
                Is.True,
                "Agent search must find " + SampleOperationId +
                " — " + (items?.ToString(
                    Newtonsoft.Json.Formatting.None) ?? "<no items>"));

            var describe = new CliResult();
            yield return RunCliReadOnly(
                describe, CliCallTimeoutMs,
                "opweave", "describe_operation",
                "--id", SampleOperationId);
            var describeJson = CliJson(describe, "describe_operation");
            AssertCliSucceeded(
                describe, describeJson, "describe_operation");
            var described = AgentData(describeJson);
            Assert.That((string?)described["id"],
                Is.EqualTo(SampleOperationId));
            Assert.That((string?)described["origin"],
                Is.EqualTo("custom"));
            Assert.That((string?)described["packageId"],
                Is.EqualTo(SamplePackage),
                "the Registry's own provenance must name the " +
                "defining package");
            Assert.That((string?)described["resolvedCommit"],
                Is.EqualTo(SamplePinnedSha),
                "the Registry's own provenance must attest the " +
                "resolved commit " + SamplePinnedSha);

            var types = new CliResult();
            yield return RunCliReadOnly(
                types, CliCallTimeoutMs,
                "opweave", "list_types",
                "--prefix", "org.opweave.sample");
            var typesJson = CliJson(types, "list_types");
            AssertCliSucceeded(types, typesJson, "list_types");
            var typeEntries = AgentData(typesJson)["types"] as JArray;
            Assert.That(
                typeEntries is not null &&
                typeEntries.Any(
                    t => (string?)t["id"] == SampleTypeId ||
                         (string?)t["typeId"] == SampleTypeId ||
                         t.ToString().Contains(SampleTypeId)),
                Is.True,
                "the sample's custom Type " + SampleTypeId +
                " must be registered in the same atomic build");

            // The persisted diff records the registration — the same
            // atomic pass the snapshot published.
            var status = new CliResult();
            yield return RunCliReadOnly(
                status, CliCallTimeoutMs,
                "opweave", "get_registry_status");
            var statusJson = CliJson(status, "get_registry_status");
            AssertCliSucceeded(
                status, statusJson, "get_registry_status");
            var statusData = AgentData(statusJson);
            Assert.That(
                statusData["stale"]?.Value<bool>(), Is.False,
                "the published snapshot must not be stale");
            var diffText =
                statusData["diff"]?.ToString(
                    Newtonsoft.Json.Formatting.None) ?? "";
            Assert.That(
                diffText.Contains(SampleOperationId) &&
                diffText.Contains("\"added\""),
                Is.True,
                "the registry diff must record the sample Operation " +
                "as added: " + diffText);

            // ── validate + execute the shipped sample workflow through
            // the CLI.
            var workflow = SampleWorkflowBody();

            var validate = new CliResult();
            yield return RunCliReadOnly(
                validate, CliCallTimeoutMs,
                "opweave", "validate_workflow",
                "--workflow", workflow);
            var validateJson = CliJson(validate, "validate_workflow");
            AssertCliSucceeded(
                validate, validateJson, "validate_workflow");
            var validateEnvelope = AgentEnvelope(validateJson);
            Assert.That(
                validateEnvelope["ok"]?.Value<bool>(), Is.True,
                "sample validate_workflow: " +
                DescribeEnvelopeErrors(validateEnvelope));
            var planHash =
                (string?)AgentData(validateJson)["planHash"];
            Assert.That(planHash, Is.Not.Null.And.Not.Empty,
                "validate_workflow must report the §D0.1 planHash");

            var execute = new CliResult();
            yield return RunCli(
                execute, CliCallTimeoutMs,
                "opweave", "execute_workflow",
                "--workflow", workflow,
                "--expectedPlanHash", planHash!);
            var executeJson = CliJson(execute, "execute_workflow");
            AssertCliSucceeded(
                execute, executeJson, "execute_workflow");
            var executeEnvelope = AgentEnvelope(executeJson);
            Assert.That(
                executeEnvelope["ok"]?.Value<bool>(), Is.True,
                "sample execute_workflow: " +
                DescribeEnvelopeErrors(executeEnvelope));
            var runId = (string?)AgentData(executeJson)["runId"];
            Assert.That(runId, Is.Not.Null.And.Not.Empty,
                "a successful execute_workflow names the Run");

            var runBox = new RunStateBox();
            yield return PollRunToDecided(runBox, runId!);
            Assert.That(runBox.State, Is.EqualTo("succeeded"),
                "the sample workflow must succeed — final state " +
                runBox.State);

            // The documented result: exactly the 'pose' output, a
            // BonePose at (0, 1, 0), read back as plain JSON — the
            // Codec contract the package itself documents.
            var finalStatus = new CliResult();
            yield return RunCliReadOnly(
                finalStatus, CliCallTimeoutMs,
                "opweave", "get_run_status",
                "--runId", runId!);
            var outputs =
                AgentData(CliJson(finalStatus, "get_run_status"))
                    ["outputs"] as JObject;
            var pose = outputs?["pose"] as JObject;
            Assert.That(pose, Is.Not.Null,
                "the Run's 'pose' output must be a BonePose object: " +
                (outputs?.ToString(
                    Newtonsoft.Json.Formatting.None) ?? "<none>"));
            Assert.That((double?)pose!["x"], Is.EqualTo(0.0));
            Assert.That((double?)pose["y"], Is.EqualTo(1.0));
            Assert.That((double?)pose["z"], Is.EqualTo(0.0));

            var logs = new CliResult();
            yield return RunCliReadOnly(
                logs, CliCallTimeoutMs,
                "opweave", "get_run_logs",
                "--runId", runId!);
            var changes =
                AgentData(CliJson(logs, "get_run_logs"))["changes"]
                    as JArray;
            Assert.That(changes, Is.Not.Null.And.Empty,
                "the sample Run records no project-state change");

            // ── removal through the CLI — the §X.9 acceptance: after
            // the real removal and its reload the Operation,
            // TypeProvider and Codec are gone and no stale Registry
            // entry survives.
            var remove = new CliResult();
            yield return RunCli(
                remove, 300000,
                "package", "remove",
                "--name", SamplePackage,
                "--confirm");
            var removeJson = CliJson(remove, "package remove");
            AssertCliSucceeded(remove, removeJson, "package remove");

            yield return WaitUntil(() => !PackageInstalled(SamplePackage));
            Assert.That(PackageInstalled(SamplePackage), Is.False,
                "the sample package stayed registered after " +
                "`unity command package remove`");

            AssetDatabase.Refresh();
            yield return QuietSettle();
            yield return WaitForReloadPending();
            Assert.That(ReloadPending(), Is.True,
                ReloadTimeoutMessage("the sample removal"));
            ArmBoundary("remove");
            s_domainAlive = true;
            yield return new WaitForDomainReload();
            AssertBoundaryOwned("remove");
            Assert.That(s_domainAlive, Is.False,
                "the sample removal did not cross a domain reload");

            yield return WaitAgentServiceReady();

            Assert.That(LockEntry(), Is.Null,
                "packages-lock.json must drop the " + SamplePackage +
                " entry after removal");

            // Two removal proofs whose criteria stay valid for an
            // unregistered Type. First, the producesType search itself:
            // the sample's TypeProvider is gone, so the filter Type ID
            // can no longer resolve — OW-TYPE-001 'Unknown type' is
            // the TypeProvider-removal evidence, not a failure of the
            // check (SearchOperations resolve-fails the request
            // through ResolveFilterType → TypeRegistry.Resolve).
            var searchAfter = new CliResult();
            yield return RunCliReadOnly(
                searchAfter, CliCallTimeoutMs,
                "opweave", "search_operations",
                "--producesType", SampleTypeId);
            var searchAfterJson =
                CliJson(searchAfter, "search_operations");
            Assert.That(searchAfter.TimedOut, Is.False,
                "search_operations after removal timed out — " +
                searchAfter.Describe());
            var searchAfterEnvelope = AgentEnvelope(searchAfterJson);
            Assert.That(
                searchAfterEnvelope["ok"]?.Value<bool>(), Is.False,
                "a producesType search for the removed package's " +
                "Type must fail once the TypeProvider is gone, not " +
                "return items: " +
                searchAfterJson.ToString(
                    Newtonsoft.Json.Formatting.None));
            Assert.That(
                ((searchAfterEnvelope["errors"] as JArray) ??
                    new JArray())
                    .Any(e => (string?)e["code"] == "OW-TYPE-001"),
                Is.True,
                "the unregistered Type must surface as OW-TYPE-001 " +
                "(unknown Type) — got: " +
                DescribeEnvelopeErrors(searchAfterEnvelope));

            // Second, an origin-filtered listing — a criterion that
            // never resolve-fails — shows no surviving Registry entry
            // for the Operation.
            var customAfter = new CliResult();
            yield return RunCliReadOnly(
                customAfter, CliCallTimeoutMs,
                "opweave", "search_operations",
                "--origin", "custom");
            var customAfterJson = CliJson(
                customAfter, "search_operations --origin custom");
            AssertCliSucceeded(
                customAfter, customAfterJson,
                "search_operations --origin custom");
            var afterItems =
                AgentData(customAfterJson)["items"] as JArray;
            Assert.That(
                afterItems is null ||
                !afterItems.Any(
                    i => (string?)i["id"] == SampleOperationId),
                Is.True,
                "the removed sample Operation must not survive in " +
                "Agent search");

            // A workflow still referencing the removed Operation fails
            // validation with OW-OP-001 — through the public CLI path.
            // The body is re-derived, never trusted across the removal
            // reload: the crossing dropped every pre-reload local (the
            // state machine's fields are rebuilt, not restored), so the
            // post-removal domain reads the same document from the
            // SessionState cache SampleWorkflowBody() fills — the
            // package's own copy is already gone.
            workflow = SampleWorkflowBody();
            var rejected = new CliResult();
            yield return RunCliReadOnly(
                rejected, CliCallTimeoutMs,
                "opweave", "validate_workflow",
                "--workflow", workflow);
            var rejectedJson = CliJson(rejected, "validate_workflow");
            var rejectedEnvelope = AgentEnvelope(rejectedJson);
            Assert.That(
                rejectedEnvelope["ok"]?.Value<bool>(), Is.False,
                "a workflow referencing the removed Operation must " +
                "fail validation");
            Assert.That(
                ((rejectedEnvelope["errors"] as JArray) ?? new JArray())
                    .Any(e => (string?)e["code"] == "OW-OP-001"),
                Is.True,
                "the rejection must be OW-OP-001 (unknown Operation), " +
                "got: " + DescribeEnvelopeErrors(rejectedEnvelope));

            var statusAfter = new CliResult();
            yield return RunCliReadOnly(
                statusAfter, CliCallTimeoutMs,
                "opweave", "get_registry_status");
            var statusAfterJson =
                CliJson(statusAfter, "get_registry_status");
            AssertCliSucceeded(
                statusAfter, statusAfterJson, "get_registry_status");
            var afterDiffText =
                (AgentData(statusAfterJson)["diff"])?.ToString(
                    Newtonsoft.Json.Formatting.None) ?? "";
            Assert.That(
                afterDiffText.Contains(SampleOperationId) &&
                afterDiffText.Contains("\"removed\""),
                Is.True,
                "the registry diff must report the sample Operation " +
                "as removed: " + afterDiffText);
        }
    }
}
