#nullable enable

using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The settings-error exclusion of SPEC §H4.1 ("設定エラーのときの動作":
    /// the Registry registers standard Operations only): while the settings
    /// load fails, every candidate whose provenance is not
    /// <see cref="OperationOrigin.Standard"/> — custom project assemblies and
    /// external extension packages, Operations and TypeProviders alike — is
    /// excluded before instantiation, exactly like a policy exclusion
    /// (SPEC §C3 rule 5: an intentional exclusion, never a registration
    /// error).
    /// </summary>
    internal sealed class StandardOnlyExtensionExclusion : IExtensionExclusionPredicate
    {
        /// <summary>The shared instance; the predicate is stateless.</summary>
        internal static readonly StandardOnlyExtensionExclusion Instance =
            new StandardOnlyExtensionExclusion();

        private StandardOnlyExtensionExclusion()
        {
        }

        /// <inheritdoc/>
        public string? Exclude(ExtensionCandidate candidate) =>
            candidate.Origin == OperationOrigin.Standard
                ? null
                : "settings error: only standard Operations may register";
    }
}
