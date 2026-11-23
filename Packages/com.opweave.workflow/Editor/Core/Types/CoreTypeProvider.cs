#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// The standard <see cref="ITypeProvider"/> shipping the built-in
    /// <c>core.*</c> type definitions of SPEC §B3 — Core provides
    /// <c>core.*</c>, OpWeave.UnityAdapter provides <c>unity.*</c>
    /// (SPEC §B2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The registration stage (SPEC §C3) wraps this provider in a
    /// <see cref="TypeProviderCandidate"/> carrying the fixed Provider ID
    /// <see cref="ProviderId"/> (<c>@builtin</c>, SPEC §0.6 SG-11) and
    /// <see cref="OperationOrigin.Standard"/> — the only origin allowed to
    /// register the reserved <c>core.*</c> namespace
    /// (<see cref="TypeRegistryBuilder"/>, SPEC §B0.1).
    /// </para>
    /// <para>
    /// The provider is deliberately <em>not</em> marked with
    /// <see cref="OpWeaveTypeProviderAttribute"/>: it is wired into the
    /// registry by the platform itself, not discovered from extension
    /// assemblies. Attribute-based discovery would offer the same Type IDs a
    /// second time and the SPEC §B0.1 collision rule would invalidate both
    /// candidates.
    /// </para>
    /// <para>
    /// Stateless and deterministic (SPEC §C0.1): every call returns the same
    /// immutable six-definition list — <c>core.String</c>, <c>core.Int</c>,
    /// <c>core.Float</c>, <c>core.Bool</c>, <c>core.Json</c> and
    /// <c>core.List&lt;&gt;</c> — each with its built-in Codec (SPEC §B7).
    /// </para>
    /// </remarks>
    public sealed class CoreTypeProvider : ITypeProvider
    {
        /// <summary>The fixed Provider ID of the built-in provider (SPEC §0.6 SG-11).</summary>
        public const string ProviderId = "@builtin";

        private static readonly IReadOnlyList<TypeDefinition> s_types = CreateTypes();

        /// <inheritdoc/>
        public IEnumerable<TypeDefinition> GetTypes() => s_types;

        private static IReadOnlyList<TypeDefinition> CreateTypes()
        {
            var noBases = Array.Empty<string>();
            return ReadOnlyCollections.CopyList(new[]
            {
                new TypeDefinition(
                    "core.String",
                    TypeKind.Primitive,
                    0,
                    noBases,
                    null,
                    "JSON string.",
                    StringCodec.Instance),
                new TypeDefinition(
                    "core.Int",
                    TypeKind.Primitive,
                    0,
                    noBases,
                    null,
                    "JSON integer (64-bit).",
                    IntCodec.Instance),
                new TypeDefinition(
                    "core.Float",
                    TypeKind.Primitive,
                    0,
                    noBases,
                    null,
                    "JSON number (finite binary64); integer tokens convert to Double.",
                    FloatCodec.Instance),
                new TypeDefinition(
                    "core.Bool",
                    TypeKind.Primitive,
                    0,
                    noBases,
                    null,
                    "JSON boolean.",
                    BoolCodec.Instance),
                new TypeDefinition(
                    "core.Json",
                    TypeKind.Json,
                    0,
                    noBases,
                    null,
                    "Any JSON value; keeps the written tokens and admits explicit null.",
                    JsonCodec.Instance),
                new TypeDefinition(
                    "core.List<>",
                    TypeKind.List,
                    1,
                    noBases,
                    null,
                    "JSON array whose elements have the element type.",
                    ListCodec.Instance),
            });
        }
    }
}
