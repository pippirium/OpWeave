#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The single class holding every v1 control-flow constraint (SPEC §D5, §N.1):
    /// exactly one <c>core.start</c> with in-degree 0, control ports limited to
    /// <c>"next"</c>, in/out-degree at most 1, no cycle, no unreachable node,
    /// data suppliers placed before their consumers in the execution order, and
    /// the 200-node limit. Future branching and looping relax this class only;
    /// the generic <see cref="ControlFlowGraph"/> stays unchanged (SPEC §N.4).
    /// </summary>
    /// <remarks>
    /// <see cref="Validate"/> returns the DFS first-visit order from the single
    /// <c>core.start</c>; for a document that passes every rule this is the linear
    /// execution order the Build Plan stage consumes (SPEC §D5, §D7 item 1).
    /// <para>
    /// The §D1 item 2 exclusion rule applies to the diagnostics a node would
    /// receive, never to the topology: nodes excluded by earlier-phase errors stay
    /// in the graph and in the traversal so a mid-chain failure does not cascade
    /// false unreachable reports onto the healthy nodes downstream of it. The
    /// active-node set is sampled when the phase starts: exclusion gates findings
    /// between phases, while — like the D2 phase — this phase never suppresses a
    /// further true finding on a node it already flagged itself.
    /// </para>
    /// <para>
    /// v1 admits no Registry access: every Operation's descriptor is restricted to
    /// <c>ControlOutputs == ["next"]</c> at registration (SPEC §N.1, §C1, emitted
    /// as OW-OP-003), so the §D5 item 2 "fromPort in the source node's
    /// ControlOutputs" check is the fixed <see cref="NextControlPort"/> set here.
    /// </para>
    /// </remarks>
    internal sealed class V1ControlFlowRules
    {
        /// <summary>The §D5 item 6 node-count limit.</summary>
        internal const int MaxNodeCount = 200;

        /// <summary>The Operation id marking the workflow entry node (SPEC §A1, §D5).</summary>
        internal const string StartOperationId = "core.start";

        /// <summary>The only control output port v1 accepts (SPEC §N.1).</summary>
        internal const string NextControlPort = "next";

        /// <summary>
        /// Applies every v1 rule to <paramref name="graph"/>, reporting each finding
        /// to <paramref name="context"/>. Returns the traversal order from the
        /// single <c>core.start</c>, or null when the document does not contain
        /// exactly one start node.
        /// </summary>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public IReadOnlyList<ControlFlowGraph.Node>? Validate(
            ControlFlowGraph graph,
            ValidationContext context)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // SPEC §D1 item 2: a node an earlier phase already failed stays in the
            // topology but gets no findings here. The set is sampled once — this
            // phase's own findings never suppress each other, so a self-loop still
            // reports OW-VAL-003 next to the degree OW-VAL-004 it also violates.
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in context.ActiveNodes)
            {
                active.Add(node.Id);
            }

            CheckNodeCount(graph, context);
            var starts = CheckStartCount(graph, active, context);
            CheckControlPortsAndDegrees(graph, active, context);
            CheckStartEntry(starts, active, context);

            // SPEC §D5 item 4: only a unique start can anchor the walk; without it
            // the reachability, cycle and dominance checks have no order to work
            // against, and the OW-VAL-001 finding already carries the failure.
            var order = starts.Count == 1 ? Traverse(starts[0], active, context) : null;
            CheckDataEdgeSuppliers(order, active, context);
            CheckReachability(graph, order, active, context);
            return order;
        }

        // SPEC §D5 item 6: node count stays at or below the limit (OW-VAL-010 is
        // also the validator-side entry point of the §0.6 SG-13 bound).
        private static void CheckNodeCount(ControlFlowGraph graph, ValidationContext context)
        {
            var count = graph.Nodes.Count;
            if (count <= MaxNodeCount)
            {
                return;
            }
            context.Report(ErrorCodes.Create(
                ErrorCodes.OW_VAL_010,
                $"The workflow has {count} nodes; the limit is {MaxNodeCount}.",
                hint: $"Reduce the node count to {MaxNodeCount} or fewer."));
        }

        // SPEC §D5 item 1, first half: exactly one core.start. Extra starts are
        // attributed to the node to remove; zero starts is a document-level
        // finding. Returns every declared start in document order.
        private static IReadOnlyList<ControlFlowGraph.Node> CheckStartCount(
            ControlFlowGraph graph, ISet<string> active, ValidationContext context)
        {
            var starts = new List<ControlFlowGraph.Node>();
            foreach (var node in graph.Nodes)
            {
                if (string.Equals(node.Model.Operation, StartOperationId, StringComparison.Ordinal))
                {
                    starts.Add(node);
                }
            }

            if (starts.Count == 0)
            {
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_VAL_001,
                    "The workflow must contain exactly one 'core.start' node.",
                    hint: "Add a node with operation 'core.start'."));
            }
            else
            {
                for (var i = 1; i < starts.Count; i++)
                {
                    var extra = starts[i];
                    if (!active.Contains(extra.Model.Id))
                    {
                        continue;
                    }
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_001,
                        "The workflow contains more than one 'core.start' node.",
                        nodeId: extra.Model.Id,
                        jsonPath: $"$.nodes[{extra.DocumentIndex}].operation",
                        hint: "Remove this node or change its operation; exactly one start is allowed."));
                }
            }
            return starts;
        }

        // SPEC §D5 item 1, second half, and items 2-3: the single start takes no
        // incoming edge, every fromPort is a v1 control output ("next" only), and
        // every degree stays at most 1. Each excess edge gets its own diagnostic
        // so the Agent can pick the edge to remove.
        private static void CheckControlPortsAndDegrees(
            ControlFlowGraph graph, ISet<string> active, ValidationContext context)
        {
            foreach (var node in graph.Nodes)
            {
                if (!active.Contains(node.Model.Id))
                {
                    continue;
                }

                foreach (var edge in node.Outgoing)
                {
                    if (!string.Equals(edge.FromPort, NextControlPort, StringComparison.Ordinal))
                    {
                        context.Report(ErrorCodes.Create(
                            ErrorCodes.OW_VAL_004,
                            $"Control port '{edge.FromPort}' on node '{node.Model.Id}' is not supported in this version.",
                            nodeId: node.Model.Id,
                            port: edge.FromPort,
                            jsonPath: $"$.controlEdges[{edge.DocumentIndex}].fromPort",
                            hint: "Branching is not supported in this version; only the 'next' control port may be used."));
                    }
                }

                for (var i = 1; i < node.Outgoing.Count; i++)
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_004,
                        $"Node '{node.Model.Id}' has more than one outgoing control edge.",
                        nodeId: node.Model.Id,
                        jsonPath: $"$.controlEdges[{node.Outgoing[i].DocumentIndex}]",
                        hint: "Branching is not supported in this version; a node may have at most one outgoing control edge."));
                }

                for (var i = 1; i < node.Incoming.Count; i++)
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_004,
                        $"Node '{node.Model.Id}' has more than one incoming control edge.",
                        nodeId: node.Model.Id,
                        jsonPath: $"$.controlEdges[{node.Incoming[i].DocumentIndex}]",
                        hint: "Merging control flow is not supported in this version; a node may have at most one incoming control edge."));
                }
            }
        }

        // The start node's in-degree must be 0 (SPEC §D5 item 1). Each incoming
        // edge is reported; the excess also trips the degree rule above.
        private static void CheckStartEntry(
            IReadOnlyList<ControlFlowGraph.Node> starts,
            ISet<string> active,
            ValidationContext context)
        {
            if (starts.Count != 1 || !active.Contains(starts[0].Model.Id))
            {
                return;
            }
            var start = starts[0];
            foreach (var edge in start.Incoming)
            {
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_VAL_001,
                    $"The 'core.start' node '{start.Model.Id}' must not have incoming control edges.",
                    nodeId: start.Model.Id,
                    jsonPath: $"$.controlEdges[{edge.DocumentIndex}]",
                    hint: "Remove the control edge into the start node."));
            }
        }

        // SPEC §D5 item 4: walk from start; an edge back onto the current path
        // closes a cycle (OW-VAL-003). The iterative three-state DFS handles
        // arbitrary degree violations and document sizes without recursion.
        private static IReadOnlyList<ControlFlowGraph.Node> Traverse(
            ControlFlowGraph.Node start, ISet<string> active, ValidationContext context)
        {
            var order = new List<ControlFlowGraph.Node>();
            var onPath = new HashSet<ControlFlowGraph.Node>();
            var done = new HashSet<ControlFlowGraph.Node>();
            var stack = new Stack<Frame>();

            void Enter(ControlFlowGraph.Node node)
            {
                onPath.Add(node);
                order.Add(node);
                stack.Push(new Frame(node, 0));
            }

            Enter(start);
            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                var node = frame.Node;
                if (frame.Next >= node.Outgoing.Count)
                {
                    onPath.Remove(node);
                    done.Add(node);
                    continue;
                }

                stack.Push(new Frame(node, frame.Next + 1));
                var edge = node.Outgoing[frame.Next];
                var target = edge.To;
                if (onPath.Contains(target))
                {
                    if (active.Contains(node.Model.Id))
                    {
                        context.Report(ErrorCodes.Create(
                            ErrorCodes.OW_VAL_003,
                            $"Control edge from '{node.Model.Id}' to '{target.Model.Id}' closes a control-flow cycle.",
                            nodeId: node.Model.Id,
                            jsonPath: $"$.controlEdges[{edge.DocumentIndex}]",
                            hint: "Loops are not supported in this version; remove the edge returning to an already-visited node."));
                    }
                }
                else if (!done.Contains(target))
                {
                    Enter(target);
                }
            }
            return order;
        }

        // SPEC §D5 item 4, second half: a node the walk never reached is
        // unreachable (OW-VAL-002). Runs only when a unique start exists.
        private static void CheckReachability(
            ControlFlowGraph graph,
            IReadOnlyList<ControlFlowGraph.Node>? order,
            ISet<string> active,
            ValidationContext context)
        {
            if (order is null)
            {
                return;
            }

            var reached = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in order)
            {
                reached.Add(node.Model.Id);
            }

            foreach (var node in graph.Nodes)
            {
                if (reached.Contains(node.Model.Id) || !active.Contains(node.Model.Id))
                {
                    continue;
                }
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_VAL_002,
                    $"Node '{node.Model.Id}' is not reachable from 'core.start'.",
                    nodeId: node.Model.Id,
                    jsonPath: $"$.nodes[{node.DocumentIndex}]",
                    hint: "Connect the node to the control flow or remove it."));
            }
        }

        // SPEC §D5 item 5: a data-edge supplier must be a node guaranteed to run
        // before the consumer (the dominator rule), which in v1's linear flow is
        // "earlier in the execution order". A supplier with no position — it is
        // unreachable or the consumer itself — is never a dominator (OW-VAL-007).
        // Consumers without a position get no finding here: the edge is a
        // consequence of the unreachable node, which OW-VAL-002 already reports.
        private static void CheckDataEdgeSuppliers(
            IReadOnlyList<ControlFlowGraph.Node>? order,
            ISet<string> active,
            ValidationContext context)
        {
            if (order is null)
            {
                return;
            }

            var position = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < order.Count; i++)
            {
                var id = order[i].Model.Id;
                if (!position.ContainsKey(id))
                {
                    position.Add(id, i);
                }
            }

            var edges = context.Document.DataEdges;
            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                var supplier = edge.From.Node;
                var consumer = edge.To.Node;
                if (!context.IsDeclaredNode(supplier) || !context.IsDeclaredNode(consumer))
                {
                    continue; // dangling reference: OW-VAL-011 belongs to D2
                }
                if (!active.Contains(supplier) || !active.Contains(consumer))
                {
                    continue;
                }
                if (!position.TryGetValue(consumer, out var consumerPosition))
                {
                    continue;
                }
                if (!position.TryGetValue(supplier, out var supplierPosition) ||
                    supplierPosition >= consumerPosition)
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_007,
                        $"Data edge source node '{supplier}' is not guaranteed to execute before '{consumer}'.",
                        nodeId: consumer,
                        port: edge.To.Port,
                        jsonPath: $"$.dataEdges[{i}]",
                        hint: $"Place '{supplier}' earlier in the control flow than '{consumer}', or choose a different source."));
                }
            }
        }

        private readonly struct Frame
        {
            internal Frame(ControlFlowGraph.Node node, int next)
            {
                Node = node;
                Next = next;
            }

            internal ControlFlowGraph.Node Node { get; }
            internal int Next { get; }
        }
    }
}
