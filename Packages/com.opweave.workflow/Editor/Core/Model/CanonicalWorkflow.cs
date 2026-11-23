#nullable enable

using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Model
{
    /// <summary>
    /// The canonical form of a <see cref="WorkflowDocument"/> (SPEC §A4, §D0.2,
    /// §0.6 SG-12): the document restructured without <c>layout</c>, <c>title</c>,
    /// <c>description</c>, <c>tags</c> and <c>x-*</c> extensions, with nodes, inputs
    /// and outputs each ordered by id and edges and bindings ordered by their fixed
    /// sort keys — control edges by (from, fromPort, to), data edges by
    /// (from.node, from.port, to.node, to.port), input bindings by
    /// (fromInput, to.node, to.port), all in Ordinal ascending order — serialized with
    /// the single Canonical JSON implementation (SPEC §0.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §A4/SG-12 exclusion drops the members it names: document
    /// <c>layout</c>/<c>title</c>/<c>description</c>/<c>tags</c>/<c>x-*</c> and,
    /// carrying the same excluded member name, the optional <c>description</c>
    /// member of §A6 inputs and outputs. Node <c>label</c> is a §A1 NodeModel
    /// field that neither §A4 nor SG-12 excludes, so it stays. Everything the
    /// exclusion keeps — input <c>id</c>/<c>type</c>/<c>required</c>/<c>default</c>,
    /// output <c>id</c>/<c>from</c>, node <c>id</c>/<c>operation</c>/<c>version</c>/
    /// <c>parameters</c>/<c>label</c>/<c>policy</c> — is semantic content that stays.
    /// </para>
    /// <para>
    /// The canonical object uses the §A1 member names in camelCase: the eight
    /// top-level members <c>schemaVersion</c>, <c>id</c>, <c>inputs</c>,
    /// <c>outputs</c>, <c>nodes</c>, <c>controlEdges</c>, <c>dataEdges</c> and
    /// <c>inputBindings</c> are always emitted so an omitted optional collection is
    /// identical to an explicit empty one. <c>parameters</c> is always emitted on
    /// each node (an omitted object is an empty one) and the omitted
    /// <c>fromPort</c> default <c>"next"</c> is always explicit. Members that carry
    /// no documented default — node <c>label</c>/<c>policy</c> and input
    /// <c>default</c> — are emitted only when present; explicit JSON null values
    /// kept by the model stay <c>null</c> in the canonical form.
    /// </para>
    /// <para>
    /// <see cref="CanonicalText"/> is the document identity used by equality and GUI
    /// ⇔ JSON sync; <see cref="WorkflowHash"/> is its SHA-256 (lowercase hex). Per
    /// SPEC §A4/§D0.2 the WorkflowHash is never a planHash input: planHash is
    /// computed from the Codec-resolved <c>ResolvedWorkflow</c> instead.
    /// </para>
    /// </remarks>
    public sealed class CanonicalWorkflow
    {
        private readonly JObject _object;

        private CanonicalWorkflow(JObject canonicalObject, string canonicalText, string workflowHash)
        {
            _object = canonicalObject;
            CanonicalText = canonicalText;
            WorkflowHash = workflowHash;
        }

        /// <summary>
        /// Builds the canonical form of <paramref name="document"/> (SPEC §A4, §0.6
        /// SG-12). Parameter values keep the form they were written in — the
        /// Codec-resolved replacement used for planHash is
        /// <c>ResolvedWorkflow</c>'s job (SPEC §D0.2), not this type's.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
        public static CanonicalWorkflow From(WorkflowDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }
            var canonicalObject = Build(document);
            return new CanonicalWorkflow(
                canonicalObject,
                CanonicalJson.Serialize(canonicalObject),
                CanonicalJson.Sha256(canonicalObject));
        }

        /// <summary>The Canonical JSON v1 form of the canonical object (SPEC §0.3, §A4).</summary>
        public string CanonicalText { get; }

        /// <summary>
        /// The workflow hash: lowercase-hex SHA-256 of <see cref="CanonicalText"/>'s
        /// UTF-8 bytes (SPEC §A4). Document identity and GUI sync only — never a
        /// planHash input (SPEC §D0.2).
        /// </summary>
        public string WorkflowHash { get; }

        /// <summary>A fresh clone of the canonical object; mutating it changes nothing.</summary>
        public JObject ToToken() => (JObject)_object.DeepClone();

        private static JObject Build(WorkflowDocument document)
        {
            var root = new JObject();
            root["schemaVersion"] = document.SchemaVersion;
            root["id"] = document.Id;
            root["inputs"] = BuildInputs(document.Inputs);
            root["outputs"] = BuildOutputs(document.Outputs);
            root["nodes"] = BuildNodes(document.Nodes);
            root["controlEdges"] = BuildControlEdges(document.ControlEdges);
            root["dataEdges"] = BuildDataEdges(document.DataEdges);
            root["inputBindings"] = BuildInputBindings(document.InputBindings);
            return root;
        }

        private static JArray BuildInputs(System.Collections.Generic.IReadOnlyList<WorkflowInput> inputs)
        {
            var array = new JArray();
            foreach (var input in inputs.OrderBy(i => i.Id, StringComparer.Ordinal))
            {
                var item = new JObject();
                item["id"] = input.Id;
                item["type"] = input.Type;
                item["required"] = input.Required;
                if (input.Default is not null)
                {
                    item["default"] = input.Default.ToToken();
                }
                array.Add(item);
            }
            return array;
        }

        private static JArray BuildOutputs(System.Collections.Generic.IReadOnlyList<WorkflowOutput> outputs)
        {
            var array = new JArray();
            foreach (var output in outputs.OrderBy(o => o.Id, StringComparer.Ordinal))
            {
                var item = new JObject();
                item["id"] = output.Id;
                item["from"] = PortRefToJson(output.From);
                array.Add(item);
            }
            return array;
        }

        private static JArray BuildNodes(System.Collections.Generic.IReadOnlyList<NodeModel> nodes)
        {
            var array = new JArray();
            foreach (var node in nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
            {
                var item = new JObject();
                item["id"] = node.Id;
                item["operation"] = node.Operation;
                item["version"] = node.Version;
                var parameters = new JObject();
                foreach (var pair in node.Parameters)
                {
                    parameters[pair.Key] = pair.Value.ToToken();
                }
                item["parameters"] = parameters;
                if (node.Label is not null)
                {
                    item["label"] = node.Label;
                }
                if (node.Policy is not null)
                {
                    var policy = new JObject();
                    if (node.Policy.TimeoutSeconds is not null)
                    {
                        policy["timeoutSeconds"] = node.Policy.TimeoutSeconds;
                    }
                    if (node.Policy.MaxAttempts is not null)
                    {
                        policy["maxAttempts"] = node.Policy.MaxAttempts;
                    }
                    item["policy"] = policy;
                }
                array.Add(item);
            }
            return array;
        }

        private static JArray BuildControlEdges(System.Collections.Generic.IReadOnlyList<ControlEdge> edges)
        {
            var array = new JArray();
            foreach (var edge in edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.FromPort, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal))
            {
                var item = new JObject();
                item["from"] = edge.From;
                item["fromPort"] = edge.FromPort;
                item["to"] = edge.To;
                array.Add(item);
            }
            return array;
        }

        private static JArray BuildDataEdges(System.Collections.Generic.IReadOnlyList<DataEdge> edges)
        {
            var array = new JArray();
            foreach (var edge in edges
                .OrderBy(e => e.From.Node, StringComparer.Ordinal)
                .ThenBy(e => e.From.Port, StringComparer.Ordinal)
                .ThenBy(e => e.To.Node, StringComparer.Ordinal)
                .ThenBy(e => e.To.Port, StringComparer.Ordinal))
            {
                var item = new JObject();
                item["from"] = PortRefToJson(edge.From);
                item["to"] = PortRefToJson(edge.To);
                array.Add(item);
            }
            return array;
        }

        private static JArray BuildInputBindings(System.Collections.Generic.IReadOnlyList<InputBinding> bindings)
        {
            var array = new JArray();
            foreach (var binding in bindings
                .OrderBy(b => b.FromInput, StringComparer.Ordinal)
                .ThenBy(b => b.To.Node, StringComparer.Ordinal)
                .ThenBy(b => b.To.Port, StringComparer.Ordinal))
            {
                var item = new JObject();
                item["fromInput"] = binding.FromInput;
                item["to"] = PortRefToJson(binding.To);
                array.Add(item);
            }
            return array;
        }

        private static JObject PortRefToJson(PortRef portRef)
        {
            var item = new JObject();
            item["node"] = portRef.Node;
            item["port"] = portRef.Port;
            return item;
        }
    }
}
