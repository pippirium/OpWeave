#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The <c>typeDefinitionHash</c> of the weak Type fingerprint (SPEC §C0.2
    /// weak, §0.6 SG-11): the lowercase-hex SHA-256 of the TypeDefinition's
    /// Canonical JSON — the Descriptor analogue for types
    /// (<see cref="OpWeave.Core.Operations.DescriptorHash"/>).
    /// </summary>
    /// <remarks>
    /// SPEC fixes the hash inputs of the weak fingerprint but not the inner
    /// shape of a TypeDefinition's contract document; the projection here is a
    /// local, reversible choice frozen by the local:T-W2.2-06 tests. It
    /// carries every definition field of the §B2 record — camelCase member
    /// names and a snake_case <see cref="TypeKind"/> value (SPEC §0.4, §0.6
    /// SG-12) — and nothing else: the Codec is never a member (its identity
    /// is the separate SG-11 Codec identity object), the optional
    /// <see cref="TypeDefinition.GenericConstraint"/> serializes as explicit
    /// <c>null</c>, and the <see cref="JsonValue"/>-typed
    /// <see cref="TypeDefinition.Schema"/> fragment is omitted when the
    /// reference is null so a schema-less definition stays byte-distinct from
    /// one whose schema is an explicit JSON <c>null</c> (SPEC §A0) — the same
    /// convention <see cref="OpWeave.Core.Operations.DescriptorHash"/> applies
    /// to JsonValue members.
    /// </remarks>
    public static class TypeDefinitionHash
    {
        /// <summary>
        /// The lowercase-hex SHA-256 of <paramref name="definition"/>'s
        /// Canonical JSON (SPEC §0.6 SG-12).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
        public static string Compute(TypeDefinition definition)
        {
            if (definition is null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            return CanonicalJson.Sha256(ToToken(definition));
        }

        /// <summary>The canonical-JSON token the hash is computed over.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
        public static JsonValue ToJsonValue(TypeDefinition definition)
        {
            if (definition is null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            return JsonValue.From(ToToken(definition));
        }

        private static JObject ToToken(TypeDefinition definition)
        {
            var baseTypes = new JArray();
            foreach (var baseType in definition.BaseTypes)
            {
                baseTypes.Add(baseType);
            }
            var root = new JObject
            {
                ["typeId"] = definition.Id,
                ["kind"] = JsonNaming.ToSnakeCase(definition.Kind.ToString()),
                ["genericArity"] = definition.GenericArity,
                ["baseTypes"] = baseTypes,
                ["genericConstraint"] = definition.GenericConstraint is null
                    ? JValue.CreateNull()
                    : new JValue(definition.GenericConstraint),
                ["description"] = definition.Description,
            };
            if (definition.Schema is not null)
            {
                root["schema"] = definition.Schema.ToToken();
            }
            return root;
        }
    }
}
