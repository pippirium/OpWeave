#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The D4 type-and-supplier validation phase (SPEC §D4): checks that every
    /// data edge's output type — after <see cref="OperationDescriptor.OutputRefinement"/> —
    /// is §B6-assignable to the input port it feeds, enforces the §A1/R1
    /// at-most-one-explicit-supplier rule per input port, rejects ConstantOnly
    /// ports fed by edges or bindings, checks WF input type assignability and
    /// execution-input coverage, and records the resolved suppliers and output
    /// types for the Build Plan stage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The explicit suppliers of an input port are the data edges, the WF input
    /// bindings and the constant parameters naming it (SPEC §A1 item 2). Two or
    /// more are OW-VAL-006 — none is adopted, and the diagnostic's Hint
    /// enumerates every supplier JsonPath so the Agent can pick what to remove.
    /// With no explicit supplier the Descriptor default fills the port; a
    /// required port without one is OW-VAL-005 and an optional one is recorded
    /// as <see cref="PortSupplier.Absent"/> (SPEC §A0.1). A
    /// <see cref="PortConstraints.ConstantOnly"/> port fed by an edge or binding
    /// is OW-OP-006 — one diagnostic per offending supplier, each naming that
    /// supplier's member — and the diagnostic never echoes a value (SPEC §G8.1).
    /// </para>
    /// <para>
    /// Port existence on edges and bindings is reported as OW-OP-005 — the same
    /// code the D3 phase uses for unknown parameter keys (SPEC §J assigns it to
    /// D3 and D4). A data edge or binding whose endpoint node or operation is
    /// not resolvable, or whose endpoint nodes are excluded by earlier-phase
    /// errors, is skipped without a diagnostic: the dangling reference is the
    /// D2 phase's OW-VAL-011, the unresolvable Operation is D3's OW-OP-001/002,
    /// and the exclusion rule of SPEC §D1 item 2 removes the node from further
    /// checks.
    /// </para>
    /// <para>
    /// WF inputs follow SPEC §A6: a declared input's Type ID that does not parse
    /// or resolve is OW-TYPE-001 at <c>$.inputs[i].type</c> (the §J code for an
    /// unknown or malformed Type ID — a document-side check the registry-facing
    /// OW-OP-014 cannot reach); its bindings' assignability is then skipped.
    /// When the request carries execution inputs, a required input left
    /// unsupplied and undefaulted is OW-VAL-008; a member naming no declared
    /// input is ignored and reported once each as OW-VAL-014 at
    /// <c>$.inputs.&lt;id&gt;</c> (SPEC §A6). A null
    /// <see cref="ValidationContext.Inputs"/> means no execution inputs were
    /// given, so the coverage checks are skipped entirely: an inputless
    /// <c>validate_workflow</c> is a document check (SPEC §H1, §C8 test 1), not
    /// a claim that required inputs are satisfied. A non-object inputs payload
    /// counts as given-but-memberless — every required input is missing. Codec
    /// (B7) validation of supplied values is the Resolve stage's OW-TYPE-003,
    /// run against each input's declared type codec; D4 does not repeat it.
    /// </para>
    /// <para>
    /// <see cref="OperationDescriptor.OutputRefinement"/> narrows a source
    /// output's type by the node's constant
    /// <see cref="TypeRefinement.FromParameter"/>: the template's <c>{0}</c> hole
    /// is filled with the parameter's class-id form — the SPEC §B5 short Type
    /// ID when the string names a built-in class, the
    /// <c>type:&lt;parameter string&gt;</c> form for an arbitrary class
    /// (SPEC §B1) — so <c>"UnityEngine.Animator"</c> refines
    /// <c>unity.ComponentRef&lt;{0}&gt;</c> to
    /// <c>unity.ComponentRef&lt;unity.Animator&gt;</c> (SPEC §C1), which the
    /// snapshot judges without consulting
    /// <see cref="IClassRelationProvider"/>.
    /// A missing or non-string parameter, an unparsable or unresolvable refined
    /// Type ID, leaves the declared output type in place; a misdeclared port
    /// type is the registration-time OW-OP-003/OW-OP-014 finding a validated
    /// snapshot cannot carry, so no diagnostic is emitted for it. Both the
    /// substitution rule and the declared-type fallback are local, reversible
    /// choices.
    /// </para>
    /// </remarks>
    internal sealed class DataBindingValidationPhase : IValidationPhase
    {
        private readonly IOperationSource _operations;
        private readonly ITypeRegistry _types;
        private readonly Dictionary<(string Id, int Version), OperationDescriptor?> _descriptors =
            new Dictionary<(string Id, int Version), OperationDescriptor?>();

        internal DataBindingValidationPhase(IOperationSource operations, ITypeRegistry types)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _types = types ?? throw new ArgumentNullException(nameof(types));
        }

        public ValidationPhaseKind Kind => ValidationPhaseKind.D4;

        public void Run(ValidationContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            var document = context.Document;

            var nodesById = new Dictionary<string, NodeModel>(StringComparer.Ordinal);
            foreach (var node in document.Nodes)
            {
                // Duplicate node ids are the D2 phase's OW-WF-004 finding; the
                // first occurrence wins, matching ValidationContext.NodeOrder.
                if (!nodesById.ContainsKey(node.Id))
                {
                    nodesById.Add(node.Id, node);
                }
            }

            var edgesByTarget = new Dictionary<PortRef, List<int>>();
            var dataEdges = document.DataEdges;
            for (var i = 0; i < dataEdges.Count; i++)
            {
                AddTarget(edgesByTarget, dataEdges[i].To, i);
            }

            var bindingsByTarget = new Dictionary<PortRef, List<int>>();
            var inputBindings = document.InputBindings;
            for (var i = 0; i < inputBindings.Count; i++)
            {
                AddTarget(bindingsByTarget, inputBindings[i].To, i);
            }

            var inputIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var inputs = document.Inputs;
            for (var i = 0; i < inputs.Count; i++)
            {
                // Duplicate input ids are OW-WF-004 (D2); the first wins.
                if (!inputIndex.ContainsKey(inputs[i].Id))
                {
                    inputIndex.Add(inputs[i].Id, i);
                }
            }

            var suppliers = new Dictionary<string, IReadOnlyDictionary<string, PortSupplier>>(
                StringComparer.Ordinal);
            var outputTypes = new Dictionary<string, IReadOnlyDictionary<string, TypeRef>>(
                StringComparer.Ordinal);

            // SPEC §D1 item 2 gates findings between phases, not inside one:
            // the active set is sampled once — a node this phase flags itself
            // (say OW-VAL-005 for an unfed required port) keeps its other true
            // findings, matching the D5 phase's convention.
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in context.ActiveNodes)
            {
                active.Add(node.Id);
            }

            foreach (var node in context.ActiveNodes)
            {
                var descriptor = FindDescriptor(node.Operation, node.Version);
                if (descriptor is null)
                {
                    // Unresolvable (id, version) is the D3 phase's OW-OP-001/002.
                    continue;
                }
                if (!suppliers.ContainsKey(node.Id))
                {
                    suppliers.Add(
                        node.Id,
                        CheckSuppliers(
                            context, node, descriptor, dataEdges, inputBindings,
                            edgesByTarget, bindingsByTarget));
                }
                if (!outputTypes.ContainsKey(node.Id))
                {
                    outputTypes.Add(node.Id, RecordOutputTypes(node, descriptor));
                }
            }

            CheckDataEdges(context, nodesById, active);
            CheckInputBindings(context, nodesById, inputs, inputIndex, active);
            CheckWorkflowInputs(context, inputs, inputIndex);

            context.PortSuppliers = suppliers;
            context.OutputTypes = outputTypes;
        }

        private static void AddTarget(
            Dictionary<PortRef, List<int>> byTarget, PortRef to, int index)
        {
            if (!byTarget.TryGetValue(to, out var list))
            {
                list = new List<int>();
                byTarget.Add(to, list);
            }
            list.Add(index);
        }

        // SPEC §A1 item 2 / §D4 items 2-4: count the port's explicit suppliers —
        // data edges, WF input bindings, a written parameter — apply the
        // ConstantOnly rule, and resolve the port to its single supplier, the
        // Descriptor default or Missing. Returns the per-port resolution map.
        private IReadOnlyDictionary<string, PortSupplier> CheckSuppliers(
            ValidationContext context,
            NodeModel node,
            OperationDescriptor descriptor,
            IReadOnlyList<DataEdge> dataEdges,
            IReadOnlyList<InputBinding> inputBindings,
            Dictionary<PortRef, List<int>> edgesByTarget,
            Dictionary<PortRef, List<int>> bindingsByTarget)
        {
            var nodeIndex = context.NodeOrder(node.Id);
            var resolved = new Dictionary<string, PortSupplier>(StringComparer.Ordinal);
            foreach (var port in descriptor.Inputs)
            {
                var target = new PortRef(node.Id, port.Name);
                edgesByTarget.TryGetValue(target, out var edgeIndexes);
                bindingsByTarget.TryGetValue(target, out var bindingIndexes);
                var hasParameter = node.Parameters.ContainsKey(port.Name);
                var count =
                    (edgeIndexes?.Count ?? 0) + (bindingIndexes?.Count ?? 0) +
                    (hasParameter ? 1 : 0);

                // SPEC §D4 item 4: a ConstantOnly port admits no edge or
                // binding — one OW-OP-006 per offending supplier, each naming
                // that supplier's member and never echoing a value (§G8.1).
                if (port.Constraints?.ConstantOnly == true)
                {
                    if (edgeIndexes is not null)
                    {
                        foreach (var index in edgeIndexes)
                        {
                            context.Report(ConstantOnlyViolation(
                                node, port, $"$.dataEdges[{index}]"));
                        }
                    }
                    if (bindingIndexes is not null)
                    {
                        foreach (var index in bindingIndexes)
                        {
                            context.Report(ConstantOnlyViolation(
                                node, port, $"$.inputBindings[{index}]"));
                        }
                    }
                }

                if (count >= 2)
                {
                    // OW-VAL-006: enumerate every supplier JsonPath in the
                    // SPEC's order — edges, bindings, parameter — so the Agent
                    // can pick what to remove; no supplier is adopted (§A1).
                    var paths = new List<string>(count);
                    if (edgeIndexes is not null)
                    {
                        foreach (var index in edgeIndexes)
                        {
                            paths.Add($"$.dataEdges[{index}]");
                        }
                    }
                    if (bindingIndexes is not null)
                    {
                        foreach (var index in bindingIndexes)
                        {
                            paths.Add($"$.inputBindings[{index}]");
                        }
                    }
                    if (hasParameter)
                    {
                        paths.Add(StructuralValidationPhase.MemberPath(
                            $"$.nodes[{nodeIndex}].parameters", port.Name));
                    }
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_006,
                        $"Input port '{port.Name}' has {count} explicit suppliers; at most one is allowed.",
                        nodeId: node.Id,
                        port: port.Name,
                        jsonPath: paths[0],
                        hint: "Remove all but one supplier: " + string.Join(", ", paths) + "."));
                    continue;
                }

                if (count == 1)
                {
                    if (edgeIndexes is not null)
                    {
                        var edge = dataEdges[edgeIndexes[0]];
                        resolved[port.Name] = new PortSupplier.Edge(edge.From.Node, edge.From.Port);
                    }
                    else if (bindingIndexes is not null)
                    {
                        resolved[port.Name] = new PortSupplier.Input(
                            inputBindings[bindingIndexes[0]].FromInput);
                    }
                    else
                    {
                        resolved[port.Name] = new PortSupplier.Parameter(
                            ResolvedValue(context, node, port.Name));
                    }
                    continue;
                }

                if (port.Default is JsonValue @default)
                {
                    resolved[port.Name] = new PortSupplier.Default(@default);
                }
                else if (port.Required)
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_005,
                        $"Required input port '{port.Name}' has no supplier.",
                        nodeId: node.Id,
                        port: port.Name,
                        jsonPath: $"$.nodes[{nodeIndex}]",
                        hint: "Supply the port with a data edge, a workflow input binding or a constant parameter."));
                }
                else
                {
                    resolved[port.Name] = new PortSupplier.Absent();
                }
            }
            return resolved;
        }

        private static Diagnostic ConstantOnlyViolation(
            NodeModel node, PortDescriptor port, string jsonPath) =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                $"Input port '{port.Name}' of operation '{node.Operation}' is ConstantOnly and must not be supplied by a data edge or workflow input binding.",
                nodeId: node.Id,
                port: port.Name,
                jsonPath: jsonPath,
                hint: "Remove the supplier; the port accepts only a constant parameter value.");

        // The recorded value of a parameter supplier: the codec-normalized
        // value when the Resolve stage produced one, else the written token —
        // the same value the D3 static check observes (SPEC §D0.2).
        private static JsonValue ResolvedValue(
            ValidationContext context, NodeModel node, string port)
        {
            if (context.ResolvedParameters is not null &&
                context.ResolvedParameters.TryGetValue(node.Id, out var parameters) &&
                parameters.TryGetValue(port, out var resolved))
            {
                return resolved;
            }
            return node.Parameters[port];
        }

        // SPEC §D4 item 6: record each output port's type after
        // OutputRefinement — the Plan's output typing input.
        private IReadOnlyDictionary<string, TypeRef> RecordOutputTypes(
            NodeModel node, OperationDescriptor descriptor)
        {
            var resolved = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            foreach (var port in descriptor.Outputs)
            {
                var type = OutputType(node, descriptor, port);
                if (type is not null && !resolved.ContainsKey(port.Name))
                {
                    resolved.Add(port.Name, type);
                }
            }
            return resolved;
        }

        // The declared output type after OutputRefinement: the template's {0}
        // hole is filled with the class-id form of the node's constant
        // FromParameter — the §B5 built-in short Type ID for a class name in
        // the §B5 built-in table (SPEC §C1: "UnityEngine.Animator" →
        // unity.Animator; the UnityEditor classes are covered too), the
        // "type:<parameter string>" form of §B1 for an arbitrary class — or
        // the declared type when the parameter is absent, non-string or
        // refines to an unparsable Type ID.
        private TypeRef? OutputType(
            NodeModel node, OperationDescriptor descriptor, PortDescriptor port)
        {
            var type = ParseType(port.Type);
            var refinement = descriptor.OutputRefinement;
            if (refinement is null ||
                !string.Equals(refinement.OutputPort, port.Name, StringComparison.Ordinal))
            {
                return type;
            }
            if (!node.Parameters.TryGetValue(refinement.FromParameter, out var parameter) ||
                parameter.ToToken() is not JValue { Type: JTokenType.String } text)
            {
                return type;
            }
            var className = (string)text.Value!;
            var classId = UnityShapeTypes.BuiltInClassId(className) ??
                TypeRef.ClassRefPrefix + className;
            var refinedId = refinement.Template.Replace("{0}", classId);
            var refined = TypeRef.Parse(refinedId);
            return refined.Ok ? refined.Value : type;
        }

        // SPEC §D4 item 1: each data edge's output type — after
        // OutputRefinement — must be §B6-assignable to the input it feeds
        // (OW-TYPE-002). Port existence on either end is OW-OP-005.
        private void CheckDataEdges(
            ValidationContext context,
            IReadOnlyDictionary<string, NodeModel> nodesById,
            ISet<string> active)
        {
            var edges = context.Document.DataEdges;
            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                var path = $"$.dataEdges[{i}]";

                // Excluded nodes are removed from further checks (SPEC §D1
                // item 2, matching the D5 data-supplier rule); dangling node
                // references are the D2 phase's OW-VAL-011.
                if (!active.Contains(edge.From.Node) || !active.Contains(edge.To.Node))
                {
                    continue;
                }
                var fromDescriptor = FindDescriptorFor(edge.From.Node, nodesById);
                var toDescriptor = FindDescriptorFor(edge.To.Node, nodesById);
                if (fromDescriptor is null || toDescriptor is null)
                {
                    continue;
                }

                var outPort = FindPort(fromDescriptor.Outputs, edge.From.Port);
                var inPort = FindPort(toDescriptor.Inputs, edge.To.Port);
                if (outPort is null)
                {
                    context.Report(UnknownPort(
                        nodesById[edge.From.Node], edge.From.Port, "output",
                        path + ".from.port"));
                }
                if (inPort is null)
                {
                    context.Report(UnknownPort(
                        nodesById[edge.To.Node], edge.To.Port, "input",
                        path + ".to.port"));
                }
                if (outPort is null || inPort is null)
                {
                    continue;
                }

                var source = OutputType(nodesById[edge.From.Node], fromDescriptor, outPort);
                var target = ParseType(inPort.Type);
                if (source is null || target is null)
                {
                    continue;
                }
                var diagnostic = TypeAssignability.CreateNotAssignableDiagnostic(
                    _types, source, target,
                    nodeId: edge.To.Node, port: edge.To.Port, jsonPath: path);
                if (diagnostic is not null)
                {
                    context.Report(diagnostic);
                }
            }
        }

        // SPEC §D4 item 5: a WF input's declared type must be assignable to
        // every port it binds (OW-TYPE-002); the bound port must exist
        // (OW-OP-005).
        private void CheckInputBindings(
            ValidationContext context,
            IReadOnlyDictionary<string, NodeModel> nodesById,
            IReadOnlyList<WorkflowInput> inputs,
            IReadOnlyDictionary<string, int> inputIndex,
            ISet<string> active)
        {
            var bindings = context.Document.InputBindings;
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                var path = $"$.inputBindings[{i}]";
                if (!active.Contains(binding.To.Node))
                {
                    continue;
                }
                var descriptor = FindDescriptorFor(binding.To.Node, nodesById);
                if (descriptor is null)
                {
                    continue;
                }
                var inPort = FindPort(descriptor.Inputs, binding.To.Port);
                if (inPort is null)
                {
                    context.Report(UnknownPort(
                        nodesById[binding.To.Node], binding.To.Port, "input",
                        path + ".to.port"));
                    continue;
                }
                if (!inputIndex.TryGetValue(binding.FromInput, out var index))
                {
                    // A binding naming an undeclared input is the D2 phase's
                    // OW-VAL-011 finding.
                    continue;
                }
                var source = InputType(context, inputs[index], index);
                var target = ParseType(inPort.Type);
                if (source is null || target is null)
                {
                    continue;
                }
                var diagnostic = TypeAssignability.CreateNotAssignableDiagnostic(
                    _types, source, target,
                    nodeId: binding.To.Node, port: binding.To.Port, jsonPath: path);
                if (diagnostic is not null)
                {
                    context.Report(diagnostic);
                }
            }
        }

        // The declared WF input type: a Type ID that does not parse or does
        // not resolve is OW-TYPE-001 at $.inputs[i].type — the §J code for an
        // unknown or malformed type, applied to a document-side declaration
        // the registration-time OW-OP-014 cannot reach.
        private TypeRef? InputType(
            ValidationContext context, WorkflowInput input, int index)
        {
            var path = $"$.inputs[{index}].type";
            var parsed = TypeRef.Parse(input.Type);
            if (!parsed.Ok)
            {
                ReportAll(context, parsed.Diagnostics, path);
                return null;
            }
            var resolved = _types.Resolve(parsed.Value!);
            if (!resolved.Ok)
            {
                ReportAll(context, resolved.Diagnostics, path);
                return null;
            }
            return parsed.Value;
        }

        // SPEC §A6 + §D4 item 5: given execution inputs, a required input left
        // unsupplied and undefaulted is OW-VAL-008, and each payload member
        // naming no declared input is ignored with one OW-VAL-014 warning per
        // member at $.inputs.<id>. A null Inputs means no execution inputs
        // were given — the coverage of required inputs is then unknown and
        // nothing is checked.
        private void CheckWorkflowInputs(
            ValidationContext context,
            IReadOnlyList<WorkflowInput> inputs,
            IReadOnlyDictionary<string, int> inputIndex)
        {
            if (context.Inputs is null)
            {
                return;
            }

            // A non-object payload is given-but-memberless: every required
            // input is missing; the malformed request itself is the API
            // layer's OW-API-001, not a document finding.
            var payload = context.Inputs.ToToken() as JObject;
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                if (input.Required &&
                    input.Default is null &&
                    payload?[input.Id] is null)
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_008,
                        $"Required workflow input '{input.Id}' is missing: no value was supplied and the declaration has no default.",
                        jsonPath: $"$.inputs[{i}]",
                        hint: $"Pass a value for '{input.Id}' in the execution inputs or give the declaration a default."));
                }
            }

            if (payload is null)
            {
                return;
            }
            foreach (var member in payload.Properties())
            {
                if (inputIndex.ContainsKey(member.Name))
                {
                    continue;
                }
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_VAL_014,
                    $"Execution input '{member.Name}' is not declared by the workflow and is ignored.",
                    jsonPath: StructuralValidationPhase.MemberPath("$.inputs", member.Name),
                    hint: "Remove the member from the execution inputs or declare a workflow input with that id."));
            }
        }

        private OperationDescriptor? FindDescriptor(string operation, int version)
        {
            var key = (operation, version);
            if (!_descriptors.TryGetValue(key, out var descriptor))
            {
                descriptor = _operations.Find(operation, version);
                _descriptors.Add(key, descriptor);
            }
            return descriptor;
        }

        private OperationDescriptor? FindDescriptorFor(
            string nodeId, IReadOnlyDictionary<string, NodeModel> nodesById) =>
            nodesById.TryGetValue(nodeId, out var node)
                ? FindDescriptor(node.Operation, node.Version)
                : null;

        private static PortDescriptor? FindPort(
            IReadOnlyList<PortDescriptor> ports, string name)
        {
            for (var i = 0; i < ports.Count; i++)
            {
                if (string.Equals(ports[i].Name, name, StringComparison.Ordinal))
                {
                    return ports[i];
                }
            }
            return null;
        }

        private static Diagnostic UnknownPort(
            NodeModel node, string port, string direction, string jsonPath) =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_005,
                $"The {direction} port '{port}' does not exist on operation '{node.Operation}' version {node.Version}.",
                nodeId: node.Id,
                port: port,
                jsonPath: jsonPath);

        // A port or declared-input type that does not parse is a malformed
        // Type ID; on descriptor ports that is the registration-time
        // OW-OP-003/OW-OP-014 finding a validated snapshot cannot carry, so it
        // stays silent here — the same judgment OperationValidationPhase makes.
        private static TypeRef? ParseType(string typeId)
        {
            var parsed = TypeRef.Parse(typeId);
            return parsed.Ok ? parsed.Value : null;
        }

        private static void ReportAll(
            ValidationContext context, IReadOnlyList<Diagnostic> diagnostics, string path)
        {
            for (var i = 0; i < diagnostics.Count; i++)
            {
                context.Report(diagnostics[i] with { JsonPath = path });
            }
        }
    }
}
