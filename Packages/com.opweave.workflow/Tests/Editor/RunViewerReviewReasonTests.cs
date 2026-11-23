#nullable enable

// T-W3.5-04 [Edit] tests: local:T-W3.5-04:review-reason — the NeedsReview
// panel's reason is the cause that put the Run in its current NeedsReview
// state (SPEC §H5 item 1): a Run Store load-time integrity failure shows
// its own OW-STORE-002 diagnostic by code and fixed message; a journaled
// step_needs_review still shows the recorded step reason; a dropped-tail
// OW-STORE-002 warning that did not cause the state (SPEC §F3 item 3)
// never replaces it; and a NeedsReview neither source explains is
// reported as unknown — never a stale or unrelated reason. Every fixture
// is a real Run Store folder built through the store's own seams
// (CreateRun/Append/byte-level edits), exactly like the store load suite.
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Persistence;
using OpWeave.Viewer;
using UnityEngine;
using UnityEngine.UIElements;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RunViewerReviewReasonTests
    {
        private const string WorkflowId = "example.review-reason";
        private const string StepOp = "test.reviewreason.step";
        private const string Ts = "2026-11-06T00:00:01.000Z";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();
        private readonly List<RunViewerWindow> _windows =
            new List<RunViewerWindow>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveReviewReasonTests",
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
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        private RunViewerWindow OpenBound(RunnerHostFixture fixture)
        {
            var window = ScriptableObject.CreateInstance<RunViewerWindow>();
            _windows.Add(window);
            window.BuildView();
            window.BindSource(
                new RunViewerSource(fixture.Host, fixture.Root, fixture.Clock));
            return window;
        }

        private static VisualElement Q(RunViewerWindow window, string name)
        {
            var element = window.rootVisualElement.Q<VisualElement>(name);
            Assert.IsNotNull(element, "missing element " + name);
            return element!;
        }

        private static bool Shown(VisualElement element) =>
            element.style.display.value == DisplayStyle.Flex;

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        private static string ReviewReason(RunViewerWindow window) =>
            Q(window, RunViewerWindow.ReviewPanelName)
                .Q<Label>(RunViewerWindow.ReviewReasonName)!.text;

        private static Diagnostic? ErrorDiagnosticOf(
            IReadOnlyList<Diagnostic> diagnostics)
        {
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == Severity.Error)
                {
                    return diagnostic;
                }
            }
            return null;
        }

        // A real Run Store folder through the store's own seams: plan.json,
        // workflow.json, the journal's header + run_accepted and the
        // state/index caches — the §F1 layout every load verifies.
        private static string CreateRunFolder(RunnerHostFixture fixture)
        {
            var step = RunnerHostFixture.Step(
                0, "flaky", StepOp, 'a',
                outputTypes: new Dictionary<string, string>
                {
                    ["mark"] = "core.String",
                });
            var plan = RunnerHostFixture.Plan(WorkflowId, new[] { step });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "flaky", Operation = StepOp, Version = 1 } },
                Array.Empty<ControlEdge>());
            var runId = Identifiers.NewRunId();
            var created = fixture.Host.Store.CreateRun(runId, plan, doc);
            Assert.IsTrue(created.Ok, "create failed: " + Describe(created.Diagnostics));
            return runId;
        }

        // The journaled step_needs_review of SPEC §E1, written through the
        // store's append path with the {error, message} payload every
        // writer of the record emits.
        private static void JournalStepNeedsReview(
            RunnerHostFixture fixture, string runId)
        {
            var appended = fixture.Host.Store.Append(
                runId,
                new JournalRecord(
                    2, Ts, fixture.SessionId, JournalRecordType.StepNeedsReview,
                    0, null,
                    RunnerHostFixture.Json(
                        "{\"error\":\"OW-UNITY-008\"," +
                        "\"message\":\"needs a human.\"}")),
                Durability.Flushed);
            Assert.IsTrue(appended.Ok, "append failed: " + Describe(appended.Diagnostics));
        }

        // An altered plan.json: one byte flipped inside the stored bytes —
        // the recorded planFileDigest no longer matches (SPEC §D0.1).
        private static void AlterPlan(RunnerHostFixture fixture, string runId)
        {
            var planPath = Path.Combine(
                fixture.Root, "Runs", runId, "plan.json");
            var bytes = File.ReadAllBytes(planPath);
            bytes[bytes.Length / 2] ^= 0x01;
            File.WriteAllBytes(planPath, bytes);
        }

        // The torn tail of an interrupted journal write (SPEC §F3 item 3):
        // unterminated bytes after the last complete record, dropped on
        // load with an OW-STORE-002 warning that never moves the state.
        private static void AppendTornTail(RunnerHostFixture fixture, string runId)
        {
            File.AppendAllText(
                RunnerHostFixture.JournalPath(fixture.Root, runId),
                "{\"seq\":99,\"ts\":\"");
        }

        private static RunViewerDetail Detail(RunnerHostFixture fixture, string runId)
        {
            var detail = new RunViewerSource(fixture.Host, fixture.Root, fixture.Clock)
                .DetailOf(runId);
            Assert.IsNotNull(detail, "the stored Run must produce a detail");
            return detail!;
        }

        [Test]
        public void IntegrityNeedsReviewShowsTheStoreReason()
        {
            var fixture = NewFixture();
            var runId = CreateRunFolder(fixture);
            AlterPlan(fixture, runId);

            var loaded = fixture.Host.Store.Load(runId);
            Assert.IsTrue(loaded.Ok, "load failed: " + Describe(loaded.Diagnostics));
            Assert.AreEqual(RunState.NeedsReview, loaded.Value!.State);
            var cause = ErrorDiagnosticOf(loaded.Diagnostics);
            Assert.IsNotNull(cause, "the integrity failure must be reported");
            Assert.AreEqual(ErrorCodes.OW_STORE_002, cause!.Code);

            var detail = Detail(fixture, runId);
            Assert.AreEqual(RunState.NeedsReview, detail.State);
            var review = detail.Review;
            Assert.IsNotNull(review, "the integrity cause must be named");
            Assert.AreEqual(RunViewerReviewSource.StoreIntegrity, review!.Source);
            Assert.IsNull(review.StepIndex);
            Assert.AreEqual(cause.Code, review.Code);
            Assert.AreEqual(cause.Message, review.Message);

            var window = OpenBound(fixture);
            Assert.IsTrue(
                Shown(Q(window, RunViewerWindow.ReviewPanelName)),
                "the review panel must show in NeedsReview");
            var reason = ReviewReason(window);
            StringAssert.Contains(ErrorCodes.OW_STORE_002, reason);
            StringAssert.Contains(cause.Message, reason);
            Assert.IsNotNull(
                Q(window, RunViewerWindow.ReviewPanelName)
                    .Q<Button>(RunViewerWindow.ReviewRetryButtonName));
            Assert.IsNotNull(
                Q(window, RunViewerWindow.ReviewPanelName)
                    .Q<Button>(RunViewerWindow.ReviewAbortButtonName));
        }

        [Test]
        public void JournaledNeedsReviewShowsTheStepReason()
        {
            var fixture = NewFixture();
            var runId = CreateRunFolder(fixture);
            JournalStepNeedsReview(fixture, runId);

            var loaded = fixture.Host.Store.Load(runId);
            Assert.IsTrue(loaded.Ok, "load failed: " + Describe(loaded.Diagnostics));
            Assert.AreEqual(RunState.NeedsReview, loaded.Value!.State);
            Assert.IsNull(
                ErrorDiagnosticOf(loaded.Diagnostics),
                "a journal-origin NeedsReview carries no load failure");

            var detail = Detail(fixture, runId);
            var review = detail.Review;
            Assert.IsNotNull(review, "the journaled reason must be named");
            Assert.AreEqual(RunViewerReviewSource.StepJournal, review!.Source);
            Assert.AreEqual(0, review.StepIndex);
            Assert.AreEqual("flaky", review.NodeId);
            Assert.AreEqual(ErrorCodes.OW_UNITY_008, review.Code);
            Assert.AreEqual("needs a human.", review.Message);

            var window = OpenBound(fixture);
            var reason = ReviewReason(window);
            StringAssert.Contains("flaky", reason);
            StringAssert.Contains(ErrorCodes.OW_UNITY_008, reason);
            StringAssert.Contains("needs a human.", reason);
            StringAssert.DoesNotContain(ErrorCodes.OW_STORE_002, reason);
        }

        [Test]
        public void IntegrityNeedsReviewSupersedesAnEarlierStepReason()
        {
            var fixture = NewFixture();
            var runId = CreateRunFolder(fixture);
            JournalStepNeedsReview(fixture, runId);
            AlterPlan(fixture, runId);

            var loaded = fixture.Host.Store.Load(runId);
            Assert.IsTrue(loaded.Ok, "load failed: " + Describe(loaded.Diagnostics));
            Assert.AreEqual(RunState.NeedsReview, loaded.Value!.State);
            var cause = ErrorDiagnosticOf(loaded.Diagnostics);
            Assert.IsNotNull(cause, "the integrity failure must be reported");
            Assert.AreEqual(ErrorCodes.OW_STORE_002, cause!.Code);

            // The store's diagnostic is the cause that moved the state: it,
            // not the earlier step_needs_review reason, is shown.
            var detail = Detail(fixture, runId);
            var review = detail.Review;
            Assert.IsNotNull(review, "the integrity cause must be named");
            Assert.AreEqual(RunViewerReviewSource.StoreIntegrity, review!.Source);
            Assert.AreEqual(cause.Code, review.Code);
            Assert.AreEqual(cause.Message, review.Message);

            var window = OpenBound(fixture);
            var reason = ReviewReason(window);
            StringAssert.Contains(ErrorCodes.OW_STORE_002, reason);
            StringAssert.Contains(cause.Message, reason);
            StringAssert.DoesNotContain(ErrorCodes.OW_UNITY_008, reason);
            StringAssert.DoesNotContain("needs a human.", reason);
        }

        [Test]
        public void DroppedTailWarningKeepsTheStepReason()
        {
            var fixture = NewFixture();
            var runId = CreateRunFolder(fixture);
            JournalStepNeedsReview(fixture, runId);
            AppendTornTail(fixture, runId);

            var loaded = fixture.Host.Store.Load(runId);
            Assert.IsTrue(loaded.Ok, "load failed: " + Describe(loaded.Diagnostics));
            Assert.AreEqual(RunState.NeedsReview, loaded.Value!.State);
            var warning = loaded.Diagnostics;
            Assert.AreEqual(1, warning.Count);
            Assert.AreEqual(ErrorCodes.OW_STORE_002, warning[0].Code);
            Assert.AreEqual(
                Severity.Warning, warning[0].Severity,
                "the dropped tail is a warning that never caused the state");

            // The non-causal OW-STORE-002 warning never replaces the
            // step_needs_review reason the state actually came from.
            var detail = Detail(fixture, runId);
            var review = detail.Review;
            Assert.IsNotNull(review, "the journaled reason must be named");
            Assert.AreEqual(RunViewerReviewSource.StepJournal, review!.Source);
            Assert.AreEqual(0, review.StepIndex);
            Assert.AreEqual("flaky", review.NodeId);
            Assert.AreEqual(ErrorCodes.OW_UNITY_008, review.Code);
            Assert.AreEqual("needs a human.", review.Message);

            var window = OpenBound(fixture);
            var reason = ReviewReason(window);
            StringAssert.Contains(ErrorCodes.OW_UNITY_008, reason);
            StringAssert.Contains("needs a human.", reason);
            StringAssert.DoesNotContain(ErrorCodes.OW_STORE_002, reason);
        }

        [Test]
        public void NeedsReviewWithoutAReasonSourceReportsUnknown()
        {
            var fixture = NewFixture();
            var runId = CreateRunFolder(fixture);
            var scan = RunJournalScan.Read(
                RunnerHostFixture.JournalPath(fixture.Root, runId));
            Assert.IsNull(scan.LatestReview, "the journal records no review reason");
            Assert.IsNull(scan.Failure, "the controlled journal reads clean");

            // A real Load derives NeedsReview only from a decodable
            // step_needs_review record or an OW-STORE-002 integrity
            // diagnostic — both reason sources — so the unknown display is
            // exercised through the RunSnapshot assembly seam the Core
            // store doubles use: a NeedsReview snapshot over this real
            // folder with no step_needs_review record and no NeedsReview-
            // causing load diagnostic.
            var snapshot = new RunSnapshot(
                runId, null, RunState.NeedsReview,
                Array.Empty<StepSnapshot>(), null, Array.Empty<RunResource>(),
                false, false, null, null, null, 1);
            var review = RunViewerSource.ReviewOf(
                snapshot, scan,
                Array.Empty<RunViewerStepRow>(), Array.Empty<Diagnostic>());
            Assert.IsNull(review, "no reason source may yield a review");

            var reason = RunViewerWindow.ReviewReasonText(review);
            StringAssert.Contains("unknown", reason);
        }
    }
}
