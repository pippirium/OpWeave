#nullable enable

using System;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using Unity.GraphToolkit.Editor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The SPEC §H8 item 3 execution display: while a graph-launched Run
    /// is tracked the display follows that Run's §E8 events and repaints
    /// each step's node with the colour of its current
    /// <see cref="StepState"/> through <see cref="INode.DefaultColor"/> —
    /// the public colour surface Graph Toolkit 6000.5 offers (the same
    /// property the T-W5.1-02 L-03 probe verified). A step event whose
    /// node id no graph node carries — the Run's Plan names a step the
    /// graph cannot display — cannot be coloured, so it degrades to the
    /// SPEC's GraphLogger-info fallback: the message waits in
    /// <see cref="OpWeaveGraph.pendingDisplayInfos"/> for the next
    /// processing pass and is recorded in
    /// <see cref="OpWeaveGraph.executionLog"/>, the windowless delivery
    /// record every state change leaves.
    /// </summary>
    /// <remarks>
    /// The subscription lives only for the tracked Run: the Run's
    /// terminal §E1 state unsubscribes, leaving the last painted colours
    /// as the Run's final display; a new <see cref="Attach"/> first
    /// restores the pre-Run colours a previous display recorded. Step
    /// events of other Runs are ignored — each graph follows the Run its
    /// own action accepted.
    /// </remarks>
    internal static class GraphExecutionDisplay
    {
        // The §E0 → colour mapping (a local, reversible choice — SPEC
        // requires only that each node's current state is displayed):
        // active work in amber/orange, terminal verdicts in green, red
        // and grey, a review request in purple.
        private static readonly Color s_running = new Color(0.95f, 0.72f, 0.20f);
        private static readonly Color s_waiting = new Color(0.95f, 0.50f, 0.15f);
        private static readonly Color s_succeeded = new Color(0.30f, 0.75f, 0.40f);
        private static readonly Color s_failed = new Color(0.85f, 0.25f, 0.25f);
        private static readonly Color s_cancelled = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color s_needsReview = new Color(0.65f, 0.40f, 0.80f);

        /// <summary>
        /// Attaches the display to <paramref name="runId"/> over
        /// <paramref name="events"/>: any prior subscription ends, the
        /// pre-Run colours the previous display recorded are restored —
        /// a new Run starts from the graph's own colours — and the §E8
        /// step/run channels are subscribed, so every journal state
        /// change paints live until the terminal state unsubscribes.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="graph"/> or <paramref name="events"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException"><paramref name="runId"/> is empty.</exception>
        internal static void Attach(OpWeaveGraph graph, IRunEvents events, string runId)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (events is null)
            {
                throw new ArgumentNullException(nameof(events));
            }
            if (string.IsNullOrEmpty(runId))
            {
                throw new ArgumentException(
                    "The Run id must not be null or empty.", nameof(runId));
            }
            Detach(graph);
            RestoreOriginalColors(graph);
            graph.executionRunId = runId;
            graph.executionEvents = events;
            Action<StepStateChanged> step = change => OnStepStateChanged(graph, change);
            Action<RunStateChanged> run = change => OnRunStateChanged(graph, change);
            graph.executionStepHandler = step;
            graph.executionRunHandler = run;
            events.StepStateChanged += step;
            events.RunStateChanged += run;
        }

        /// <summary>
        /// Ends the display's §E8 subscription — the last painted colours
        /// stay as the tracked Run's final display. Idempotent.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="graph"/> is null.</exception>
        internal static void Detach(OpWeaveGraph graph)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            var events = graph.executionEvents;
            if (events is not null)
            {
                if (graph.executionStepHandler is not null)
                {
                    events.StepStateChanged -= graph.executionStepHandler;
                }
                if (graph.executionRunHandler is not null)
                {
                    events.RunStateChanged -= graph.executionRunHandler;
                }
            }
            graph.executionEvents = null;
            graph.executionStepHandler = null;
            graph.executionRunHandler = null;
            graph.executionRunId = null;
        }

        /// <summary>The Run the display currently follows; null while none is attached.</summary>
        internal static string? RunIdOf(OpWeaveGraph graph) => graph.executionRunId;

        /// <summary>The display colour of one SPEC §E0 step state.</summary>
        internal static Color ColorFor(StepState state) => state switch
        {
            StepState.Running => s_running,
            StepState.Waiting => s_waiting,
            StepState.Succeeded => s_succeeded,
            StepState.Failed => s_failed,
            StepState.Cancelled => s_cancelled,
            StepState.Skipped => s_cancelled,
            StepState.NeedsReview => s_needsReview,
            // Pending — and any member the enum gains — keeps the node's
            // own colour rather than painting a misleading one.
            _ => new Color(0f, 0f, 0f, 0f),
        };

        private static void OnStepStateChanged(OpWeaveGraph graph, StepStateChanged change)
        {
            if (!string.Equals(change.RunId, graph.executionRunId, StringComparison.Ordinal))
            {
                return;
            }
            graph.executionLog.Add((change.NodeId, change.State, change.Attempt));
            var node = FindNode(graph, change.NodeId);
            if (node is null)
            {
                // No node to colour: the SPEC's GraphLogger-info fallback —
                // the message waits for the next processing pass.
                graph.pendingDisplayInfos.Add((
                    "Run " + change.RunId + ": node '" + change.NodeId +
                    "' reached state " + StateName(change.State) + ".",
                    change.NodeId));
                return;
            }
            try
            {
                if (!graph.executionOriginalColors.ContainsKey(node))
                {
                    graph.executionOriginalColors[node] = node.DefaultColor;
                }
                node.DefaultColor = ColorFor(change.State);
            }
            catch (Exception exception)
            {
                // A colour surface the toolkit refuses degrades to the
                // same GraphLogger-info fallback (SPEC §H8 item 3).
                Debug.LogException(exception);
                graph.pendingDisplayInfos.Add((
                    "Run " + change.RunId + ": node '" + change.NodeId +
                    "' reached state " + StateName(change.State) + ".",
                    change.NodeId));
            }
        }

        private static void OnRunStateChanged(OpWeaveGraph graph, RunStateChanged change)
        {
            if (!string.Equals(change.RunId, graph.executionRunId, StringComparison.Ordinal))
            {
                return;
            }
            if (change.State is RunState.Succeeded or RunState.Failed or RunState.Cancelled)
            {
                // Terminal: the last painted colours stay as the Run's
                // final display; the subscription ends so a later Run
                // starts clean through Attach.
                Detach(graph);
            }
        }

        private static void RestoreOriginalColors(OpWeaveGraph graph)
        {
            foreach (var pair in graph.executionOriginalColors)
            {
                if (pair.Key is not null)
                {
                    pair.Key.DefaultColor = pair.Value;
                }
            }
            graph.executionOriginalColors.Clear();
        }

        private static INode? FindNode(OpWeaveGraph graph, string nodeId)
        {
            foreach (var node in graph.GetNodes())
            {
                if (string.Equals(
                    GraphJsonConverter.NodeIdOf(node), nodeId, StringComparison.Ordinal))
                {
                    return node;
                }
            }
            return null;
        }

        private static string StateName(StepState state) => state.ToString();
    }
}
