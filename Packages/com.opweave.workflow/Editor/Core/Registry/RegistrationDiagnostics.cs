#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// The SPEC §J.1 OW-OP-013..019 mapping of the internal
    /// <see cref="TypeRegistryFailure"/> records the type build produces
    /// (T-W2.12-03, SPEC §C3 step 4 and the §C3 error paragraph).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure kind maps to a published code only where a §J.1 meaning
    /// applies exactly; the record's fixed English
    /// <see cref="TypeRegistryFailure.Detail"/> becomes the diagnostic
    /// message — it never embeds exception text or secrets — and the
    /// failure's Type/Provider IDs supply the safe context SPEC §C3
    /// requires ("Type/Operation/Provider ID、出自アセンブリ、診断コード、
    /// 安全な理由・hint").
    /// </para>
    /// <para>
    /// SPEC §C3 step 4 and §J.1 assign published codes to a provider-scope
    /// duplicate Provider ID and a Type ID collision (OW-OP-013),
    /// unresolvable base references (OW-OP-014), a base-type cycle
    /// (OW-OP-015), a missing Codec or a normalization-contract violation
    /// detectable at registration (OW-OP-016) and a reserved-namespace
    /// offer by a non-standard provider (OW-OP-007, the single
    /// reserved-namespace code). They assign <em>no</em> code to a
    /// non-canonical Type ID or unrecognized <see cref="TypeKind"/>
    /// (<see cref="TypeRegistryFailureKind.MalformedTypeId"/> — OW-OP-016
    /// is the Codec bucket only) or to an already constructed provider's
    /// <c>GetTypes()</c> throwing, returning null or yielding a null
    /// definition (<see cref="TypeRegistryFailureKind.ProviderFailure"/> —
    /// OW-OP-019 is the construction failure only). Those stay the safe
    /// empty-code internal diagnostics the prior extension tickets emit;
    /// a published code is never invented.
    /// </para>
    /// </remarks>
    public static class RegistrationDiagnostics
    {
        /// <summary>
        /// The published <see cref="Diagnostic"/> for one type-build
        /// rejection; the failure's own detail becomes the message and a
        /// hint names the affected type or provider. A kind with no
        /// assigned published code produces an empty-code internal
        /// diagnostic (see <see cref="CodeFor"/>).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="failure"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="failure"/> carries an unrecognized kind — an
        /// unmapped internal kind must never reach a published code
        /// (fail closed at development time, not silently).
        /// </exception>
        public static Diagnostic For(TypeRegistryFailure failure)
        {
            if (failure is null)
            {
                throw new ArgumentNullException(nameof(failure));
            }
            var code = CodeFor(failure.Kind);
            return code.Length == 0
                ? new Diagnostic(string.Empty, Severity.Error, failure.Detail,
                    Hint: HintFor(failure))
                : ErrorCodes.Create(code, failure.Detail, hint: HintFor(failure));
        }

        /// <summary>
        /// The <see cref="RegistryCandidateFailure"/> view of one type-build
        /// rejection: provider-scope kinds become a
        /// <see cref="RegistryCandidateKind.TypeProvider"/> entry keyed by
        /// Provider ID; definition-scope kinds become a
        /// <see cref="RegistryCandidateKind.Type"/> entry keyed by Type ID
        /// with the offering provider attached. The offering provider's
        /// defining assembly is attached either way (SPEC §C3 error
        /// paragraph).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="failure"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Unrecognized kind.</exception>
        public static RegistryCandidateFailure ForCandidate(TypeRegistryFailure failure)
        {
            var diagnostic = For(failure);
            var providerScope = failure.TypeId is null;
            return new RegistryCandidateFailure(
                providerScope
                    ? RegistryCandidateKind.TypeProvider
                    : RegistryCandidateKind.Type,
                failure.TypeId ?? failure.ProviderId,
                providerScope ? null : failure.ProviderId,
                failure.AssemblyName,
                SourceFile: null,
                new[] { diagnostic });
        }

        /// <summary>
        /// The SPEC §J.1 code for <paramref name="kind"/>, or
        /// <see cref="string.Empty"/> when §C3/§J.1 assigns no published
        /// code to the kind — the rejection stays an internal diagnostic.
        /// </summary>
        public static string CodeFor(TypeRegistryFailureKind kind) => kind switch
        {
            TypeRegistryFailureKind.DuplicateProviderId => ErrorCodes.OW_OP_013,
            TypeRegistryFailureKind.DuplicateTypeId => ErrorCodes.OW_OP_013,
            TypeRegistryFailureKind.UnregisteredBaseType => ErrorCodes.OW_OP_014,
            TypeRegistryFailureKind.BaseTypeCycle => ErrorCodes.OW_OP_015,
            TypeRegistryFailureKind.MissingCodec => ErrorCodes.OW_OP_016,
            TypeRegistryFailureKind.CodecContractViolation => ErrorCodes.OW_OP_016,
            TypeRegistryFailureKind.MalformedTypeId => string.Empty,
            TypeRegistryFailureKind.ProviderFailure => string.Empty,
            TypeRegistryFailureKind.ReservedNamespace => ErrorCodes.OW_OP_007,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Unmapped TypeRegistryFailureKind."),
        };

        // The safe context of SPEC §C3: which registered identity is broken
        // and who offered it — IDs only, never exception text.
        private static string? HintFor(TypeRegistryFailure failure) =>
            failure.TypeId is null
                ? $"Provider ID '{failure.ProviderId}'."
                : $"Type ID '{failure.TypeId}' offered by provider '{failure.ProviderId}'.";
    }
}
