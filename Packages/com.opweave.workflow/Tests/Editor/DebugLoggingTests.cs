#nullable enable

// T-W2.4-04 [Edit] tests: local:T-W2.4-04:debug-log-setting — SPEC §F6 SG-08
// and §H4.1. Missing/false/true debugLogging settings are exercised through
// the real settings loader against a real Run Store; the real journal and
// the real Unity Console channel (Application.logMessageReceived) are
// inspected for the debug filter and for credential masking before any
// Agent API adapter exists.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class DebugLoggingTests
    {
        private const string WorkflowId = "example.debuglogging";
        private const string OpEmit = "test.debuglogging.emit";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveDebugLoggingTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RunnerHostFixture NewFixture(string? settingsJson = null)
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            if (settingsJson is not null)
            {
                fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(settingsJson);
            }
            return fixture;
        }

        private static void Drive(OpWeaveRunnerHost host, string runId, int maxTicks = 50)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    return;
                }
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not reach a terminal state within " + maxTicks + " ticks.");
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // The real Unity Console channel: logMessageReceived is what the
        // Editor's Console window shows, so capturing it is "inspecting the
        // Console records" of the ticket's verification method.
        private sealed class ConsoleTap : IDisposable
        {
            private readonly List<(string condition, LogType type)> _received =
                new List<(string condition, LogType type)>();

            internal ConsoleTap() =>
                Application.logMessageReceived += OnMessage;

            private void OnMessage(string condition, string stackTrace, LogType type) =>
                _received.Add((condition, type));

            internal int CountContaining(string fragment)
            {
                var count = 0;
                foreach (var (condition, _) in _received)
                {
                    if (condition.Contains(fragment))
                    {
                        count++;
                    }
                }
                return count;
            }

            internal string Joined()
            {
                var text = new List<string>();
                foreach (var (condition, _) in _received)
                {
                    text.Add(condition);
                }
                return string.Join("\n", text);
            }

            public void Dispose() =>
                Application.logMessageReceived -= OnMessage;
        }

        // An Immediate that drives the host's §F6 debug channel and one info
        // entry from inside its step — the "internal diagnostic" the ticket
        // gates.
        private sealed class EmitOperation : IOperation
        {
            private readonly OpWeaveRunnerHost _host;
            private readonly string _debugMessage;
            private readonly JsonValue? _debugData;

            internal EmitOperation(
                OpWeaveRunnerHost host, string debugMessage, JsonValue? debugData = null)
            {
                _host = host;
                _debugMessage = debugMessage;
                _debugData = debugData;
                Descriptor = Op.Define(OpEmit, 1)
                    .Summary("Emits a debug diagnostic and an info entry.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                _host.DebugLog(ctx.Log, _debugMessage, data: _debugData);
                ctx.Log.Write(Severity.Info, "info-always-recorded");
                ctx.SetOutput("mark", "done:" + Descriptor.Id);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException("Idempotent operations are never reconciled.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        private static string AcceptAndDrive(
            RunnerHostFixture fixture, IOperation operation, char seed = 'a')
        {
            fixture.Register(operation, seed);
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "emit", operation.Descriptor.Id, seed,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "emit", Operation = operation.Descriptor.Id, Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture.Host, runId);
            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId),
                "the fixture Run must succeed");
            return runId;
        }

        // ── local:T-W2.4-04:debug-log-setting — SPEC §F6 SG-08 ─────────────

        // Missing and false suppress debug persistence AND the Console mirror;
        // info+ recording is unchanged (SPEC §F6). The two cases share one
        // body: a settings file without the key and one with false mean the
        // same SPEC §H4.1 default.
        [Test]
        public void MissingOrFalseDebugLoggingSuppressesDebugEverywhere(
            [Values("{\"version\":2}", "{\"version\":2,\"debugLogging\":false}")]
            string settingsJson)
        {
            var fixture = NewFixture(settingsJson);
            using var tap = new ConsoleTap();
            var runId = AcceptAndDrive(
                fixture,
                new EmitOperation(fixture.Host, "debug-marker-must-not-appear"));
            var flushed = fixture.Host.FlushJournal();
            Assert.IsTrue(flushed.Ok, "journal flush failed");

            // No debug-level record reached the journal; the info entry is
            // journaled unchanged (SPEC §F6 — the setting changes only the
            // recorded level).
            Assert.AreEqual(
                0,
                RunnerHostFixture.JournalCount(_root, runId, "\"level\":\"debug\""),
                "a suppressed debug write must spend no journal line");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"log\"", "info-always-recorded"),
                "info+ recording is unchanged");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "debug-marker-must-not-appear"),
                "the debug text must never reach the Run Store");

            // The same gated decision emits nothing to the Unity Console.
            Assert.AreEqual(
                0, tap.CountContaining("debug-marker-must-not-appear"),
                "a suppressed debug write must reach no Console record");

            // The sanitized output contract is equally empty of debug —
            // under either includeDebug flag, since nothing was journaled.
            var read = RunLogReader.Read(_root, runId, includeDebug: true);
            Assert.IsTrue(read.Ok, "log read failed: " + Describe(read.Diagnostics));
            foreach (var entry in read.Value!)
            {
                Assert.AreNotEqual("debug", entry.Level);
                Assert.IsFalse(entry.Message.Contains("debug-marker-must-not-appear"));
            }
        }

        // debugLogging:true adds debug to the Run Store AND the Unity
        // Console without changing info+ (SPEC §F6); the sanitized contract
        // serves the entry only under the live policy.
        [Test]
        public void TrueDebugLoggingJournalsAndMirrorsToConsole()
        {
            var fixture = NewFixture("{\"version\":2,\"debugLogging\":true}");
            using var tap = new ConsoleTap();
            var runId = AcceptAndDrive(
                fixture,
                new EmitOperation(
                    fixture.Host, "debug-detail-now-recorded",
                    debugData: RunnerHostFixture.Json("{\"detail\":\"payload\"}")));
            var flushed = fixture.Host.FlushJournal();
            Assert.IsTrue(flushed.Ok, "journal flush failed");

            // Exactly one debug record is journaled — the Console mirror is
            // LogType.Log, below the Warning-以上 capture channel, so it
            // cannot re-enter the step's log.
            Assert.AreEqual(
                1,
                RunnerHostFixture.JournalCount(_root, runId, "\"level\":\"debug\""),
                "the debug entry must be journaled exactly once");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"level\":\"debug\"", "debug-detail-now-recorded"),
                "the debug record must carry the diagnostic message");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"level\":\"debug\"", "\"step\":0"),
                "the debug record must be bound to the step");
            // A step-bound entry on a custom Operation carries the §F6
            // provenance members like every other entry of the step.
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"level\":\"debug\"",
                    "\"operationId\":\"" + OpEmit + "\"", "\"origin\":\"custom\""),
                "the custom step's debug entry carries operationId and origin");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"log\"", "info-always-recorded"),
                "info+ recording is unchanged");

            // The same gated decision mirrored the masked text to the Unity
            // Console.
            Assert.AreEqual(
                1, tap.CountContaining("OpWeave debug: debug-detail-now-recorded"),
                "the debug entry must reach the Console exactly once");

            // The sanitized output contract: debug participates only while
            // the live policy permits (SPEC §F6 — API応答に記録するのは
            // 設定が有効な間だけ).
            var all = RunLogReader.Read(_root, runId, includeDebug: true);
            Assert.IsTrue(all.Ok, "log read failed: " + Describe(all.Diagnostics));
            var debug = new List<SanitizedRunLogEntry>();
            var levels = new List<string>();
            foreach (var entry in all.Value!)
            {
                levels.Add(entry.Level);
                if (entry.Level == "debug")
                {
                    debug.Add(entry);
                }
            }
            Assert.AreEqual(1, debug.Count, "one debug entry must be readable");
            Assert.AreEqual("debug-detail-now-recorded", debug[0].Message);
            Assert.AreEqual(0, debug[0].Step);
            Assert.IsTrue(debug[0].Seq > 0, "the entry carries its journal seq");
            Assert.AreEqual(
                "payload", debug[0].Data!.ToToken()["detail"]!.Value<string>());
            CollectionAssert.Contains(levels, "info");

            var filtered = RunLogReader.Read(_root, runId);
            Assert.IsTrue(filtered.Ok, "filtered log read failed");
            foreach (var entry in filtered.Value!)
            {
                Assert.AreNotEqual(
                    "debug", entry.Level,
                    "debug is served only while the setting permits");
            }
        }

        // A non-boolean debugLogging is OW-CFG-002 through the settings
        // loader (T-W2.1-03) — the settings gate refuses the Run before any
        // side effect and nothing is persisted (SPEC §H4.1, SG-18).
        [Test]
        public void InvalidDebugLoggingTypeRefusesRunAcceptance()
        {
            var fixture = NewFixture("{\"version\":2,\"debugLogging\":\"verbose\"}");
            var operation = new EmitOperation(fixture.Host, "unreached");
            fixture.Register(operation, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "emit", OpEmit, 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "emit", Operation = OpEmit, Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());

            Assert.IsFalse(accepted.Ok, "an invalid debugLogging must refuse the Run");
            var found = false;
            foreach (var diagnostic in accepted.Diagnostics)
            {
                if (diagnostic.Code == ErrorCodes.OW_CFG_002)
                {
                    found = true;
                }
            }
            Assert.IsTrue(found, "the refusal must carry OW-CFG-002: " +
                Describe(accepted.Diagnostics));
            Assert.IsFalse(
                Directory.Exists(Path.Combine(_root, "Runs")),
                "a refused Run creates no Run Store directory");
        }

        // Credential-bearing Git URLs and sensitive-keyed fields are masked
        // before the Run Store AND the Unity Console on the debug channel —
        // and the info channel alike (SPEC §F6: password/token/secret/
        // authorization and URL userinfo collapse to "***").
        [Test]
        public void SecretsAreMaskedInStoreConsoleAndContract()
        {
            var fixture = NewFixture("{\"version\":2,\"debugLogging\":true}");
            using var tap = new ConsoleTap();
            var runId = AcceptAndDrive(
                fixture,
                new EmitOperation(
                    fixture.Host,
                    "clone https://user:s3cretP4ss@example.com/repo.git finished",
                    debugData: RunnerHostFixture.Json(
                        "{\"token\":\"abc123XYZ\",\"repo\":" +
                        "\"https://u:p@host/g.git\",\"nested\":" +
                        "{\"password\":\"pw-value\",\"authorization\":\"Bearer zzz\"}}")));
            var flushed = fixture.Host.FlushJournal();
            Assert.IsTrue(flushed.Ok, "journal flush failed");

            // Run Store: no raw credential survives in the debug record.
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "s3cretP4ss"),
                "URL userinfo must not reach the journal");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "abc123XYZ"),
                "a token value must not reach the journal");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "pw-value"),
                "a password value must not reach the journal");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "Bearer zzz"),
                "an authorization value must not reach the journal");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"level\":\"debug\"", "https://***@example.com/repo.git"),
                "the masked userinfo form is what the journal holds");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"level\":\"debug\"", "\"token\":\"***\""),
                "sensitive-keyed values collapse to ***");

            // Unity Console: the mirrored line is equally masked.
            var consoleText = tap.Joined();
            Assert.IsFalse(consoleText.Contains("s3cretP4ss"),
                "URL userinfo must not reach the Console");
            Assert.IsTrue(
                tap.CountContaining("https://***@example.com/repo.git") == 1,
                "the Console line carries the masked form");

            // The sanitized contract: same masking, and an unmasked-looking
            // member can never leave the store through it.
            var read = RunLogReader.Read(_root, runId, includeDebug: true);
            Assert.IsTrue(read.Ok, "log read failed: " + Describe(read.Diagnostics));
            SanitizedRunLogEntry? debug = null;
            foreach (var entry in read.Value!)
            {
                if (entry.Level == "debug")
                {
                    debug = entry;
                }
            }
            Assert.IsNotNull(debug, "the debug entry must be readable");
            Assert.IsTrue(debug!.Message.Contains("https://***@example.com"));
            var data = debug.Data!.ToToken();
            Assert.AreEqual("***", data["token"]!.Value<string>());
            Assert.AreEqual(
                "***", data["nested"]!["password"]!.Value<string>(),
                "nested sensitive keys are masked too");
        }

        // The contract re-masks on the way out: a log record a writer
        // journaled WITHOUT the StepLogSink masking (appended directly at
        // the coordinator seam) still leaves the store sanitized (SPEC §F6 —
        // the API consumes already sanitized data).
        [Test]
        public void SanitizedContractMasksUnsanitizedJournalContent()
        {
            var fixture = NewFixture("{\"version\":2,\"debugLogging\":true}");
            var runId = AcceptAndDrive(
                fixture, new EmitOperation(fixture.Host, "a normal debug note"));

            // A record appended past the sink — as an internal writer
            // without masking would produce — holds the raw credential.
            var appended = fixture.Host.Coordinator.AppendRecord(
                runId, JournalRecordType.Log, 0, null,
                JsonValue.From(new JObject
                {
                    ["level"] = "info",
                    ["message"] = "unmasked https://u:p@host/repo.git line",
                    ["data"] = new JObject
                    {
                        ["secret"] = "raw-secret-value",
                        ["note"] = "kept",
                    },
                }));
            Assert.IsTrue(appended.Ok, "the direct append must succeed");
            var flushed = fixture.Host.FlushJournal();
            Assert.IsTrue(flushed.Ok, "journal flush failed");
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(_root, runId, "raw-secret-value"),
                "the fixture must stage genuinely unsanitized journal content");

            var read = RunLogReader.Read(_root, runId, includeDebug: true);
            Assert.IsTrue(read.Ok, "log read failed: " + Describe(read.Diagnostics));
            SanitizedRunLogEntry? unsanitized = null;
            foreach (var entry in read.Value!)
            {
                if (entry.Message.Contains("unmasked"))
                {
                    unsanitized = entry;
                }
            }
            Assert.IsNotNull(unsanitized, "the direct record must be readable");
            Assert.IsTrue(
                unsanitized!.Message.Contains("https://***@host/repo.git"),
                "the contract re-masks URL userinfo on the way out");
            var data = unsanitized.Data!.ToToken();
            Assert.AreEqual("***", data["secret"]!.Value<string>());
            Assert.AreEqual("kept", data["note"]!.Value<string>(),
                "non-credential members pass through unchanged");
        }

        // The contract's failure surface: an unknown runId is OW-RUN-010 and
        // a malformed runId is rejected like the Run Store's own grammar —
        // never a guessed path (fail closed).
        [Test]
        public void SanitizedContractFailsClosedOnUnknownAndMalformedRuns()
        {
            var fixture = NewFixture();
            _ = fixture.Host; // the fixture root exists only once the host is built

            var unknown = RunLogReader.Read(
                _root, "run_01J0000000000000000000000A");
            Assert.IsFalse(unknown.Ok);
            Assert.AreEqual(
                ErrorCodes.OW_RUN_010, unknown.Diagnostics[0].Code);

            Assert.Throws<ArgumentException>(
                () => RunLogReader.Read(_root, "../escape"));
            Assert.Throws<ArgumentException>(
                () => RunLogReader.Read(_root, "run_tooshort"));
        }
    }
}
