#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// Immutable snapshot of the registered type definitions (SPEC §B2). Built
    /// once per domain reload by <see cref="TypeRegistryBuilder"/> and never
    /// mutated afterwards; providers cannot reach its internals (SPEC §C0.1).
    /// </summary>
    public interface ITypeRegistry
    {
        /// <summary>
        /// Resolves a parsed Type ID to its registered definition.
        /// Unknown names fail with OW-TYPE-001; a known name used with the wrong
        /// number of generic arguments fails with OW-TYPE-005 (SPEC §B1/§J).
        /// </summary>
        Result<TypeDefinition> Resolve(TypeRef type);

        /// <summary>
        /// Whether a value of <paramref name="source"/> may be assigned to a
        /// position typed <paramref name="target"/> — the SPEC §B6 rules in
        /// order (identity, <c>core.Json</c>, transitive BaseTypes, covariant
        /// generic arguments with class relations through the snapshot's
        /// <see cref="IClassRelationProvider"/>, and no implicit conversion).
        /// Results are cached per (source, target) pair.
        /// </summary>
        AssignResult IsAssignable(TypeRef source, TypeRef target);

        /// <summary>Every registered definition, sorted by Type ID (Ordinal).</summary>
        IReadOnlyList<TypeDefinition> All { get; }
    }
}
