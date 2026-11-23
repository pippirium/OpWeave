#nullable enable

using System;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// One Operation published inside a <see cref="RegistrySnapshot"/> (SPEC
    /// §C3): the validated Descriptor, its origin (Standard/Custom), the
    /// defining assembly's simple name, the optional project-relative source
    /// file used only for provenance display (SPEC §C2 item 2), and the two
    /// §C3 rule-6 fingerprints.
    /// </summary>
    /// <remarks>
    /// <see cref="PackageId"/> and <see cref="ResolvedCommit"/> are the package
    /// information slots SPEC §C3 requires on the same snapshot (Package ID
    /// and resolved commit, §X.3). They are optional members added under the
    /// §B0.2 MINOR rule — the positional signature matches the §C3 record
    /// verbatim — and stay null for Operations not defined inside a UPM
    /// package. Neither member is a <c>registryHash</c> input (SPEC §C3
    /// rule 6).
    /// </remarks>
    public sealed record RegisteredOperation(
        OperationDescriptor Descriptor,
        OperationOrigin Origin,
        string AssemblyName,
        string? SourceFile,
        string DescriptorHash,
        string ImplementationHash)
    {
        /// <summary>The immutable contract this Operation declares (SPEC §C1).</summary>
        public OperationDescriptor Descriptor { get; init; } =
            Descriptor ?? throw new ArgumentNullException(nameof(Descriptor));

        /// <summary>The simple name of the assembly the Operation was defined in.</summary>
        public string AssemblyName { get; init; } =
            string.IsNullOrEmpty(AssemblyName)
                ? throw new ArgumentException(
                    "Assembly name must not be null or empty.", nameof(AssemblyName))
                : AssemblyName;

        /// <summary>Lowercase-hex SHA-256 of the Descriptor's Canonical JSON (SPEC §C3 rule 6).</summary>
        public string DescriptorHash { get; init; } =
            CheckHash(DescriptorHash, nameof(DescriptorHash));

        /// <summary>Lowercase-hex SHA-256 of the defining assembly's compiled bytes (SPEC §C3 rule 6).</summary>
        public string ImplementationHash { get; init; } =
            CheckHash(ImplementationHash, nameof(ImplementationHash));

        /// <summary>
        /// The UPM Package ID of the package that defines the Operation, when
        /// it ships inside one (SPEC §C3, §X.3); null for project assemblies.
        /// </summary>
        public string? PackageId { get; init; }

        /// <summary>
        /// The full resolved commit the defining package was installed at
        /// (SPEC §C3, §X.3); null when the package manager records no commit.
        /// </summary>
        public string? ResolvedCommit { get; init; }

        /// <summary>
        /// The registered <see cref="IOperation"/> implementation instance when
        /// the candidate supplied one — SPEC §C3 step 6 instantiates permitted
        /// Operations before validating their Descriptors. Instances are
        /// stateless (SPEC §C2 item 4); the D3 validation phase drives
        /// <see cref="IOperation.ValidateStatic"/> through this slot and the
        /// Runner drives the lifecycle through it. Null for snapshots built
        /// from metadata-only candidates.
        /// </summary>
        public IOperation? Implementation { get; init; }

        private static string CheckHash(string hash, string name) =>
            string.IsNullOrEmpty(hash)
                ? throw new ArgumentException(
                    "A fingerprint must be a non-empty lowercase-hex SHA-256 string.", name)
                : hash;
    }
}
