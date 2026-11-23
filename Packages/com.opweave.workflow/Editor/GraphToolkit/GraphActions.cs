#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The outcome one SPEC §H8 item 2 action reports: the fixed English
    /// summary the action surface displays plus the diagnostics the
    /// attempt produced — errors cross the action boundary as data, never
    /// as exceptions.
    /// </summary>
    internal sealed class GraphActionResult
    {
        internal GraphActionResult(
            bool ok,
            string message,
            IReadOnlyList<Diagnostic> diagnostics,
            string? runId = null)
        {
            Ok = ok;
            Message = message;
            Diagnostics = diagnostics;
            RunId = runId;
        }

        /// <summary>Whether the action completed its requested effect.</summary>
        internal bool Ok { get; }

        /// <summary>The fixed English summary the action surface displays.</summary>
        internal string Message { get; }

        /// <summary>The diagnostics the attempt produced — refusal reasons included.</summary>
        internal IReadOnlyList<Diagnostic> Diagnostics { get; }

        /// <summary>The Run the action accepted or cancelled, when one exists.</summary>
        internal string? RunId { get; }

        internal static GraphActionResult Passed(
            string message,
            IReadOnlyList<Diagnostic> diagnostics,
            string? runId = null) =>
            new GraphActionResult(true, message, diagnostics, runId);

        internal static GraphActionResult Failed(
            string message,
            IReadOnlyList<Diagnostic> diagnostics,
            string? runId = null) =>
            new GraphActionResult(false, message, diagnostics, runId);
    }

    /// <summary>
    /// The SPEC §H8 item 2 GUI actions — Validate, Dry Run, Run, Cancel,
    /// Export JSON, Import JSON and open Run Viewer — wired onto the
    /// existing compile/execution/approval seams rather than rebuilt:
    /// validation runs the shared §D pipeline of
    /// <see cref="GraphLiveValidation.Compile"/>, the Run passes the
    /// production <see cref="OpWeaveRunnerHost.AcceptRun"/> gate chain and
    /// the SPEC §H4 item 4 pre-start confirmation panel approves every ask
    /// effect the Plan carries together before the accept proceeds.
    /// </summary>
    /// <remarks>
    /// Graph Toolkit 6000.5's public API has no toolbar extension point
    /// and <see cref="OpWeaveGraph"/> is a managed object, not a
    /// UnityEngine.Object an inspector can be bound to — the actions are
    /// invoked from the <see cref="OpWeaveGraphActionsWindow"/> fallback
    /// surface (the documented T-W5.1-02 L-03 substitute for the
    /// toolbar/inspector placement) and are otherwise plain internal
    /// commands taking an <see cref="OpWeaveGraph"/>.
    /// </remarks>
    internal static class GraphActions
    {
        /// <summary>The fixed English reason a GUI Cancel journals.</summary>
        internal const string CancelReasonText = "Cancelled from the graph actions.";

        /// <summary>The menu path the open-Run-Viewer action executes.</summary>
        internal const string RunViewerMenuPath = "Window/OpWeave/Runs";

        // ── Test seams ────────────────────────────────────────────────
        /// <summary>The installed runner host, or null when the adapter bootstrap has not produced one in this domain.</summary>
        internal static Func<OpWeaveRunnerHost?> RunnerHost =
            static () => OpWeaveRunnerHost.Current;

        /// <summary>
        /// The SPEC §H4 item 4 confirmation: invoked once per Run with the
        /// detail of EVERY ask effect the Plan carries; the Run starts
        /// only when it returns true. Production binds the modal panel of
        /// <see cref="GraphRunConfirmWindow"/>; a test installs a scripted
        /// answer.
        /// </summary>
        internal static Func<RunApprovalDetail, bool> ConfirmAskEffects =
            DefaultConfirmAskEffects;

        /// <summary>
        /// The open-Run-Viewer command — <see cref="EditorApplication.ExecuteMenuItem"/>
        /// in production; a test substitutes a counter.
        /// </summary>
        internal static Action OpenRunViewerCommand =
            static () => EditorApplication.ExecuteMenuItem(RunViewerMenuPath);

        // ── Validate / Dry Run ────────────────────────────────────────

        /// <summary>
        /// The Validate action: the §D static pass over the graph's §H7
        /// export — the same pipeline the live validation runs — with the
        /// diagnostics published onto the nodes it names.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static GraphActionResult Validate(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var outcome = GraphLiveValidation.Compile(
                graph, ValidationMode.Static, ActorKind.Human);
            if (outcome.EnvironmentUnavailable || outcome.RegistryUnavailable)
            {
                return RunnerNotReady("Validate", outcome.Diagnostics);
            }
            GraphLiveValidation.Publish(graph, outcome.Diagnostics);
            var errors = CountErrors(outcome.Diagnostics);
            return errors == 0
                ? GraphActionResult.Passed("Validation passed.", Flatten(outcome.Diagnostics))
                : GraphActionResult.Failed(
                    "Validation failed: " + errors + " error(s).", Flatten(outcome.Diagnostics));
        }

        /// <summary>
        /// The Dry Run action: the §D8 Preflight-only pass over the
        /// graph's §H7 export — planned changes are reported, never
        /// applied (the SPEC §D8 guarantee is <c>"none"</c>).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static GraphActionResult DryRun(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var outcome = GraphLiveValidation.Compile(
                graph, ValidationMode.DryRun, ActorKind.Human);
            if (outcome.EnvironmentUnavailable || outcome.RegistryUnavailable)
            {
                return RunnerNotReady("Dry Run", outcome.Diagnostics);
            }
            GraphLiveValidation.Publish(graph, outcome.Diagnostics);
            var report = outcome.Report;
            var errors = CountErrors(outcome.Diagnostics);
            if (errors > 0 || report is null || !report.Ok)
            {
                return GraphActionResult.Failed(
                    "Dry Run failed: " + errors + " error(s).", Flatten(outcome.Diagnostics));
            }
            var planned = report.PlannedChanges ?? (IReadOnlyList<PlannedChange>)
                Array.Empty<PlannedChange>();
            for (var i = 0; i < planned.Count; i++)
            {
                Debug.Log(
                    "OpWeave Dry Run: node '" + planned[i].NodeId + "' would " +
                    planned[i].Kind + " " + planned[i].Target +
                    " (" + planned[i].Effect + ").");
            }
            return GraphActionResult.Passed(
                "Dry Run passed: " + planned.Count +
                " planned change(s); guarantee: " + report.Guarantee + ".",
                Flatten(outcome.Diagnostics));
        }

        // ── Run / Cancel ─────────────────────────────────────────────

        /// <summary>
        /// The Run action: validates the graph, applies the SPEC §H4
        /// item 4 pre-start confirmation when the Plan asks approval —
        /// every ask effect listed and approved together — and feeds the
        /// Plan to the production <see cref="OpWeaveRunnerHost.AcceptRun"/>
        /// gate chain. Declining the panel starts nothing and surfaces
        /// OW-PERM-003; an accepted Run with asks is approved through the
        /// same human-only journal path the Run Viewer uses, then the
        /// §H8 item 3 display attaches to its §E8 events.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static GraphActionResult Run(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var host = RunnerHost();
            if (host is null)
            {
                return GraphActionResult.Failed(
                    "Run could not start: the OpWeave runner is not installed in this Editor.",
                    Array.Empty<Diagnostic>());
            }
            var outcome = GraphLiveValidation.Compile(
                graph, ValidationMode.Static, ActorKind.Human);
            if (outcome.EnvironmentUnavailable || outcome.RegistryUnavailable)
            {
                return RunnerNotReady("Run", outcome.Diagnostics);
            }
            // The fresh diagnostics land on the nodes they name either
            // way — a refusal shows the failing set, an accepted Run
            // clears a stale badge the last pass left.
            GraphLiveValidation.Publish(graph, outcome.Diagnostics);
            var report = outcome.Report;
            if (report is null || !report.Ok || report.Plan is null)
            {
                return GraphActionResult.Failed(
                    "Run refused: the graph does not validate.",
                    Flatten(outcome.Diagnostics));
            }
            var plan = report.Plan;
            if (plan.Approvals.Count > 0)
            {
                // SPEC §H4 item 4: one pre-start confirmation listing every
                // ask effect the Plan carries. The detail is the same
                // RunApprovalDetail the Run Viewer's approval screen
                // renders, minus a Run id — none exists yet.
                var detail = RunApprovalDetail.Build(
                    "", plan, outcome.Snapshot, null);
                if (!ConfirmAskEffects(detail))
                {
                    var declined = new List<Diagnostic>
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_PERM_003,
                            "The Run was not started: its ask effects were declined."),
                    };
                    Debug.LogWarning(
                        "OW-PERM-003: The Run was not started: its ask effects were declined.");
                    return GraphActionResult.Failed(
                        "Run declined: " + plan.Approvals.Count +
                        " ask effect(s) were not approved (OW-PERM-003).",
                        declined);
                }
            }
            var accepted = host.AcceptRun(
                plan,
                outcome.Document!,
                ActorKind.Human,
                // planHash-bound (SPEC §D7 item 7); queue: the GUI Run
                // waits in the §E2 item 2 queue rather than refusing a
                // busy Runner outright.
                new AcceptOptions(plan.PlanHash, Queue: true));
            if (!accepted.Ok || accepted.Value is null)
            {
                return GraphActionResult.Failed(
                    "Run was refused at acceptance.", accepted.Diagnostics);
            }
            var handle = accepted.Value;
            if (handle.State == RunState.AwaitingApproval)
            {
                // The panel's approval lands through the same human-only
                // journal path the Run Viewer uses — approval_granted
                // bound to exactly this planHash (SPEC §H4 item 3).
                var approved = host.Approvals.Approve(handle.RunId, plan.PlanHash);
                if (!approved.Ok || approved.Value is null)
                {
                    return GraphActionResult.Failed(
                        "Run " + handle.RunId + " is awaiting approval but the " +
                        "approval decision failed.",
                        approved.Diagnostics,
                        handle.RunId);
                }
                handle = approved.Value;
            }
            GraphExecutionDisplay.Attach(graph, host.Events, handle.RunId);
            return GraphActionResult.Passed(
                "Run " + handle.RunId + " accepted (" + handle.State + ").",
                accepted.Diagnostics,
                handle.RunId);
        }

        /// <summary>
        /// The Cancel action: cancels the Run this graph's display
        /// currently tracks through the Core cancel path — the journaled
        /// cancel request the run loop applies.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static GraphActionResult Cancel(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var runId = GraphExecutionDisplay.RunIdOf(graph);
            if (runId is null)
            {
                return GraphActionResult.Failed(
                    "Cancel: this graph tracks no Run.",
                    Array.Empty<Diagnostic>());
            }
            var host = RunnerHost();
            if (host is null)
            {
                return GraphActionResult.Failed(
                    "Cancel could not run: the OpWeave runner is not installed in this Editor.",
                    Array.Empty<Diagnostic>(),
                    runId);
            }
            var cancelled = host.Coordinator.Cancel(runId, CancelReasonText);
            return cancelled.Ok
                ? GraphActionResult.Passed(
                    "Cancel requested for Run " + runId + ".",
                    cancelled.Diagnostics,
                    runId)
                : GraphActionResult.Failed(
                    "Cancel failed for Run " + runId + ".",
                    cancelled.Diagnostics,
                    runId);
        }

        // ── Export / Import JSON ──────────────────────────────────────

        /// <summary>
        /// The Export JSON action: the explicit human save of SPEC §H7
        /// item 3 — the same single save path the
        /// <c>Tools &gt; OpWeave &gt; Save Graph JSON</c> command drives,
        /// first-link, revision-bound and conflict-prompted.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static GraphActionResult ExportJson(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var result = OpWeaveGraphJsonCommands.SaveJsonForGraph(graph);
            switch (result.Outcome)
            {
                case GraphJsonSyncOutcome.Saved:
                case GraphJsonSyncOutcome.Overwritten:
                    return GraphActionResult.Passed(
                        "JSON saved: " + graph.jsonPath + ".",
                        Array.Empty<Diagnostic>());
                case GraphJsonSyncOutcome.Reloaded:
                    return GraphActionResult.Passed(
                        "Graph reloaded from the JSON on disk.",
                        Array.Empty<Diagnostic>());
                case GraphJsonSyncOutcome.NoAction:
                    return GraphActionResult.Passed(
                        "Export JSON: nothing to write.",
                        Array.Empty<Diagnostic>());
                case GraphJsonSyncOutcome.Cancelled:
                    return GraphActionResult.Failed(
                        "Export JSON cancelled.",
                        Array.Empty<Diagnostic>());
                default:
                    return GraphActionResult.Failed(
                        "Export JSON did not complete: " +
                        (result.Check.ErrorMessage ??
                            result.Outcome.ToString()) + ".",
                        Array.Empty<Diagnostic>());
            }
        }

        /// <summary>
        /// The Import JSON action: re-runs the §H7.3 open check against
        /// the graph's linked JSON — a divergence prompts the same
        /// reload/overwrite choice the open path offers.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static GraphActionResult ImportJson(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (string.IsNullOrEmpty(graph.jsonPath))
            {
                return GraphActionResult.Failed(
                    "Import JSON: the graph has no linked JSON file.",
                    Array.Empty<Diagnostic>());
            }
            var result = GraphJsonSync.ResolveOnOpen(
                graph,
                GraphJsonSync.OpenChoiceProvider ??
                    GraphJsonSyncDialog.ShowConflictPrompt);
            switch (result.Outcome)
            {
                case GraphJsonSyncOutcome.Reloaded:
                    return GraphActionResult.Passed(
                        "Graph reloaded from the JSON on disk.",
                        Array.Empty<Diagnostic>());
                case GraphJsonSyncOutcome.Overwritten:
                    return GraphActionResult.Passed(
                        "JSON overwritten with the graph's content.",
                        Array.Empty<Diagnostic>());
                case GraphJsonSyncOutcome.NoAction:
                    return GraphActionResult.Passed(
                        "Import JSON: the JSON matches the graph.",
                        Array.Empty<Diagnostic>());
                case GraphJsonSyncOutcome.Cancelled:
                    return GraphActionResult.Failed(
                        "Import JSON cancelled.",
                        Array.Empty<Diagnostic>());
                default:
                    return GraphActionResult.Failed(
                        "Import JSON did not complete: " +
                        (result.Check.ErrorMessage ??
                            result.Outcome.ToString()) + ".",
                        Array.Empty<Diagnostic>());
            }
        }

        // ── Open Run Viewer ───────────────────────────────────────────

        /// <summary>The open-Run-Viewer action (SPEC §H8 item 2, §H5).</summary>
        internal static GraphActionResult OpenRunViewer()
        {
            OpenRunViewerCommand();
            return GraphActionResult.Passed(
                "Run Viewer opened.", Array.Empty<Diagnostic>());
        }

        // ── internals ─────────────────────────────────────────────────

        private static GraphActionResult RunnerNotReady(
            string action, IReadOnlyList<GraphNodeDiagnostic> diagnostics)
        {
            return GraphActionResult.Failed(
                action + " could not run: the OpWeave runner is not ready in this Editor.",
                Flatten(diagnostics));
        }

        // The §H8 diagnostics flatten into Core Diagnostics once they
        // leave the graph layer: a GraphNodeDiagnostic is a compiler
        // Diagnostic plus the node it maps to, so the mapping is a pure
        // projection — severity, code, message and node id carry through.
        private static List<Diagnostic> Flatten(
            IReadOnlyList<GraphNodeDiagnostic> diagnostics)
        {
            var flattened = new List<Diagnostic>(diagnostics.Count);
            foreach (var diagnostic in diagnostics)
            {
                flattened.Add(new Diagnostic(
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.Message,
                    diagnostic.NodeId));
            }
            return flattened;
        }

        private static int CountErrors(IReadOnlyList<GraphNodeDiagnostic> diagnostics)
        {
            var count = 0;
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == Severity.Error)
                {
                    count++;
                }
            }
            return count;
        }

        private static bool DefaultConfirmAskEffects(RunApprovalDetail detail)
        {
            if (detail is null)
            {
                throw new ArgumentNullException(nameof(detail));
            }
            if (Application.isBatchMode)
            {
                // No human can answer in a non-interactive Editor — decline
                // rather than block, the same fail-closed rule the
                // open-time JSON prompt applies (SPEC §0.6 SG-17).
                return false;
            }
            return GraphRunConfirmWindow.ShowModal(detail) ==
                GraphRunConfirmWindow.Choice.Run;
        }
    }
}
