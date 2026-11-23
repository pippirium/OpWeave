#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The <c>auto</c> DiscoveryMode predicate of SPEC §C0.2: every extension
    /// passing the contract and Editor-assembly checks registers — nothing is
    /// excluded beyond eligibility and provenance.
    /// </summary>
    internal sealed class AutoExtensionExclusion : IExtensionExclusionPredicate
    {
        /// <summary>The shared instance; the predicate is stateless.</summary>
        internal static readonly AutoExtensionExclusion Instance = new AutoExtensionExclusion();

        private AutoExtensionExclusion()
        {
        }

        /// <inheritdoc/>
        public string? Exclude(ExtensionCandidate candidate) => null;
    }
}
