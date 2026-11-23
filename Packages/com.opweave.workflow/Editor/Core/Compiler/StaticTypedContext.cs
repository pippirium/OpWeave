#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The <see cref="StaticContext"/> decorator the compiler phases place
    /// over the factory-produced context so
    /// <see cref="StaticContext.StaticTypeOf"/> answers from the document and
    /// the D4 typing (SPEC §0.6 SG-06, added 2026-11-06): a data-edge-fed port
    /// reports the connected output's type as the D4 phase recorded it
    /// (<see cref="ValidationContext.OutputTypes"/>), a port bound to a
    /// workflow input reports that input's declared type, a constant or
    /// defaulted port reports the port's declared type, and a Missing port —
    /// or one whose supplier is not uniquely resolved — reports
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The factory signature of SPEC §0.6 SG-06 carries only the node id and
    /// the resolved parameters, so the supply-kind data this answer needs
    /// cannot come from the produced implementation; wrapping keeps every
    /// existing implementation compiling while the phases supply the resolved
    /// types. Every other member delegates verbatim.
    /// </para>
    /// <para>
    /// A phase that runs before D4 (the D3 static check) has no
    /// <see cref="ValidationContext.OutputTypes"/> yet, so an edge-fed port
    /// reports <see langword="null"/> — "not statically known" — rather than
    /// the port's declared type, which could misstate what the edge actually
    /// supplies. Constants, defaults and workflow-input bindings are
    /// document-level facts and answer fully at every stage; a Missing port
    /// always reports <see langword="null"/>.
    /// </para>
    /// </remarks>
    internal sealed class StaticTypedContext : StaticContext
    {
        private readonly StaticContext _inner;
        private readonly IReadOnlyDictionary<string, TypeRef> _staticTypes;

        internal StaticTypedContext(
            StaticContext inner, IReadOnlyDictionary<string, TypeRef> staticTypes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _staticTypes = staticTypes ?? throw new ArgumentNullException(nameof(staticTypes));
        }

        public override string NodeId => _inner.NodeId;

        public override IReadOnlyDictionary<string, InputValue> Parameters => _inner.Parameters;

        public override T Get<T>(string port) => _inner.Get<T>(port);

        public override TypeRef? StaticTypeOf(string port) =>
            port is not null && _staticTypes.TryGetValue(port, out var type) ? type : null;

        /// <summary>
        /// The port name → statically resolved type table of one node's
        /// declared input ports (SPEC §0.6 SG-06); ports without a statically
        /// known type have no entry.
        /// </summary>
        internal static IReadOnlyDictionary<string, TypeRef> StaticTypesFor(
            ValidationContext context, NodeModel node, OperationDescriptor descriptor)
        {
            var types = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            foreach (var port in descriptor.Inputs)
            {
                var type = StaticType(context, node, port);
                if (type is not null)
                {
                    types[port.Name] = type;
                }
            }
            return types;
        }

        // One input port's statically resolved type. The explicit suppliers
        // are counted under the same §A1/R1 rule the D4 phase applies — data
        // edges, workflow-input bindings and a written parameter — and two or
        // more mean no supplier is adopted (OW-VAL-006), so the type is not
        // statically known.
        private static TypeRef? StaticType(
            ValidationContext context, NodeModel node, PortDescriptor port)
        {
            var document = context.Document;
            var target = new PortRef(node.Id, port.Name);
            DataEdge? edge = null;
            InputBinding? binding = null;
            var suppliers = node.Parameters.ContainsKey(port.Name) ? 1 : 0;
            foreach (var candidate in document.DataEdges)
            {
                if (candidate.To == target)
                {
                    edge ??= candidate;
                    suppliers++;
                }
            }
            foreach (var candidate in document.InputBindings)
            {
                if (candidate.To == target)
                {
                    binding ??= candidate;
                    suppliers++;
                }
            }

            if (suppliers >= 2)
            {
                return null;
            }
            if (edge is not null)
            {
                // The connected output's type as the D4 phase resolved it
                // (SPEC §D4 item 6) — unknown before D4 runs or when the
                // source node carried no recorded type.
                return context.OutputTypes is not null &&
                    context.OutputTypes.TryGetValue(edge.From.Node, out var outputs) &&
                    outputs.TryGetValue(edge.From.Port, out var outputType)
                        ? outputType
                        : null;
            }
            if (binding is not null)
            {
                // The bound workflow input's declared type; the first
                // declaration wins, matching the D4 duplicate rule.
                var inputs = document.Inputs;
                for (var i = 0; i < inputs.Count; i++)
                {
                    if (string.Equals(
                            inputs[i].Id, binding.FromInput, StringComparison.Ordinal))
                    {
                        var parsed = TypeRef.Parse(inputs[i].Type);
                        return parsed.Ok ? parsed.Value : null;
                    }
                }
                return null;
            }
            if (suppliers == 1 || port.Default is not null)
            {
                // A constant parameter or the Descriptor default: the port's
                // declared type. A Type ID that does not parse is the
                // registration-time OW-OP-003 finding a validated snapshot
                // cannot carry, so the type stays unknown rather than wrong.
                var parsed = TypeRef.Parse(port.Type);
                return parsed.Ok ? parsed.Value : null;
            }
            return null;
        }
    }
}
