#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// One rejected registration candidate on the published
    /// <see cref="RegistrySnapshot"/> (SPEC §C3): the safe diagnostic data
    /// <c>get_registry_status</c>, the Registry tab and Console report —
    /// the candidate's kind, the Type/Operation/Provider ID (or the class's
    /// full name when no contract identity was learned), the defining
    /// assembly and the published OW-* diagnostics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every entry carries only data safe to surface to an Agent: IDs,
    /// assembly names, fixed-English messages and hints. Exception text,
    /// credentials and source content never reach this record — every
    /// stored string field and every stored diagnostic passes through
    /// <see cref="CredentialMask"/> at this publication boundary, so an
    /// emitter that interpolated an unvalidated ID cannot leak a
    /// credential (SPEC §C3 error paragraph, §B7.1 item 4).
    /// </para>
    /// <para>
    /// <see cref="ProviderId"/> is set only for
    /// <see cref="RegistryCandidateKind.Type"/> entries — the provider that
    /// offered the rejected <see cref="TypeDefinition"/>; for provider-scope
    /// rejections (<see cref="RegistryCandidateKind.TypeProvider"/>) the
    /// Provider ID itself is the <see cref="CandidateId"/>.
    /// </para>
    /// </remarks>
    public sealed record RegistryCandidateFailure(
        RegistryCandidateKind Kind,
        string CandidateId,
        string? ProviderId,
        string? AssemblyName,
        string? SourceFile,
        IReadOnlyList<Diagnostic> Diagnostics)
    {
        /// <summary>The candidate kind.</summary>
        public RegistryCandidateKind Kind { get; init; } = Kind;

        /// <summary>
        /// The best identity the pipeline learned: the Type ID for a type
        /// offer, the bound Provider ID for a provider-scope rejection, the
        /// Operation ID once its Descriptor was read, otherwise the
        /// candidate class's full name.
        /// </summary>
        public string CandidateId { get; init; } =
            string.IsNullOrEmpty(CandidateId)
                ? throw new ArgumentException(
                    "Candidate ID must not be null or empty.", nameof(CandidateId))
                : CredentialMask.MaskText(CandidateId);

        /// <summary>The offering provider for type-scope entries.</summary>
        public string? ProviderId { get; init; } =
            ProviderId is null ? null : CredentialMask.MaskText(ProviderId);

        /// <summary>The candidate's defining assembly when known.</summary>
        public string? AssemblyName { get; init; } =
            AssemblyName is null ? null : CredentialMask.MaskText(AssemblyName);

        /// <summary>The project-relative source file when known.</summary>
        public string? SourceFile { get; init; } =
            SourceFile is null ? null : CredentialMask.MaskText(SourceFile);

        /// <summary>The rejection diagnostics; never null, entries never null.</summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; init; } =
            CheckDiagnostics(Diagnostics);

        private static IReadOnlyList<Diagnostic> CheckDiagnostics(
            IReadOnlyList<Diagnostic> diagnostics)
        {
            var masked = new List<Diagnostic>(diagnostics.Count);
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var diagnostic = diagnostics[i];
                if (diagnostic is null)
                {
                    throw new ArgumentException(
                        "Diagnostic entries must not be null.", nameof(diagnostics));
                }
                masked.Add(CredentialMask.MaskDiagnostic(diagnostic));
            }
            return ReadOnlyCollections.CopyList(masked);
        }
    }
}
