#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The standard <see cref="ITypeProvider"/> of SPEC §B2/§0.6 SG-10 — the
    /// <em>only</em> provider that registers the standard <c>unity.*</c>
    /// definitions into the production TypeRegistry (T-W2.1-05).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The definitions themselves are the Unity-independent JSON shapes of
    /// <see cref="UnityShapeTypes"/> (T-W1.4-05): IDs, kinds, base types,
    /// constraints and shape validation are exactly the Core contract. The
    /// provider adds what only the Editor side can supply — the
    /// <see cref="IClrValueCodec{T}"/> conversion of SPEC §B7 for the §B4
    /// value types, built on the same shape codecs (SPEC §0.6 SG-10: "builds
    /// its CLR conversion on these same shape validators"). Every other
    /// definition keeps its Core Codec unchanged: the string types of §B3
    /// are their own CLR value, the Ref types of §B5 stay JSON for the G2
    /// resolver, and class IDs carry no JSON value.
    /// </para>
    /// <para>
    /// Because a Codec implemented outside OpWeave.Core cannot implement the
    /// internal <c>ITypeSchemaCodec</c> hook, the six CLR-backed definitions
    /// declare their SPEC §B8 fragment through
    /// <see cref="TypeDefinition.Schema"/> — the public contract designed
    /// for exactly this case (SPEC §B0.1). The fragments are the same shapes
    /// the Core codecs emit; the EditMode tests assert byte-identical schema
    /// output between a registry built on this provider and one built on the
    /// raw shape definitions, so a drift between the two cannot ship
    /// silently.
    /// </para>
    /// <para>
    /// Stateless and deterministic (SPEC §C0.1): every call returns the same
    /// immutable list. Like <see cref="CoreTypeProvider"/> the provider is
    /// deliberately <em>not</em> marked with
    /// <see cref="OpWeaveTypeProviderAttribute"/>: it is wired into the
    /// registry by the platform's registration stage, not discovered from
    /// extension assemblies — a discovered re-offer of the same Type IDs
    /// would invalidate both candidates (SPEC §B0.1 → OW-OP-013).
    /// </para>
    /// </remarks>
    public sealed class UnityTypeProvider : ITypeProvider
    {
        /// <summary>
        /// The Provider ID the §C3 registration stage assigns the standard
        /// unity.* provider. It is deliberately distinct from
        /// <see cref="CoreTypeProvider.ProviderId"/> (<c>@builtin</c>, SPEC
        /// §0.6 SG-11): the type build rejects a duplicated Provider ID
        /// outright, and the two providers live in different assemblies so
        /// their fingerprints can diverge independently.
        /// </summary>
        public const string ProviderId = "opweave.unity-types";

        private static readonly string[] s_components2 = { "x", "y" };
        private static readonly string[] s_components3 = { "x", "y", "z" };
        private static readonly string[] s_components4 = { "x", "y", "z", "w" };

        private static readonly IReadOnlyList<TypeDefinition> s_types = CreateTypes();

        /// <inheritdoc/>
        public IEnumerable<TypeDefinition> GetTypes() => s_types;

        private static IReadOnlyList<TypeDefinition> CreateTypes()
        {
            var definitions = new List<TypeDefinition>(UnityShapeTypes.All.Count);
            foreach (var definition in UnityShapeTypes.All)
            {
                switch (definition.Id)
                {
                    case "unity.Vector2":
                        definitions.Add(WithClr(
                            definition, new Vector2Codec(definition.Codec), s_components2));
                        break;
                    case "unity.Vector3":
                        definitions.Add(WithClr(
                            definition, new Vector3Codec(definition.Codec), s_components3));
                        break;
                    case "unity.Vector4":
                        definitions.Add(WithClr(
                            definition, new Vector4Codec(definition.Codec), s_components4));
                        break;
                    case "unity.Quaternion":
                        definitions.Add(WithClr(
                            definition, new QuaternionCodec(definition.Codec), s_components4));
                        break;
                    case "unity.Euler":
                        definitions.Add(WithClr(
                            definition, new EulerCodec(definition.Codec), s_components3));
                        break;
                    case "unity.Color":
                        definitions.Add(definition with
                        {
                            Codec = new ColorCodec(definition.Codec),
                            Schema = ColorSchema(),
                        });
                        break;
                    default:
                        definitions.Add(definition);
                        break;
                }
            }
            return ReadOnlyCollections.CopyList(definitions);
        }

        private static TypeDefinition WithClr(
            TypeDefinition definition, IValueCodec codec, string[] components) =>
            definition with { Codec = codec, Schema = ObjectSchema(components) };

        // The §B8 fragment of a component object — the same keywords the
        // Core shape codecs emit through the internal schema hook (strict
        // shape: every component required, no unknown members).
        private static JsonValue ObjectSchema(string[] components)
        {
            var properties = new JObject();
            var required = new JArray();
            for (var i = 0; i < components.Length; i++)
            {
                properties[components[i]] = new JObject { ["type"] = "number" };
                required.Add(components[i]);
            }
            return JsonValue.From(new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            });
        }

        // The §B8 fragment of unity.Color: object and string keywords in one
        // fragment (each applies only to its own JSON kind), with the
        // ECMA-262 form of the '#RRGGBBAA' pattern — the strict end anchor
        // written as the (?![\s\S]) "no character follows" lookahead.
        private static JsonValue ColorSchema() =>
            JsonValue.From(new JObject
            {
                ["type"] = new JArray("object", "string"),
                ["properties"] = new JObject
                {
                    ["r"] = new JObject { ["type"] = "number" },
                    ["g"] = new JObject { ["type"] = "number" },
                    ["b"] = new JObject { ["type"] = "number" },
                    ["a"] = new JObject { ["type"] = "number" },
                },
                ["required"] = new JArray("r", "g", "b", "a"),
                ["additionalProperties"] = false,
                ["pattern"] = "^#[0-9A-Fa-f]{8}(?![\\s\\S])",
            });
    }
}
