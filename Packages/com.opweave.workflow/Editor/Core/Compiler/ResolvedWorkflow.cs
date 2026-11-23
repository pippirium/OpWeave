#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The codec-resolved canonical form of a workflow (SPEC §D0.2): the
    /// <see cref="Model.CanonicalWorkflow"/> object with every node parameter
    /// and workflow-input default replaced by the value its declared type's
    /// Codec produced through the SPEC §B7.1 normalization contract —
    /// <c>core.Json</c> positions keep the written tokens. This is the
    /// "Workflow canonical form" slot of the planHash input; it is never a
    /// document identity (<see cref="Model.CanonicalWorkflow.WorkflowHash"/>
    /// keeps that role).
    /// </summary>
    /// <remarks>
    /// <see cref="CanonicalText"/> is the Canonical JSON v1 form of the
    /// resolved object (SPEC §0.3): the byte sequence planHash hashes as the
    /// workflow member of its input (SPEC §D7 item 5).
    /// <see cref="ParametersFor"/> exposes the same normalized values per node
    /// so the Build Plan stage can place them into <c>Constant</c> input
    /// sources without normalizing twice.
    /// </remarks>
    internal sealed class ResolvedWorkflow
    {
        private readonly JObject _object;
        private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonValue>> _parameters;

        internal ResolvedWorkflow(
            JObject resolvedObject,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonValue>> parameters)
        {
            _object = resolvedObject;
            _parameters = parameters;
            CanonicalText = CanonicalJson.Serialize(resolvedObject);
        }

        /// <summary>The Canonical JSON v1 form of the resolved object (SPEC §0.3, §D0.2).</summary>
        public string CanonicalText { get; }

        /// <summary>A fresh clone of the resolved object; mutating it changes nothing.</summary>
        internal JObject ToToken() => (JObject)_object.DeepClone();

        /// <summary>
        /// The codec-normalized parameters of the node <paramref name="nodeId"/>,
        /// or null when the node is not declared (duplicate node ids resolve to
        /// the first declaration, matching <see cref="ValidationContext"/>).
        /// </summary>
        internal IReadOnlyDictionary<string, JsonValue>? ParametersFor(string nodeId) =>
            _parameters.TryGetValue(nodeId, out var parameters) ? parameters : null;
    }
}
