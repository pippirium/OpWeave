#nullable enable

using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The validation verdict for one installed package's extension
    /// metadata (SPEC §X.3.2): accepted means the extension's Operations and
    /// TypeProviders may proceed to registration; rejected means nothing
    /// from the extension is registered and <see cref="Diagnostics"/>
    /// explains why.
    /// </summary>
    /// <remarks>
    /// Only the published codes OW-OP-010 and OW-OP-012 are emitted here;
    /// every other metadata rejection carries an internal diagnostic
    /// (empty code, fixed English) whose published mapping is assigned by
    /// T-W2.12-03 (SPEC §J.1 OW-OP-013..019 — a Non-goal of this ticket).
    /// </remarks>
    public sealed class ExtensionValidationOutcome
    {
        private static readonly IReadOnlyList<TypeProviderBinding> s_noBindings =
            System.Array.AsReadOnly(System.Array.Empty<TypeProviderBinding>());

        private ExtensionValidationOutcome(
            string packageId,
            bool manifestPresent,
            bool accepted,
            ExtensionManifest? manifest,
            IReadOnlyList<TypeProviderBinding> typeProviders,
            IReadOnlyList<Diagnostic> diagnostics,
            UpmPackageProvenance? package)
        {
            PackageId = packageId;
            ManifestPresent = manifestPresent;
            Accepted = accepted;
            Manifest = manifest;
            TypeProviders = typeProviders;
            Diagnostics = diagnostics;
            Package = package;
        }

        /// <summary>The UPM package name this verdict applies to.</summary>
        public string PackageId { get; }

        /// <summary>True when <c>opweave.extension.json</c> was present at the package root.</summary>
        public bool ManifestPresent { get; }

        /// <summary>True when the extension may register its candidates.</summary>
        public bool Accepted { get; }

        /// <summary>The parsed manifest, or null when absent or rejected before parsing finished.</summary>
        public ExtensionManifest? Manifest { get; }

        /// <summary>
        /// The Provider ID → class bindings resolved for an accepted
        /// extension; empty otherwise.
        /// </summary>
        public IReadOnlyList<TypeProviderBinding> TypeProviders { get; }

        /// <summary>Diagnostics describing the rejection (or parse warnings); never null.</summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        /// <summary>
        /// The UPM-resolved provenance of this package (package ID, source,
        /// source repository and resolved commit, SPEC §X.3.2 / §G0), attached
        /// by <see cref="ExtensionDiscovery"/> and also published at snapshot
        /// level through <see cref="ExtensionDiscoveryReport.Packages"/> —
        /// the RegistrySnapshot's package info slot (SPEC §C3). Null only
        /// for outcomes built without package information (directly
        /// constructed validator inputs, never live discovery).
        /// </summary>
        public UpmPackageProvenance? Package { get; }

        /// <summary>Returns a copy of this outcome with <see cref="Package"/> set.</summary>
        public ExtensionValidationOutcome WithPackage(UpmPackageProvenance? package) =>
            new ExtensionValidationOutcome(
                PackageId, ManifestPresent, Accepted, Manifest, TypeProviders, Diagnostics, package);

        /// <summary>An accepted extension.</summary>
        public static ExtensionValidationOutcome Accept(
            string packageId,
            ExtensionManifest? manifest,
            IReadOnlyList<TypeProviderBinding>? typeProviders) =>
            new ExtensionValidationOutcome(
                packageId,
                manifestPresent: manifest is not null,
                accepted: true,
                manifest,
                typeProviders ?? s_noBindings,
                System.Array.AsReadOnly(System.Array.Empty<Diagnostic>()),
                package: null);

        /// <summary>A rejected extension; nothing from it registers.</summary>
        public static ExtensionValidationOutcome Reject(
            string packageId,
            bool manifestPresent,
            ExtensionManifest? manifest,
            IReadOnlyList<Diagnostic> diagnostics) =>
            new ExtensionValidationOutcome(
                packageId,
                manifestPresent,
                accepted: false,
                manifest,
                s_noBindings,
                diagnostics,
                package: null);
    }
}
