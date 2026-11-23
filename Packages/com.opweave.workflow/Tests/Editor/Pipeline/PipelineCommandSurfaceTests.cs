#nullable enable
// T-W3.3-02 [Edit] tests: the SPEC §H2 command-surface acceptance checks —
// "unity command opweave" lists exactly the 14 §H1 commands and
// get_run_status answers while a long import holds the main thread. This
// file lives in the OpWeave.Pipeline.Tests assembly, which compiles only
// when com.unity.pipeline 0.9.1-exp.1 is installed (versionDefines +
// defineConstraints, the same gate as OpWeave.Pipeline and the L-01 probe
// pattern): without the package the fixture compiles out entirely instead
// of reporting Inconclusive, which -runTests maps to a failed exit code.
//
// Like the L-01 probe, the wire checks ride the server's published loopback
// transport — the same /api/commands and /api/exec forms the `unity` CLI
// itself issues — authenticated by the evalToken in
// Library/Pipeline/.unity-pipeline-port. The exact-count check reads the
// compiled assembly's [CliCommand] attributes directly, so it is
// independent of server timing. This file names no Pipeline type:
// everything travels over HTTP plus reflection, so no reference to
// Unity.Pipeline* is needed (keeping the §H2 item 7 reference surface
// confined to the adapter itself).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.Pipeline.Tests
{
    /// <summary>The SPEC §H2 surface tests of T-W3.3-02.</summary>
    internal sealed class PipelineCommandSurfaceTests
    {
        // The 14 SPEC §H1 commands under group "opweave" — the value is the
        // expected mainThreadRequired: the run-observation trio reads the
        // immutable snapshot off the main thread (SPEC §H2 item 3), every
        // other command runs on the main thread.
        private static readonly KeyValuePair<string, bool>[] ExpectedCommands =
        {
            new KeyValuePair<string, bool>("search_operations", true),
            new KeyValuePair<string, bool>("describe_operation", true),
            new KeyValuePair<string, bool>("search_compatible_operations", true),
            new KeyValuePair<string, bool>("list_types", true),
            new KeyValuePair<string, bool>("get_workflow_schema", true),
            new KeyValuePair<string, bool>("list_workflows", true),
            new KeyValuePair<string, bool>("validate_workflow", true),
            new KeyValuePair<string, bool>("execute_workflow", true),
            new KeyValuePair<string, bool>("get_run_status", false),
            new KeyValuePair<string, bool>("get_run_logs", false),
            new KeyValuePair<string, bool>("list_runs", false),
            new KeyValuePair<string, bool>("cancel_workflow", true),
            new KeyValuePair<string, bool>("migrate_workflow", true),
            new KeyValuePair<string, bool>("get_registry_status", true),
        };

        private const string DescriptorRelPath = "Library/Pipeline/.unity-pipeline-port";
        private const int HttpTimeoutMs = 20000;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string DescriptorPath => Path.Combine(
            ProjectRoot, DescriptorRelPath.Replace('/', Path.DirectorySeparatorChar));

        // ── SPEC §H2 test: the adapter declares exactly the 14 §H1 commands ──

        // The compiled-assembly check: every [CliCommand] in OpWeave.Pipeline
        // declares group "opweave" plus an explicit name, the set is exactly
        // the 14 SPEC §H1 names (no extras, no omissions), and the
        // observation trio is flagged MainThreadRequired = false. Reading the
        // compiled attributes is independent of server startup timing.
        [Test]
        public void AdapterAssembly_DeclaresExactlyTheFourteenSpecCommands()
        {
            var adapter = FindAssembly("OpWeave.Pipeline");
            Assert.That(adapter, Is.Not.Null,
                "the OpWeave.Pipeline adapter assembly is not compiled");

            var found = new Dictionary<string, bool>(StringComparer.Ordinal);
            const BindingFlags declared =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var type in adapter!.GetTypes())
            {
                foreach (var method in type.GetMethods(declared))
                {
                    foreach (var attributeData in method.GetCustomAttributesData())
                    {
                        var attributeName = attributeData.AttributeType.FullName;
                        if (attributeName != "Unity.Pipeline.Commands.CliCommandAttribute" &&
                            attributeName != "Unity.Pipeline.Commands.CliCommand")
                        {
                            continue;
                        }
                        var args = attributeData.ConstructorArguments;
                        Assert.That(args.Count, Is.GreaterThanOrEqualTo(3),
                            method.Name + " must declare group, name and description");
                        Assert.That(args[0].Value as string, Is.EqualTo("opweave"),
                            method.Name + " must register under group opweave");
                        var name = args[1].Value as string;
                        Assert.That(name, Is.Not.Null.And.Not.Empty,
                            method.Name + " must declare its command name explicitly");

                        // The effective thread flag, read off the instantiated
                        // attribute — independent of how the value was supplied
                        // and of whether it is a property or a field.
                        var instance = method.GetCustomAttributes(inherit: false)
                            .First(a => a.GetType() == attributeData.AttributeType);
                        var property = attributeData.AttributeType.GetProperty("MainThreadRequired");
                        var field = attributeData.AttributeType.GetField("MainThreadRequired");
                        Assert.That(property is not null || field is not null, Is.True,
                            "CliCommandAttribute carries no MainThreadRequired flag");
                        var mainThreadRequired = (bool)(
                            property?.GetValue(instance) ?? field!.GetValue(instance))!;
                        Assert.IsFalse(found.ContainsKey(name!),
                            "duplicate command name " + name);
                        found[name!] = mainThreadRequired;
                    }
                }
            }

            Assert.That(found.Count, Is.EqualTo(ExpectedCommands.Length),
                "OpWeave.Pipeline must register exactly the 14 SPEC §H1 commands; found " +
                string.Join(", ", found.Keys));
            foreach (var expected in ExpectedCommands)
            {
                Assert.That(found.ContainsKey(expected.Key), Is.True,
                    "missing command " + expected.Key);
                Assert.That(found[expected.Key], Is.EqualTo(expected.Value),
                    expected.Key + " has the wrong MainThreadRequired flag");
            }
        }

        // ── SPEC §H2 test: `unity command opweave` lists the 14 commands ──

        // The wire listing the CLI itself consumes: GET /api/commands
        // ?group=opweave returns every registered command in the group with
        // its mainThreadRequired flag and declaring package. The 14 §H1 names
        // must all be present with the §H2 item-3 thread flags, and the
        // entries the server attributes to the com.opweave.workflow package
        // must be exactly those 14 — no extras.
        [Test]
        public void CommandListing_ServerListsEverySpecCommand()
        {
            Assert.IsTrue(WaitForServerReady(60),
                "the pipeline server did not report status ready");

            var exchange = HttpSync("GET", "/api/commands?group=opweave&detail=full", null);
            Assert.That(exchange.StatusCode, Is.EqualTo(200), exchange.Describe());
            var doc = JsonUtility.FromJson<CommandsDoc>(exchange.Body);
            var entries = doc?.commands ?? new List<CommandEntry>();
            Assert.That(entries.Count, Is.GreaterThanOrEqualTo(ExpectedCommands.Length),
                "fewer commands are listed under group opweave than the SPEC §H1 surface");

            foreach (var expected in ExpectedCommands)
            {
                var entry = entries.Find(e =>
                    e.command == "opweave " + expected.Key || e.name == expected.Key);
                Assert.That(entry, Is.Not.Null,
                    "command opweave " + expected.Key + " is not listed");
                Assert.That(entry!.mainThreadRequired, Is.EqualTo(expected.Value),
                    "opweave " + expected.Key + " has the wrong mainThreadRequired flag");
            }

            // Nothing outside the 14 SPEC names may sit under group opweave —
            // the L-01 probe commands in Assets/ (l01_*) are the known
            // exceptions owned by a different fixture.
            foreach (var entry in entries)
            {
                var name = !string.IsNullOrEmpty(entry.name)
                    ? entry.name
                    : entry.command.StartsWith("opweave ", StringComparison.Ordinal)
                        ? entry.command.Substring("opweave ".Length)
                        : entry.command;
                if (name.StartsWith("l01_", StringComparison.Ordinal))
                {
                    continue;
                }
                Assert.That(
                    Array.Exists(ExpectedCommands, e => e.Key == name), Is.True,
                    "unexpected command under group opweave: " + entry.command);
            }
        }

        // ── SPEC §H2 test: get_run_status answers during a long import ──

        // get_run_status is MainThreadRequired = false, so it runs on a
        // thread-pool worker (L-01 item 6): while the main thread is held by
        // back-to-back synchronous asset imports the exec answer must still
        // arrive — L-01 measured HTTP 200 for off-main commands even while
        // the editor's main thread never ticked. The answer itself is either
        // the normal envelope (installed service: the well-formed but unknown
        // runId reports OW-RUN-010 inside ok:false) or, before the domain
        // install lands, the OW-API-003 busy refusal through Pipeline's error
        // channel — both prove the command answered.
        [Test]
        public void GetRunStatus_AnswersWhileMainThreadIsImporting()
        {
            Assert.IsTrue(WaitForServerReady(60),
                "the pipeline server did not report status ready");

            var dirRel = "Assets/OpWeavePipelineTests";
            var dirAbs = Path.Combine(
                ProjectRoot, dirRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dirAbs);
            var assetRel = dirRel + "/long-import-payload.bytes";
            var assetAbs = Path.Combine(dirAbs, "long-import-payload.bytes");
            File.WriteAllBytes(assetAbs, PayloadBytes());
            try
            {
                // Warm the importer and prove the asset is real before the
                // measurement loop — a failed import would make the busy
                // window empty and the overlap check meaningless.
                AssetDatabase.ImportAsset(assetRel, ImportAssetOptions.ForceSynchronousImport);

                var body =
                    "{\"command\":\"opweave get_run_status\"," +
                    "\"parameters\":{\"runId\":\"run_00000000000000000000000000\"}}";
                // The pre-install OW-API-003 refusal leaves through the
                // Pipeline error channel, which logs the envelope as [Error]
                // — consume that expected log. The installed path answers a
                // normal ok:false envelope and logs nothing, so the
                // expectation is registered only where the log is certain.
                if (!AgentApiInstalled())
                {
                    LogAssert.Expect(LogType.Error, new Regex(
                        "Command 'opweave get_run_status' failed[\\s\\S]*OW-API-003"));
                }
                var task = HttpAsync("POST", "/api/exec", body, true, HttpTimeoutMs);

                var answeredWhileBusy = false;
                var stopwatch = Stopwatch.StartNew();
                var stamp = 0;
                while (!answeredWhileBusy && stopwatch.ElapsedMilliseconds < 8000)
                {
                    // Touch the payload so each iteration performs a real
                    // synchronous import rather than a cached no-op.
                    using (var stream = new FileStream(
                        assetAbs, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        stream.WriteByte((byte)(stamp++ & 0xFF));
                    }
                    AssetDatabase.ImportAsset(assetRel, ImportAssetOptions.ForceSynchronousImport);
                    answeredWhileBusy = task.IsCompleted;
                }
                Assert.IsTrue(answeredWhileBusy,
                    "get_run_status did not answer while the import loop held the main thread" +
                    (task.IsCompleted ? "" : " (still outstanding at loop end)"));

                var exchange = task.Result;
                Assert.IsFalse(exchange.TransportFailed, exchange.Describe());
                if (exchange.StatusCode == 200)
                {
                    // The normal response path: the AgentApi envelope inside
                    // the exec result — the well-formed but unknown runId is
                    // a business failure, so ok:false with an OW-* diagnostic.
                    Assert.That(
                        exchange.Body.Contains("\"ok\":false") ||
                        exchange.Body.Contains("\"ok\": false"), Is.True,
                        "the answered envelope must be ok:false: " + exchange.Describe());
                    Assert.That(exchange.Body, Does.Contain("OW-"),
                        "the answered envelope must carry a diagnostic: " + exchange.Describe());
                }
                else
                {
                    // Before the Agent API install lands the adapter refuses
                    // through the Pipeline error channel as OW-API-003.
                    Assert.That(exchange.StatusCode, Is.EqualTo(400), exchange.Describe());
                    Assert.That(exchange.Body, Does.Contain("OW-API-003"),
                        "an uninstalled service must surface as OW-API-003: " +
                        exchange.Describe());
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(dirRel);
                if (Directory.Exists(dirAbs))
                {
                    Directory.Delete(dirAbs, recursive: true);
                }
                var metaFile = dirAbs + ".meta";
                if (File.Exists(metaFile))
                {
                    File.Delete(metaFile);
                }
            }
        }

        // ── SPEC §H2 item 5: API misuse leaves through the Pipeline error channel ──

        // A malformed runId is request-shape misuse: the service answers
        // OW-API-001 and the adapter lifts the envelope into a Pipeline error
        // response (L-01 item 5: a thrown non-argument exception surfaces as
        // "Command Execution Failed" carrying the message), so the wire shows
        // HTTP 400 with the OW-API-* code inside — never a 200 with ok:false.
        // Before the service install lands the refusal is OW-API-003 instead.
        [Test]
        public void ApiMisuse_LeavesAsPipelineError()
        {
            Assert.IsTrue(WaitForServerReady(60),
                "the pipeline server did not report status ready");

            // Both refusal paths lift the envelope into a Pipeline error
            // response, and DefaultCommandQueue.InvokeDirect logs it as
            // [Error] ("Command 'opweave get_run_status' failed: {…}") — that
            // log IS the expected Pipeline-error outcome, so it is consumed
            // with an explicit expectation, not a blanket ignore.
            var expectedCode = AgentApiInstalled() ? "OW-API-001" : "OW-API-003";
            LogAssert.Expect(LogType.Error, new Regex(
                "Command 'opweave get_run_status' failed[\\s\\S]*" + expectedCode));
            var exchange = HttpSync("POST", "/api/exec",
                "{\"command\":\"opweave get_run_status\"," +
                "\"parameters\":{\"runId\":\"not-a-run\"}}");
            Assert.IsFalse(exchange.TransportFailed, exchange.Describe());
            Assert.That(exchange.StatusCode, Is.EqualTo(400), exchange.Describe());
            Assert.That(exchange.Body, Does.Contain(expectedCode),
                "the misuse answer must carry " + expectedCode + ": " + exchange.Describe());
        }

        // ── helpers (the L-01 probe's verified wire pattern) ──────────────

        // Whether the domain-load install of AgentApiHost.Api has landed —
        // read over reflection so this fixture names no AgentApi type.
        private static bool AgentApiInstalled()
        {
            try
            {
                var host = FindAssembly("OpWeave.AgentApi")
                    ?.GetType("OpWeave.AgentApi.AgentApiHost");
                return host?.GetProperty("Api")?.GetValue(null) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Assembly? FindAssembly(string assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }
            try
            {
                return Assembly.Load(new AssemblyName(assemblyName));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[] PayloadBytes()
        {
            var payload = new byte[2 * 1024 * 1024];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i * 31 & 0xFF);
            }
            return payload;
        }

        private sealed class Exchange
        {
            public int StatusCode = -1;
            public string StatusName = "";
            public string Body = "";
            public string Error = "";

            public bool TransportFailed => StatusCode < 0;

            public string Describe() =>
                TransportFailed
                    ? "transport:" + Error
                    : "HTTP " + StatusCode + " " + StatusName + " " +
                      (Body.Length <= 400 ? Body : Body.Substring(0, 400) + "...");
        }

        [Serializable]
        public sealed class PipelineDescriptor
        {
            public int port;
            public string evalToken = "";
        }

        [Serializable]
        public sealed class CommandParam
        {
            public string name = "";
            public string type = "";
            public bool required;
        }

        [Serializable]
        public sealed class CommandEntry
        {
            public string command = "";
            public string name = "";
            public string group = "";
            public bool mainThreadRequired;
            public string package = "";
            public List<CommandParam> parameters = new List<CommandParam>();
        }

        [Serializable]
        public sealed class CommandsDoc
        {
            public List<CommandEntry> commands = new List<CommandEntry>();
        }

        private static PipelineDescriptor? ReadDescriptor()
        {
            try
            {
                if (!File.Exists(DescriptorPath))
                {
                    return null;
                }
                var descriptor = JsonUtility.FromJson<PipelineDescriptor>(
                    File.ReadAllText(DescriptorPath));
                return descriptor != null && descriptor.port > 0 &&
                    !string.IsNullOrEmpty(descriptor.evalToken)
                    ? descriptor
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadAll(WebResponse response)
        {
            using (var stream = response.GetResponseStream())
            {
                if (stream is null)
                {
                    return "";
                }
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static Exchange HttpRoundTrip(
            string method, string path, string? bodyJson, bool authorize, int timeoutMs)
        {
            var exchange = new Exchange();
            try
            {
                var descriptor = ReadDescriptor();
                if (descriptor is null)
                {
                    exchange.Error = "no pipeline descriptor at " + DescriptorPath;
                    return exchange;
                }
                var request = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:" + descriptor.port + path);
                request.Method = method;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.KeepAlive = false;
                if (authorize)
                {
                    request.Headers["Authorization"] = "Bearer " + descriptor.evalToken;
                }
                if (bodyJson is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(bodyJson);
                    request.ContentType = "application/json";
                    request.ContentLength = bytes.Length;
                    using (var stream = request.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        exchange.StatusCode = (int)response.StatusCode;
                        exchange.StatusName = response.StatusDescription ?? "";
                        exchange.Body = ReadAll(response);
                    }
                }
                catch (WebException exception)
                {
                    if (exception.Response is HttpWebResponse errorResponse)
                    {
                        exchange.StatusCode = (int)errorResponse.StatusCode;
                        exchange.StatusName = errorResponse.StatusDescription ?? "";
                        exchange.Body = ReadAll(errorResponse);
                        errorResponse.Close();
                    }
                    else
                    {
                        exchange.Error = exception.Status + ": " + FirstLine(exception.Message);
                    }
                }
            }
            catch (Exception exception)
            {
                exchange.Error = exception.GetType().Name + ": " + FirstLine(exception.Message);
            }
            return exchange;
        }

        private static Task<Exchange> HttpAsync(string method, string path, string? bodyJson,
            bool authorize = true, int timeoutMs = HttpTimeoutMs) =>
            Task.Run(() => HttpRoundTrip(method, path, bodyJson, authorize, timeoutMs));

        // The synchronous exchange for [Test] methods: keeps the server's
        // dispatcher pumped while a request is outstanding — a blocked main
        // thread that never ticks cannot dispatch a main-thread command
        // (L-01). TickOnce pumps the server's own work queue, never
        // EditorApplication.update itself.
        private static Exchange HttpSync(string method, string path, string? bodyJson,
            bool authorize = true, int timeoutMs = HttpTimeoutMs)
        {
            var task = HttpAsync(method, path, bodyJson, authorize, timeoutMs + 5000);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs + 8000);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                TickOnce();
                Thread.Sleep(2);
            }
            return task.IsCompleted ? task.Result : new Exchange { Error = "wait deadline" };
        }

        private static void TickOnce()
        {
            try
            {
                var startup = Type.GetType(
                    "Unity.Pipeline.Editor.PipelineServerStartup, Unity.Pipeline.Editor");
                var server = startup?.GetProperty("Server",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var dispatcher = server?.GetType().GetProperty("Dispatcher")?.GetValue(server);
                dispatcher?.GetType().GetMethod("ProcessWorkQueue", Type.EmptyTypes)
                    ?.Invoke(dispatcher, null);
            }
            catch (Exception)
            {
                // Best-effort pump — an in-flight request ends by its own
                // timeout instead of dying inside the wait loop.
            }
        }

        private static bool WaitForServerReady(double seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var sawSettling = false;
            while (DateTime.UtcNow < deadline)
            {
                var status = HttpSync("GET", "/api/status", null, true, 8000);
                if (status.StatusCode == 200)
                {
                    if (status.Body.Contains("\"status\": \"ready\"") ||
                        status.Body.Contains("\"status\":\"ready\""))
                    {
                        return true;
                    }
                    if (status.Body.Contains("\"status\": \"settling\"") ||
                        status.Body.Contains("\"status\":\"settling\""))
                    {
                        sawSettling = true;
                    }
                }
                TickOnce();
                Thread.Sleep(250);
            }
            return !sawSettling;
        }

        private static string FirstLine(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            var end = text!.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? text : text.Substring(0, end);
        }
    }
}
