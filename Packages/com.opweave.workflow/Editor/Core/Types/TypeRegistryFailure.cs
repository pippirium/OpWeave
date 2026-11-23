#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// A structured internal record of one rejected candidate from a TypeRegistry
    /// build (SPEC §C3 step 4): the failure <see cref="Kind"/>, the affected
    /// <see cref="TypeId"/> (null for provider-scope failures) and the
    /// <see cref="ProviderId"/> of the offering provider, plus an English
    /// <see cref="Detail"/> suitable for a diagnostic hint and the offering
    /// provider's defining <see cref="AssemblyName"/> (SPEC §C3 error
    /// paragraph: the safe context carries the candidate's 出自アセンブリ).
    /// </summary>
    /// <remarks>
    /// <para>
    /// These records carry no diagnostic code or severity on purpose: the
    /// registration stage of a later ticket attaches the SPEC §J.1 OW-OP-013..019
    /// diagnostics, so the Core build stays free of registration-UI concerns.
    /// One record is emitted per rejected candidate — a Type ID collision
    /// between two providers therefore yields two
    /// <see cref="TypeRegistryFailureKind.DuplicateTypeId"/> records sharing the
    /// same <see cref="TypeId"/>.
    /// </para>
    /// <para>
    /// <see cref="TypeId"/> and <see cref="Detail"/> never carry an
    /// unvalidated ID verbatim: a malformed Type ID is recorded in the
    /// redacted form <see cref="TypeRegistryBuilder.RedactedTypeId"/>
    /// produces, so the record — and every published field derived from
    /// it — stays free of credentials and other unsafe text (SPEC §C3
    /// error paragraph). As a second line at the publication boundary,
    /// every stored string field additionally passes through
    /// <see cref="CredentialMask"/> at construction: an emitter that
    /// interpolated a credential-bearing ID or any other unsafe text is
    /// masked the same way on this record and on every
    /// <see cref="OpWeave.Core.Registry.RegistryCandidateFailure"/> or
    /// diagnostic derived from it. Canonical IDs are recorded unchanged.
    /// </para>
    /// </remarks>
    public sealed record TypeRegistryFailure(
        TypeRegistryFailureKind Kind,
        string? TypeId,
        string ProviderId,
        string Detail,
        string? AssemblyName = null)
    {
        /// <summary>The offered Type ID; null for provider-scope failures.</summary>
        public string? TypeId { get; init; } =
            TypeId is null ? null : CredentialMask.MaskText(TypeId);

        /// <summary>The offering provider's bound Provider ID.</summary>
        public string ProviderId { get; init; } = CredentialMask.MaskText(ProviderId);

        /// <summary>The fixed-English failure detail.</summary>
        public string Detail { get; init; } = CredentialMask.MaskText(Detail);

        /// <summary>The offering provider's defining assembly when known.</summary>
        public string? AssemblyName { get; init; } =
            AssemblyName is null ? null : CredentialMask.MaskText(AssemblyName);
    }
}
