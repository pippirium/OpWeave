#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using OpWeave.Core.Model;

namespace OpWeave.Core
{
    /// <summary>
    /// The ID grammar and in-workflow uniqueness rules of SPEC §A4: WF id
    /// (<c>^[a-z0-9]+([._-][a-z0-9]+)*$</c>, at most 128 characters), the shared scoped
    /// grammar for node/input/output ids and port/control-port names
    /// (<c>^[A-Za-z][A-Za-z0-9_]{0,63}$</c>), and the Operation id grammar
    /// (<c>^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$</c>, at most 128 characters),
    /// together with the reserved namespace prefixes <c>core.</c>, <c>unity.</c>
    /// and <c>opweave.</c> that custom Operations must not use (OW-OP-007,
    /// SPEC §C3).
    /// </summary>
    /// <remarks>
    /// <see cref="Validate"/> applies the §A4 rules to a parsed
    /// <see cref="WorkflowDocument"/>: every id and port/control-port name in the
    /// document is format-checked (OW-WF-005) and the union of node, input and output
    /// ids — the single "WF 内" uniqueness scope of the §A4 table — is checked for
    /// duplicates (OW-WF-004). Each diagnostic carries the JsonPath of the offending
    /// member. Reference targets (a <c>fromInput</c> that names no declared input, an
    /// edge to a missing node, duplicate control edges) and the project-wide WF id
    /// uniqueness warning are semantic checks owned by later layers (SPEC §D2,
    /// §A4 table row 1); they are not reported here.
    /// </remarks>
    public static class IdGrammar
    {
        /// <summary>Maximum length of a workflow id in UTF-16 code units (SPEC §A4).</summary>
        public const int WorkflowIdMaxLength = 128;

        /// <summary>
        /// Maximum length of an Operation id in UTF-16 code units (SPEC §A4,
        /// the 2026-11-16 bound on every standard or custom Operation id).
        /// </summary>
        public const int OperationIdMaxLength = 128;

        private const string WorkflowIdPattern = @"\A[a-z0-9]+([._-][a-z0-9]+)*\z";
        private const string ScopedIdPattern = @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z";
        private const string OperationIdPattern = @"\A[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+\z";

        private static readonly Regex s_workflowId =
            new Regex(WorkflowIdPattern, RegexOptions.CultureInvariant);
        private static readonly Regex s_scopedId =
            new Regex(ScopedIdPattern, RegexOptions.CultureInvariant);
        private static readonly Regex s_operationId =
            new Regex(OperationIdPattern, RegexOptions.CultureInvariant);

        private static readonly string[] s_reservedPrefixes = { "core.", "unity.", "opweave." };

        /// <summary>
        /// True when <paramref name="id"/> satisfies the WF id grammar of SPEC §A4:
        /// <c>^[a-z0-9]+([._-][a-z0-9]+)*$</c> and at most
        /// <see cref="WorkflowIdMaxLength"/> characters.
        /// </summary>
        public static bool IsValidWorkflowId(string? id) =>
            id is not null && id.Length <= WorkflowIdMaxLength && s_workflowId.IsMatch(id);

        /// <summary>
        /// True when <paramref name="id"/> satisfies the scoped id grammar of SPEC §A4
        /// shared by node ids, WF input/output ids, port names and control-port names:
        /// <c>^[A-Za-z][A-Za-z0-9_]{0,63}$</c>.
        /// </summary>
        public static bool IsValidScopedId(string? id) =>
            id is not null && s_scopedId.IsMatch(id);

        /// <summary>
        /// True when <paramref name="id"/> satisfies the Operation id grammar of
        /// SPEC §A4: <c>^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$</c> (at least one
        /// dot) and at most <see cref="OperationIdMaxLength"/> characters.
        /// </summary>
        public static bool IsValidOperationId(string? id) =>
            id is not null && id.Length <= OperationIdMaxLength && s_operationId.IsMatch(id);

