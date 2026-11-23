#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Model
{
    /// <summary>
    /// GUI layout and metadata: <c>{nodes: {"&lt;nodeId&gt;": {x, y}}, notes: [{id, x, y, text}]}</c>
    /// (SPEC §A8). It never affects execution, is not read by the Compiler and is
    /// excluded from planHash and the §A4 canonical form.
    /// </summary>
    /// <remarks>
    /// The collections are already read-only views; <see cref="WorkflowDocument.Create"/>
    /// still copies them so later mutation of the caller's containers cannot reach the
    /// document (SPEC §A1 item 1).
    /// </remarks>
    public sealed record LayoutInfo(
        IReadOnlyDictionary<string, LayoutNodePosition> Nodes,
        IReadOnlyList<LayoutNote> Notes);

    /// <summary>One node's canvas position <c>{x, y}</c> (SPEC §A8).</summary>
    public sealed record LayoutNodePosition(double X, double Y);

    /// <summary>One free-text note <c>{id, x, y, text}</c> (SPEC §A8).</summary>
    public sealed record LayoutNote(string Id, double X, double Y, string Text);
}
