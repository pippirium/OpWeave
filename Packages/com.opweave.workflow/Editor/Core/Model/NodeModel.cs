#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Model
{
    /// <summary>
    /// One node of a <see cref="WorkflowDocument"/>: <c>{id, operation, version,
    /// parameters?, label?, policy?}</c> (SPEC §A1, §A2). Sealed with get-only
    /// properties and created only inside <see cref="WorkflowDocument.Create"/>, so no
    /// <c>with</c>-expression or public constructor can bypass Create's checks.
    /// </summary>
    /// <remarks>
    /// <see cref="Parameters"/> keys are input-port names; every value is an immutable
    /// <see cref="JsonValue"/>. An explicit JSON null is a real <see cref="JsonValue"/>
    /// whose <see cref="JsonValue.CanonicalText"/> is <c>"null"</c>; a port without a
    /// parameter simply has no key — never a <c>null</c> value (SPEC §0.1 F-07, §A0.1).
    /// </remarks>
    public sealed class NodeModel
    {
        internal NodeModel(
            string id,
            string operation,
            int version,
            IReadOnlyDictionary<string, JsonValue> parameters,
            string? label,
            NodePolicy? policy)
        {
            Id = id;
            Operation = operation;
            Version = version;
            Parameters = parameters;
            Label = label;
            Policy = policy;
        }

        public string Id { get; }
        public string Operation { get; }
        public int Version { get; }

        /// <summary>Constant parameters keyed by input-port name (SPEC §A5).</summary>
        public IReadOnlyDictionary<string, JsonValue> Parameters { get; }
        public string? Label { get; }
        public NodePolicy? Policy { get; }
    }
}
