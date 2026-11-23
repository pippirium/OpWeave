#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Model;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The generic "node / control port / edge" graph SPEC §D5 builds from a
    /// document's <c>controlEdges</c> before any versioned rule set inspects it.
    /// The graph carries no v1 policy: control ports stay arbitrary strings and
    /// in/out-degrees stay unbounded, so the same structure serves the future
    /// branch and loop rule sets of appendix N — loosening
    /// <see cref="V1ControlFlowRules"/> never requires rebuilding this graph
    /// (SPEC §N.1, §N.4).
    /// </summary>
    internal sealed class ControlFlowGraph
    {
        /// <summary>
        /// One directed control-flow edge: it leaves <see cref="From"/> through the
        /// source node's <see cref="FromPort"/> control output and enters
        /// <see cref="To"/>. <see cref="DocumentIndex"/> is the edge's index in
        /// <see cref="WorkflowDocument.ControlEdges"/>, used for JsonPath
        /// attribution (<c>$.controlEdges[i]</c>).
        /// </summary>
        internal sealed class Edge
        {
            internal Edge(Node from, string fromPort, Node to, int documentIndex)
            {
                From = from;
                FromPort = fromPort;
                To = to;
                DocumentIndex = documentIndex;
            }

            public Node From { get; }
            public string FromPort { get; }
            public Node To { get; }
            public int DocumentIndex { get; }
        }

        /// <summary>
        /// One graph node over a declared <see cref="NodeModel"/>: its outgoing and
        /// incoming control edges, each kept in <c>controlEdges</c> document order.
        /// </summary>
        internal sealed class Node
        {
            internal Node(NodeModel model, int documentIndex)
            {
                Model = model;
                DocumentIndex = documentIndex;
                Outgoing = new List<Edge>();
                Incoming = new List<Edge>();
            }

            public NodeModel Model { get; }

            /// <summary>The node's index in <see cref="WorkflowDocument.Nodes"/>.</summary>
            public int DocumentIndex { get; }

            /// <summary>Edges leaving this node, in document order.</summary>
            public List<Edge> Outgoing { get; }

            /// <summary>Edges entering this node, in document order.</summary>
            public List<Edge> Incoming { get; }
        }

        private ControlFlowGraph(IReadOnlyList<Node> nodes)
        {
            Nodes = nodes;
        }

        /// <summary>Every declared document node, in document order.</summary>
        public IReadOnlyList<Node> Nodes { get; }

        /// <summary>
        /// Builds the graph from <paramref name="document"/>: one node per declared
        /// <see cref="NodeModel"/> and one edge per <c>controlEdges</c> entry.
        /// </summary>
        /// <remarks>
        /// An edge that names an undeclared node is skipped: reference integrity is
        /// the D2 phase's OW-VAL-011 finding (SPEC §D2), and duplicating it here
        /// would only attribute a second diagnostic to the same defect. Duplicate
        /// node ids are likewise the D2 phase's OW-WF-004 finding; the first
        /// declaration wins the edge endpoints, matching
        /// <see cref="ValidationContext.NodeOrder"/>.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
        public static ControlFlowGraph Build(WorkflowDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var models = document.Nodes;
            var nodes = new Node[models.Count];
            var byId = new Dictionary<string, Node>(StringComparer.Ordinal);
            for (var i = 0; i < models.Count; i++)
            {
                var node = new Node(models[i], i);
                nodes[i] = node;
                if (!byId.ContainsKey(models[i].Id))
                {
                    byId.Add(models[i].Id, node);
                }
            }

            var edges = document.ControlEdges;
            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                if (!byId.TryGetValue(edge.From, out var from) ||
                    !byId.TryGetValue(edge.To, out var to))
                {
                    continue;
                }
                var graphEdge = new Edge(from, edge.FromPort, to, i);
                from.Outgoing.Add(graphEdge);
                to.Incoming.Add(graphEdge);
            }

            return new ControlFlowGraph(Array.AsReadOnly(nodes));
        }
    }
}
