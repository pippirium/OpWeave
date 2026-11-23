#nullable enable

using System;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// One deliberately excluded extension candidate on the published
    /// <see cref="RegistrySnapshot"/> (SPEC §C3 rule 5): a candidate the
    /// DiscoveryMode / <c>disabledOperations</c> policy rejected before its
    /// constructor ran. An intentional permission exclusion is not a
    /// registration error ("許可設定による意図的除外は登録エラーにしない"),
    /// so it is reported here — Registry state — and never in
    /// <see cref="RegistrySnapshot.Failures"/> or
    /// <see cref="RegistrySnapshot.CandidateFailures"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every entry carries only data safe to surface: the identity the
    /// pipeline already knew without constructing the candidate — the
    /// attribute-declared Operation (id, version) or the bound Provider ID,
    /// or the class's full name when no contract identity exists — the
    /// package it belongs to (null for a project assembly, the
    /// <c>"@project"</c> of SPEC §H4.1), its assembly and the fixed-English
    /// exclusion reason. Every stored string field passes through
    /// <see cref="CredentialMask"/> at this publication boundary (SPEC §C3
    /// error paragraph, §B7.1 item 4).
    /// </para>
    /// <para>
    /// <see cref="Kind"/> is never <see cref="RegistryCandidateKind.Type"/>:
    /// exclusions apply to whole candidates, and type offers exist only
    /// after a provider was admitted and invoked.
    /// </para>
    /// </remarks>
    public sealed record RegistryCandidateExclusion(
        RegistryCandidateKind Kind,
        string CandidateId,
        int? OperationVersion,
        string? PackageId,
        string? AssemblyName,
        string? SourceFile,
        string? Reason)
    {
        /// <summary>The candidate kind — Operation or TypeProvider only.</summary>
        public RegistryCandidateKind Kind { get; init; } =
            Kind == RegistryCandidateKind.Operation || Kind == RegistryCandidateKind.TypeProvider
                ? Kind
                : throw new ArgumentException(
                    "An exclusion can only name an Operation or TypeProvider candidate.",
                    nameof(Kind));

        /// <summary>
        /// The identity the pipeline learned without constructing the
        /// candidate: the publishable attribute-declared Operation ID for an
        /// Operation, the bound Provider ID for a TypeProvider, otherwise
        /// the class's full name.
        /// </summary>
        public string CandidateId { get; init; } =
            string.IsNullOrEmpty(CandidateId)
                ? throw new ArgumentException(
                    "Candidate ID must not be null or empty.", nameof(CandidateId))
                : CredentialMask.MaskText(CandidateId);

        /// <summary>
        /// The attribute-declared version for an Operation candidate —
        /// part of the whitelist <c>(package, assembly, id, version)</c>
        /// identity — null for TypeProvider entries, whose identity has no
        /// version.
        /// </summary>
        public int? OperationVersion { get; init; } = OperationVersion;

        /// <summary>
        /// The UPM package containing the candidate's assembly, or null for
        /// a project (unpackaged) assembly — the whitelist
        /// <c>"@project"</c> (SPEC §H4.1).
        /// </summary>
        public string? PackageId { get; init; } =
            PackageId is null ? null : CredentialMask.MaskText(PackageId);

        /// <summary>The candidate's defining assembly when known.</summary>
        public string? AssemblyName { get; init; } =
            AssemblyName is null ? null : CredentialMask.MaskText(AssemblyName);

        /// <summary>The project-relative source file when known.</summary>
        public string? SourceFile { get; init; } =
            SourceFile is null ? null : CredentialMask.MaskText(SourceFile);

        /// <summary>The fixed-English reason the exclusion predicate returned.</summary>
        public string? Reason { get; init; } =
            Reason is null ? null : CredentialMask.MaskText(Reason);
    }
}
