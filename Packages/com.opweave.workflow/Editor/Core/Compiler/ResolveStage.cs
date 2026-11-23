#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The Resolve stage of the compiler pipeline (SPEC §N.5.5 item 4, §D0.2):
    /// runs after the document exists and before the Validate phases, and
    /// produces the <see cref="ResolvedWorkflow"/> that is the planHash
    /// workflow input by replacing every node parameter and workflow-input
    /// default with the value its declared type's Codec produces under the
    /// SPEC §B7.1 contract, plus the same conversion for the request's
    /// execution inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stage converts exactly the positions it can resolve — a node whose
    /// (operation, version) has no Descriptor, a parameter naming no input
    /// port, and a port whose type does not resolve in the type registry all
    /// keep the written tokens with no diagnostic: unknown Operations,
    /// unknown ports and unregistered types are the D3/D4 phases' findings
    /// (OW-OP-001, OW-OP-005, OW-TYPE-001), not this stage's. <c>core.Json</c>
    /// positions are never converted; their tokens enter
    /// <see cref="ResolvedWorkflow"/> as written (SPEC §D0.2).
    /// </para>
    /// <para>
    /// A Codec rejection or a SPEC §B7.1 normalization failure is reported as
    /// an OW-TYPE-003 diagnostic attributed to the node/port/JsonPath of the
    /// value, and no <see cref="ResolvedWorkflow"/> or resolved inputs are
    /// produced — every rejection in the document is still collected so the
    /// report carries as many findings as possible (SPEC §D1 item 2). The
    /// resolved object is built only on full success, because the planHash
    /// input must contain normalized values exclusively. The per-node
    /// parameter dictionaries are published either way
    /// (<see cref="ValidationContext.ResolvedParameters"/>): each rejection
    /// excludes only the node it names (SPEC §D1 item 2), so the D3 static
    /// check on unaffected nodes still observes their normalized values.
    /// </para>
    /// <para>
    /// Workflow-input defaults are normalized by the declared input type's
    /// Codec under the same rule that covers execution inputs (SPEC §D0.2):
    /// a typed value stored in <see cref="ResolvedWorkflow"/> must be the
    /// normalized one, so a default written as <c>1</c> and as <c>1.0</c> for
    /// a <c>core.Float</c> input resolves identically. Inputs not declared on
    /// the workflow keep their written tokens for the D4 phase to report.
    /// </para>
    /// </remarks>
    internal sealed class ResolveStage
    {
        private readonly IOperationSource? _operations;
        private readonly ITypeRegistry? _types;
        private readonly Dictionary<(string Id, int Version), OperationDescriptor?> _descriptors =
            new Dictionary<(string Id, int Version), OperationDescriptor?>();

        internal ResolveStage(IOperationSource? operations, ITypeRegistry? types)
        {
            _operations = operations;
            _types = types;
        }

        /// <summary>
        /// Runs the stage, reporting codec rejections to <paramref name="context"/>
        /// and publishing <see cref="ValidationContext.ResolvedParameters"/> plus —
        /// on full success — <see cref="ValidationContext.ResolvedWorkflow"/> and
        /// <see cref="ValidationContext.ResolvedInputs"/>. A stage with
        /// no operation source or type registry converts nothing: the snapshot-free
        /// pipeline stays inert until the registry work wires the real source.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        internal void Run(ValidationContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (_operations is null || _types is null)
            {
                return;
            }

            var document = context.Document;
            var resolved = CanonicalWorkflow.From(document).ToToken();
            var failed = false;

            var resolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, JsonValue>>(
                StringComparer.Ordinal);
            ResolveParameters(context, document, resolved, resolvedParameters, ref failed);
            ResolveInputDefaults(context, document, resolved, ref failed);
            var resolvedInputs = ResolveExecutionInputs(context, document, ref failed);

            // The per-node parameters are retained even on failure: the node
            // that failed conversion is excluded by its own Error (SPEC §D1
            // item 2), so the D3 static check on the remaining active nodes
            // still observes their normalized values. The complete resolved
            // object stays gated on full success — it is the planHash input
            // and must contain normalized values exclusively.
            context.ResolvedParameters = resolvedParameters;
            if (!failed)
            {
                context.ResolvedWorkflow = new ResolvedWorkflow(resolved, resolvedParameters);
                context.ResolvedInputs = resolvedInputs;
            }
        }

        // Node parameters: the canonical nodes array is sorted by id (stable
        // OrderBy, so duplicate ids keep document order), which aligns it
        // element-wise with the document's nodes under the same sort — that is
        // how a canonical node finds its declaration even when ids repeat.
        private void ResolveParameters(
            ValidationContext context,
            WorkflowDocument document,
            JObject resolved,
            Dictionary<string, IReadOnlyDictionary<string, JsonValue>> resolvedParameters,
            ref bool failed)
        {
            var canonicalNodes = (JArray)resolved["nodes"]!;
            var sorted = document.Nodes
                .Select((model, index) => (Model: model, Index: index))
                .OrderBy(pair => pair.Model.Id, StringComparer.Ordinal)
                .ToList();

            for (var i = 0; i < sorted.Count; i++)
            {
                var node = sorted[i].Model;
                var descriptor = FindDescriptor(node.Operation, node.Version);
                var parameters = (JObject)canonicalNodes[i]!["parameters"]!;
                var normalized = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                foreach (var pair in node.Parameters)
                {
                    var path = StructuralValidationPhase.MemberPath(
                        "$.nodes[" + sorted[i].Index + "].parameters", pair.Key);
                    var port = FindPort(descriptor, pair.Key);
                    var converted = port is null
                        ? pair.Value
                        : Convert(
                            context, pair.Value, port.Type, port.Nullable, port.Constraints,
                            path, node.Id, pair.Key, ref failed);
                    normalized.Add(pair.Key, converted);
                    parameters[pair.Key] = converted.ToToken();
                }
                if (!resolvedParameters.ContainsKey(node.Id))
                {
                    resolvedParameters.Add(node.Id, normalized);
                }
            }
        }

        // Declared WF-input defaults: same typed-value rule as parameters, keyed
        // by the declaration's type rather than a port.
        private void ResolveInputDefaults(
            ValidationContext context, WorkflowDocument document, JObject resolved, ref bool failed)
        {
            var canonicalInputs = (JArray)resolved["inputs"]!;
            var sorted = document.Inputs
                .Select((input, index) => (Input: input, Index: index))
                .OrderBy(pair => pair.Input.Id, StringComparer.Ordinal)
                .ToList();

            for (var i = 0; i < sorted.Count; i++)
            {
                var input = sorted[i].Input;
                if (input.Default is null)
                {
                    continue;
                }
                var converted = Convert(
                    context, input.Default, input.Type, false, null,
                    "$.inputs[" + sorted[i].Index + "].default", null, input.Id, ref failed);
                canonicalInputs[i]!["default"] = converted.ToToken();
            }
        }

        // Execution inputs: every member naming a declared WF input converts
        // with that input's declared Codec; undeclared members keep the written
        // tokens (the D4 phase reports them). A non-object payload cannot be
        // resolved member-wise and stays as written.
        private JsonValue? ResolveExecutionInputs(
            ValidationContext context, WorkflowDocument document, ref bool failed)
        {
            var inputs = context.Inputs;
            if (inputs is null)
            {
                return null;
            }
            var token = inputs.ToToken();
            if (token is not JObject payload)
            {
                return JsonValue.From(token);
            }

            var declared = new Dictionary<string, WorkflowInput>(StringComparer.Ordinal);
            foreach (var input in document.Inputs)
            {
                if (!declared.ContainsKey(input.Id))
                {
                    declared.Add(input.Id, input);
                }
            }

            foreach (var property in payload.Properties())
            {
                if (!declared.TryGetValue(property.Name, out var input))
                {
                    continue;
                }
                var converted = Convert(
                    context, JsonValue.From(property.Value), input.Type, false, null,
                    StructuralValidationPhase.MemberPath("$.inputs", property.Name),
                    null, input.Id, ref failed);
                property.Value = converted.ToToken();
            }
            return JsonValue.From(payload);
        }

        // The single conversion point: Validate then Normalize through the
        // SPEC §B7.1 guarded contract. Positions that cannot be resolved — an
        // unparsable or unregistered port type — keep the written value without
        // a diagnostic; codec rejections are reported and flag the failure.
        // The written value itself is validated without PortConstraints: a
        // constraint-violating value still has a well-defined typed normal
        // form, and written-value constraint violations are the D3/D4 phases'
        // OW-OP-006 findings (SPEC §J), not codec-level OW-TYPE-003 rejections —
        // flagging them at this stage would exclude the node before D3 can
        // emit OW-OP-006. Normalize instead runs under the port's constraints
        // whenever the written value satisfies them, so the SPEC §B7.1 item 4
        // revalidation rejects a Codec that moves an in-bounds value to an
        // out-of-bounds normal form (sanitized OW-TYPE-003, no ResolvedWorkflow).
        private JsonValue Convert(
            ValidationContext context,
            JsonValue value,
            string typeId,
            bool nullable,
            PortConstraints? constraints,
            string path,
            string? nodeId,
            string? port,
            ref bool failed)
        {
            var parsed = TypeRef.Parse(typeId);
            if (!parsed.Ok)
            {
                return value;
            }
            var type = parsed.Value!;
            if (ValueValidation.IsCoreJson(type))
            {
                return value;
            }
            if (!_types!.Resolve(type).Ok)
            {
                return value;
            }

            var diagnostics = ValueValidation.Validate(_types, type, value, null, nullable);
            if (diagnostics.Count != 0)
            {
                Report(context, diagnostics, nodeId, port, path);
                failed = true;
                return value;
            }

            // A written value that violates the port's constraints stays D3's
            // OW-OP-006 finding, so it is normalized constraint-free here.
            // Only a constraint-satisfying written value is normalized under
            // the constraints — that is the case whose normalized result the
            // SPEC §B7.1 revalidation must keep inside the same bounds.
            var writtenSatisfiesConstraints =
                constraints is null ||
                ValueValidation.Validate(_types, type, value, constraints, nullable).Count == 0;
            var normalized = ValueValidation.Normalize(
                _types, type, value, writtenSatisfiesConstraints ? constraints : null, nullable);
            if (!normalized.Ok)
            {
                Report(context, normalized.Diagnostics, nodeId, port, path);
                failed = true;
                return value;
            }
            return normalized.Value!;
        }

        private OperationDescriptor? FindDescriptor(string operation, int version)
        {
            var key = (operation, version);
            if (!_descriptors.TryGetValue(key, out var descriptor))
            {
                descriptor = _operations!.Find(operation, version);
                _descriptors.Add(key, descriptor);
            }
            return descriptor;
        }

        private static PortDescriptor? FindPort(OperationDescriptor? descriptor, string name)
        {
            if (descriptor is null)
            {
                return null;
            }
            var inputs = descriptor.Inputs;
            for (var i = 0; i < inputs.Count; i++)
            {
                if (string.Equals(inputs[i].Name, name, StringComparison.Ordinal))
                {
                    return inputs[i];
                }
            }
            return null;
        }

        // Attributes a codec-produced diagnostic to the value's document
        // position: nodeId, port and the composed JsonPath — the port path with
        // the diagnostic's own value-relative path appended (a codec "$.x"
        // becomes "$.nodes[i].parameters.p.x").
        private static void Report(
            ValidationContext context,
            IReadOnlyList<Diagnostic> diagnostics,
            string? nodeId,
            string? port,
            string path)
        {
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var diagnostic = diagnostics[i];
                var jsonPath =
                    diagnostic.JsonPath is not null &&
                    diagnostic.JsonPath.StartsWith("$", StringComparison.Ordinal)
                        ? path + diagnostic.JsonPath.Substring(1)
                        : path;
                context.Report(diagnostic with
                {
                    NodeId = diagnostic.NodeId ?? nodeId,
                    Port = diagnostic.Port ?? port,
                    JsonPath = jsonPath,
                });
            }
        }

    }
}
