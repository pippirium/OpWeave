#nullable enable

using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The parsed <c>opweave.extension.json</c> of SPEC §X.3.2: the metadata
    /// an external UPM extension declares before its Operations or
    /// TypeProviders may be registered.
    /// </summary>
    /// <remarks>
    /// Produced by <see cref="ExtensionManifestReader"/> only when every
    /// manifest-local rule holds; environment checks (declared assemblies
    /// exist in the package, declared TypeProviders are found, the
    /// <c>operations[]</c> list equals the discovered Descriptor set,
    /// example paths resolve inside the package, the Unity minimum is met)
    /// run afterwards in <see cref="ExtensionManifestValidator"/>.
    /// </remarks>
    public sealed class ExtensionManifest
    {
        public ExtensionManifest(
            string schemaVersion,
            string extensionId,
            string? displayName,
            CoreApiRange coreApi,
            string unityMinInclusive,
            IEnumerable<string> assemblies,
            IEnumerable<ExtensionOperationRef> operations,
            IEnumerable<ExtensionTypeProviderRef> typeProviders,
            IEnumerable<string> examples,
            IEnumerable<string> blueprints)
        {
            SchemaVersion = schemaVersion;
            ExtensionId = extensionId;
            DisplayName = displayName;
            CoreApi = coreApi;
            UnityMinInclusive = unityMinInclusive;
            Assemblies = ReadOnlyCollections.CopyList(assemblies);
            Operations = ReadOnlyCollections.CopyList(operations);
            TypeProviders = ReadOnlyCollections.CopyList(typeProviders);
            Examples = ReadOnlyCollections.CopyList(examples);
            Blueprints = ReadOnlyCollections.CopyList(blueprints);
        }

        /// <summary>The <c>schemaVersion</c> value; always "1.0" for a parsed manifest.</summary>
        public string SchemaVersion { get; }

        /// <summary>The <c>extensionId</c> value (SPEC §X.3.2).</summary>
        public string ExtensionId { get; }

        /// <summary>The optional <c>displayName</c>, or null when absent.</summary>
        public string? DisplayName { get; }

        /// <summary>
        /// The declared <c>coreApi</c> compatibility range
        /// (<c>[minInclusive, maxExclusive)</c>, SPEC §B0.2).
        /// </summary>
        public CoreApiRange CoreApi { get; }

        /// <summary>
        /// The declared <c>unity.minInclusive</c> version text
        /// (<c>MAJOR.MINOR</c> or <c>MAJOR.MINOR.PATCH</c>).
        /// </summary>
        public string UnityMinInclusive { get; }

        /// <summary>Declared <c>assemblies</c> contributing Operations/TypeProviders.</summary>
        public IReadOnlyList<string> Assemblies { get; }

        /// <summary>Declared <c>operations</c> identities.</summary>
        public IReadOnlyList<ExtensionOperationRef> Operations { get; }

        /// <summary>Declared <c>typeProviders</c> bindings.</summary>
        public IReadOnlyList<ExtensionTypeProviderRef> TypeProviders { get; }

        /// <summary>Declared <c>examples</c> package-relative paths.</summary>
        public IReadOnlyList<string> Examples { get; }

        /// <summary>
        /// Declared <c>blueprints</c> entries. Always empty for a parsed
        /// manifest — a non-empty list is refused with OW-OP-010
        /// (SPEC §X.10.4, §J.1).
        /// </summary>
        public IReadOnlyList<string> Blueprints { get; }
    }
}
