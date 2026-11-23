#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Model
{
    /// <summary>
    /// The immutable C# model of a workflow document (SPEC §A1). Instances are produced
    /// only by <see cref="Create"/>, which defensively copies every collection into an
    /// array- or read-only-dictionary-backed wrapper and every JSON value into an
    /// immutable <see cref="JsonValue"/>; mutating the input afterwards cannot reach
    /// the document (SPEC §A1 item 1, §0.4). The sealed get-only shape has no public
    /// constructor and is not a record, so no <c>with</c>-expression can bypass
    /// Create's checks. Equality is decided by the §A4 canonical form
    /// (<see cref="Canonical"/>): two documents are equal exactly when their
    /// <see cref="CanonicalWorkflow.CanonicalText"/> values match (SPEC §A1 item 1).
    /// </summary>
    /// <remarks>
    /// <see cref="Create"/> performs only the minimal structural inspection: every
    /// member SPEC §A2/§A6/§A8 requires must be present. Missing structure fails with
    /// OW-WF-003 and an extension key without the <c>"x-"</c> prefix is an unknown
    /// property the model cannot hold, so Create still rejects it — the emitted
    /// OW-WF-006 keeps its catalog default Warning for ordinary keys and is an Error
    /// only for the reserved <c>"$"</c> prefix (SPEC §A3, §A5, §J). ID grammar and
    /// uniqueness, the R1 supply rule and every semantic rule are validated by later
    /// layers (SPEC §A4, §D), not here.
    /// </remarks>
    public sealed class WorkflowDocument : IEquatable<WorkflowDocument>
    {
        private const string ExtensionPrefix = "x-";

        private CanonicalWorkflow? _canonical;

        private WorkflowDocument(
            string schemaVersion,
            string id,
            string? title,
            string? description,
            IReadOnlyList<string> tags,
            IReadOnlyList<WorkflowInput> inputs,
            IReadOnlyList<WorkflowOutput> outputs,
            IReadOnlyList<NodeModel> nodes,
            IReadOnlyList<ControlEdge> controlEdges,
            IReadOnlyList<DataEdge> dataEdges,
            IReadOnlyList<InputBinding> inputBindings,
            LayoutInfo? layout,
            IReadOnlyDictionary<string, JsonValue> extensions)
        {
            SchemaVersion = schemaVersion;
            Id = id;
            Title = title;
            Description = description;
            Tags = tags;
            Inputs = inputs;
            Outputs = outputs;
            Nodes = nodes;
            ControlEdges = controlEdges;
            DataEdges = dataEdges;
            InputBindings = inputBindings;
            Layout = layout;
            Extensions = extensions;
        }

        /// <summary>
        /// Creates a document from <paramref name="data"/>: required structure is
        /// checked (OW-WF-003), extension keys without the <c>"x-"</c> prefix are
        /// rejected with OW-WF-006 (Warning for ordinary keys, Error for reserved
        /// <c>"$"</c> keys; SPEC §A3, §A5, §J), and every collection and
        /// <see cref="JToken"/> is defensively copied into immutable form.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        public static Result<WorkflowDocument> Create(WorkflowDocumentData data)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var diagnostics = new List<Diagnostic>();
            Require(diagnostics, data.SchemaVersion, "$.schemaVersion");
            Require(diagnostics, data.Id, "$.id");
            Require(diagnostics, data.Nodes, "$.nodes");
            Require(diagnostics, data.ControlEdges, "$.controlEdges");
            CheckNodes(diagnostics, data.Nodes);
            CheckControlEdges(diagnostics, data.ControlEdges);
            CheckTags(diagnostics, data.Tags);
            CheckInputs(diagnostics, data.Inputs);
            CheckOutputs(diagnostics, data.Outputs);
            CheckDataEdges(diagnostics, data.DataEdges);
            CheckInputBindings(diagnostics, data.InputBindings);
            CheckExtensions(diagnostics, data.Extensions);
            CheckLayout(diagnostics, data.Layout);
            if (diagnostics.Count != 0)
            {
                return Result<WorkflowDocument>.Failure(diagnostics);
            }

            var nodes = new List<NodeModel>(data.Nodes!.Count);
            foreach (var node in data.Nodes)
            {
                var parameters = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                if (node!.Parameters is not null)
                {
                    foreach (var pair in node.Parameters)
                    {
                        parameters.Add(pair.Key, JsonValue.From(pair.Value));
                    }
                }
                nodes.Add(new NodeModel(
                    node.Id!,
                    node.Operation!,
                    node.Version,
                    new ReadOnlyDictionary<string, JsonValue>(parameters),
                    node.Label,
                    node.Policy));
            }

            var extensions = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            if (data.Extensions is not null)
            {
                foreach (var pair in data.Extensions)
                {
                    extensions.Add(pair.Key, JsonValue.From(pair.Value));
                }
            }

            var document = new WorkflowDocument(
                data.SchemaVersion!,
                data.Id!,
                data.Title,
                data.Description,
                CopyOrEmpty(data.Tags),
                CopyOrEmpty(data.Inputs),
                CopyOrEmpty(data.Outputs),
                Array.AsReadOnly(nodes.ToArray()),
                ReadOnlyCollections.CopyList(data.ControlEdges!),
                CopyOrEmpty(data.DataEdges),
                CopyOrEmpty(data.InputBindings),
                data.Layout is null ? null : CopyLayout(data.Layout),
                new ReadOnlyDictionary<string, JsonValue>(extensions));
            return Result<WorkflowDocument>.Success(document);
        }

        public string SchemaVersion { get; }
        public string Id { get; }
        public string? Title { get; }
        public string? Description { get; }
        public IReadOnlyList<string> Tags { get; }
        public IReadOnlyList<WorkflowInput> Inputs { get; }
        public IReadOnlyList<WorkflowOutput> Outputs { get; }
        public IReadOnlyList<NodeModel> Nodes { get; }
        public IReadOnlyList<ControlEdge> ControlEdges { get; }
        public IReadOnlyList<DataEdge> DataEdges { get; }
        public IReadOnlyList<InputBinding> InputBindings { get; }
        public LayoutInfo? Layout { get; }

        /// <summary>The <c>x-*</c> extension properties, kept verbatim (SPEC §A2).</summary>
        public IReadOnlyDictionary<string, JsonValue> Extensions { get; }

        /// <summary>
        /// The §A4 canonical form of this document: layout, title, description, tags
        /// and <c>x-*</c> members are dropped, everything else is deterministically
        /// ordered and serialized with Canonical JSON (SPEC §A4, §0.6 SG-12).
        /// </summary>
        public CanonicalWorkflow Canonical =>
            _canonical ??= CanonicalWorkflow.From(this);

        /// <summary>
        /// Canonical equality (SPEC §A1 item 1): true exactly when both documents'
        /// canonical forms serialize to the same Canonical JSON text.
        /// </summary>
        public bool Equals(WorkflowDocument? other) =>
            other is not null &&
            string.Equals(
                Canonical.CanonicalText, other.Canonical.CanonicalText, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as WorkflowDocument);

        public override int GetHashCode() =>
            StringComparer.Ordinal.GetHashCode(Canonical.CanonicalText);

        private static void Require(List<Diagnostic> diagnostics, object? value, string path)
        {
            if (value is null)
            {
                diagnostics.Add(Missing(path));
            }
        }

        private static Diagnostic Missing(string path) =>
            ErrorCodes.Create(
                ErrorCodes.OW_WF_003,
                $"Missing required structure '{path}'.",
                jsonPath: path);

        private static void CheckNodes(List<Diagnostic> diagnostics, IList<NodeData>? nodes)
        {
            if (nodes is null)
            {
                return;
            }
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var path = $"$.nodes[{i}]";
                if (node is null)
                {
                    diagnostics.Add(Missing(path));
                    continue;
                }
                Require(diagnostics, node.Id, path + ".id");
                Require(diagnostics, node.Operation, path + ".operation");
                if (node.Parameters is not null)
                {
                    foreach (var pair in node.Parameters)
                    {
                        if (pair.Key is null)
                        {
                            diagnostics.Add(Missing(path + ".parameters"));
                        }
                        else if (pair.Value is null)
                        {
                            diagnostics.Add(Missing(path + ".parameters." + pair.Key));
                        }
                    }
                }
            }
        }

        private static void CheckControlEdges(List<Diagnostic> diagnostics, IList<ControlEdge>? edges)
        {
            if (edges is null)
            {
                return;
            }
            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                var path = $"$.controlEdges[{i}]";
                if (edge is null)
                {
                    diagnostics.Add(Missing(path));
                    continue;
                }
                Require(diagnostics, edge.From, path + ".from");
                Require(diagnostics, edge.To, path + ".to");
                Require(diagnostics, edge.FromPort, path + ".fromPort");
            }
        }

        private static void CheckTags(List<Diagnostic> diagnostics, IList<string>? tags)
        {
            if (tags is null)
            {
                return;
            }
            for (var i = 0; i < tags.Count; i++)
            {
                Require(diagnostics, tags[i], $"$.tags[{i}]");
            }
        }

        private static void CheckInputs(List<Diagnostic> diagnostics, IList<WorkflowInput>? inputs)
        {
            if (inputs is null)
            {
                return;
            }
            for (var i = 0; i < inputs.Count; i++)
            {
                var input = inputs[i];
                var path = $"$.inputs[{i}]";
                if (input is null)
                {
                    diagnostics.Add(Missing(path));
                    continue;
                }
                Require(diagnostics, input.Id, path + ".id");
                Require(diagnostics, input.Type, path + ".type");
            }
        }

        private static void CheckOutputs(List<Diagnostic> diagnostics, IList<WorkflowOutput>? outputs)
        {
            if (outputs is null)
            {
                return;
            }
            for (var i = 0; i < outputs.Count; i++)
            {
                var output = outputs[i];
                var path = $"$.outputs[{i}]";
                if (output is null)
                {
                    diagnostics.Add(Missing(path));
                    continue;
                }
                Require(diagnostics, output.Id, path + ".id");
                CheckPortRef(diagnostics, output.From, path + ".from");
            }
        }

        private static void CheckDataEdges(List<Diagnostic> diagnostics, IList<DataEdge>? edges)
        {
            if (edges is null)
            {
                return;
            }
            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                var path = $"$.dataEdges[{i}]";
                if (edge is null)
                {
                    diagnostics.Add(Missing(path));
                    continue;
                }
                CheckPortRef(diagnostics, edge.From, path + ".from");
                CheckPortRef(diagnostics, edge.To, path + ".to");
            }
        }

        private static void CheckInputBindings(List<Diagnostic> diagnostics, IList<InputBinding>? bindings)
        {
            if (bindings is null)
            {
                return;
            }
            for (var i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                var path = $"$.inputBindings[{i}]";
                if (binding is null)
                {
                    diagnostics.Add(Missing(path));
                    continue;
                }
                Require(diagnostics, binding.FromInput, path + ".fromInput");
                CheckPortRef(diagnostics, binding.To, path + ".to");
            }
        }

        private static void CheckPortRef(List<Diagnostic> diagnostics, PortRef? portRef, string path)
        {
            if (portRef is null)
            {
                diagnostics.Add(Missing(path));
                return;
            }
            Require(diagnostics, portRef.Node, path + ".node");
            Require(diagnostics, portRef.Port, path + ".port");
        }

        private static void CheckExtensions(
            List<Diagnostic> diagnostics, IDictionary<string, JToken>? extensions)
        {
            if (extensions is null)
            {
                return;
            }
            foreach (var pair in extensions)
            {
                if (pair.Key is null)
                {
                    diagnostics.Add(Missing("$"));
                    continue;
                }
                if (!pair.Key.StartsWith(ExtensionPrefix, StringComparison.Ordinal))
                {
                    // A document member without the "x-" prefix is an unknown property
                    // (OW-WF-006) the model cannot hold, so Create still rejects it.
                    // Ordinary keys keep the catalog default Warning; a "$"-prefixed
                    // key is reserved for future expressions and is an Error
                    // (SPEC §A3, §A5, §J).
                    var reserved = pair.Key.StartsWith("$", StringComparison.Ordinal);
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_WF_006,
                        reserved
                            ? $"Extension key '{pair.Key}' uses the reserved \"$\" prefix."
                            : $"Extension key '{pair.Key}' does not start with the \"x-\" prefix.",
                        jsonPath: "$." + pair.Key,
                        severity: reserved ? Severity.Error : (Severity?)null));
                    continue;
                }
                if (pair.Value is null)
                {
                    diagnostics.Add(Missing("$." + pair.Key));
                }
            }
        }

        private static void CheckLayout(List<Diagnostic> diagnostics, LayoutInfo? layout)
        {
            if (layout is null)
            {
                return;
            }
            if (layout.Nodes is null)
            {
                diagnostics.Add(Missing("$.layout.nodes"));
            }
            else
            {
                foreach (var pair in layout.Nodes)
                {
                    if (pair.Key is null)
                    {
                        diagnostics.Add(Missing("$.layout.nodes"));
                    }
                    else if (pair.Value is null)
                    {
                        diagnostics.Add(Missing("$.layout.nodes." + pair.Key));
                    }
                }
            }
            if (layout.Notes is null)
            {
                diagnostics.Add(Missing("$.layout.notes"));
            }
            else
            {
                for (var i = 0; i < layout.Notes.Count; i++)
                {
                    var note = layout.Notes[i];
                    var path = $"$.layout.notes[{i}]";
                    if (note is null)
                    {
                        diagnostics.Add(Missing(path));
                        continue;
                    }
                    Require(diagnostics, note.Id, path + ".id");
                    Require(diagnostics, note.Text, path + ".text");
                }
            }
        }

        private static IReadOnlyList<T> CopyOrEmpty<T>(IEnumerable<T>? source) =>
            source is null
                ? Array.AsReadOnly(Array.Empty<T>())
                : ReadOnlyCollections.CopyList(source);

        private static LayoutInfo CopyLayout(LayoutInfo layout) =>
            new LayoutInfo(
                ReadOnlyCollections.CopyMap(layout.Nodes),
                ReadOnlyCollections.CopyList(layout.Notes));
    }
}
