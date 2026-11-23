#nullable enable

using System;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The single C# data type of every data port (SPEC §H6.3). Type ID
    /// compatibility is judged by the §H8 validation on the Descriptor's Type
    /// IDs, not by the port type — so every data port shares this one class
    /// and Graph Toolkit's own type checks never reject a valid OpWeave wire.
    /// </summary>
    /// <remarks>
    /// The serialized <see cref="json"/> is the constant a port carries when
    /// nothing is connected: SPEC §H7.2 assigns it through
    /// <c>IPort.TrySetValue</c> and Graph Toolkit persists the port value
    /// inside the graph asset, so the payload is the constant's Canonical
    /// JSON text — the only representation that survives serialization.
    /// </remarks>
    [Serializable]
    public sealed class OwValue
    {
        /// <summary>The port constant's Canonical JSON text (SPEC §H7.2); empty until assigned.</summary>
        [SerializeField] public string json = "";
    }
}
