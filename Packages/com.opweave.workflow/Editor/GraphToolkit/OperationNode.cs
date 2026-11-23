#nullable enable

using System;
using Unity.GraphToolkit.Editor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The single node class every Operation maps to (SPEC §H6.2). The
    /// <see cref="operationId"/>/<see cref="version"/>/<see cref="nodeId"/>
    /// carrier lives in serialized node-class fields — Graph Toolkit node
    /// options expose no public value setter — set before
    /// <see cref="Graph.AddNode"/> when a graph is assembled from JSON
    /// (SPEC §H7.2). Custom Operations need no per-kind node class: the same
    /// class serves every Descriptor the Registry holds.
    /// </summary>
    /// <remarks>
    /// <see cref="OnDefinePorts"/> builds the ports from the Registry
    /// Descriptor found through <see cref="GraphToolkitRegistry"/>: one fixed
    /// <see cref="OwControl"/> input <see cref="ControlInputName"/>, every
    /// <c>Descriptor.Inputs</c> entry as an <see cref="OwValue"/> input, every
    /// <c>Descriptor.ControlOutputs</c> name as an <see cref="OwControl"/>
    /// output (<c>"next"</c> in v1, SPEC appendix N) and every
    /// <c>Descriptor.Outputs</c> entry as an <see cref="OwValue"/> output. A
    /// node whose Operation is absent from the Registry defines no ports —
    /// the §H6.5 error display is layered on top by the graph window work.
    /// </remarks>
    [Serializable]
    public sealed class OperationNode : Node
    {
        /// <summary>The fixed name of the single control input port.</summary>
        internal const string ControlInputName = "in";

        /// <summary>The registered Operation ID this node executes (SPEC §A4 grammar).</summary>
        [SerializeField] public string operationId = "";

        /// <summary>The registered Operation version.</summary>
        [SerializeField] public int version;

        /// <summary>The workflow node ID (SPEC §A4) this graph node represents.</summary>
        [SerializeField] public string nodeId = "";

        /// <summary>
        /// The §A1 node members the graph has no dedicated carrier for —
        /// <c>label</c> and <c>policy</c> — as one Canonical JSON object so a
        /// JSON → graph → JSON round-trip loses nothing (SPEC §H7); empty
        /// when the node carries neither.
        /// </summary>
        [SerializeField] public string extrasJson = "";

        /// <summary>
        /// The import token proving this element was created by the last
        /// <see cref="GraphJsonConverter.Import"/> (SPEC §0.6 SG-17
        /// provenance; see <see cref="OpWeaveGraph.importedTokens"/>); empty
        /// for a GUI-created node. Internal converter state — hand-editing
        /// it is unsupported.
        /// </summary>
        [SerializeField] internal string importToken = "";

        /// <inheritdoc/>
        protected override void OnDefinePorts(IPortDefinitionContext context)
        {
            context.AddInputPort<OwControl>(ControlInputName).Build();
            var descriptor = GraphToolkitRegistry.FindOperation(operationId, version);
            if (descriptor is null)
            {
                return;
            }
            foreach (var input in descriptor.Inputs)
            {
                context.AddInputPort<OwValue>(input.Name)
                    .WithConnectorUI(PortConnectorUI.Circle)
                    .Build();
            }
            foreach (var controlOutput in descriptor.ControlOutputs)
            {
                context.AddOutputPort<OwControl>(controlOutput).Build();
            }
            foreach (var output in descriptor.Outputs)
            {
                context.AddOutputPort<OwValue>(output.Name).Build();
            }
        }
    }
}
