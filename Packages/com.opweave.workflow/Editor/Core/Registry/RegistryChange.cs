#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// The closed <see cref="RegistryChange.Kind"/> vocabulary of SPEC §C3
    /// rule 7, recorded to <c>registry-changes.ndjson</c> and surfaced through
    /// <c>get_registry_status</c>, the Registry tab and the
    /// <c>RegistryChanged</c> event.
    /// </summary>
    public static class RegistryChangeKinds
    {
        public const string Added = "added";
        public const string Removed = "removed";
        public const string DescriptorChanged = "descriptorChanged";
        public const string ImplementationChanged = "implementationChanged";
        public const string Failed = "failed";
    }

    /// <summary>
    /// One entry of the Registry diff between two consecutive published
    /// snapshots (SPEC §C3 rule 7): the change kind, the affected Operation's
    /// id and version, its project-relative source file when known, and the
    /// diagnostics attached to the change — non-empty for
    /// <see cref="RegistryChangeKinds.Failed"/> records, which a build emits
    /// per rejected candidate. Every stored string field and every stored
    /// diagnostic passes through <see cref="CredentialMask"/> at this
    /// publication boundary so an emitter that interpolated an unvalidated
    /// ID cannot leak a credential (SPEC §C3 error paragraph).
    /// </summary>
    public sealed record RegistryChange(
        string Kind,
        string OperationId,
        int Version,
        string? SourceFile,
        IReadOnlyList<Diagnostic> Diagnostics)
    {
        /// <summary>One of the <see cref="RegistryChangeKinds"/> constants.</summary>
        public string Kind { get; init; } =
            CheckKind(Kind is null ? null : CredentialMask.MaskText(Kind));

        /// <summary>The affected Operation ID.</summary>
        public string OperationId { get; init; } =
            string.IsNullOrEmpty(OperationId)
                ? throw new ArgumentException(
                    "Operation ID must not be null or empty.", nameof(OperationId))
                : CredentialMask.MaskText(OperationId);

        /// <summary>The project-relative source file when known.</summary>
        public string? SourceFile { get; init; } =
            SourceFile is null ? null : CredentialMask.MaskText(SourceFile);

        /// <summary>The diagnostics attached to the change; never null.</summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; init; } =
            CheckDiagnostics(Diagnostics);

        private static string CheckKind(string? kind) =>
            kind == RegistryChangeKinds.Added ||
            kind == RegistryChangeKinds.Removed ||
            kind == RegistryChangeKinds.DescriptorChanged ||
            kind == RegistryChangeKinds.ImplementationChanged ||
            kind == RegistryChangeKinds.Failed
                ? kind
                : throw new ArgumentException(
                    $"Unknown registry change kind '{kind ?? "<null>"}'.", nameof(Kind));

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
