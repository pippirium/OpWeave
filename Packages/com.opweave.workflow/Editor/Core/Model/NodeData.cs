#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Model
{
    /// <summary>
    /// The mutable input side of one node for <see cref="WorkflowDocument.Create"/>.
    /// Required members are nullable so a serializer can represent their absence and
    /// let Create report OW-WF-003 instead of failing beforehand.
    /// </summary>
    /// <remarks>
    /// <see cref="Parameters"/> holds boundary <see cref="JToken"/>s keyed by input-port
    /// name (SPEC §0.4); Create converts each to an immutable <see cref="JsonValue"/>,
    /// cloning first, so mutating the dictionary or its tokens afterwards never reaches
    /// the document. A null dictionary means no parameters.
    /// </remarks>
    public sealed class NodeData
    {
        public string? Id { get; set; }
        public string? Operation { get; set; }
        public int Version { get; set; }
        public IDictionary<string, JToken>? Parameters { get; set; }
        public string? Label { get; set; }
        public NodePolicy? Policy { get; set; }
    }
}
