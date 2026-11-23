#nullable enable

// T-W2.4-03 [Edit] tests: local:T-W2.4-03:log-capture — SPEC §F6. A test
// operation emits Console warnings/errors and ctx.Log entries inside its
// step; the entries must be attributed to that step, capped at 50, and a
// custom Operation's entries must carry the operationId and origin fields.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class ConsoleLogCaptureTests
    {
        private const string WorkflowId = "example.logcapture";
        private const string OpEmitter = "test.logcapture.emit";
        private const string OpSecond = "test.logcapture.second";
        private const string OpFlood = "test.logcapture.flood";
        private const string OpStandard = "test.logcapture.standard";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveLogCaptureTests",
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

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        // Drives the run forward one editor-update slice at a time (the
        // RunnerHostTests pattern — the EditorApplication.update event itself
        // is never invoked from tests).
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

        private static List<RunLogEntry> Watch(OpWeaveRunnerHost host)
        {
            var entries = new List<RunLogEntry>();
            host.Events.LogAppended += entries.Add;
            return entries;
        }

        private static List<RunLogEntry> FromStep(IReadOnlyList<RunLogEntry> entries, int step)
        {
            var own = new List<RunLogEntry>();
            foreach (var entry in entries)
            {
                if (entry.Step == step)
                {
                    own.Add(entry);
                }
            }
            return own;
        }

        private static List<RunLogEntry> ConsoleEntries(IReadOnlyList<RunLogEntry> entries)
        {
            var own = new List<RunLogEntry>();
            foreach (var entry in entries)
            {
                if (entry.Data?.ToToken() is JObject data &&
                    string.Equals(
                        data["source"]?.Value<string>(), "unity_console",
                        StringComparison.Ordinal))
                {
                    own.Add(entry);
                }
            }
            return own;
        }

        private static RunLogEntry? FindByMessage(
            IReadOnlyList<RunLogEntry> entries, string message)
        {
            foreach (var entry in entries)
            {
                if (entry.Message.Contains(message))
                {
                    return entry;
                }
            }
            return null;
        }

        private static bool AnyMessage(IReadOnlyList<RunLogEntry> entries, string message) =>
            FindByMessage(entries, message) is not null;

        private static JObject? DataOf(RunLogEntry entry) =>
            entry.Data?.ToToken() as JObject;

        private static EmitOperation Emitter(string id, Action<StepContext> emit) =>
            new EmitOperation(id, emit);

        // An Immediate that runs the injected emit step — the verification's
        // "test operation emitting Console warnings/errors and ctx.Log
        // entries".
        private sealed class EmitOperation : IOperation
        {
            private readonly Action<StepContext> _emit;

            internal EmitOperation(string id, Action<StepContext> emit)
            {
                _emit = emit ?? throw new ArgumentNullException(nameof(emit));
                Descriptor = Op.Define(id, 1)
                    .Summary("Emits ctx.Log and Console entries.")
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
                _emit(ctx);
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
            RunnerHostFixture fixture, IReadOnlyList<(string nodeId, IOperation op, char seed)> steps)
        {
            var planSteps = new List<PlanStep>();
            var nodes = new List<NodeData>();
            var edges = new List<ControlEdge>();
            for (var i = 0; i < steps.Count; i++)
            {
                var (nodeId, op, seed) = steps[i];
                fixture.Register(op, seed);
                planSteps.Add(RunnerHostFixture.Step(
                    i, nodeId, op.Descriptor.Id, seed,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: i + 1 < steps.Count ? i + 1 : -1,
                    idempotency: Idempotency.Idempotent));
                nodes.Add(new NodeData { Id = nodeId, Operation = op.Descriptor.Id, Version = 1 });
                if (i + 1 < steps.Count)
                {
                    edges.Add(new ControlEdge(nodeId, steps[i + 1].nodeId));
                }
            }
            var plan = RunnerHostFixture.Plan(WorkflowId, planSteps);
            var doc = RunnerHostFixture.Doc(WorkflowId, nodes, edges);
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

        // ── local:T-W2.4-03:log-capture / SPEC §F6 ─────────────────────────

        // The verification's own shape: one test operation emitting Console
        // warnings/errors and ctx.Log entries; the entries must be attributed
        // to the step and carry the custom origin fields.
        [Test]
        public void ConsoleWarningsErrorsAndCtxLogEntriesAreAttributedToTheStep()
        {
            var fixture = NewFixture();
            var emitter = Emitter(OpEmitter, ctx =>
            {
                ctx.Log.Write(Severity.Info, "op-info-note");
                ctx.Log.Write(
                    Severity.Warning, "op-warning-note",
                    data: RunnerHostFixture.Json("{\"own\":\"payload\"}"));
                Debug.LogWarning("console-warning-from-step");
                Debug.LogError("console-error-from-step");
                Debug.Log("console-info-below-the-channel");
            });
            var entries = Watch(fixture.Host);
            // The UTF default fails a test on an unhandled error log; the
            // step's LogError is exactly what this test must see captured.
            LogAssert.Expect(LogType.Error, new Regex("console-error-from-step"));
            var runId = AcceptAndDrive(
                fixture, new[] { ("emit", (IOperation)emitter, 'a') });

            var step = FromStep(entries, 0);

            // The Operation's own ctx.Log entries land on its step.
            var info = FindByMessage(step, "op-info-note");
            Assert.IsNotNull(info, "the ctx.Log info entry must reach the step log");
            Assert.AreEqual(Severity.Info, info!.Level);

            // A warning logged to the Console during the step appears in that
            // step's log as a warning; an error as an error.
            var warning = FindByMessage(step, "console-warning-from-step");
            Assert.IsNotNull(warning, "the Console warning must reach the step log");
            Assert.AreEqual(Severity.Warning, warning!.Level);
            var error = FindByMessage(step, "console-error-from-step");
            Assert.IsNotNull(error, "the Console error must reach the step log");
            Assert.AreEqual(Severity.Error, error!.Level);
            var consoleData = DataOf(warning)!;
            Assert.AreEqual(
                "unity_console", consoleData!["source"]?.Value<string>(),
                "the captured entry marks its §F6 出どころ channel");
            Assert.AreEqual(
                "warning", consoleData["logType"]?.Value<string>());
            Assert.AreEqual(
                "unity_console", DataOf(error)!["source"]?.Value<string>());
            Assert.AreEqual(
                "error", DataOf(error)!["logType"]?.Value<string>());

            // SPEC §F6: the Console channel is Warning 以上 — a LogType.Log
            // message of the same step is not recorded.
            Assert.IsFalse(
                AnyMessage(entries, "console-info-below-the-channel"),
                "an info-level Console message must not be captured");

            // SPEC §F6: the custom Operation's entries carry operationId and
            // origin — its ctx.Log writes and the Console entries alike.
            var infoData = DataOf(info)!;
            Assert.AreEqual(OpEmitter, infoData!["operationId"]?.Value<string>());
            Assert.AreEqual("custom", infoData["origin"]?.Value<string>());
            var own = FindByMessage(step, "op-warning-note")!;
            var ownData = DataOf(own)!;
            Assert.AreEqual(
                "payload", ownData!["own"]?.Value<string>(),
                "the Operation's own data members survive the stamp");
            Assert.AreEqual(OpEmitter, ownData["operationId"]?.Value<string>());
            Assert.AreEqual("custom", ownData["origin"]?.Value<string>());
            Assert.AreEqual(OpEmitter, consoleData["operationId"]?.Value<string>());
            Assert.AreEqual("custom", consoleData["origin"]?.Value<string>());

            // The journaled evidence agrees: log records bound to step 0.
            var flushed = fixture.Host.FlushJournal();
            Assert.IsTrue(flushed.Ok, "journal flush failed");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"log\"", "\"step\":0", "console-warning-from-step"),
                "the journal must bind the Console entry to the step");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"log\"", "\"step\":0",
                    "\"operationId\":\"" + OpEmitter + "\"", "\"origin\":\"custom\""),
                "the journal must carry the custom provenance members");
        }

        // SPEC §F6 errors and boundaries: the Console channel is capped at 50
        // entries per step — the 51st is not recorded.
        [Test]
        public void FiftyFirstConsoleEntryOfAStepIsNotRecorded()
        {
            var fixture = NewFixture();
            var flood = Emitter(OpFlood, _ =>
            {
                for (var i = 0; i < 55; i++)
                {
                    Debug.LogWarning("flood-" + i);
                }
            });
            var entries = Watch(fixture.Host);
            AcceptAndDrive(fixture, new[] { ("flood", (IOperation)flood, 'a') });

            var captured = new List<string>();
            foreach (var entry in ConsoleEntries(FromStep(entries, 0)))
            {
                captured.Add(entry.Message);
            }
            Assert.AreEqual(
                ConsoleLogCapture.MaxEntriesPerStep, captured.Count,
                "exactly the SPEC §F6 per-step bound is recorded");
            for (var i = 0; i < ConsoleLogCapture.MaxEntriesPerStep; i++)
            {
                Assert.Contains("flood-" + i, captured);
            }
            for (var i = ConsoleLogCapture.MaxEntriesPerStep; i < 55; i++)
            {
                Assert.IsFalse(
                    captured.Contains("flood-" + i),
                    "entry " + i + " must be dropped past the 50-entry bound");
            }
        }

        // SPEC §F6 / Codex F3: the error lane is a property of a §G4 import
        // wait owning the step — a step no wait owns keeps the old first-50
        // rule at every severity, so error-level entries logged after the
        // bound is spent drop exactly like warnings.
        [Test]
        public void NonWaitStepDropsErrorsPastTheBound()
        {
            ImportErrorCapture.Clear();
            try
            {
                var fixture = NewFixture();
                var flood = Emitter(OpFlood, _ =>
                {
                    for (var i = 0; i < ConsoleLogCapture.MaxEntriesPerStep; i++)
                    {
                        Debug.LogWarning("bound-warning-" + i);
                    }
                    for (var i = 0; i < ConsoleLogCapture.ErrorBudgetReserve; i++)
                    {
                        Debug.LogError("bound-error-" + i);
                    }
                });
                var entries = Watch(fixture.Host);
                var ignore = LogAssert.ignoreFailingMessages;
                LogAssert.ignoreFailingMessages = true;
                try
                {
                    AcceptAndDrive(fixture, new[] { ("flood", (IOperation)flood, 'a') });
                }
                finally
                {
                    LogAssert.ignoreFailingMessages = ignore;
                }

                var captured = ConsoleEntries(FromStep(entries, 0));
                Assert.AreEqual(
                    ConsoleLogCapture.MaxEntriesPerStep, captured.Count,
                    "a non-wait step records exactly the first-50 entries");
                for (var i = 0; i < ConsoleLogCapture.ErrorBudgetReserve; i++)
                {
                    Assert.IsFalse(
                        AnyMessage(captured, "bound-error-" + i),
                        "an error past the bound must drop like a warning");
                }
            }
            finally
            {
                ImportErrorCapture.Clear();
            }
        }

        // SPEC §F6 attribution: a Console message is captured only while a Run
        // is executing — nothing outside a Run is ever blamed on one.
        [Test]
        public void ConsoleMessagesOutsideAnExecutingRunAreNotRecorded()
        {
            var fixture = NewFixture();
            var entries = Watch(fixture.Host);
            Debug.LogWarning("before-any-run-marker");
            var emitter = Emitter(OpEmitter, _ => { });
            var runId = AcceptAndDrive(
                fixture, new[] { ("emit", (IOperation)emitter, 'a') });
            Debug.LogWarning("after-run-end-marker");

            Assert.IsFalse(
                AnyMessage(entries, "before-any-run-marker"),
                "a message before the Run must not be attributed to it");
            Assert.IsFalse(
                AnyMessage(entries, "after-run-end-marker"),
                "a message after the Run must not be attributed to it");
            var flushed = fixture.Host.FlushJournal();
            Assert.IsTrue(flushed.Ok, "journal flush failed");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "before-any-run-marker"));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "after-run-end-marker"));
        }

        // SPEC §F6: the operationId/origin members belong to a custom
        // Operation's entries; a standard Operation's step already names the
        // Plan step in the journal envelope, so its entries carry neither.
        [Test]
        public void StandardOperationEntriesDoNotCarryCustomProvenanceFields()
        {
            var fixture = NewFixture();
            var standard = Emitter(OpStandard, ctx =>
            {
                ctx.Log.Write(Severity.Warning, "standard-op-note");
                Debug.LogWarning("standard-console-warning");
            });
            // The fixture's Register always registers Custom; a standard
            // registration is staged directly on the snapshot.
            fixture.Operations.Add(new RegisteredOperation(
                standard.Descriptor,
                OperationOrigin.Standard,
                "OpWeave.Operations",
                null,
                new string('a', 64),
                new string('b', 64))
            {
                Implementation = standard,
            });
            var entries = Watch(fixture.Host);
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(
                    0, "std", OpStandard, 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "std", Operation = OpStandard, Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            Drive(fixture.Host, accepted.Value!.RunId);
            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(accepted.Value.RunId));

            var step = FromStep(entries, 0);
            var own = FindByMessage(step, "standard-op-note");
            Assert.IsNotNull(own, "the standard ctx.Log entry must reach the step log");
            var ownData = DataOf(own!);
            Assert.IsTrue(
                ownData is null ||
                    (ownData["operationId"] is null && ownData["origin"] is null),
                "a standard Operation's ctx.Log entry carries no provenance fields");
            var console = FindByMessage(step, "standard-console-warning");
            Assert.IsNotNull(console, "the standard Console warning must be captured");
            var consoleData = DataOf(console!)!;
            Assert.AreEqual("unity_console", consoleData!["source"]?.Value<string>());
            Assert.IsNull(consoleData["operationId"]);
            Assert.IsNull(consoleData["origin"]);
        }

        // SPEC §F6 attribution: each Console entry lands on the step that
        // produced it — the second step's warning is not blamed on the first.
        [Test]
        public void ConsoleEntriesLandOnTheStepThatProducedThem()
        {
            var fixture = NewFixture();
            var quiet = Emitter(OpEmitter, _ => { });
            var loud = Emitter(OpSecond, _ => Debug.LogWarning("second-step-warning"));
            var entries = Watch(fixture.Host);
            AcceptAndDrive(
                fixture,
                new (string, IOperation, char)[]
                {
                    ("quiet", quiet, 'a'),
                    ("loud", loud, 'c'),
                });

            Assert.IsNull(
                FindByMessage(FromStep(entries, 0), "second-step-warning"),
                "the second step's warning must not be attributed to the first");
            var entry = FindByMessage(FromStep(entries, 1), "second-step-warning");
            Assert.IsNotNull(entry, "the second step's warning must land on step 1");
            Assert.AreEqual(OpSecond, DataOf(entry!)!["operationId"]?.Value<string>());
        }

        // SPEC §F6 / Codex F2: the per-step cap is persisted across a domain
        // reload. A step that spent its budget before the reload and resumes
        // in the new domain — a fresh tracker reopening the same (Run, step)
        // attempt stands for the reload — must still drop its 51st entry.
        [Test]
        public void ConsoleEntryCapSurvivesADomainReload()
        {
            var runId = "run-cap-reload-" + Guid.NewGuid().ToString("N");
            var before = new RecordingLogSink();
            var capture = new ConsoleLogCapture();
            try
            {
                capture.OnRunStateChanged(new RunStateChanged(runId, RunState.Running));
                capture.OpenAttempt(runId, before, 0, OpEmitter, OperationOrigin.Standard);
                for (var i = 0; i < ConsoleLogCapture.MaxEntriesPerStep; i++)
                {
                    Debug.LogWarning("cap-before-reload-" + i);
                }
                Assert.AreEqual(
                    ConsoleLogCapture.MaxEntriesPerStep, before.Messages.Count,
                    "the first domain records up to the §F6 bound");
            }
            finally
            {
                // The domain teardown: the tracker dies with the domain; the
                // SessionState counters it wrote stay.
                capture.Dispose();
            }

            var reloaded = new ConsoleLogCapture();
            try
            {
                var after = new RecordingLogSink();
                reloaded.OnRunStateChanged(new RunStateChanged(runId, RunState.Running));
                reloaded.OpenAttempt(runId, after, 0, OpEmitter, OperationOrigin.Standard);

                Debug.LogWarning("cap-after-reload-51st");

                Assert.AreEqual(
                    0, after.Messages.Count,
                    "the 51st Console entry of the step stays dropped across a domain reload");
                // The Run's terminal state erases the persisted budget.
                reloaded.OnRunStateChanged(new RunStateChanged(runId, RunState.Succeeded));
            }
            finally
            {
                reloaded.Dispose();
            }
        }

        // Codex F1 regression: the §F6 provenance stamp is a property of the
        // shared UnityStepContext construction — the Operation's own ctx.Log
        // entries carry operationId/origin whoever builds the context, once
        // the registration's provenance is supplied; a standard Operation's
        // entries carry neither.
        [Test]
        public void CustomOperationProvenanceStampsTheSharedStepContextLog()
        {
            var inner = new RecordingLogSink();
            var context = new UnityStepContext(
                new Dictionary<string, InputValue>(StringComparer.Ordinal),
                null,
                null,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "probe"),
                inner,
                operationId: OpEmitter,
                operationOrigin: OperationOrigin.Custom);

            context.Log.Write(
                Severity.Info, "ctx-note",
                data: RunnerHostFixture.Json("{\"own\":\"payload\"}"));

            var data = (JObject)inner.Datas[0]!.ToToken();
            Assert.AreEqual(OpEmitter, data["operationId"]?.Value<string>());
            Assert.AreEqual("custom", data["origin"]?.Value<string>());
            Assert.AreEqual(
                "payload", data["own"]?.Value<string>(),
                "the Operation's own data members survive the stamp");

            var standard = new UnityStepContext(
                new Dictionary<string, InputValue>(StringComparer.Ordinal),
                null, null, RunnerHostFixture.Types, new RefResolver(),
                new StepChangeRecorder(0, "probe"), inner,
                operationId: OpEmitter, operationOrigin: OperationOrigin.Standard);
            standard.Log.Write(Severity.Info, "std-note");
            Assert.IsNull(
                inner.Datas[1], "a standard Operation's entries carry no provenance");
        }

        // The IRunLogSink a step's Console entries journal through in these
        // fixtures — records the messages it is asked to write.
        private sealed class RecordingLogSink : IRunLogSink
        {
            internal List<string> Messages { get; } = new List<string>();
            internal List<JsonValue?> Datas { get; } = new List<JsonValue?>();

            public void Write(
                Severity level, string message, string? code = null, JsonValue? data = null)
            {
                Messages.Add(message);
                Datas.Add(data);
            }
        }
    }
}
