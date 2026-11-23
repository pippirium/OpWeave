#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// One registered type: Type ID, kind, base types, generic arity and Codec
    /// (SPEC §B2, §B0.1). Generic definitions carry the unbound marker in
    /// <see cref="Id"/> (e.g. <c>unity.ComponentRef&lt;&gt;</c>) with
    /// <see cref="GenericArity"/> &gt; 0; non-generic definitions use the bare
    /// Type ID and arity 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="BaseTypes"/> lists the Type IDs this type is explicitly
    /// assignable to (SPEC §B6 rule 3); entries may be closed generic
    /// instantiations such as <c>unity.AssetRef&lt;unity.GameObject&gt;</c>.
    /// Whether a base type is registered is a snapshot-build concern, not a
    /// record invariant.
    /// </para>
    /// <para>
    /// The record validates only the structural invariants a candidate cannot
    /// violate without corrupting itself — a non-empty <see cref="Id"/>, a
    /// non-negative <see cref="GenericArity"/> and non-null collections.
    /// <see cref="Codec"/> is deliberately <em>not</em> null-checked at
    /// construction: a provider must be able to hand a codec-less candidate to
    /// <see cref="TypeRegistryBuilder"/> so the build can reject that specific
    /// definition with a <see cref="TypeRegistryFailureKind.MissingCodec"/>
    /// record carrying its Type ID (SPEC §C3 step 4 → OW-OP-016). Registered
    /// definitions in a built snapshot always have a non-null
    /// <see cref="Codec"/>.
    /// </para>
    /// </remarks>
    public sealed record TypeDefinition(
        string Id,
        TypeKind Kind,
        int GenericArity,
        IReadOnlyList<string> BaseTypes,
        string? GenericConstraint,
        string Description,
        IValueCodec Codec)
    {
        /// <summary>The canonical Type ID; generic definitions end in <c>&lt;&gt;</c>.</summary>
        public string Id { get; init; } =
            string.IsNullOrEmpty(Id)
                ? throw new ArgumentException(
                    "Type ID must not be null or empty.", nameof(Id))
                : Id;

        /// <summary>The number of generic arguments a use of this type takes; 0 for non-generic types.</summary>
        public int GenericArity { get; init; } =
            GenericArity < 0
                ? throw new ArgumentOutOfRangeException(
                    nameof(GenericArity), GenericArity, "Generic arity must not be negative.")
                : GenericArity;

        private readonly IReadOnlyList<string> _baseTypes = CheckBaseTypes(BaseTypes);

        /// <summary>The Type IDs this type is explicitly assignable to (SPEC §B6 rule 3).</summary>
        public IReadOnlyList<string> BaseTypes
        {
            get => _baseTypes;
            init => _baseTypes = CheckBaseTypes(value);
        }

        /// <summary>Optional generic argument constraint such as <c>unity.Component</c> (SPEC §B2).</summary>
        public string? GenericConstraint { get; init; } = GenericConstraint;

        /// <summary>Human-readable description of the type (SPEC §B0.1).</summary>
        public string Description { get; init; } =
            Description ?? throw new ArgumentNullException(nameof(Description));

        /// <summary>
        /// The Codec validating and normalizing this type's values (SPEC §B7/§B7.1).
        /// May be null at runtime when a misbehaving provider passes one;
        /// <see cref="TypeRegistryBuilder"/> rejects such candidates.
        /// </summary>
        public IValueCodec Codec { get; init; } = Codec;

        private readonly JsonValue? _schema;

        /// <summary>
        /// The optional SPEC §B8 JSON Schema fragment declaring this type's JSON
        /// shape (SPEC §B0.1 "Schema情報"), or null. This is the public contract by
        /// which a custom <see cref="ITypeProvider"/> supplies its type's schema:
        /// external Codecs cannot implement the internal schema hook, so the
        /// fragment travels on the definition itself. The value must be a JSON
        /// object; its members use the §B8 keyword subset, and references to
        /// other types are <c>"$ref": "#/$defs/&lt;TypeId&gt;"</c> members, which
        /// <see cref="TypeSchemas.Generate"/> registers into the emitted
        /// <c>$defs</c>. A fragment carrying any member name outside the §B8
        /// subset — at the root or in a nested schema — is never emitted:
        /// generation degrades to the same unconstrained fragment a
        /// schema-less definition gets. Built-in types leave it null and let
        /// their Codec write the fragment through the internal hook.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The value is not a JSON object.
        /// </exception>
        public JsonValue? Schema
        {
            get => _schema;
            init => _schema = CheckSchema(value);
        }

        private static JsonValue? CheckSchema(JsonValue? schema)
        {
            if (schema is not null && schema.ToToken() is not JObject)
            {
                throw new ArgumentException(
                    "A type schema fragment must be a JSON object.", nameof(Schema));
            }
            return schema;
        }

        private static IReadOnlyList<string> CheckBaseTypes(IReadOnlyList<string> baseTypes)
        {
            var copy = ReadOnlyCollections.CopyList(baseTypes);
            for (var i = 0; i < copy.Count; i++)
            {
                if (string.IsNullOrEmpty(copy[i]))
                {
                    throw new ArgumentException(
                        "Base type IDs must not be null or empty.", nameof(baseTypes));
                }
            }
            return copy;
        }
    }
}
