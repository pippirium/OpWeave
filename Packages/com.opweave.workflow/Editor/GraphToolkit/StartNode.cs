#nullable enable

using System;
using Unity.GraphToolkit.Editor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The workflow entry node (SPEC §H6.2) — the graph form of the single
    /// <c>core.start</c> node every v1 workflow carries exactly once. Its only
    /// port is the named <see cref="OwControl"/> output <see cref="NextOutputName"/>,
    /// the v1 catalogue's sole control output (SPEC appendix N).
    /// </summary>
    [Serializable]
    public sealed class StartNode : Node
    {
        /// <summary>The v1 control output name (SPEC §N.4).</summary>
        internal const string NextOutputName = "next";

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
            context.AddOutputPort<OwControl>(NextOutputName).Build();
        }
    }
}
