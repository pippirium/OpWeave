#nullable enable

// T-W2.1-04 [Edit] tests: local:T-W2.1-04:runner-host — a Run of test
// operations executes tick by tick; entering Play Mode suspends a running
// Run with a journaled run_suspended; the SG-18 settings error refuses a new
// Run with OW-CFG-001/OW-CFG-002 before side effects; a plugged-in gate
// (the stale-Registry refusal of T-W2.2-07) refuses with its own OW-RUN-009;
// the startup hook list orders Registry rebuild before recovery.
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
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RunnerHostTests
    {
        private const string WorkflowId = "example.runnerhost";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveRunnerHostTests",
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

        // Drives the run forward one editor-update slice at a time, as the
        // production EditorApplication.update subscription does (the event
        // itself is never invoked from tests — the UTF callback would
        // re-enter).
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

        private static int Occurrences(string[] lines, string fragment)
        {
            var count = 0;
            foreach (var line in lines)
            {
                if (line.Contains(fragment))
                {
                    count++;
                }
            }
            return count;
        }

        // ── Acceptance: a Run of test operations executes tick by tick ─────

        [Test]
        public void RunOfTestOperationsExecutesTickByTickAndSucceeds()
        {
            var fixture = NewFixture();
            var a = new RunnerHostFixture.MarkerOperation("test.host.marker_a", fixture.Events);
            var b = new RunnerHostFixture.MarkerOperation("test.host.marker_b", fixture.Events);
            var c = new RunnerHostFixture.MarkerOperation("test.host.marker_c", fixture.Events);
            fixture.Register(a, 'a');
            fixture.Register(b, 'c');
            fixture.Register(c, 'e');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "a", "test.host.marker_a", 'a', next: 1,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(1, "b", "test.host.marker_b", 'c', next: 2,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(2, "c", "test.host.marker_c", 'e', next: -1,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "a", Operation = "test.host.marker_a", Version = 1 },
                    new NodeData { Id = "b", Operation = "test.host.marker_b", Version = 1 },
                    new NodeData { Id = "c", Operation = "test.host.marker_c", Version = 1 },
                },
                new[] { new ControlEdge("a", "b"), new ControlEdge("b", "c") });

            var accepted = fixture.Host.AcceptRun(plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            Assert.AreEqual(RunState.Queued, accepted.Value!.State);
            var runId = accepted.Value.RunId;

            Drive(fixture.Host, runId);

            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Assert.That(fixture.Events, Is.EqualTo(new[]
            {
                "execute:test.host.marker_a",
                "execute:test.host.marker_b",
                "execute:test.host.marker_c",
            }));
            var lines = RunnerHostFixture.JournalLines(_root, runId);
            Assert.AreEqual(3, Occurrences(lines, "\"type\":\"step_succeeded\""));
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId,
                    "\"type\":\"run_finished\""), Is.True);
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId,
                    "\"state\":\"succeeded\""), Is.True);
        }

        // ── Play Mode entry suspends and journals the running Run ──────────

        [Test]
        public void PlayModeEntrySuspendsRunningRunAndJournalsIt()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.LabelWaitableOperation("test.host.waitable");
            fixture.Register(waitable, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "w", "test.host.waitable", 'a',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "w", Operation = "test.host.waitable", Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));

            // SPEC §1.4: the playModeStateChanged hook suspends the active
            // Run at the event itself — the run_suspended record reaches the
            // journal before a reload can tear the domain down.
            fixture.Host.OnPlayModeStateChanged(PlayModeStateChange.ExitingEditMode);
            Assert.AreEqual(RunState.Suspended, fixture.Host.Coordinator.StateOf(runId));
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"run_suspended\""),
                Is.True, "run_suspended was not journaled");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "\"reason\":\"play_mode\""),
                Is.True);

            // Other transitions are ignored — the suspension happens once.
            fixture.Host.OnPlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);
            fixture.Host.OnPlayModeStateChanged(PlayModeStateChange.ExitingEditMode);
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"run_suspended\""),
                "a suspended Run must not be suspended twice");
        }

        // SPEC §F1: a run_suspended record that cannot reach the store ends
        // the Run Failed AND surfaces at the hook — the host logs the store
        // diagnostic, it is never silently dropped.
        [Test]
        public void PlayModeEntryLogsTheStoreFailureWhenSuspendCannotBeJournaled()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.LabelWaitableOperation("test.host.waitable");
            fixture.Register(waitable, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "w", "test.host.waitable", 'a',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "w", Operation = "test.host.waitable", Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));

            // The injected store failure: with the journal gone the real
            // FileRunStore refuses the Flushed run_suspended with OW-STORE-001.
            File.Delete(RunnerHostFixture.JournalPath(_root, runId));

            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex("OW-STORE-001"));
            fixture.Host.OnPlayModeStateChanged(PlayModeStateChange.ExitingEditMode);

            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "the unjournaled suspend takes the SPEC §F1 store-failure path");
        }

        // ── SG-18 settings error refuses before side effects ───────────────

        [Test]
        public void SettingsErrorBlocksNewRunWithOwCfg001BeforeSideEffects()
        {
            var fixture = NewFixture();
            // A malformed settings file — the real loader's OW-CFG-001.
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse("{ not json");
            var gateHit = false;
            fixture.ExtraGates.Add(new ProbeGate(() =>
            {
                gateHit = true;
                return Result<Unit>.Success(Unit.Value);
            }));
            var accepted = AcceptSimple(fixture);

            Assert.IsFalse(accepted.Ok);
            Assert.AreEqual(1, accepted.Diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_CFG_001, accepted.Diagnostics[0].Code,
                "SG-18: the refusal reports the underlying OW-CFG-001, not a RUN code");
            Assert.IsFalse(gateHit, "a later gate must not run after the settings refusal");
            Assert.IsFalse(
                Directory.Exists(Path.Combine(_root, "Runs")),
                "the refusal happened before any Run side effect");
        }

        [Test]
        public void SettingsErrorBlocksNewRunWithOwCfg002BeforeSideEffects()
        {
            var fixture = NewFixture();
            // An invalid value of a known key — the real loader's OW-CFG-002.
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"tickBudgetMs\":\"soon\"}");
            var accepted = AcceptSimple(fixture);

            Assert.IsFalse(accepted.Ok);
            Assert.AreEqual(ErrorCodes.OW_CFG_002, accepted.Diagnostics[0].Code);
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Runs")));
        }

        // SPEC §H4.1: a readable file's warnings ride on the successful
        // admission — the settings gate's OW-CFG-003/004/005 diagnostics
        // accumulate onto the acceptance result, not just the refusal path.
        [Test]
        public void AcceptedRunCarriesTheSettingsWarningsOfItsAdmission()
        {
            var fixture = NewFixture();
            // An unknown top-level key — the loader's OW-CFG-003 warning on
            // a successful load.
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"futureTop\":true}");
            var accepted = AcceptSimple(fixture);

            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var sawWarning = false;
            foreach (var diagnostic in accepted.Diagnostics)
            {
                sawWarning |= diagnostic.Code == ErrorCodes.OW_CFG_003;
            }
            Assert.IsTrue(
                sawWarning, "the settings gate's OW-CFG-003 warning was dropped by the accept");
        }

        // ── The gate chain: a plugged-in gate refuses with its own code ────

        [Test]
        public void PluggedInGateRefusesWithItsOwnDiagnostic()
        {
            var fixture = NewFixture();
            // The T-W2.2-07 stale-Registry refusal plugs in here: a fake gate
            // reporting OW-RUN-009 must refuse the accept unchanged — SG-18
            // "no new miscellaneous RUN code".
            fixture.ExtraGates.Add(new ProbeGate(() =>
                Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_009,
                    "The Registry is stale; a compile is in progress."))));
            var accepted = AcceptSimple(fixture);

            Assert.IsFalse(accepted.Ok);
            Assert.AreEqual(1, accepted.Diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_RUN_009, accepted.Diagnostics[0].Code);
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Runs")));
        }

        [Test]
        public void AdmittingGatesLetTheRunThrough()
        {
            var fixture = NewFixture();
            var hits = 0;
            fixture.ExtraGates.Add(new ProbeGate(() =>
            {
                hits++;
                return Result<Unit>.Success(Unit.Value);
            }));
            var accepted = AcceptSimple(fixture);

            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            Assert.AreEqual(1, hits);
            Assert.AreEqual(RunState.Queued, accepted.Value!.State);
        }

        private static Result<RunHandle> AcceptSimple(RunnerHostFixture fixture)
        {
            var op = new RunnerHostFixture.MarkerOperation("test.host.marker_a", fixture.Events);
            fixture.Register(op, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "a", "test.host.marker_a", 'a',
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "a", Operation = "test.host.marker_a", Version = 1 } },
                Array.Empty<ControlEdge>());
            return fixture.Host.AcceptRun(plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
        }

        // A gate double reporting the refusal its delegate supplies.
        private sealed class ProbeGate : IRunAcceptanceGate
        {
            private readonly Func<Result<Unit>> _check;
            internal ProbeGate(Func<Result<Unit>> check) => _check = check;
            public Result<Unit> Admit(RunAcceptanceRequest request) => _check();
        }

        // ── Startup sequencing: Registry rebuild before recovery ───────────

        [Test]
        public void StartupHooksRunRegisteredOrderRegistryBeforeRecovery()
        {
            var fixture = NewFixture();
            var ran = new List<string>();
            // Registered in the wrong order on purpose — the order keys
            // (SPEC §1.4 / §F4 item 1) must still run the Registry first.
            fixture.Host.StartupHooks.Register(
                RunnerStartupOrder.RunRecovery, "recovery", () => ran.Add("recovery"));
            fixture.Host.StartupHooks.Register(
                RunnerStartupOrder.RegistryRebuild, "registry", () => ran.Add("registry"));

            fixture.Host.RunStartupHooks();
            Assert.That(ran, Is.EqualTo(new[] { "registry", "recovery" }));

            // The first delayCall runs the list once; later calls are no-ops.
            fixture.Host.RunStartupHooks();
            Assert.AreEqual(2, ran.Count);
        }

        [Test]
        public void StartupHookFailureStopsTheSequence()
        {
            var fixture = NewFixture();
            var ran = new List<string>();
            fixture.Host.StartupHooks.Register(
                RunnerStartupOrder.RegistryRebuild, "registry",
                () => throw new InvalidOperationException("rebuild failed"));
            fixture.Host.StartupHooks.Register(
                RunnerStartupOrder.RunRecovery, "recovery", () => ran.Add("recovery"));

            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Exception,
                new System.Text.RegularExpressions.Regex("rebuild failed"));
            var executed = fixture.Host.StartupHooks.RunAll();
            Assert.That(executed, Is.EqualTo(new[] { "registry" }),
                "a failed Registry rebuild must not let recovery run");
            Assert.IsEmpty(ran);
        }

        // ── Buffered journal records reach disk on the explicit flush ──────

        [Test]
        public void FlushJournalWritesPendingBufferedRecords()
        {
            var fixture = NewFixture();
            var accepted = AcceptSimple(fixture);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            // A Buffered (required:false) log record stays pending in memory
            // until the next Flushed append or the §F3 item 1 flush hook.
            var pending = new JournalRecord(
                2,
                UtcTimestamp.Format(fixture.Clock.UtcNow),
                fixture.SessionId,
                JournalRecordType.Log,
                0,
                null,
                JsonValue.From(new JObject
                {
                    ["level"] = "info",
                    ["message"] = "buffered-probe",
                }));
            var appended = fixture.Host.Store.Append(runId, pending, Durability.Buffered);
            Assert.IsTrue(appended.Ok, "append failed");
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(_root, runId, "buffered-probe"),
                "the buffered record reached disk too early");

            var flushed = fixture.Host.FlushJournal();
            Assert.IsTrue(flushed.Ok, "flush failed: " + Describe(flushed.Diagnostics));
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "buffered-probe"),
                Is.True, "the buffered record was not flushed to disk");
        }

        // ── SPEC §E6 item 4: terminal cleanup releases through the host ──

        [Test]
        public void OpenRunResourceIsReleasedThroughTheHostDispatcherAtRunEnd()
        {
            var fixture = NewFixture();
            fixture.ReleaseDispatcher.Register(
                "editing_contents",
                (_, resource) =>
                {
                    fixture.Released.Add(resource);
                    return Result<Unit>.Success(Unit.Value);
                });
            var resourceful = new RunnerHostFixture.ResourceAcquiringOperation(
                "test.host.resourceful");
            fixture.Register(resourceful, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "r", "test.host.resourceful", 'a',
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "r", Operation = "test.host.resourceful", Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Drive(fixture.Host, runId);

            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Assert.AreEqual(
                1, fixture.Released.Count,
                "the host's release dispatcher ran at Run end");
            Assert.AreEqual("editing_contents", fixture.Released[0].Kind);
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"resource_acquired\""),
                Is.True, "the acquisition was not journaled");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"resource_released\""),
                Is.True, "the terminal cleanup did not journal resource_released");
        }

        // An unregistered resource kind fails safe: the release reports a
        // diagnostic and the acquisition stays open in the journal for the
        // §F4 recovery engine — never a silent skip.
        [Test]
        public void UnregisteredRunResourceKindFailsSafeWithDiagnostic()
        {
            var fixture = NewFixture();
            var resourceful = new RunnerHostFixture.ResourceAcquiringOperation(
                "test.host.resourceful");
            fixture.Register(resourceful, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "r", "test.host.resourceful", 'a',
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "r", Operation = "test.host.resourceful", Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));

            var diagnosed = false;
            foreach (var diagnostic in tick.Diagnostics)
            {
                if (diagnostic.Code == ErrorCodes.OW_API_004)
                {
                    diagnosed = true;
                }
            }
            Assert.IsTrue(
                diagnosed,
                "the unknown resource kind reported no diagnostic: " + Describe(tick.Diagnostics));
            Assert.AreEqual(0, fixture.Released.Count);
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"resource_acquired\""),
                Is.True);
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"resource_released\""),
                Is.False, "the unreleased acquisition must stay open in the journal");
        }
    }
}
