#nullable enable

using OpWeave.Core.Operations;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The read-only (id, version) → <see cref="OperationDescriptor"/> lookup the
    /// Resolve stage uses to find each node's input-port types (SPEC §D0.2).
    /// </summary>
    /// <remarks>
    /// Internal seam: the production source is the SPEC §C3
    /// <c>RegistrySnapshot</c>, which the registry work supplies once it lands;
    /// until then the snapshot-free <see cref="WorkflowCompiler"/> runs the
    /// Resolve stage with no source and the stage converts nothing. A null
    /// result means "no such registered Operation" — the OW-OP-001/OW-OP-002
    /// diagnostics that distinguish unknown ids from unsupported versions
    /// belong to the D3 phase, not to this lookup.
    /// </remarks>
    internal interface IOperationSource
    {
        /// <summary>
        /// The Descriptor registered for <paramref name="id"/> at
        /// <paramref name="version"/>, or null when none is registered.
        /// </summary>
        OperationDescriptor? Find(string id, int version);
    }
}
