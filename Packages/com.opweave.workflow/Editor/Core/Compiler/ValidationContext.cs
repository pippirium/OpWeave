#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using OpWeave.Core.Model;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The mutable state one <see cref="IWorkflowCompiler.Validate"/> call shares with
    /// its phases (SPEC §D1): the request being checked, the diagnostics reported so
    /// far in emission order, and the set of nodes already excluded from later
    /// phases (SPEC §D1 item 2).
    /// </summary>
    /// <remarks>
    /// <see cref="Report"/> fills a missing <see cref="Diagnostic.NodeId"/> from a
    /// JsonPath that names a member of <c>$.nodes[i]</c>, so phase code that only
    /// knows the document location still gets the §D1 item 4 node attribution. An
    /// Error-severity diagnostic excludes the node it names — after enrichment —
    /// from <see cref="ActiveNodes"/>; warnings never exclude. Diagnostics naming a
    /// node that does not resolve to a document node (for example the missing target
    /// of a dangling edge) change no membership.
    /// </remarks>
    internal sealed class ValidationContext
    {
        private const string NodesPathPrefix = "$.nodes[";

        private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
        private readonly List<PlannedChange> _plannedChanges = new List<PlannedChange>();
        private readonly Dictionary<string, int> _nodeOrder;
        private readonly HashSet<string> _excluded = new HashSet<string>(StringComparer.Ordinal);
        private int _errorCount;

        public ValidationContext(ValidationRequest request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            _nodeOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            var nodes = Document.Nodes;
            for (var i = 0; i < nodes.Count; i++)
            {
                // Duplicate node ids are an OW-WF-004 finding of the D2 phase; the
                // first occurrence wins the document-order slot.
                if (!_nodeOrder.ContainsKey(nodes[i].Id))
                {
                    _nodeOrder.Add(nodes[i].Id, i);
                }
            }
        }

        /// <summary>The request being validated (SPEC §D1).</summary>
        public ValidationRequest Request { get; }

        /// <summary>The workflow document under validation.</summary>
        public WorkflowDocument Document => Request.Document;

        /// <summary>The optional runtime WF-input values of the request (SPEC §D1).</summary>
        public JsonValue? Inputs => Request.Inputs;

        /// <summary>The requested validation mode (SPEC §D1).</summary>
        public ValidationMode Mode => Request.Mode;

        /// <summary>The actor the permission rules apply to (SPEC §D1).</summary>
        public ActorKind Actor => Request.Actor;

        /// <summary>Every diagnostic reported so far, in emission order.</summary>
        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

        /// <summary>How many Error-severity diagnostics have been reported.</summary>
        public int ErrorCount => _errorCount;

        /// <summary>True once any Error-severity diagnostic has been reported.</summary>
        public bool HasErrors => _errorCount > 0;

        /// <summary>True when <paramref name="nodeId"/> is a node declared in the document.</summary>
        public bool IsDeclaredNode(string nodeId) =>
            nodeId is not null && _nodeOrder.ContainsKey(nodeId);

        /// <summary>
        /// The document-order index of <paramref name="nodeId"/>, or -1 when it is
        /// null or names no declared node (SPEC §D1 item 3 ordering input).
        /// </summary>
        public int NodeOrder(string? nodeId) =>
            nodeId is not null && _nodeOrder.TryGetValue(nodeId, out var index) ? index : -1;

        /// <summary>True when <paramref name="nodeId"/> is declared and not excluded.</summary>
        public bool IsNodeActive(string nodeId) =>
            IsDeclaredNode(nodeId) && !_excluded.Contains(nodeId);

        /// <summary>
        /// The execution order computed by the D5 graph phase (SPEC §D5): the
        /// document's nodes in first-visit order from the single <c>core.start</c>,
        /// which for a valid v1 document is the linear chain the Build Plan stage
        /// orders its Steps by (SPEC §D7 item 1). Null until the D5 phase runs, and
        /// left null whenever the control graph violates the v1 rules.
        /// </summary>
        public IReadOnlyList<NodeModel>? ExecutionOrder { get; internal set; }

        /// <summary>
        /// The codec-resolved canonical form produced by the Resolve stage
        /// (SPEC §D0.2): the workflow member of the planHash input the Build
        /// Plan stage consumes (SPEC §D7 item 5). Null until Resolve runs with
        /// an operation source and a type registry, and left null whenever a
        /// value failed codec conversion (OW-TYPE-003).
        /// </summary>
        public ResolvedWorkflow? ResolvedWorkflow { get; internal set; }

        /// <summary>
        /// The codec-resolved parameters of every document node, keyed by node
        /// id (SPEC §D0.2): the same per-node dictionaries a published
        /// <see cref="ResolvedWorkflow"/> exposes through
        /// <see cref="ResolvedWorkflow.ParametersFor"/>, retained even when a
        /// conversion failure keeps the complete resolved object from being
        /// produced. The node that failed conversion is excluded by its own
        /// Error diagnostic (SPEC §D1 item 2), so an entry a still-active node
        /// looks up carries the normalized values its D3 static check must
        /// observe. Null until Resolve runs with an operation source and a
        /// type registry.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonValue>>? ResolvedParameters
        { get; internal set; }

        /// <summary>
        /// The request's execution inputs after the Resolve stage normalized
        /// every member naming a declared WF input through that input's
        /// declared type Codec (SPEC §D0.2); undeclared members keep the
        /// written tokens. Null until Resolve produces it — it stays null on a
        /// conversion failure and when the request carries no inputs.
        /// </summary>
        public JsonValue? ResolvedInputs { get; internal set; }

        /// <summary>
        /// The supplier every input port resolved to under the §A1/R1 rule,
        /// keyed by node id then port name (SPEC §D4 item 6): the document-level
        /// form of the <see cref="InputSource"/> bindings the Build Plan stage
        /// writes on each <c>PlanStep</c> — an <see cref="PortSupplier.Edge"/>
        /// still names its source node because the step index only exists once
        /// the D5 execution order does. A port with two or more suppliers
        /// (OW-VAL-006) or on a node whose operation did not resolve has no
        /// entry; the map stays keyed on the first declaration of a duplicate
        /// node id. Null until the D4 phase runs with an operation source and a
        /// type registry.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, PortSupplier>>? PortSuppliers
        { get; internal set; }

        /// <summary>
        /// The output-port type of every checked node after
        /// <see cref="Operations.OperationDescriptor.OutputRefinement"/> (SPEC
        /// §D4 items 1 and 6), keyed by node id then port name — the typing the
        /// Build Plan stage writes on each <c>PlanStep</c>. Null until the D4
        /// phase runs with an operation source and a type registry.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypeRef>>? OutputTypes
        { get; internal set; }

        /// <summary>
        /// The actual §C4 effects the D6 phase resolved for every node it
        /// could reach — the <see cref="IEffectResolver"/> answer where the
        /// Operation declares one, the declared list otherwise — keyed by
        /// node id (SPEC §D6 item 1): the content the Build Plan stage writes
        /// on each <c>PlanStep</c>'s <c>Effects</c>. Null until the D6 phase
        /// runs.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Effects { get; internal set; }

        /// <summary>
        /// The (node, effect) pairs the D6 permission check raised to
        /// <see cref="PermissionLevel.Ask"/>, in node order (SPEC §D6 item 1):
        /// the content the Build Plan stage writes on the Plan's
        /// <c>approvals</c> (SPEC §D7, §H4 item 3 supplies each entry's detail).
        /// Null until the D6 phase runs.
        /// </summary>
        public IReadOnlyList<ApprovalRequest>? Approvals { get; internal set; }

        /// <summary>
        /// The <see cref="PlannedChange"/> list the D8 Dry Run phase collects in
        /// execution order (SPEC §D8 item 2), node id already stamped; the
        /// report publishes it for a <see cref="ValidationMode.DryRun"/> request.
        /// Empty until the phase runs — and stays so when it found no order to
        /// run or no predicted changes.
        /// </summary>
        public List<PlannedChange> PlannedChanges => _plannedChanges;

        /// <summary>
        /// The document's nodes that have not been excluded by an earlier Error, in
        /// document order (SPEC §D1 item 2). Later phases check only these.
        /// </summary>
        public IReadOnlyList<NodeModel> ActiveNodes
        {
            get
            {
                var nodes = Document.Nodes;
                if (_excluded.Count == 0)
                {
                    return nodes;
                }
                var active = new List<NodeModel>(nodes.Count - _excluded.Count);
                foreach (var node in nodes)
                {
                    if (!_excluded.Contains(node.Id))
                    {
                        active.Add(node);
                    }
                }
                return active;
            }
        }

        /// <summary>
        /// Records <paramref name="diagnostic"/>: fills a missing
        /// <see cref="Diagnostic.NodeId"/> from a <c>$.nodes[i]</c> JsonPath, keeps
        /// emission order, and — for Error severity — excludes the named document
        /// node from later phases (SPEC §D1 items 2 and 4).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is null.</exception>
        public void Report(Diagnostic diagnostic)
        {
            if (diagnostic is null)
            {
                throw new ArgumentNullException(nameof(diagnostic));
            }

            diagnostic = WithEnrichedNodeId(diagnostic);
            _diagnostics.Add(diagnostic);
            if (diagnostic.Severity == Severity.Error)
            {
                _errorCount++;
                if (diagnostic.NodeId is not null && IsDeclaredNode(diagnostic.NodeId))
                {
                    _excluded.Add(diagnostic.NodeId);
                }
            }
        }

        // SPEC §D1 item 4: populate nodeId wherever possible. A diagnostic that only
        // carries a JsonPath naming a member of "$.nodes[i]" is attributed to that
        // node.
        private Diagnostic WithEnrichedNodeId(Diagnostic diagnostic)
        {
            if (diagnostic.NodeId is not null)
            {
                return diagnostic;
            }
            var index = NodeIndexFromPath(diagnostic.JsonPath);
            var nodes = Document.Nodes;
            if (index < 0 || index >= nodes.Count)
            {
                return diagnostic;
            }
            return diagnostic with { NodeId = nodes[index].Id };
        }

        private static int NodeIndexFromPath(string? jsonPath)
        {
            if (jsonPath is null || !jsonPath.StartsWith(NodesPathPrefix, StringComparison.Ordinal))
            {
                return -1;
            }
            var start = NodesPathPrefix.Length;
            var end = start;
            while (end < jsonPath.Length && jsonPath[end] >= '0' && jsonPath[end] <= '9')
            {
                end++;
            }
            if (end == start || end >= jsonPath.Length || jsonPath[end] != ']')
            {
                return -1;
            }
            if (end + 1 < jsonPath.Length && jsonPath[end + 1] != '.' && jsonPath[end + 1] != '[')
            {
                return -1;
            }
            return int.TryParse(
                jsonPath.Substring(start, end - start),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index)
                ? index
                : -1;
        }
    }
}
