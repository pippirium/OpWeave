#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The per-package record the SPEC §C3 stages 1–5 hand to the
    /// pre-publication extension gate: everything the observed-fact phase of
    /// SPEC §X.3.2 validation needs to re-verify an admitted extension —
    /// its manifest facts, the provider and Operation candidates that stand
    /// or fall together, and the Type IDs its instantiated providers
    /// actually offered.
    /// </summary>
    /// <remarks>
    /// A package enters this record only after the metadata gate accepted
    /// its manifest and it still has at least one admitted (non-excluded)
    /// candidate. The gate re-runs <see cref="ExtensionManifestValidator"/>
    /// over these facts; a rejection removes the package's providers from
    /// the type snapshot and its Operations from the snapshot candidates —
    /// the package contributes nothing to the published Registry.
    /// </remarks>
    internal sealed class ExtensionPackageState
    {
        internal ExtensionPackageState(
            string packageId,
            string manifestText,
            string? rootPath,
            IReadOnlyList<string> editorAssemblies,
            IReadOnlyList<DiscoveredTypeProvider> discoveredTypeProviders,
            IReadOnlyList<ExtensionCandidate> providers,
            IReadOnlyList<TypeProviderCandidate> providerInstances,
            IReadOnlyList<ExtensionCandidate> operations,
            IReadOnlyList<ObservedExtensionType> observedTypes,
            int refusedOperationCount = 0,
            IReadOnlyList<ExtensionCandidate>? excludedOperations = null)
        {
            PackageId = packageId;
            ManifestText = manifestText;
            RootPath = rootPath;
            EditorAssemblies = ReadOnlyCollections.CopyList(editorAssemblies);
            DiscoveredTypeProviders = ReadOnlyCollections.CopyList(discoveredTypeProviders);
            Providers = ReadOnlyCollections.CopyList(providers);
            ProviderInstances = ReadOnlyCollections.CopyList(providerInstances);
            Operations = ReadOnlyCollections.CopyList(operations);
            ObservedTypes = ReadOnlyCollections.CopyList(observedTypes);
            RefusedOperationCount = refusedOperationCount;
            ExcludedOperations = ReadOnlyCollections.CopyList(
                excludedOperations ??
                (IReadOnlyList<ExtensionCandidate>)Array.Empty<ExtensionCandidate>());
        }

        /// <summary>The UPM package name this record belongs to.</summary>
        internal string PackageId { get; }

        /// <summary>The raw <c>opweave.extension.json</c> text the metadata gate accepted.</summary>
        internal string ManifestText { get; }

        /// <summary>The package's resolved root path, or null when the environment did not supply one.</summary>
        internal string? RootPath { get; }

        /// <summary>Names of the Editor assemblies rooted inside the package.</summary>
        internal IReadOnlyList<string> EditorAssemblies { get; }

        /// <summary>Every <c>[OpWeaveTypeProvider]</c> class discovered in the package (metadata only).</summary>
        internal IReadOnlyList<DiscoveredTypeProvider> DiscoveredTypeProviders { get; }

        /// <summary>The admitted, manifest-bound provider candidates of the package.</summary>
        internal IReadOnlyList<ExtensionCandidate> Providers { get; }

        /// <summary>
        /// The instantiated <see cref="TypeProviderCandidate"/>s of <see cref="Providers"/>,
        /// aligned by index — the entries the gate removes from the type build on rejection.
        /// </summary>
        internal IReadOnlyList<TypeProviderCandidate> ProviderInstances { get; }

        /// <summary>The admitted Operation candidates of the package.</summary>
        internal IReadOnlyList<ExtensionCandidate> Operations { get; }

        /// <summary>
        /// Operation candidates of the package refused on their
        /// attribute-declared identity (SPEC §C3 rules 3–4) — rejected
        /// before construction, so no Descriptor was read. They widen the
        /// pre-publication gate's unobserved budget exactly like an
        /// admitted candidate that failed construction (OW-OP-008): a
        /// manifest-declared entry they account for is "not observed", not
        /// a declared-vs-observed mismatch (SPEC §X.3.2, §M.5).
        /// </summary>
        internal int RefusedOperationCount { get; }

        /// <summary>
        /// Operation candidates of the package the exclusion predicate kept
        /// out before construction (SPEC §C3 rule 5 — DiscoveryMode or
        /// <c>disabledOperations</c>), so no Descriptor was read. Unlike an
        /// anonymous unobserved-budget entry their attribute-declared
        /// (id, version) is known, so the pre-publication gate lets each
        /// satisfy only the manifest <c>operations[]</c> entry naming that
        /// same identity: a policy-excluded Operation still exists in the
        /// package, so its own declaration is "not observed", never a
        /// declared-vs-observed mismatch — but it can never stand in for a
        /// different declared Operation (SPEC §X.3.2, §M.5 — an intentional
        /// exclusion is not an error).
        /// </summary>
        internal IReadOnlyList<ExtensionCandidate> ExcludedOperations { get; }

        /// <summary>The Type IDs the package's instantiated providers offered (observed facts).</summary>
        internal IReadOnlyList<ObservedExtensionType> ObservedTypes { get; }
    }
}
