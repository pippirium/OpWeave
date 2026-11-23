#nullable enable

using System;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// One permitted Operation handed to <see cref="RegistrySnapshotBuilder"/>
    /// as pure metadata (SPEC §C3 steps 6–7). The Editor-side pipeline
    /// instantiates the class (SPEC §C2 eligibility, OW-OP-008), resolves its
    /// defining assembly (OW-OP-017 when unresolvable), computes the compiled
    /// bytes fingerprint and the Poll flag, then submits this record; the
    /// builder never reflects on the class itself.
    /// </summary>
    /// <remarks>
    /// <see cref="HasPollImplementation"/> is the flag computed by the
    /// Editor-side stage (T-W2.2-07): whether the candidate's class provides a
    /// real <c>Poll</c> implementation. The builder consults it only when the
    /// Descriptor declares <see cref="ExecutionMode.Waitable"/> — a waitable
    /// Operation without a Poll implementation fails with OW-OP-003 (SPEC §C3
    /// rule 3). Exclusion by permission settings (SPEC §C3 rule 5) happens
    /// before candidacy and never reaches this record.
    /// </remarks>
    public sealed record OperationCandidate(
        OperationDescriptor Descriptor,
        OperationOrigin Origin,
        string AssemblyName,
        string? SourceFile,
        string ImplementationHash,
        bool HasPollImplementation,
        string? PackageId = null,
        string? ResolvedCommit = null,
        IOperation? Implementation = null,
        string? DeclaredId = null,
        int? DeclaredVersion = null)
    {
        /// <summary>The Descriptor obtained from the instantiated Operation.</summary>
        public OperationDescriptor Descriptor { get; init; } =
            Descriptor ?? throw new ArgumentNullException(nameof(Descriptor));

        /// <summary>The simple name of the resolved defining assembly.</summary>
        public string AssemblyName { get; init; } =
            string.IsNullOrEmpty(AssemblyName)
                ? throw new ArgumentException(
                    "Assembly name must not be null or empty.", nameof(AssemblyName))
                : AssemblyName;

        /// <summary>Lowercase-hex SHA-256 of the defining assembly's compiled bytes (SPEC §C3 rule 6).</summary>
        public string ImplementationHash { get; init; } =
            string.IsNullOrEmpty(ImplementationHash)
                ? throw new ArgumentException(
                    "The implementation fingerprint must not be null or empty; an unresolvable defining assembly is OW-OP-017 and never reaches this record.",
                    nameof(ImplementationHash))
                : ImplementationHash;

        /// <summary>
        /// The instantiated Operation the Editor-side stage produced for this
        /// candidate (SPEC §C3 step 6), carried through to
        /// <see cref="RegisteredOperation.Implementation"/> so snapshot
        /// consumers — the D3 phase's <c>ValidateStatic</c> call and the
        /// Runner — reach the implementation without a parallel lookup.
        /// Stateless per SPEC §C2 item 4; may be null for metadata-only
        /// candidates.
        /// </summary>
        public IOperation? Implementation { get; init; } = Implementation;

        /// <summary>
        /// The <see cref="OpWeave.Core.Operations.OpWeaveOperationAttribute.Id"/>
        /// the candidate's class declared (SPEC §C2 item 2) — type metadata read
        /// before construction (SPEC §C3 step 2). Null when the identity is
        /// unavailable on this code path; when both it and
        /// <see cref="DeclaredVersion"/> are supplied they must equal
        /// <see cref="Descriptor"/>.<c>Id</c>/<c>Version</c> or the candidate is
        /// rejected with OW-OP-003.
        /// </summary>
        public string? DeclaredId { get; init; } = DeclaredId;

        /// <summary>
        /// The <see cref="OpWeave.Core.Operations.OpWeaveOperationAttribute.Version"/>
        /// the candidate's class declared; paired with
        /// <see cref="DeclaredId"/>. Null when the declared identity is not
        /// carried on this code path.
        /// </summary>
        public int? DeclaredVersion { get; init; } = DeclaredVersion;
    }
}
