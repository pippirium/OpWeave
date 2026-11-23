#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// The kind of a <see cref="TypeRegistryFailure"/> produced while building a
    /// TypeRegistry snapshot (SPEC §B0.1, §C3 step 4). These kinds are the
    /// internal, structured form of the registration rejections; a later stage
    /// (T-W2.12-03) maps them to the published OW-OP-013..019 diagnostics —
    /// no error code is attached here.
    /// </summary>
    public enum TypeRegistryFailureKind
    {
        /// <summary>
        /// The Provider ID was declared by more than one candidate; all of them
        /// were rejected (SPEC §C3 step 4 → OW-OP-013). Provider-scope failure.
        /// </summary>
        DuplicateProviderId,

        /// <summary>
        /// <see cref="ITypeProvider.GetTypes"/> threw, returned null or yielded a
        /// null candidate; every definition from that provider was rejected.
        /// Provider-scope failure (SPEC §C3 → OW-OP-016/019 class).
        /// </summary>
        ProviderFailure,

        /// <summary>
        /// A non-standard provider offered a Type ID inside the reserved
        /// namespaces <c>core.*</c>, <c>unity.*</c> or <c>opweave.*</c>
        /// (SPEC §B0.1: reserved-namespace spoofing is refused).
        /// </summary>
        ReservedNamespace,

        /// <summary>
        /// The candidate's <see cref="TypeDefinition.Id"/> is not a canonical
        /// SPEC §B1 Type ID — including an absent or misplaced <c>&lt;&gt;</c>
        /// generic-definition marker or an unrecognized <see cref="TypeKind"/>.
        /// </summary>
        MalformedTypeId,

        /// <summary>
        /// Two or more candidates declared the same Type ID; every candidate
        /// for that ID was invalidated, never just the later one
        /// (SPEC §B0.1 → OW-OP-013).
        /// </summary>
        DuplicateTypeId,

        /// <summary>
        /// The candidate has no Codec — it passed a null
        /// <see cref="TypeDefinition.Codec"/> (SPEC §C3 step 4 → OW-OP-016).
        /// </summary>
        MissingCodec,

        /// <summary>
        /// The candidate's Codec provably violates the SPEC §B7.1
        /// normalization contract on the registration-time probe — it
        /// throws, returns null or an explicit <c>null</c>, produces a
        /// value it then rejects, or is not idempotent (SPEC §B7.1 item 4
        /// → OW-OP-016).
        /// </summary>
        CodecContractViolation,

        /// <summary>
        /// A <see cref="TypeDefinition.BaseTypes"/> entry does not resolve to a
        /// registered type — unparsable, unknown, wrong arity or containing an
        /// unregistered type argument (SPEC §C3 step 4 → OW-OP-014). Also used
        /// when the base was itself rejected earlier.
        /// </summary>
        UnregisteredBaseType,

        /// <summary>
        /// The candidate sits on a cycle of <see cref="TypeDefinition.BaseTypes"/>
        /// relations (SPEC §C3 step 4 → OW-OP-015).
        /// </summary>
        BaseTypeCycle,
    }
}
