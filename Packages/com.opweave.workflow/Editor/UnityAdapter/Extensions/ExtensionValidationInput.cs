#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Everything the environment knows about one installed UPM package for
    /// manifest validation (SPEC §X.3.2, §B0.2): the package identity and
    /// root, the manifest text when present, the Editor assemblies rooted in
    /// the package, the <c>[OpWeaveTypeProvider]</c> classes and Operation
    /// Descriptor identities discovered in it, the Type IDs its instantiated
    /// providers offer, and the running Unity version.
    /// </summary>
    /// <remarks>
    /// <see cref="ExtensionDiscovery"/> builds these inputs from the live
    /// project — only packages already installed by the user are enumerated
    /// (SPEC §X.1: OpWeave never searches, downloads or updates extensions
    /// by itself). Tests construct inputs directly to cover the validation
    /// matrix without installing packages.
    /// </remarks>
    public sealed class ExtensionValidationInput
    {
        public ExtensionValidationInput(
            string packageId,
            string packageRootPath,
            string? manifestText,
            IEnumerable<string> packageEditorAssemblies,
            IEnumerable<DiscoveredTypeProvider> discoveredTypeProviders,
            IEnumerable<ObservedExtensionOperation> observedOperations,
            IEnumerable<ObservedExtensionType> observedTypes,
            string currentUnityVersion,
            int unobservedOperationCandidates = 0,
            IEnumerable<ObservedExtensionOperation>? excludedOperations = null)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                throw new ArgumentException("Package ID must not be null or empty.", nameof(packageId));
            }
            PackageId = packageId;
            PackageRootPath = packageRootPath ?? throw new ArgumentNullException(nameof(packageRootPath));
            ManifestText = manifestText;
            PackageEditorAssemblies =
                ReadOnlyCollections.CopyList(packageEditorAssemblies ?? throw new ArgumentNullException(nameof(packageEditorAssemblies)));
            DiscoveredTypeProviders =
                ReadOnlyCollections.CopyList(discoveredTypeProviders ?? throw new ArgumentNullException(nameof(discoveredTypeProviders)));
            ObservedOperations =
                ReadOnlyCollections.CopyList(observedOperations ?? throw new ArgumentNullException(nameof(observedOperations)));
            ObservedTypes =
                ReadOnlyCollections.CopyList(observedTypes ?? throw new ArgumentNullException(nameof(observedTypes)));
            CurrentUnityVersion = currentUnityVersion ?? throw new ArgumentNullException(nameof(currentUnityVersion));
            UnobservedOperationCandidates = unobservedOperationCandidates;
            ExcludedOperations = excludedOperations is null
                ? (IReadOnlyList<ObservedExtensionOperation>)Array.Empty<ObservedExtensionOperation>()
                : ReadOnlyCollections.CopyList(excludedOperations);
        }

        /// <summary>The UPM package name (e.g. <c>com.example.opweave.animation</c>).</summary>
        public string PackageId { get; }

        /// <summary>Absolute path of the package's resolved root on disk.</summary>
        public string PackageRootPath { get; }

        /// <summary>
        /// The raw <c>opweave.extension.json</c> text when the file exists at
        /// the package root, otherwise null.
        /// </summary>
        public string? ManifestText { get; }

        /// <summary>
        /// Names of the Editor assemblies rooted in this package (compiled
        /// asmdef products and Editor-only precompiled DLLs alike). An empty
        /// set means the package contributes no usable extension assembly.
        /// </summary>
        public IReadOnlyList<string> PackageEditorAssemblies { get; }

        /// <summary>
        /// The <c>[OpWeaveTypeProvider]</c> classes discovered inside this
        /// package's assemblies (metadata only; never instantiated here).
        /// </summary>
        public IReadOnlyList<DiscoveredTypeProvider> DiscoveredTypeProviders { get; }

        /// <summary>
        /// Operation Descriptor identities discovered inside this package's
        /// assemblies by the registration stage.
        /// </summary>
        public IReadOnlyList<ObservedExtensionOperation> ObservedOperations { get; }

        /// <summary>
        /// Type IDs the registration stage observed in this package's
        /// assemblies by instantiating its bound providers (SPEC §C3
        /// steps 3–4).
        /// </summary>
        public IReadOnlyList<ObservedExtensionType> ObservedTypes { get; }

        /// <summary>The running Unity version text (e.g. <c>6000.0.85f1</c>).</summary>
        public string CurrentUnityVersion { get; }

        /// <summary>
        /// How many of the package's admitted Operation candidates produced
        /// no Descriptor — SPEC §C3 step-6 individual failures (construction
        /// or Descriptor-getter OW-OP-008). Each one covers a single declared
        /// <c>operations[]</c> entry no Descriptor was read for: such an
        /// entry is "not observed because of an individual failure", never a
        /// declared-vs-observed mismatch (SPEC §X.3.2, §M.5). The
        /// registration gate supplies the count; discovery callers leave the
        /// default — their observed set claims exact equality.
        /// </summary>
        public int UnobservedOperationCandidates { get; }

        /// <summary>
        /// The attribute-declared <c>(id, version)</c> identities of the
        /// package's policy-excluded Operation candidates (SPEC §C3 rule 5 —
        /// DiscoveryMode, whitelist, <c>disabledOperations</c>): candidates
        /// the exclusion predicate kept out before any constructor ran, so
        /// no Descriptor was read for them. Unlike the anonymous
        /// <see cref="UnobservedOperationCandidates"/> budget each one
        /// satisfies only the declared <c>operations[]</c> entry naming its
        /// own declared identity — never an unrelated one (SPEC §X.3.2).
        /// The registration gate supplies the set; discovery callers leave
        /// the default.
        /// </summary>
        public IReadOnlyList<ObservedExtensionOperation> ExcludedOperations { get; }
    }
}
