#nullable enable

using System.Collections.Generic;
using OpWeave.Core.Model;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The D5 graph phase (SPEC §D5): builds the generic
    /// <see cref="ControlFlowGraph"/> from the document's control edges, applies
    /// <see cref="V1ControlFlowRules"/> to it, and — when the graph passes — records
    /// the resulting execution order on <see cref="ValidationContext.ExecutionOrder"/>
    /// for the Build Plan stage (SPEC §D7 item 1).
    /// </summary>
    internal sealed class GraphValidationPhase : IValidationPhase
    {
        public ValidationPhaseKind Kind => ValidationPhaseKind.D5;

        public void Run(ValidationContext context)
        {
            var errorsBefore = context.ErrorCount;
            var graph = ControlFlowGraph.Build(context.Document);
            var order = new V1ControlFlowRules().Validate(graph, context);
            if (order is not null && context.ErrorCount == errorsBefore)
            {
                var nodes = new List<NodeModel>(order.Count);
                foreach (var node in order)
                {
                    nodes.Add(node.Model);
                }
                context.ExecutionOrder = ReadOnlyCollections.CopyList(nodes);
            }
        }
    }
}
