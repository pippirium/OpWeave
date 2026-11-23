#nullable enable

// T-W3.5-02 [Edit] tests: local:T-W3.5-02:approval-review-panels — the
// three SPEC §H5 paths through the actual Run Viewer UI: the approval
// panel's Approve runs the approved Plan to Succeeded with
// approval_granted journaled; its Reject ends the Run Cancelled with
// approval_rejected and OW-PERM-003; and the NeedsReview panel's "retry
// this step" re-runs the step with review_resolved journaled to success.
// The panels surface only in the states that allow their actions — the
// approval panel only while AwaitingApproval, the review panel only
// while NeedsReview — and the §H5 item 3 notification selects the
// pending Run and posts the status. The fixtures bind the window to a
// real OpWeaveRunnerHost over a probe Run Store and drive the same
// handlers the buttons wire, exactly like the Runs-tab suite.
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
using OpWeave.Viewer;
using UnityEngine;
using UnityEngine.UIElements;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RunViewerApprovalReviewPanelTests
    {
        private const string WorkflowId = "example.panels";
        private const string MarkerOp = "test.panels.marker";
        private const string FlakyOp = "test.panels.flaky";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();
        private readonly List<RunViewerWindow> _windows =
            new List<RunViewerWindow>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeavePanelTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
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
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root)
            {
                MonotonicTimerFactory = () => new FakeMonotonicTimer(),
            };
            _fixtures.Add(fixture);
            return fixture;
        }

        // The window bound to the fixture's runner host — a CreateInstance'd
        // window whose rootVisualElement is populated through the same
        // BuildView path OnEnable uses; the handlers the buttons wire are
        // invoked the way the Runs-tab suite drives them.
        private RunViewerWindow OpenBound(RunnerHostFixture fixture)
        {
            var window = ScriptableObject.CreateInstance<RunViewerWindow>();
            _windows.Add(window);
            window.BuildView();
            window.BindSource(
                new RunViewerSource(fixture.Host, fixture.Root, fixture.Clock));
            return window;
        }

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

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        private static VisualElement Q(RunViewerWindow window, string name)
        {
            var element = window.rootVisualElement.Q<VisualElement>(name);
            Assert.IsNotNull(element, "missing element " + name);
            return element!;
        }

        private static bool Shown(VisualElement element) =>
            element.style.display.value == DisplayStyle.Flex;

        // ── Plan builders ───────────────────────────────────────────────

        private static PlanStep Step(
            int index,
            string nodeId,
            string operationId,
            char hashSeed,
            IReadOnlyList<string>? effects = null,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            int next = -1,
            Idempotency idempotency = Idempotency.Idempotent) =>
            new PlanStep(
                index,
                nodeId,
                operationId,
                1,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64),
                inputs ?? new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = next },
                effects ?? (IReadOnlyList<string>)Array.Empty<string>(),
                30,
                new RetryPolicy(),
                idempotency);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"fixture JSON must parse: {text}");
            return parsed.Value!;
        }

        // The approvals-carrying variant of RunnerHostFixture.Plan: the asks
        // flow into requiredEffects so the planHash covers them (SPEC §D7).
        private static CompiledPlan Plan(
            string workflowId,
            IReadOnlyList<PlanStep> steps,
            IReadOnlyList<ApprovalRequirement> approvals)
        {
            var hashInput = new JObject
            {
                ["canonicalJsonVersion"] = 1,
                ["environmentIdentifiers"] = new JObject
                {
                    ["unityVersion"] = "6000.0.85f1",
                    ["editorOS"] = "windows",
                    ["activeBuildTarget"] = "windows",
                    ["scriptingBackend"] = "mono",
                    ["apiCompatibilityLevel"] = "netstandard2.1",
                    ["scriptingDefineSymbols"] = new JArray(),
                },
                ["executionInputs"] = PlanHashInputParts.ExecutionInputs(
                    Array.Empty<string>(), null),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(steps, approvals),
                ["resolvedWorkflow"] = new JObject
                {
                    ["id"] = workflowId,
                    ["inputs"] = new JArray(),
                    ["outputs"] = new JArray(),
                },
                ["retry"] = PlanHashInputParts.Retry(steps),
                ["steps"] = PlanHashInputParts.Steps(steps),
                ["timeouts"] = PlanHashInputParts.Timeouts(steps),
                ["transitions"] = PlanHashInputParts.Transitions(steps),
                ["usedCodecFingerprints"] = new JArray(),
                ["usedOperationFingerprints"] =
                    PlanHashInputParts.OperationFingerprints(steps),
                ["usedTypeFingerprints"] = new JArray(),
            };
            var input = JsonValue.From(hashInput);
            return new CompiledPlan(
                "plan_" + Identifiers.NewUlid(),
                CanonicalJson.Sha256(input.ToToken()),
                workflowId,
                new string('b', 64),
                new string('c', 64),
                Json("{}"),
                steps,
                Array.Empty<PlanOutput>(),
                PlanBuilder.UnionEffects(steps),
                approvals,
                Array.Empty<PlanSourceMapEntry>(),
                input);
        }

        private static CompiledPlan AskPlan(
            string workflowId,
            char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null)
        {
            var steps = new[]
            {
                Step(0, "ask", MarkerOp, hashSeed,
                    effects: new[] { "asset.delete" },
                    inputs: inputs,
                    outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" }),
            };
            var approvals = new[]
            {
                new ApprovalRequirement("ask", "asset.delete", MarkerOp + " v1"),
            };
            return Plan(workflowId, steps, approvals);
        }

        private static WorkflowDocument AskDoc(string workflowId) =>
            RunnerHostFixture.Doc(
                workflowId,
                new[] { new NodeData { Id = "ask", Operation = MarkerOp, Version = 1 } },
                Array.Empty<ControlEdge>());

        private static Result<RunHandle> Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc) =>
            fixture.Host.AcceptRun(plan, doc, ActorKind.Agent, RunnerHostFixture.Options());

        private static Result<RunHandle> AcceptAsk(
            RunnerHostFixture fixture, out CompiledPlan plan)
        {
            plan = AskPlan(WorkflowId, 'a', inputs: new Dictionary<string, InputSource>
            {
                ["path"] = new Constant(Json("\"Assets/Scripts/Generated.cs\"")),
            });
            var accepted = Accept(fixture, plan, AskDoc(WorkflowId));
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted;
        }

        // The deterministic Tick-budget timer — Elapsed never advances, so
        // the RunLoop's BudgetExceeded never fires inside a Tick and no
        // wall-clock cost can leave a Plan step unstarted (the Issue #323
        // class of timing defect the other fixtures remove the same way).
        private sealed class FakeMonotonicTimer : IMonotonicTimer
        {
            public TimeSpan Elapsed => TimeSpan.Zero;
        }

        // A Reconcilable step that fails retryably and never reconciles:
        // the first failed attempt's §F5 consult answers Unknown, so the
        // Run reaches NeedsReview after one Tick — the review panel's
        // fixture. Armed, its next Execute succeeds.
        private sealed class FlakyThenFixedOperation : OperationBase
        {
            internal bool Succeed;
            internal int ExecuteCalls;

            public override OperationDescriptor Descriptor { get; } =
                Op.Define(FlakyOp, 1)
                    .Summary("Fails until armed.")
                    .Output("mark", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Reconcilable)
                    .Build();

            public override StepResult Execute(StepContext ctx)
            {
                ExecuteCalls++;
                if (!Succeed)
                {
                    return StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_008,
                        "A transient failure.",
                        retryable: true));
                }
                ctx.SetOutput("mark", "done");
                return StepResult.Succeeded();
            }

            public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never decides.");
        }

        private string AcceptNeedsReview(RunnerHostFixture fixture, FlakyThenFixedOperation op)
        {
            fixture.Register(op, 'a');
            var step = Step(
                0, "flaky", FlakyOp, 'a',
                outputTypes: new Dictionary<string, string> { ["mark"] = "core.String" },
                next: -1,
                idempotency: Idempotency.Reconcilable);
            var plan = RunnerHostFixture.Plan(WorkflowId, new[] { step });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "flaky", Operation = FlakyOp, Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture, runId);
            Assert.AreEqual(
                RunState.NeedsReview, fixture.Host.Coordinator.StateOf(runId),
                "the fixture must reach NeedsReview");
            return runId;
        }

        // ── The approval panel (SPEC §H4 item 3, §H5 item 1) ───────────

        [Test]
        public void ApprovalPanelShowsTheDecisionDetailWhileAwaiting()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation(MarkerOp, fixture.Events);
            fixture.Register(op, 'a');
            var accepted = AcceptAsk(fixture, out var plan);
            var runId = accepted.Value!.RunId;
            Assert.AreEqual(RunState.AwaitingApproval, fixture.Host.Coordinator.StateOf(runId));

            var window = OpenBound(fixture);

            // The approval panel is the only action shown: the asked
            // effects, the bound planHash and the decision detail the
            // human reads (SPEC §H4 item 3).
            var panel = Q(window, RunViewerWindow.ApprovalPanelName);
            Assert.IsTrue(Shown(panel), "the approval panel must show while awaiting");
            Assert.IsFalse(
                Shown(Q(window, RunViewerWindow.ReviewPanelName)),
                "the review panel must not show while awaiting");
            Assert.IsFalse(
                Shown(Q(window, RunViewerWindow.CancelButtonName)),
                "AwaitingApproval offers no cancel");

            var title = panel.Q<Label>(RunViewerWindow.ApprovalTitleName);
            Assert.IsNotNull(title);
            var hash = panel.Q<Label>(RunViewerWindow.ApprovalPlanHashName);
            StringAssert.Contains(plan.PlanHash, hash!.text);
            var deadline = panel.Q<Label>(RunViewerWindow.ApprovalDeadlineName);
            StringAssert.Contains("decide by", deadline!.text);

            var nodeRows = panel.Query<VisualElement>(RunViewerWindow.ApprovalNodeRowName).ToList();
            Assert.AreEqual(1, nodeRows.Count);
            var nodeTitle = nodeRows[0].Q<Label>(RunViewerWindow.ApprovalNodeTitleName)!.text;
            StringAssert.Contains("ask", nodeTitle);
            StringAssert.Contains(MarkerOp, nodeTitle);
            StringAssert.Contains("custom", nodeTitle);
            StringAssert.Contains("Example.Operations", nodeTitle);
            var nodeText = "";
            foreach (var line in nodeRows[0].Query<Label>(RunViewerWindow.ApprovalNodeLineName).ToList())
            {
                nodeText += line.text + "\n";
            }
            StringAssert.Contains("asks asset.delete", nodeText);
            StringAssert.Contains("effects asset.delete", nodeText);
            StringAssert.Contains("path: Assets/Scripts/Generated.cs", nodeText);

            var usedRows = panel.Query<VisualElement>(RunViewerWindow.ApprovalUsedRowName).ToList();
            Assert.AreEqual(1, usedRows.Count);
            StringAssert.Contains(
                MarkerOp, usedRows[0].Q<Label>(RunViewerWindow.ApprovalUsedLineName)!.text);

            Assert.IsNotNull(panel.Q<Button>(RunViewerWindow.ApproveButtonName));
            Assert.IsNotNull(panel.Q<Button>(RunViewerWindow.RejectButtonName));
            Assert.IsNotNull(panel.Q<TextField>(RunViewerWindow.RejectReasonName));
        }

        [Test]
        public void ApproveFromThePanelRunsTheApprovedPlanToSuccess()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation(MarkerOp, fixture.Events);
            fixture.Register(op, 'a');
            var accepted = AcceptAsk(fixture, out var plan);
            var runId = accepted.Value!.RunId;
            var window = OpenBound(fixture);

            // The button's wired action — the human's Approve.
            window.OnApproveClicked();

            Assert.AreEqual(
                RunState.Queued, fixture.Host.Coordinator.StateOf(runId),
                "the approved Run re-enters the queue");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"approval_granted\"",
                    "\"approver\":\"" + Environment.UserName + "\""),
                "approval_granted must be journaled with the OS user (SPEC §T.4)");
            Assert.IsFalse(
                Shown(Q(window, RunViewerWindow.ApprovalPanelName)),
                "the panel closes once the Run is decided");

            Drive(fixture, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Assert.That(
                fixture.Events, Is.EqualTo(new[] { "execute:" + MarkerOp }));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"run_finished\"", "\"state\":\"succeeded\""));
            Assert.AreEqual(0, fixture.Host.Approvals.PendingApprovals.Count);
        }

        [Test]
        public void RejectFromThePanelCancelsWithApprovalRejected()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation(MarkerOp, fixture.Events);
            fixture.Register(op, 'a');
            var accepted = AcceptAsk(fixture, out var plan);
            var runId = accepted.Value!.RunId;
            var window = OpenBound(fixture);
            var reason = Q(window, RunViewerWindow.RejectReasonName) as TextField;
            Assert.IsNotNull(reason);
            reason!.value = "not today";

            // The button's wired action — the human's Reject.
            window.OnRejectClicked();

            Assert.AreEqual(RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"approval_rejected\"",
                    "\"approver\":\"" + Environment.UserName + "\"",
                    "\"reason\":\"not today\""));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"run_finished\"", "\"state\":\"cancelled\""));
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(_root, runId, ErrorCodes.OW_PERM_003));
            Assert.IsEmpty(fixture.Events, "a rejected Plan never executes");
            Assert.IsFalse(
                Shown(Q(window, RunViewerWindow.ApprovalPanelName)));
            Assert.IsFalse(
                Shown(Q(window, RunViewerWindow.ReviewPanelName)));
            Assert.AreEqual(0, fixture.Host.Approvals.PendingApprovals.Count);
        }

        // ── The NeedsReview panel (SPEC §E2, §H5 item 1) ────────────────

        [Test]
        public void ReviewPanelShowsTheJournaledReasonWhileNeedsReview()
        {
            var fixture = NewFixture();
            var op = new FlakyThenFixedOperation();
            var runId = AcceptNeedsReview(fixture, op);
            var window = OpenBound(fixture);

            var panel = Q(window, RunViewerWindow.ReviewPanelName);
            Assert.IsTrue(Shown(panel), "the review panel must show in NeedsReview");
            Assert.IsFalse(
                Shown(Q(window, RunViewerWindow.ApprovalPanelName)));
            Assert.IsFalse(
                Shown(Q(window, RunViewerWindow.CancelButtonName)),
                "NeedsReview offers no cancel");

            var reason = panel.Q<Label>(RunViewerWindow.ReviewReasonName)!.text;
            StringAssert.Contains("flaky", reason);
            StringAssert.Contains(ErrorCodes.OW_UNITY_008, reason);
            StringAssert.Contains("A transient failure.", reason);
            Assert.AreEqual(
                "Retry this step",
                panel.Q<Button>(RunViewerWindow.ReviewRetryButtonName)!.text);
            Assert.AreEqual(
                "Abort",
                panel.Q<Button>(RunViewerWindow.ReviewAbortButtonName)!.text);
        }

        [Test]
        public void RetryFromThePanelRerunsTheStepAndResolvesTheRun()
        {
            var fixture = NewFixture();
            var op = new FlakyThenFixedOperation();
            var runId = AcceptNeedsReview(fixture, op);
            var window = OpenBound(fixture);
            op.Succeed = true;

            // The button's wired action — the human's "retry this step".
            window.OnReviewRetryClicked();

            Assert.AreEqual(
                RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                "retry re-arms the Run");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"review_resolved\"", "\"retry_step\""),
                "review_resolved must journal the retry decision");
            Assert.IsFalse(
                Shown(Q(window, RunViewerWindow.ReviewPanelName)),
                "the panel closes once resolved");

            Drive(fixture, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            Assert.AreEqual(
                2, op.ExecuteCalls,
                "the retried step executes once more (attempt 2)");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId,
                    "\"type\":\"step_succeeded\"", "\"attempt\":2"));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"run_finished\"", "\"state\":\"succeeded\""));
        }

        [Test]
        public void AbortFromThePanelCancelsTheRun()
        {
            var fixture = NewFixture();
            var op = new FlakyThenFixedOperation();
            var runId = AcceptNeedsReview(fixture, op);
            var window = OpenBound(fixture);

            // The button's wired action — the human's Abort.
            window.OnReviewAbortClicked();

            Assert.AreEqual(RunState.Cancelled, fixture.Host.Coordinator.StateOf(runId));
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(_root, runId, "\"type\":\"review_resolved\""));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"run_finished\"", "\"state\":\"cancelled\""));
            Assert.AreEqual(
                1, op.ExecuteCalls, "an aborted step is never re-executed");
            Assert.IsFalse(
                Shown(Q(window, RunViewerWindow.ReviewPanelName)));
        }

        // ── Panels never appear for states that do not allow them ──────

        [Test]
        public void PanelsNeverAppearForStatesThatDoNotAllowTheirActions()
        {
            var fixture = NewFixture();
            var waitable = new RunnerHostFixture.LabelWaitableOperation("test.panels.waitable");
            fixture.Register(waitable, 'c');
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                Step(0, "w", "test.panels.waitable", 'c',
                    outputTypes: new Dictionary<string, string> { ["done"] = "core.Bool" },
                    idempotency: Idempotency.Idempotent),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "w", Operation = "test.panels.waitable", Version = 1 } },
                Array.Empty<ControlEdge>());
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            var window = OpenBound(fixture);

            // Queued: neither panel — only cancel.
            Assert.IsFalse(Shown(Q(window, RunViewerWindow.ApprovalPanelName)));
            Assert.IsFalse(Shown(Q(window, RunViewerWindow.ReviewPanelName)));
            Assert.IsTrue(Shown(Q(window, RunViewerWindow.CancelButtonName)));

            // Running: still neither panel.
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            Assert.AreEqual(RunState.Running, fixture.Host.Coordinator.StateOf(runId));
            window.RefreshDetail();
            Assert.IsFalse(Shown(Q(window, RunViewerWindow.ApprovalPanelName)));
            Assert.IsFalse(Shown(Q(window, RunViewerWindow.ReviewPanelName)));

            // Succeeded (terminal): nothing actionable.
            Drive(fixture, runId);
            Assert.AreEqual(RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId));
            window.RefreshDetail();
            Assert.IsFalse(Shown(Q(window, RunViewerWindow.ApprovalPanelName)));
            Assert.IsFalse(Shown(Q(window, RunViewerWindow.ReviewPanelName)));
            Assert.IsFalse(Shown(Q(window, RunViewerWindow.CancelButtonName)));

            // An approve/reject click outside AwaitingApproval is a no-op —
            // no record is journaled for a decided or running Run.
            window.OnApproveClicked();
            window.OnRejectClicked();
            window.OnReviewRetryClicked();
            window.OnReviewAbortClicked();
            Assert.AreEqual(
                0,
                RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"approval_") +
                RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"review_resolved\""),
                "no decision record may journal for a terminal Run");
        }

        // ── SPEC §H5 item 3: the pending-approval notification ─────────

        [Test]
        public void ApprovalPendingSelectsTheRunAndPostsTheNotification()
        {
            var fixture = NewFixture();
            var op = new RunnerHostFixture.MarkerOperation(MarkerOp, fixture.Events);
            fixture.Register(op, 'a');
            var plan = AskPlan(WorkflowId, 'a');
            var doc = AskDoc(WorkflowId);

            // The window is already open and bound when the Run enters
            // AwaitingApproval — the service's ApprovalPending reaches it
            // through the source's §H5 item 3 subscription.
            var window = OpenBound(fixture);
            var accepted = Accept(fixture, plan, doc);
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            Assert.AreEqual(
                runId, window.SelectedRunId,
                "the pending Run is selected so its approval panel is the open detail");
            StringAssert.Contains(
                "awaits an approval decision",
                window.rootVisualElement.Q<Label>(RunViewerWindow.StatusLabelName)!.text);
            Assert.IsTrue(
                Shown(Q(window, RunViewerWindow.ApprovalPanelName)),
                "the approval panel is shown for the pending Run");

            // The notifier's window-opening half: handed the detail and an
            // opener, it selects and notifies the window it returns —
            // the same entry the production delayCall subscription uses.
            var opened = 0;
            var detail = fixture.Host.Approvals.DetailOf(runId);
            Assert.IsNotNull(detail, "the pending Run must carry its detail");
            RunViewerApprovalNotifier.NotifyApproval(
                detail!,
                () =>
                {
                    opened++;
                    return window;
                });
            Assert.AreEqual(1, opened, "the notifier opens the window once");
            Assert.AreEqual(runId, window.SelectedRunId);
        }
    }
}
