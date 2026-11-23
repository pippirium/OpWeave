#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Model
{
    /// <summary>
    /// The mutable input consumed by <see cref="WorkflowDocument.Create"/> (SPEC §A1).
    /// Members required by SPEC §A2 (<see cref="SchemaVersion"/>, <see cref="Id"/>,
    /// <see cref="Nodes"/>, <see cref="ControlEdges"/>) are nullable so their absence
    /// can be reported as OW-WF-003; every other member is optional and a null
    /// collection means "not present".
    /// </summary>
    /// <remarks>
    /// <see cref="Extensions"/> holds the document's <c>x-*</c> extension properties as
    /// boundary <see cref="JToken"/>s (SPEC §0.4). Create accepts only keys starting
    /// with the <c>"x-"</c> prefix and converts each value to an immutable
    /// <see cref="JsonValue"/>; mutating the dictionary or its tokens afterwards never
    /// reaches the document.
    /// </remarks>
    public sealed class WorkflowDocumentData
    {
        public string? SchemaVersion { get; set; }
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public IList<string>? Tags { get; set; }
        public IList<WorkflowInput>? Inputs { get; set; }
        public IList<WorkflowOutput>? Outputs { get; set; }
        public IList<NodeData>? Nodes { get; set; }
        public IList<ControlEdge>? ControlEdges { get; set; }
        public IList<DataEdge>? DataEdges { get; set; }
        public IList<InputBinding>? InputBindings { get; set; }
        public LayoutInfo? Layout { get; set; }
        public IDictionary<string, JToken>? Extensions { get; set; }
    }
}
