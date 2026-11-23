#nullable enable

using OpWeave.Core.Operations;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One Descriptor identity SPEC §C3 stage 6 actually read from an
    /// instantiated Operation candidate, with the package and defining
    /// assembly attribution the observed-fact extension gate needs.
    /// </summary>
    /// <remarks>
    /// Recorded before the implementationHash step, so the set also covers
    /// candidates a later step refuses individually: an OW-OP-017
    /// fingerprint failure rejects only its own Operation (SPEC §M.5
    /// "無関係なOperationは維持"), it never shrinks the Descriptor set the
    /// §X.3.2 declared-vs-observed comparison sees.
    /// </remarks>
    internal sealed record ObservedOperation(
        OperationDescriptor Descriptor,
        string? PackageId,
        string AssemblyName);
}
