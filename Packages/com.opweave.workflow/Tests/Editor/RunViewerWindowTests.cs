#nullable enable

// T-W3.5-01 [Edit] tests: local:T-W3.5-01:runs-tab — the Run Viewer's Runs
// tab lists the Run Store's Runs and the selected Run's steps (state,
// duration, attempts), logs and changes; entries update on SPEC §E8 events
// without reopening; clicking a change pings/selects its target; Cancel
// ends a running Run. Fixtures bind the window to a real OpWeaveRunnerHost
// over a probe Run Store, exactly like the runner-host tests. Two suites
// share the fixture: the detached-tree cases populate a CreateInstance'd
// window's rootVisualElement through the same BuildView path OnEnable uses
// and invoke the same methods the rows' click callbacks and the scheduler
// call, while the real-window cases open the window through
// EditorWindow.GetWindow — the entry Window > OpWeave > Runs uses — and
// dispatch pooled events into its real panel so the controls' production
// activation paths run.
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using OpWeave.Viewer;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RunViewerWindowTests
    {
        private const string WorkflowId = "example.runviewer";
        private const string ProbeAssetPath = "Assets/OpWeaveRunViewerProbe.asset";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();
        private readonly List<EditorWindow> _windows = new List<EditorWindow>();
        private readonly List<RunViewerWindow> _realWindows =
            new List<RunViewerWindow>();
        private readonly List<GameObject> _gameObjects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveRunViewerTests",
                "t" + Guid.NewGuid().ToString("N"));
            DeleteProbeAsset();
            Selection.activeObject = null;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var window in _realWindows)
            {
                // A window is closed only when one was really opened —
                // without a graphics device no view exists and Close
                // would only log the unhandled no-graphic-device error.
                if (window != null && HasGraphicsDevice)
                {
                    window.Close();
                }
            }
            foreach (var window in _windows)
            {
                if (window != null)
                {
                    UnityEngine.Object.DestroyImmediate(window);
                }
            }
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            foreach (var go in _gameObjects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            DeleteProbeAsset();
            Selection.activeObject = null;
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void DeleteProbeAsset()
        {
            if (AssetDatabase.LoadMainAssetAtPath(ProbeAssetPath) is not null)
            {
                AssetDatabase.DeleteAsset(ProbeAssetPath);
            }
        }

        private RunnerHostFixture NewFixture()
        {
            // The Issue #323 class of timing defect: the real
            // StopwatchMonotonicTimer can spend the Tick's 50 ms budget
            // inside an Editor-side step (asset import, undo scope) and
            // leave a Plan step unstarted in the same Tick. The zero
            // timer keeps every Tick's budget deterministic — no wall
            // clock enters the step-scheduling assertions below.
            var fixture = new RunnerHostFixture(_root)
            {
                MonotonicTimerFactory = () => new FakeMonotonicTimer(),
            };
            _fixtures.Add(fixture);
            return fixture;
        }

        // The window bound to the fixture's runner host — the production
        // host binding of OnEnable is replaced through the test seam. The
        // detached-tree cases use a CreateInstance'd window: its
        // rootVisualElement is populated through the same BuildView path
        // OnEnable uses, and queries need no panel. The real-panel cases
        // use OpenReal below.
        private RunViewerWindow OpenBound(RunnerHostFixture fixture)
        {
            var window = ScriptableObject.CreateInstance<RunViewerWindow>();
            _windows.Add(window);
            window.BuildView();
            window.BindSource(
                new RunViewerSource(fixture.Host, fixture.Root, fixture.Clock));
            return window;
        }

        // A headless (-nographics) Editor has no graphics device to
        // initialize a window's view: GetWindow and Close log the
        // unhandled "[Error] No graphic device is available to initialize
        // the view." The real-panel acceptance cases therefore require a
        // graphics device; the detached-tree cases do not.
        private static bool HasGraphicsDevice =>
            SystemInfo.graphicsDeviceType !=
            UnityEngine.Rendering.GraphicsDeviceType.Null;

        private static void RequireGraphicsDevice()
        {
            if (!HasGraphicsDevice)
            {
                Assert.Ignore(
                    "requires a graphics device; run without -nographics");
            }
        }

        // The real-window opener of the acceptance cases:
        // EditorWindow.GetWindow is the entry Window > OpWeave > Runs
        // calls, so rootVisualElement lives on the host view's real panel
        // and pooled events dispatch through the production propagation
        // path. The fixture's runner host binds through the same
        // BindSource seam the detached cases use; the window is closed in
        // TearDown.
        private RunViewerWindow OpenReal(RunnerHostFixture fixture)
        {
            var window = EditorWindow.GetWindow<RunViewerWindow>();
            _realWindows.Add(window);
            window.BindSource(
                new RunViewerSource(fixture.Host, fixture.Root, fixture.Clock));
            Assert.IsNotNull(
                window.rootVisualElement.panel,
                "the opened window must sit on a real panel for event dispatch");
            return window;
        }

        // Puts a pooled event on the element and dispatches it through the
        // real panel — the controls' production activation paths (a row's
        // ClickEvent callback, a Button's Clickable submit handling) run,
        // not a fixture-side method call.
        private static void SendTo(VisualElement element, EventBase evt)
        {
            Assert.IsNotNull(
                element.panel,
                "the element must sit on a real panel for event dispatch");
            evt.target = element;
            element.SendEvent(evt);
            evt.Dispose();
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

        // Drives the run forward one editor-update slice at a time, as the
        // production EditorApplication.update subscription does — and lets
        // one second of fixture time pass per slice, so a Waiting step's
        // journaled nextPollAt is crossed deterministically. This is the
        // same Clock.Advance + Tick pump the other runner-host tests apply
        // by hand around a waitable's polls.
        private static void Drive(RunnerHostFixture fixture, string runId, int maxTicks = 50)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var state = fixture.Host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    return;
                }
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not leave the running states within " +
                maxTicks + " ticks.");
        }

        private static List<Label> RunRowSubtitles(RunViewerWindow window)
        {
            var subtitles = new List<Label>();
            foreach (var row in window.rootVisualElement
                         .Q<VisualElement>(RunViewerWindow.RunRowsName).Children())
            {
                var label = row.Q<Label>(RunViewerWindow.RunRowSubtitleName);
                Assert.IsNotNull(label, "a run row must carry its subtitle label");
                subtitles.Add(label!);
            }
            return subtitles;
        }

        private static List<VisualElement> RowsOf(VisualElement container) =>
            new List<VisualElement>(container.Children());

        private static VisualElement DetailContainer(
            RunViewerWindow window, string name)
        {
            var container = window.rootVisualElement.Q<VisualElement>(name);
            Assert.IsNotNull(container, "missing detail container " + name);
            return container!;
        }

        // The rows carrying a named label — the "(no …)" placeholder labels
        // a section shows while empty have no such row label.
        private static int LabelledRows(RunViewerWindow window, string containerName)
        {
            var count = 0;
            foreach (var row in DetailContainer(window, containerName).Children())
            {
                if (row.Q<Label>() is not null && row.Q<Label>().name is { } n && n.Length > 0)
                {
                    count++;
                }
            }
            return count;
        }

        // ── The run list ──────────────────────────────────────────────────

        [Test]
        public void RunListPopulatesAndTracksStatesWithoutReopening()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.LabelWaitableOperation("test.viewer.waitable");
            fixture.Register(waitable, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "w", "test.viewer.waitable", 'a',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "w", Operation = "test.viewer.waitable", Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            var window = OpenBound(fixture);
            Assert.AreEqual(runId, window.SelectedRunId,
                "the newest-listed Run is selected when none was picked");
            var subtitles = RunRowSubtitles(window);
            Assert.AreEqual(1, subtitles.Count, "one run row expected");
            StringAssert.Contains("queued", subtitles[0].text);
            StringAssert.Contains(WorkflowId, subtitles[0].text);

            // The first tick moves the Run to Running — the §E8
            // run_started event updates the same open window.
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
            StringAssert.Contains("running", RunRowSubtitles(window)[0].text);

            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            Drive(fixture, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            StringAssert.Contains("succeeded", RunRowSubtitles(window)[0].text);
        }

        // ── The detail pane: steps, logs ─────────────────────────────────

        [Test]
        public void DetailShowsStepsWithStateAttemptsDurationAndLogs()
        {
            var fixture = NewFixture();
            var marker = new ProbeMarkerOperation(
                "test.viewer.marker", "marker finished");
            var waitable = new RunnerHostFixture.LabelWaitableOperation("test.viewer.waitable");
            fixture.Register(marker, 'a');
            fixture.Register(waitable, 'c');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "s0", "test.viewer.marker", 'a', next: 1,
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(1, "s1", "test.viewer.waitable", 'c', next: -1,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "s0", Operation = "test.viewer.marker", Version = 1 },
                    new NodeData { Id = "s1", Operation = "test.viewer.waitable", Version = 1 },
                },
                new[] { new ControlEdge("s0", "s1") });
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            // One tick runs the marker step to success and parks the
            // waitable step in Waiting.
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));

            var window = OpenBound(fixture);
            var header = DetailContainer(window, RunViewerWindow.RunHeaderName);
            Assert.AreEqual(runId, header.Q<Label>(RunViewerWindow.RunIdLabelName).text);
            StringAssert.Contains(
                "running", header.Q<Label>(RunViewerWindow.RunStateLabelName).text);
            StringAssert.Contains(
                WorkflowId, header.Q<Label>(RunViewerWindow.RunWorkflowLabelName).text);

            var steps = RowsOf(DetailContainer(window, RunViewerWindow.StepRowsName));
            Assert.AreEqual(2, steps.Count, "both Plan steps are listed");

            var first = steps[0];
            StringAssert.Contains(
                "s0", first.Q<Label>(RunViewerWindow.StepTitleName).text);
            StringAssert.Contains(
                "test.viewer.marker", first.Q<Label>(RunViewerWindow.StepTitleName).text);
            Assert.AreEqual(
                "succeeded", first.Q<Label>(RunViewerWindow.StepStateName).text);
            Assert.AreEqual(
                "attempt 1", first.Q<Label>(RunViewerWindow.StepAttemptName).text);
            Assert.AreNotEqual(
                "—", first.Q<Label>(RunViewerWindow.StepDurationName).text,
                "a finished step shows its elapsed time");

            var second = steps[1];
            StringAssert.Contains(
                "s1", second.Q<Label>(RunViewerWindow.StepTitleName).text);
            Assert.AreEqual(
                "waiting", second.Q<Label>(RunViewerWindow.StepStateName).text);
            Assert.AreEqual(
                "attempt 1", second.Q<Label>(RunViewerWindow.StepAttemptName).text);
            Assert.AreNotEqual(
                "—", second.Q<Label>(RunViewerWindow.StepDurationName).text,
                "a running step shows its accruing time");

            var logs = RowsOf(DetailContainer(window, RunViewerWindow.LogRowsName));
            Assert.GreaterOrEqual(logs.Count, 1, "the fixture log line is shown");
            var saw = false;
            foreach (var row in logs)
            {
                var text = row.Q<Label>(RunViewerWindow.LogTextName).text;
                saw |= text.Contains("marker finished") && text.Contains("info");
            }
            Assert.IsTrue(saw, "the journaled fixture log line is missing");
        }

        [Test]
        public void PendingStepShowsNoDurationUntilItStarts()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.LabelWaitableOperation("test.viewer.waitable");
            var marker = new ProbeMarkerOperation("test.viewer.marker", "later");
            fixture.Register(waitable, 'a');
            fixture.Register(marker, 'c');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "w", "test.viewer.waitable", 'a', next: 1,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(1, "m", "test.viewer.marker", 'c', next: -1,
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "w", Operation = "test.viewer.waitable", Version = 1 },
                    new NodeData { Id = "m", Operation = "test.viewer.marker", Version = 1 },
                },
                new[] { new ControlEdge("w", "m") });
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));

            var window = OpenBound(fixture);
            var steps = RowsOf(DetailContainer(window, RunViewerWindow.StepRowsName));
            Assert.AreEqual(2, steps.Count);
            Assert.AreEqual(
                "pending", steps[1].Q<Label>(RunViewerWindow.StepStateName).text);
            Assert.AreEqual(
                "", steps[1].Q<Label>(RunViewerWindow.StepAttemptName).text,
                "a step that never started shows no attempt");
            Assert.AreEqual(
                "—", steps[1].Q<Label>(RunViewerWindow.StepDurationName).text,
                "a step that never started shows no duration");
        }

        // ── Changes: click pings the asset / selects the GameObject ──────

        [Test]
        public void ClickingAnAssetChangePingsTheAsset()
        {
            var fixture = NewFixture();
            var op = new AssetChangeOperation("test.viewer.asset", ProbeAssetPath);
            fixture.Register(op, 'a');
            var runId = AcceptAndDrive(fixture, new[]
            {
                RunnerHostFixture.Step(0, "a", "test.viewer.asset", 'a',
                    idempotency: Idempotency.Idempotent),
            }, new[] { new NodeData { Id = "a", Operation = "test.viewer.asset", Version = 1 } });
            Assert.IsNotNull(op.Asset, "the fixture operation must have created the asset");

            var window = OpenBound(fixture);
            var rows = RowsOf(DetailContainer(window, RunViewerWindow.ChangeRowsName));
            Assert.AreEqual(1, rows.Count, "one change row expected");
            StringAssert.Contains(
                "created asset " + ProbeAssetPath,
                rows[0].Q<Label>(RunViewerWindow.ChangeTextName).text);

            // No panel exists to dispatch a ClickEvent through — the
            // fixture invokes the same activation the row's click callback
            // does, against the record the row carries.
            window.ActivateChange((ChangeRecord)rows[0].userData!);
            Assert.IsTrue(
                Selection.activeObject == op.Asset,
                "the change row must ping the asset it recorded");
        }

        [Test]
        public void ClickingAGameObjectChangeSelectsTheGameObject()
        {
            var fixture = NewFixture();
            var op = new GameObjectChangeOperation(
                "test.viewer.gameobject", _gameObjects);
            fixture.Register(op, 'a');
            var runId = AcceptAndDrive(fixture, new[]
            {
                RunnerHostFixture.Step(0, "g", "test.viewer.gameobject", 'a',
                    idempotency: Idempotency.Idempotent),
            }, new[] { new NodeData { Id = "g", Operation = "test.viewer.gameobject", Version = 1 } });
            Assert.IsNotNull(op.Created, "the fixture operation must have created the GameObject");

            var window = OpenBound(fixture);
            var rows = RowsOf(DetailContainer(window, RunViewerWindow.ChangeRowsName));
            Assert.AreEqual(1, rows.Count, "one change row expected");
            StringAssert.Contains(
                "created gameObject",
                rows[0].Q<Label>(RunViewerWindow.ChangeTextName).text);

            // No panel exists to dispatch a ClickEvent through — the
            // fixture invokes the same activation the row's click callback
            // does, against the record the row carries.
            window.ActivateChange((ChangeRecord)rows[0].userData!);
            Assert.IsTrue(
                Selection.activeObject == op.Created,
                "the change row must select the GameObject it recorded");
        }

        private string AcceptAndDrive(
            RunnerHostFixture fixture, PlanStep[] steps, NodeData[] nodes)
        {
            var edges = new List<ControlEdge>();
            for (var i = 0; i + 1 < nodes.Length; i++)
            {
                edges.Add(new ControlEdge(nodes[i].Id!, nodes[i + 1].Id!));
            }
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(WorkflowId, nodes, edges);
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture, runId);
            return runId;
        }

        // ── Cancel ────────────────────────────────────────────────────────

        [Test]
        public void CancelButtonEndsARunningRun()
        {
            var fixture = NewFixture();
            var hold = new HoldOperation("test.viewer.hold");
            fixture.Register(hold, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "h", "test.viewer.hold", 'a',
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "h", Operation = "test.viewer.hold", Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));

            var window = OpenBound(fixture);
            var cancel = window.rootVisualElement.Q<Button>(RunViewerWindow.CancelButtonName);
            Assert.IsNotNull(cancel, "the running Run offers Cancel");
            Assert.AreEqual(
                DisplayStyle.Flex, cancel!.style.display.value,
                "Cancel must be visible while the Run runs");

            // No panel exists to dispatch a click through — the fixture
            // invokes the same method the button's clicked action holds.
            window.OnCancelClicked();

            // The journaled request is processed by the loop — the Run ends
            // Cancelled and the same open window shows it.
            Drive(fixture, runId);
            Assert.AreEqual(
                RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"cancel_requested\""),
                "the cancel request was not journaled");
            StringAssert.Contains("cancelled", RunRowSubtitles(window)[0].text);
            Assert.AreEqual(
                DisplayStyle.None, cancel.style.display.value,
                "Cancel hides once the Run can no longer be cancelled");
        }

        // ── Live updates on §E8 ───────────────────────────────────────────

        [Test]
        public void RunningRunUpdatesStepsAndLogsOnEventsWithoutReopening()
        {
            var fixture = NewFixture();
            var poller = new PollingLogOperation("test.viewer.poller", pollsToSuccess: 2);
            fixture.Register(poller, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "p", "test.viewer.poller", 'a',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "p", Operation = "test.viewer.poller", Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));

            var window = OpenBound(fixture);
            var logsBefore = LabelledRows(window, RunViewerWindow.LogRowsName);

            // The next poll waits for its deadline — advance the fixture
            // clock, tick, and the §E8 step/log events refresh the open
            // window without reopening it.
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));

            var logsAfter = LabelledRows(window, RunViewerWindow.LogRowsName);
            Assert.Greater(logsAfter, logsBefore,
                "a poll's log line reached the open window through §E8");
            var saw = false;
            foreach (var row in RowsOf(DetailContainer(window, RunViewerWindow.LogRowsName)))
            {
                saw |= row.Q<Label>(RunViewerWindow.LogTextName).text.Contains("poll-1");
            }
            Assert.IsTrue(saw, "the new log entry never reached the open window");

            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            Drive(fixture, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            var steps = RowsOf(DetailContainer(window, RunViewerWindow.StepRowsName));
            Assert.AreEqual(
                "succeeded", steps[0].Q<Label>(RunViewerWindow.StepStateName).text,
                "the finished step updated inside the open window");
            StringAssert.Contains("succeeded", RunRowSubtitles(window)[0].text);
        }

        // ── The real window on a real panel ─────────────────────────────
        // The local:T-W3.5-01:runs-tab acceptance surface: the window is
        // opened through the same EditorWindow.GetWindow the menu entry
        // calls, the fixture Runs drive a real OpWeaveRunnerHost, and the
        // controls are activated by pooled events dispatched into the real
        // panel — not by invoking their callbacks by hand.

        [Test]
        public void RealWindowShowsListStepsLogsChangesWhileRunningAndAfterFinish()
        {
            RequireGraphicsDevice();
            var fixture = NewFixture();
            var marker = new ProbeMarkerOperation(
                "test.viewer.marker", "marker finished");
            var asset = new AssetChangeOperation("test.viewer.asset", ProbeAssetPath);
            var poller = new PollingLogOperation("test.viewer.poller", pollsToSuccess: 2);
            fixture.Register(marker, 'a');
            fixture.Register(asset, 'c');
            fixture.Register(poller, 'e');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "m", "test.viewer.marker", 'a', next: 1,
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(1, "a", "test.viewer.asset", 'c', next: 2,
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(2, "p", "test.viewer.poller", 'e', next: -1,
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "m", Operation = "test.viewer.marker", Version = 1 },
                    new NodeData { Id = "a", Operation = "test.viewer.asset", Version = 1 },
                    new NodeData { Id = "p", Operation = "test.viewer.poller", Version = 1 },
                },
                new[] { new ControlEdge("m", "a"), new ControlEdge("a", "p") });
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            // The window opens on a real host view while the Run is
            // queued and stays open for the whole lifecycle.
            var window = OpenReal(fixture);
            Assert.AreEqual(runId, window.SelectedRunId,
                "the newest-listed Run is selected when none was picked");
            StringAssert.Contains("queued", RunRowSubtitles(window)[0].text);

            // The first tick runs both Immediate steps and parks the
            // waitable — the §E8 run_started event updates the open
            // window's list row.
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
            StringAssert.Contains("running", RunRowSubtitles(window)[0].text);

            var steps = RowsOf(DetailContainer(window, RunViewerWindow.StepRowsName));
            Assert.AreEqual(3, steps.Count, "all three Plan steps are listed");
            Assert.AreEqual(
                "succeeded", steps[0].Q<Label>(RunViewerWindow.StepStateName).text);
            Assert.AreEqual(
                "attempt 1", steps[0].Q<Label>(RunViewerWindow.StepAttemptName).text);
            Assert.AreNotEqual(
                "—", steps[0].Q<Label>(RunViewerWindow.StepDurationName).text,
                "a finished step shows its elapsed time");
            Assert.AreEqual(
                "succeeded", steps[1].Q<Label>(RunViewerWindow.StepStateName).text);
            Assert.AreEqual(
                "waiting", steps[2].Q<Label>(RunViewerWindow.StepStateName).text);
            Assert.AreEqual(
                "attempt 1", steps[2].Q<Label>(RunViewerWindow.StepAttemptName).text);

            var saw = false;
            foreach (var row in RowsOf(DetailContainer(window, RunViewerWindow.LogRowsName)))
            {
                saw |= row.Q<Label>(RunViewerWindow.LogTextName).text
                    .Contains("marker finished");
            }
            Assert.IsTrue(saw, "the journaled log line is missing while the Run runs");

            var changes = RowsOf(DetailContainer(window, RunViewerWindow.ChangeRowsName));
            Assert.AreEqual(1, changes.Count, "the journaled change is listed while running");
            StringAssert.Contains(
                "created asset " + ProbeAssetPath,
                changes[0].Q<Label>(RunViewerWindow.ChangeTextName).text);

            // The next poll journals a §E8 log event — the same open
            // window shows the new row, still without reopening.
            var logsBefore = LabelledRows(window, RunViewerWindow.LogRowsName);
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.Greater(
                LabelledRows(window, RunViewerWindow.LogRowsName), logsBefore,
                "a poll's log line reached the open window through §E8");

            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            Drive(fixture, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            StringAssert.Contains("succeeded", RunRowSubtitles(window)[0].text);
            Assert.AreEqual(
                "succeeded",
                RowsOf(DetailContainer(window, RunViewerWindow.StepRowsName))[2]
                    .Q<Label>(RunViewerWindow.StepStateName).text,
                "the still-open window shows the finished step");
        }

        [Test]
        public void RealWindowChangeRowClickPingsTheRecordedAsset()
        {
            RequireGraphicsDevice();
            var fixture = NewFixture();
            var op = new AssetChangeOperation("test.viewer.asset", ProbeAssetPath);
            fixture.Register(op, 'a');
            var runId = AcceptAndDrive(fixture, new[]
            {
                RunnerHostFixture.Step(0, "a", "test.viewer.asset", 'a',
                    idempotency: Idempotency.Idempotent),
            }, new[] { new NodeData { Id = "a", Operation = "test.viewer.asset", Version = 1 } });
            Assert.IsNotNull(op.Asset, "the fixture operation must have created the asset");
            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));

            // The window opens after the fixture Run finished — the row
            // lists the journaled change, and a real ClickEvent dispatched
            // through the panel runs the row's activation.
            var window = OpenReal(fixture);
            var rows = RowsOf(DetailContainer(window, RunViewerWindow.ChangeRowsName));
            Assert.AreEqual(1, rows.Count, "one change row expected");
            StringAssert.Contains(
                "created asset " + ProbeAssetPath,
                rows[0].Q<Label>(RunViewerWindow.ChangeTextName).text);

            SendTo(rows[0], ClickEvent.GetPooled());
            Assert.IsTrue(
                Selection.activeObject == op.Asset,
                "a real click on the change row must ping/select the asset it recorded");
        }

        [Test]
        public void RealWindowCancelButtonEndsARunningRun()
        {
            RequireGraphicsDevice();
            var fixture = NewFixture();
            var hold = new HoldOperation("test.viewer.hold");
            fixture.Register(hold, 'a');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                RunnerHostFixture.Step(0, "h", "test.viewer.hold", 'a',
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "h", Operation = "test.viewer.hold", Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));

            var window = OpenReal(fixture);
            var cancel = window.rootVisualElement.Q<Button>(RunViewerWindow.CancelButtonName);
            Assert.IsNotNull(cancel, "the running Run offers Cancel");
            Assert.AreEqual(
                DisplayStyle.Flex, cancel!.style.display.value,
                "Cancel must be visible while the Run runs");

            // The Button's real activation path: a NavigationSubmitEvent
            // dispatched through the panel runs the Clickable's submit
            // handling into the clicked action.
            SendTo(cancel, NavigationSubmitEvent.GetPooled());
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"cancel_requested\""),
                "the real Cancel button's activation did not journal the request");

            Drive(fixture, runId);
            Assert.AreEqual(
                RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            StringAssert.Contains("cancelled", RunRowSubtitles(window)[0].text);
            Assert.AreEqual(
                DisplayStyle.None, cancel.style.display.value,
                "Cancel hides once the Run can no longer be cancelled");
        }

        // ── Graph Toolkit independence (SPEC §H5 責務) ────────────────────

        [Test]
        public void ViewerAssemblyDoesNotReferenceGraphToolkit()
        {
            // SPEC §H5: the Run Viewer works on 6000.0 where no Graph
            // Toolkit exists — its assembly must not reference it.
            foreach (var reference in typeof(RunViewerWindow).Assembly
                         .GetReferencedAssemblies())
            {
                Assert.IsFalse(
                    reference.Name!.Contains("GraphToolkit"),
                    "OpWeave.Viewer must not reference " + reference.Name);
            }
        }

        // ── Fixture operations ────────────────────────────────────────────

        // The deterministic Tick-budget timer — Elapsed never advances, so
        // the RunLoop's BudgetExceeded never fires inside a Tick and no
        // wall-clock cost can leave a Plan step unstarted (the Issue #323
        // class of timing defect, as the Core RunLoopTests /
        // ResumeRegistrySessionTests fakes remove it).
        private sealed class FakeMonotonicTimer : IMonotonicTimer
        {
            public TimeSpan Elapsed => TimeSpan.Zero;
        }

        // An Immediate that writes one §F6 log line — drives the Runs-tab
        // log rows.
        private sealed class ProbeMarkerOperation : IOperation
        {
            private readonly string _message;

            internal ProbeMarkerOperation(string id, string message)
            {
                _message = message;
                Descriptor = Op.Define(id, 1)
                    .Summary("Writes one log line.")
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
                ctx.Log.Write(Severity.Info, _message);
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

        // An Immediate creating a real asset and recording it — drives the
        // Runs-tab change row whose click pings the asset.
        private sealed class AssetChangeOperation : IOperation
        {
            private readonly string _path;

            internal AssetChangeOperation(string id, string path)
            {
                _path = path;
                Descriptor = Op.Define(id, 1)
                    .Summary("Creates one asset and records it.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }
            internal ProbeAsset? Asset { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                var asset = ScriptableObject.CreateInstance<ProbeAsset>();
                AssetDatabase.CreateAsset(asset, _path);
                Asset = asset;
                ctx.Changes.Created(
                    new ChangeTarget(
                        "asset", _path, AssetDatabase.AssetPathToGUID(_path)),
                    undoable: false);
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

        // An Immediate creating a scene GameObject and recording it —
        // drives the Runs-tab change row whose click selects it.
        private sealed class GameObjectChangeOperation : IOperation
        {
            private readonly List<GameObject> _created;

            internal GameObjectChangeOperation(string id, List<GameObject> created)
            {
                _created = created;
                Descriptor = Op.Define(id, 1)
                    .Summary("Creates one GameObject and records it.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public OperationDescriptor Descriptor { get; }
            internal GameObject? Created { get; private set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx)
            {
                var go = new GameObject("OpWeaveRunViewerProbe");
                _created.Add(go);
                Created = go;
                ctx.Changes.Created(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(go),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(go).ToString()),
                    undoable: true);
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

        // A Waitable that never finishes — parks the Run in Running until
        // the test cancels it.
        private sealed class HoldOperation : IOperation
        {
            internal HoldOperation(string id)
            {
                Descriptor = Op.Define(id, 1)
                    .Summary("Waits forever.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(RunnerHostFixture.Json("\"held\""), "held", 3600);

            public StepResult Poll(StepContext ctx) =>
                StepResult.Waiting(RunnerHostFixture.Json("\"held\""), "held", 3600);

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // A Waitable logging one line per poll — drives the live-update
        // proof: every poll journals a log record whose §E8 event the open
        // window consumes.
        private sealed class PollingLogOperation : IOperation
        {
            private readonly int _pollsToSuccess;
            private int _polls;

            internal PollingLogOperation(string id, int pollsToSuccess)
            {
                _pollsToSuccess = pollsToSuccess;
                Descriptor = Op.Define(id, 1)
                    .Summary("Logs once per poll.")
                    .Output("done", "core.Bool")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build()
                    with { ExecutionMode = ExecutionMode.Waitable };
            }

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    RunnerHostFixture.Json("{}"));

            public StepResult Execute(StepContext ctx) =>
                StepResult.Waiting(RunnerHostFixture.Json("\"p0\""), "poll-0", 1);

            public StepResult Poll(StepContext ctx)
            {
                _polls++;
                ctx.Log.Write(Severity.Info, "poll-" + _polls);
                if (_polls < _pollsToSuccess)
                {
                    return StepResult.Waiting(
                        RunnerHostFixture.Json("\"p" + _polls + "\""),
                        "poll-" + _polls, 1);
                }
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