        /// <summary>
        /// True when <paramref name="id"/> begins with a reserved namespace prefix —
        /// <c>core.</c>, <c>unity.</c> or <c>opweave.</c> — which custom Operations must
        /// not use (OW-OP-007, SPEC §A4, §C3).
        /// </summary>
        public static bool HasReservedOperationPrefix(string? id)
        {
            if (id is null)
            {
                return false;
            }
            foreach (var prefix in s_reservedPrefixes)
            {
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Applies the §A4 rules to <paramref name="document"/>: every id and
        /// port/control-port name is format-checked (OW-WF-005) and the union of node,
        /// input and output ids is checked for duplicates inside the workflow
        /// (OW-WF-004). Returns an empty list when the document's ids are clean.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
        public static IReadOnlyList<Diagnostic> Validate(WorkflowDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var diagnostics = new List<Diagnostic>();
            if (!IsValidWorkflowId(document.Id))
            {
                diagnostics.Add(FormatError(
                    document.Id, "$.id", "workflow id",
                    $"Expected {WorkflowIdPattern}, at most {WorkflowIdMaxLength} characters."));
            }

            // The §A4 table gives node, input and output ids one shared uniqueness
            // scope "within the WF": the first declaration in this deterministic scan
            // order (nodes, inputs, outputs) wins and every later occurrence is
            // OW-WF-004. Ids that fail the grammar are flagged but do not enter the
            // set, so a repeated malformed id reports OW-WF-005 only.
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);

            var nodes = document.Nodes;
            for (var i = 0; i < nodes.Count; i++)
            {
                CheckUniqueScopedId(diagnostics, seen, nodes[i].Id, $"$.nodes[{i}].id", "node id");
            }

            var inputs = document.Inputs;
            for (var i = 0; i < inputs.Count; i++)
            {
                CheckUniqueScopedId(diagnostics, seen, inputs[i].Id, $"$.inputs[{i}].id", "input id");
            }

            var outputs = document.Outputs;
            for (var i = 0; i < outputs.Count; i++)
            {
                CheckUniqueScopedId(diagnostics, seen, outputs[i].Id, $"$.outputs[{i}].id", "output id");
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var path = $"$.nodes[{i}]";
                if (!IsValidOperationId(node.Operation))
                {
                    diagnostics.Add(FormatError(
                        node.Operation, path + ".operation", "Operation id",
                        $"Expected {OperationIdPattern}, at most {OperationIdMaxLength} characters."));
                }
                foreach (var key in node.Parameters.Keys)
                {
                    if (!IsValidScopedId(key))
                    {
                        diagnostics.Add(FormatError(
                            key, MemberPath(path + ".parameters", key), "port name",
                            "Expected " + ScopedIdPattern + "."));
                    }
                }
            }

            var controlEdges = document.ControlEdges;
            for (var i = 0; i < controlEdges.Count; i++)
            {
                var edge = controlEdges[i];
                var path = $"$.controlEdges[{i}]";
                CheckFormat(diagnostics, edge.From, path + ".from", "node id");
                CheckFormat(diagnostics, edge.To, path + ".to", "node id");
                CheckFormat(diagnostics, edge.FromPort, path + ".fromPort", "control port name");
            }

            var dataEdges = document.DataEdges;
            for (var i = 0; i < dataEdges.Count; i++)
            {
                var edge = dataEdges[i];
                var path = $"$.dataEdges[{i}]";
                CheckFormat(diagnostics, edge.From.Node, path + ".from.node", "node id");
                CheckFormat(diagnostics, edge.From.Port, path + ".from.port", "port name");
                CheckFormat(diagnostics, edge.To.Node, path + ".to.node", "node id");
                CheckFormat(diagnostics, edge.To.Port, path + ".to.port", "port name");
            }

            var bindings = document.InputBindings;
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                var path = $"$.inputBindings[{i}]";
                CheckFormat(diagnostics, binding.FromInput, path + ".fromInput", "input id");
                CheckFormat(diagnostics, binding.To.Node, path + ".to.node", "node id");
                CheckFormat(diagnostics, binding.To.Port, path + ".to.port", "port name");
            }

            for (var i = 0; i < outputs.Count; i++)
            {
                var output = outputs[i];
                var path = $"$.outputs[{i}]";
                CheckFormat(diagnostics, output.From.Node, path + ".from.node", "node id");
                CheckFormat(diagnostics, output.From.Port, path + ".from.port", "port name");
            }

            return diagnostics;
        }

        private static void CheckUniqueScopedId(
            List<Diagnostic> diagnostics,
            Dictionary<string, string> seen,
            string id,
            string path,
            string kind)
        {
            if (!IsValidScopedId(id))
            {
                diagnostics.Add(FormatError(id, path, kind, "Expected " + ScopedIdPattern + "."));
                return;
            }
            if (seen.TryGetValue(id, out var firstPath))
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_WF_004,
                    $"Duplicate {kind} '{id}' within the workflow.",
                    jsonPath: path,
                    hint: $"First declared at {firstPath}."));
                return;
            }
            seen.Add(id, path);
        }

        private static void CheckFormat(
            List<Diagnostic> diagnostics,
            string id,
            string path,
            string kind)
        {
            if (!IsValidScopedId(id))
            {
                diagnostics.Add(FormatError(id, path, kind, "Expected " + ScopedIdPattern + "."));
            }
        }

        private static Diagnostic FormatError(string id, string path, string kind, string hint) =>
            ErrorCodes.Create(
                ErrorCodes.OW_WF_005,
                $"Malformed {kind} '{id}'.",
                jsonPath: path,
                hint: hint);

        // JsonPath member steps use dotted form for plain identifiers and the
        // bracketed form for names like "a.b" — the same convention the settings
        // loader uses for dotted names (SPEC §0.6 SG-18:
        // $.policy.agent["code.write"]).
        private static string MemberPath(string parent, string name)
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
