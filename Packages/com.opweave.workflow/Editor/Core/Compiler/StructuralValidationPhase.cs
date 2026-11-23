#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Model;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The D2 structural phase (SPEC §D2): applies the §A3 schemaVersion read
    /// rule through <see cref="SchemaVersionCheck"/> (OW-WF-002), the §A4 ID
    /// rules, the model-level form of the §A3/§A5 reserved-key diagnostic,
    /// checks that every edge, binding and output reference names a declared
    /// node or WF input (OW-VAL-011), and reports a second identical
    /// <c>(from, fromPort, to)</c> control edge as OW-WF-004.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A document built programmatically — not through
    /// <see cref="WorkflowSerializer.Read"/> — reaches the validator without the A3
    /// token-level checks having run, so this phase re-applies the ones the model can
    /// still express: the §A7/§N.3 item 3 schemaVersion read rule shares
    /// <see cref="SchemaVersionCheck"/> with the serializer so code, severity,
    /// message, JsonPath and hint are identical in both paths, and the §A5
    /// reserved <c>"$"</c>-prefixed object key anywhere inside a parameter value
    /// is an OW-WF-006 error, matching the serializer. Parse, size and depth
    /// failures of §A3 have no model representation; the 200-node validator
    /// entry limit is OW-VAL-010 owned by D5 (SPEC §0.6 SG-13), and port
    /// existence belongs to D3/D4.
    /// </para>
    /// <para>
    /// Each OW-VAL-011 diagnostic names the node the defective reference concerns:
    /// the other endpoint when it is a declared node, otherwise the unresolved node
    /// id the member references; a WF-input reference that has no resolvable node
    /// gets none. The JsonPath always names the failing member
    /// (<c>$.dataEdges[i].to.node</c>).
    /// </para>
    /// </remarks>
    internal sealed class StructuralValidationPhase : IValidationPhase
    {
        public ValidationPhaseKind Kind => ValidationPhaseKind.D2;

        public void Run(ValidationContext context)
        {
            var document = context.Document;

            // SPEC §D2 applies the §A3 read diagnostics; a programmatic document
            // never passed the serializer, so the shared §A7/§N.3 schemaVersion
            // rule runs here too (OW-WF-002 at $.schemaVersion).
            var versionDiagnostic = SchemaVersionCheck.Check(document.SchemaVersion);
            if (versionDiagnostic is not null)
            {
                context.Report(versionDiagnostic);
            }

            // SPEC §A4: id grammar (OW-WF-005) and in-workflow uniqueness (OW-WF-004).
            foreach (var diagnostic in IdGrammar.Validate(document))
            {
                context.Report(diagnostic);
            }

            CheckReservedParameterKeys(context, document);
            CheckReferences(context, document);
            CheckDuplicateControlEdges(context, document);
        }

        // SPEC §A5 as applied by §A3 at read time: an object inside a parameter
        // value that carries a "$"-prefixed key — including the parameters object
        // itself — is reserved for future expressions and is an OW-WF-006 error.
        private static void CheckReservedParameterKeys(
            ValidationContext context, WorkflowDocument document)
        {
            var nodes = document.Nodes;
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var parametersPath = "$.nodes[" + i + "].parameters";
                foreach (var pair in node.Parameters)
                {
                    var memberPath = MemberPath(parametersPath, pair.Key);
                    if (pair.Key.StartsWith("$", StringComparison.Ordinal))
                    {
                        context.Report(ReservedKey(node.Id, memberPath, pair.Key));
                    }
                    CheckReservedKeys(context, pair.Value.ToToken(), memberPath, node.Id);
                }
            }
        }

        private static void CheckReservedKeys(
            ValidationContext context, JToken token, string path, string nodeId)
        {
            switch (token)
            {
                case JObject obj:
                    foreach (var property in obj.Properties())
                    {
                        var memberPath = MemberPath(path, property.Name);
                        if (property.Name.StartsWith("$", StringComparison.Ordinal))
                        {
                            context.Report(ReservedKey(nodeId, memberPath, property.Name));
                        }
                        CheckReservedKeys(context, property.Value, memberPath, nodeId);
                    }
                    break;
                case JArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        CheckReservedKeys(context, array[i], path + "[" + i + "]", nodeId);
                    }
                    break;
            }
        }

        private static Diagnostic ReservedKey(string nodeId, string jsonPath, string key) =>
            ErrorCodes.Create(
                ErrorCodes.OW_WF_006,
                "Member '" + key + "' uses the reserved \"$\" prefix.",
                nodeId: nodeId,
                jsonPath: jsonPath,
                hint: "Remove the \"$\"-prefixed key; expressions are not supported in this version.",
                severity: Severity.Error);

        // SPEC §D2: every edge, binding and output reference must name a declared
        // node or WF input (OW-VAL-011).
        private static void CheckReferences(ValidationContext context, WorkflowDocument document)
        {
            var inputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in document.Inputs)
            {
                inputIds.Add(input.Id);
            }

            var controlEdges = document.ControlEdges;
            for (var i = 0; i < controlEdges.Count; i++)
            {
                var edge = controlEdges[i];
                var path = $"$.controlEdges[{i}]";
                CheckNodeReference(context, edge.From, path + ".from", context.NodeOrder(edge.To) >= 0 ? edge.To : null);
                CheckNodeReference(context, edge.To, path + ".to", context.NodeOrder(edge.From) >= 0 ? edge.From : null);
            }

            var dataEdges = document.DataEdges;
            for (var i = 0; i < dataEdges.Count; i++)
            {
                var edge = dataEdges[i];
                var path = $"$.dataEdges[{i}]";
                CheckNodeReference(context, edge.From.Node, path + ".from.node", context.NodeOrder(edge.To.Node) >= 0 ? edge.To.Node : null);
                CheckNodeReference(context, edge.To.Node, path + ".to.node", context.NodeOrder(edge.From.Node) >= 0 ? edge.From.Node : null);
            }

            var bindings = document.InputBindings;
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                var path = $"$.inputBindings[{i}]";
                if (!inputIds.Contains(binding.FromInput))
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_VAL_011,
                        $"Reference to nonexistent workflow input '{binding.FromInput}'.",
                        nodeId: context.NodeOrder(binding.To.Node) >= 0 ? binding.To.Node : null,
                        jsonPath: path + ".fromInput",
                        hint: $"Declare an input with id '{binding.FromInput}' in $.inputs or fix the binding."));
                }
                // The other endpoint of this member is an input, not a node, so the
                // missing node id itself is the only node attribution available.
                CheckNodeReference(context, binding.To.Node, path + ".to.node", null);
            }

            var outputs = document.Outputs;
            for (var i = 0; i < outputs.Count; i++)
            {
                var output = outputs[i];
                CheckNodeReference(context, output.From.Node, $"$.outputs[{i}].from.node", null);
            }
        }

        private static void CheckNodeReference(
            ValidationContext context, string nodeId, string jsonPath, string? contextNode)
        {
            if (context.IsDeclaredNode(nodeId))
            {
                return;
            }
            context.Report(ErrorCodes.Create(
                ErrorCodes.OW_VAL_011,
                $"Reference to nonexistent node '{nodeId}'.",
                nodeId: contextNode ?? nodeId,
                jsonPath: jsonPath,
                hint: $"Declare a node with id '{nodeId}' in $.nodes or fix the reference."));
        }

        // SPEC §D2: two control edges with the same (from, fromPort, to) are a
        // duplicate reported as OW-WF-004 on the later one.
        private static void CheckDuplicateControlEdges(
            ValidationContext context, WorkflowDocument document)
        {
            var seen = new Dictionary<EdgeKey, int>();
            var edges = document.ControlEdges;
            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                var key = new EdgeKey(edge.From, edge.FromPort, edge.To);
                if (!seen.TryGetValue(key, out var first))
                {
                    seen.Add(key, i);
                    continue;
                }
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_WF_004,
                    $"Duplicate control edge from '{edge.From}' on port '{edge.FromPort}' to '{edge.To}' within the workflow.",
                    nodeId: context.NodeOrder(edge.From) >= 0 ? edge.From : edge.To,
                    jsonPath: $"$.controlEdges[{i}]",
                    hint: $"First declared at $.controlEdges[{first}]."));
            }
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            private readonly string _from;
            private readonly string _fromPort;
            private readonly string _to;

            public EdgeKey(string from, string fromPort, string to)
            {
                _from = from;
                _fromPort = fromPort;
                _to = to;
            }

            public bool Equals(EdgeKey other) =>
                string.Equals(_from, other._from, StringComparison.Ordinal) &&
                string.Equals(_fromPort, other._fromPort, StringComparison.Ordinal) &&
                string.Equals(_to, other._to, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is EdgeKey other && Equals(other);

            public override int GetHashCode() =>
                StringComparer.Ordinal.GetHashCode(_from) ^
                StringComparer.Ordinal.GetHashCode(_fromPort) ^
                StringComparer.Ordinal.GetHashCode(_to);
        }

        // JsonPath member steps: dotted form for plain identifiers, bracketed form
        // for names like "a.b" or "$expr" — the convention IdGrammar and the §0.6
        // SG-18 settings loader use ($.policy.agent["code.write"]). Shared with
        // the Resolve stage, which composes parameter and WF-input paths the
        // same way.
        internal static string MemberPath(string parent, string name)
        {
            if (IsSimpleName(name))
            {
                return parent + "." + name;
            }
            return parent + "[\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]";
        }

        private static bool IsSimpleName(string name)
        {
            if (name.Length == 0)
            {
                return false;
            }
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var ok = c == '_' ||
                    (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (i > 0 && c >= '0' && c <= '9');
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
