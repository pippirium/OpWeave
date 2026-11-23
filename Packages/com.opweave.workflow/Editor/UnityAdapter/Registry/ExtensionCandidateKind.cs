#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Which kind of extension contract a discovered candidate class offers
    /// (SPEC §C0.1, §C3 step 2): an <see cref="Core.Operations.OpWeaveOperationAttribute"/>-marked
    /// Operation implementation or an <see cref="Core.OpWeaveTypeProviderAttribute"/>-marked
    /// TypeProvider.
    /// </summary>
    internal enum ExtensionCandidateKind
    {
        /// <summary>A <c>[OpWeaveOperation]</c> class; instantiated in §C3 step 6 (T-W2.2-07).</summary>
        Operation,

        /// <summary>A <c>[OpWeaveTypeProvider]</c> class; instantiated in §C3 step 4.</summary>
        TypeProvider,
    }
}
