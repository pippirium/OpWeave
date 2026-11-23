#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The SPEC §B8 schema generator: produces a deterministic JSON Schema
    /// fragment (draft 2020-12 subset) describing the JSON shape of a
    /// registered type, for C8's describe and the H2 public surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the SPEC §B8 keyword subset is emitted — <c>type</c>,
    /// <c>properties</c>, <c>required</c>, <c>additionalProperties</c>,
    /// <c>items</c>, <c>enum</c>, <c>pattern</c>, <c>minimum</c>,
    /// <c>maximum</c>, <c>maxLength</c>, <c>description</c>, <c>$ref</c> and
    /// <c>$defs</c>. A type referenced inside a fragment (a
    /// <c>core.List&lt;T&gt;</c> element, a ComponentRef's
    /// <c>owner</c>) is collected into <c>$defs</c> and pointed to by
    /// <c>"$ref": "#/$defs/&lt;TypeId&gt;"</c>. The result is a
    /// <see cref="JsonValue"/>, so keys are in Canonical JSON ordinal order
    /// and identical input yields identical bytes
    /// (<see cref="JsonValue.CanonicalText"/>).
    /// </para>
    /// <para>
    /// Local, reversible choices where the SPEC is silent:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The signature takes what a port position knows — the registry, the
    /// <see cref="TypeRef"/>, the <see cref="PortConstraints"/>, an optional
    /// description and the SPEC §B0.1/§A5 nullable flag. A null
    /// <c>description</c> argument falls back to the type's own
    /// <see cref="TypeDefinition.Description"/>.
    /// </description></item>
    /// <item><description>
    /// Emitted <c>pattern</c> strings are ECMA-262-compatible: the .NET-only
    /// strict end anchor <c>\z</c> of the codec-side
    /// <see cref="System.Text.RegularExpressions.Regex"/> instances is written
    /// <c>(?![\s\S])</c>, a lookahead asserting "no character follows" that is
    /// valid in ECMA-262 and keeps the codec's strict end in every dialect —
    /// a <c>$</c> would let a .NET-based validator accept a trailing newline
    /// the Codec rejects.
    /// </description></item>
    /// <item><description>
    /// Kind-discriminated Ref shapes cannot be expressed exactly without
    /// <c>oneOf</c>/<c>if</c>: a Ref fragment enumerates the accepted
    /// <c>kind</c> values and unions their key sets, so it may accept a
    /// key/kind combination the Codec rejects (e.g. <c>prefabAsset</c> on
    /// <c>"kind":"gameObject"</c> for <c>unity.GameObjectRef</c>). Same for a
    /// value that matches neither of <c>unity.Color</c>'s two forms: both
    /// object and string keywords appear and each applies only to its own
    /// JSON kind.
    /// </description></item>
    /// <item><description>
    /// A type whose provider declares <see cref="TypeDefinition.Schema"/>
    /// (SPEC §B0.1) emits that fragment verbatim, but only after it is
    /// validated against the §B8 keyword subset — recursively, with the
    /// member names under <c>properties</c>/<c>$defs</c> read as property
    /// and definition names rather than keywords. SPEC §B8 permits no
    /// other keyword in generated output and §J assigns no error code to
    /// a malformed provider fragment, so a fragment carrying one is never
    /// emitted: emission continues exactly as if no Schema were declared.
    /// Valid fragments have their
    /// <c>"$ref": "#/$defs/&lt;TypeId&gt;"</c> members registered and
    /// rewritten to the canonical Type ID form; a Codec that does not
    /// implement <see cref="ITypeSchemaCodec"/> on a definition without a
    /// usable Schema yields the unconstrained fragment <c>{}</c>: an honest
    /// "shape unknown to Core" that still receives the port's constraints,
    /// the nullable flag and the description.
    /// </description></item>
    /// <item><description>
    /// A class-id type (<see cref="TypeKind.Class"/>) carries no JSON value,
    /// so its fragment is <c>{"enum":[]}</c>: draft 2020-12 makes the array
    /// only SHOULD-non-empty, and no instance satisfies an empty enum.
    /// </description></item>
    /// <item><description>
    /// <see cref="PortConstraints.Enum"/> intersects with a codec-emitted
    /// <c>enum</c> rather than replacing it, so a constraint can never
    /// widen a never-valid fragment; <see cref="PortConstraints.Pattern"/>
    /// conjoins with a codec-emitted <c>pattern</c> through an anchored
    /// lookahead, since a schema object holds only one <c>pattern</c> and
    /// <c>allOf</c> is not in the §B8 subset. <c>minimum</c>,
    /// <c>maximum</c> and <c>maxLength</c> likewise intersect: the stricter
    /// of the provider-declared and the port bound wins, so a port
    /// constraint narrows the type's declared shape, never widens it.
    /// Non-finite <see cref="PortConstraints.Min"/>/<see cref="PortConstraints.Max"/>
    /// bounds are omitted: they are vacuous comparisons the JSON value model
    /// cannot serialize (SPEC §0.3).
    /// </description></item>
    /// <item><description>
    /// The §B0.1/§A5 nullable flag widens a fragment's own <c>type</c> and
    /// <c>enum</c>; a fragment whose instance must also satisfy a root
    /// <c>$ref</c> admits explicit null only when the referenced schema
    /// does (in draft 2020-12 <c>$ref</c> applies alongside its siblings —
    /// they cannot relax it), so the flag propagates into the referenced
    /// <c>$defs</c> entry. When the same Type ID is also referenced from a
    /// non-null position of the same generated schema, its single shared
    /// definition admits null there too — the same kind of approximation
    /// the Ref-shapes bullet above documents.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class TypeSchemas
    {
        /// <summary>
        /// The SPEC §B8 keyword subset: every object member the generator
        /// emits has one of these names, and a conforming schema must not
        /// carry any other member name.
        /// </summary>
        internal static readonly ISet<string> AllowedKeywords =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "type", "properties", "required", "additionalProperties",
                "items", "enum", "pattern", "minimum", "maximum",
                "maxLength", "description", "$ref", "$defs",
            };

        /// <summary>The <c>$ref</c> prefix pointing into <c>$defs</c> (SPEC §B8).</summary>
        internal const string DefsRefPrefix = "#/$defs/";

        /// <summary>
        /// The ECMA-262 strict end assertion every emitted end anchor becomes:
        /// "no character follows", matching the codec-side <c>\z</c> exactly.
        /// </summary>
        private const string StrictEndAssertion = @"(?![\s\S])";

        /// <summary>
        /// Generates the JSON Schema fragment of <paramref name="type"/> as a
        /// <see cref="JsonValue"/> (SPEC §B8). <paramref name="constraints"/>,
        /// <paramref name="description"/> and <paramref name="nullable"/>
        /// specialize the fragment for a port position; a resolution failure
        /// returns the registry's diagnostics unchanged (OW-TYPE-001,
        /// OW-TYPE-005).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registry"/> or <paramref name="type"/> is null.
        /// </exception>
        public static Result<JsonValue> Generate(
            ITypeRegistry registry,
            TypeRef type,
            PortConstraints? constraints = null,
            string? description = null,
            bool nullable = false)
        {
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            var resolved = registry.Resolve(type);
            if (!resolved.Ok)
            {
                return Result<JsonValue>.Failure(resolved.Diagnostics);
            }
            var definition = resolved.Value!;
            var defs = new TypeSchemaDefs(registry);
            var schema = EmitFragment(definition, defs);
            ApplyConstraints(schema, constraints);
            if (nullable)
            {
                ApplyNullable(schema);
                defs.PropagateNullable(schema);
            }
            ApplyDescription(schema, description ?? definition.Description);
            var defsObject = defs.Build();
            if (defsObject is not null)
            {
                if (schema["$defs"] is JObject providedDefs)
                {
                    // A provider-declared fragment may carry private $defs
                    // members; the registered-type definitions merge in and
                    // win on a name conflict, because "#/$defs/<TypeId>"
                    // always denotes the registered type (SPEC §B8).
                    foreach (var property in defsObject.Properties())
                    {
                        providedDefs[property.Name] = property.Value;
                    }
                }
                else
                {
                    schema["$defs"] = defsObject;
                }
            }
            return Result<JsonValue>.Success(JsonValue.From(schema));
        }

        /// <summary>
        /// Writes the type's fragment into a new object — the provider-declared
        /// <see cref="TypeDefinition.Schema"/> when present and inside the §B8
        /// keyword subset (SPEC §B0.1), the codec's own keywords when it
        /// implements <see cref="ITypeSchemaCodec"/>, the empty schema
        /// <c>{}</c> otherwise (an external Codec cannot see the internal
        /// contract, and a fragment outside the subset cannot be emitted
        /// under §B8, so both degrade to the same honestly unconstrained
        /// fragment a schema-less definition gets).
        /// </summary>
        internal static JObject EmitFragment(TypeDefinition definition, TypeSchemaDefs defs)
        {
            if (definition.Schema?.ToToken() is JObject provided && IsB8Subset(provided))
            {
                RegisterSchemaRefs(provided, defs);
                return provided;
            }
            var fragment = new JObject();
            if (definition.Codec is ITypeSchemaCodec schemaCodec)
            {
                schemaCodec.WriteSchema(fragment, defs);
            }
            return fragment;
        }

        /// <summary>
        /// The schema objects nested inside <paramref name="token"/>, itself
        /// first (SPEC §B8): <c>properties</c> and <c>$defs</c> map member
        /// names — property or definition names, never keywords — to
        /// schemas, and <c>items</c>/<c>additionalProperties</c> each hold a
        /// schema. Literal-valued keywords (<c>type</c>, <c>required</c>,
        /// <c>enum</c>, <c>pattern</c>, <c>minimum</c>, <c>maximum</c>,
        /// <c>maxLength</c>, <c>description</c>, <c>$ref</c>) are never
        /// entered: object tokens there are instance data, not subschemas.
        /// A non-object at a map or schema position yields whatever objects
        /// it contains, so malformed-but-emitted provider shapes are still
        /// covered.
        /// </summary>
        private static IEnumerable<JToken> SchemaPositions(JToken token)
        {
            if (token is JObject schema)
            {
                yield return schema;
                foreach (var property in schema.Properties())
                {
                    switch (property.Name)
                    {
                        case "properties":
                        case "$defs":
                            if (property.Value is JObject named)
                            {
                                foreach (var member in named.Properties())
                                {
                                    foreach (var nested in SchemaPositions(member.Value))
                                    {
                                        yield return nested;
                                    }
                                }
                            }
                            else
                            {
                                foreach (var nested in SchemaPositions(property.Value))
                                {
                                    yield return nested;
                                }
                            }
                            break;
                        case "items":
                        case "additionalProperties":
                            foreach (var nested in SchemaPositions(property.Value))
                            {
                                yield return nested;
                            }
                            break;
                    }
                }
            }
            else if (token is JArray array)
            {
                // An array at a schema position (a tuple-form 'items') still
                // holds schemas element-wise.
                foreach (var item in array)
                {
                    foreach (var nested in SchemaPositions(item))
                    {
                        yield return nested;
                    }
                }
            }
        }

        // SPEC §B8 emits only the AllowedKeywords members, so a
        // provider-declared fragment carrying any other member name — at
        // the root or inside any nested properties/$defs/items/
        // additionalProperties schema — cannot be emitted. §J assigns no
        // error code to a malformed provider fragment and §B0.1 makes the
        // declared Schema optional, so EmitFragment treats it as unusable
        // and falls through to the codec hook or the honest unconstrained
        // {} a schema-less definition gets.
        private static bool IsB8Subset(JToken token)
        {
            foreach (var position in SchemaPositions(token))
            {
                foreach (var property in ((JObject)position).Properties())
                {
                    if (!AllowedKeywords.Contains(property.Name))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        // A provider-declared fragment refers to other types through
        // "$ref": "#/$defs/<TypeId>" members (SPEC §B8): every Type ID
        // registers for $defs emission and the reference is rewritten to the
        // canonical TypeRef form, so whitespace-tolerant spellings still
        // resolve. Only schema positions are searched — a "$ref"-looking
        // member inside 'enum' or 'required' data is a literal, not a
        // reference. A reference whose tail is no Type ID is left verbatim —
        // there is no error code for a malformed provider fragment, and the
        // emitted schema stays an honest copy of what the provider declared.
        private static void RegisterSchemaRefs(JObject schema, TypeSchemaDefs defs)
        {
            foreach (var position in SchemaPositions(schema))
            {
                foreach (var property in ((JObject)position).Properties())
                {
                    if (property.Name == "$ref" &&
                        property.Value is JValue { Type: JTokenType.String } reference)
                    {
                        var target = (string)reference.Value!;
                        if (!target.StartsWith(DefsRefPrefix, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        var parsed = TypeRef.Parse(target.Substring(DefsRefPrefix.Length));
                        if (parsed.Ok)
                        {
                            // RefFor registers the type for $defs emission
                            // and hands back the canonical pointing member.
                            reference.Value = (string)defs.RefFor(parsed.Value!)["$ref"]!;
                        }
                    }
                }
            }
        }

        /// <summary>Adds <c>description</c> when it is a non-empty string.</summary>
        internal static void ApplyDescription(JObject schema, string? description)
        {
            if (!string.IsNullOrEmpty(description))
            {
                schema["description"] = description;
            }
        }

        /// <summary>
        /// The .NET Regex pattern <paramref name="pattern"/> rewritten for the
        /// ECMA-262 dialect a JSON Schema validator runs: the strict end
        /// anchor <c>\z</c>, invalid in ECMA-262, becomes the lookahead
        /// <c>(?![\s\S])</c> — "no character follows" — which asserts strict
        /// end of input in every dialect; a <c>$</c> would let .NET-based
        /// validators match before a final newline the Codec rejects. Escape
        /// pairs are scanned left to right, so an escaped backslash is copied
        /// verbatim and <c>\\z</c> (literal backslash, then 'z') is not
        /// mistaken for the anchor.
        /// </summary>
        internal static string ToSchemaPattern(string pattern)
        {
            var builder = new StringBuilder(pattern.Length + 9);
            var i = 0;
            while (i < pattern.Length)
            {
                if (pattern[i] == '\\' && i + 1 < pattern.Length)
                {
                    if (pattern[i + 1] == 'z')
                    {
                        builder.Append(StrictEndAssertion);
                    }
                    else
                    {
                        builder.Append(pattern[i]).Append(pattern[i + 1]);
                    }
                    i += 2;
                }
                else
                {
                    builder.Append(pattern[i]);
                    i += 1;
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// Two unanchored <c>pattern</c> searches conjoined into one: a
        /// lookahead anchored at the start of the input asserts
        /// <paramref name="added"/> occurs somewhere in the string while the
        /// existing pattern keeps its own anchoring.
        /// </summary>
        private static string ConjoinPatterns(string existing, string added)
        {
            var anywhere = @"(?=[\s\S]*(?:" + added + "))";
            return existing.StartsWith("^", StringComparison.Ordinal)
                ? "^" + anywhere + existing.Substring(1)
                : "^" + anywhere + @"[\s\S]*(?:" + existing + ")";
        }

        private static void ApplyConstraints(JObject schema, PortConstraints? constraints)
        {
            if (constraints is null)
            {
                return;
            }
            if (constraints.Enum is not null)
            {
                schema["enum"] = schema["enum"] is JArray existing
                    ? IntersectEnum(existing, constraints.Enum)
                    : ToEnumArray(constraints.Enum);
            }
            if (constraints.Pattern is not null)
            {
                // A declared member that is not a JSON string is no pattern
                // a validator honours, so the port pattern applies alone —
                // the same rule the numeric bounds below follow.
                schema["pattern"] = schema["pattern"] is JValue { Type: JTokenType.String } existing
                    ? ConjoinPatterns(
                        ToSchemaPattern((string)existing.Value!),
                        ToSchemaPattern(constraints.Pattern))
                    : ToSchemaPattern(constraints.Pattern);
            }
            // Bounds intersect like the enum does — the stricter of the
            // provider-declared and the port bound is emitted, so a port
            // constraint narrows the type's shape, never widens it; a
            // declared member that is not a JSON number is no bound a
            // validator honours and the port bound applies instead.
            if (constraints.Min is double min && double.IsFinite(min) &&
                (!TryGetSchemaNumber(schema["minimum"], out var declaredMin) ||
                    min > declaredMin))
            {
                schema["minimum"] = min;
            }
            if (constraints.Max is double max && double.IsFinite(max) &&
                (!TryGetSchemaNumber(schema["maximum"], out var declaredMax) ||
                    max < declaredMax))
            {
                schema["maximum"] = max;
            }
            if (constraints.MaxLength is int maxLength &&
                (!(schema["maxLength"] is JValue declaredLength &&
                    declaredLength.Type == JTokenType.Integer) ||
                    maxLength < (long)declaredLength.Value!))
            {
                schema["maxLength"] = maxLength;
            }
        }

        // A minimum/maximum member carrying a JSON number (the only shape
        // draft 2020-12 gives those keywords); anything else — absent, or a
        // non-numeric declared value — is no usable bound.
        private static bool TryGetSchemaNumber(JToken? token, out double number)
        {
            if (token is JValue value &&
                (value.Type == JTokenType.Integer || value.Type == JTokenType.Float))
            {
                number = Convert.ToDouble(value.Value, CultureInfo.InvariantCulture);
                return true;
            }
            number = 0;
            return false;
        }

        // Explicit null admission (SPEC §B0.1/§A5): "null" joins the type
        // list and null joins the enum values; a fragment with neither
        // keyword already admits null (the empty schema or a fragment whose
        // keywords never apply to null, such as {"minimum":…}). A root
        // "$ref" still applies the referenced schema to the instance, so
        // null admission through it is TypeSchemaDefs.PropagateNullable's
        // job.
        internal static void ApplyNullable(JObject schema)
        {
            switch (schema["type"])
            {
                case JValue single when single.Type == JTokenType.String:
                    var name = (string)single.Value!;
                    if (!string.Equals(name, "null", StringComparison.Ordinal))
                    {
                        schema["type"] = new JArray(name, "null");
                    }
                    break;
                case JArray array when !HasString(array, "null"):
                    array.Add("null");
                    break;
            }
            if (schema["enum"] is JArray enumArray && !HasNull(enumArray))
            {
                enumArray.Add(JValue.CreateNull());
            }
        }

        private static JArray ToEnumArray(IReadOnlyList<string> values)
        {
            var array = new JArray();
            for (var i = 0; i < values.Count; i++)
            {
                array.Add(values[i]);
            }
            return array;
        }

        private static JArray IntersectEnum(JArray existing, IReadOnlyList<string> allowed)
        {
            var merged = new JArray();
            foreach (var item in existing)
            {
                if (item.Type == JTokenType.String &&
                    ContainsOrdinal(allowed, (string)((JValue)item).Value!))
                {
                    merged.Add(item);
                }
            }
            return merged;
        }

        private static bool ContainsOrdinal(IReadOnlyList<string> values, string text)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], text, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasString(JArray array, string text)
        {
            foreach (var item in array)
            {
                if (item.Type == JTokenType.String &&
                    string.Equals((string)((JValue)item).Value!, text, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasNull(JArray array)
        {
            foreach (var item in array)
            {
                if (item.Type == JTokenType.Null)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// The internal schema contract of the built-in Codecs (SPEC §B8): emits
    /// the type's JSON Schema keywords into <paramref name="schema"/> and
    /// registers referenced types into <paramref name="defs"/>. Internal like
    /// <see cref="IClosedGenericCodec"/>: external Codecs cannot implement
    /// it — external types declare their fragment through
    /// <see cref="TypeDefinition.Schema"/> instead, and one that does not
    /// gets the unconstrained fragment <c>{}</c>.
    /// </summary>
    internal interface ITypeSchemaCodec : IValueCodec
    {
        /// <summary>
        /// Writes this type's §B8-subset keywords into
        /// <paramref name="schema"/>; types the fragment refers to are
        /// registered through <paramref name="defs"/>.
        /// </summary>
        void WriteSchema(JObject schema, TypeSchemaDefs defs);
    }

    /// <summary>
    /// The <c>$defs</c> collector of one <see cref="TypeSchemas.Generate"/>
    /// call: hands out <c>{"$ref":"#/$defs/&lt;TypeId&gt;"}</c> member
    /// schemas and resolves each referenced type once, in registration
    /// order, into the shared definitions object (SPEC §B8).
    /// </summary>
    /// <remarks>
    /// Each referenced type is emitted exactly once, so a reference cycle —
    /// none exists among the built-in types — terminates. A type that fails
    /// to resolve cannot occur in a consistent snapshot (a Codec that refers
    /// to it sits on a registered type, so the reference registered too);
    /// the defensive fallback is the unconstrained <c>{}</c>, which keeps
    /// the emitted <c>$ref</c> resolvable.
    /// </remarks>
    internal sealed class TypeSchemaDefs
    {
        private readonly ITypeRegistry _registry;
        private readonly JObject _defs = new JObject();
        private readonly List<KeyValuePair<string, TypeRef>> _pending =
            new List<KeyValuePair<string, TypeRef>>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _nullable = new HashSet<string>(StringComparer.Ordinal);

        internal TypeSchemaDefs(ITypeRegistry registry) => _registry = registry;

        /// <summary>
        /// Registers <paramref name="type"/> for <c>$defs</c> emission and
        /// returns the pointing member schema
        /// <c>{"$ref":"#/$defs/&lt;TypeId&gt;"}</c> (SPEC §B8). The Type ID
        /// is the canonical <see cref="TypeRef.ToString"/> form; it needs no
        /// JSON Pointer escaping because the §B1 grammar admits neither
        /// <c>~</c> nor <c>/</c>.
        /// </summary>
        internal JObject RefFor(TypeRef type)
        {
            var key = type.ToString();
            if (_seen.Add(key))
            {
                _pending.Add(new KeyValuePair<string, TypeRef>(key, type));
            }
            return new JObject { ["$ref"] = TypeSchemas.DefsRefPrefix + key };
        }

        /// <summary>
        /// <see cref="RefFor(TypeRef)"/> for a constant canonical Type ID;
        /// a string that does not parse is a programming error.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="typeId"/> is not a valid Type ID.
        /// </exception>
        internal JObject RefFor(string typeId)
        {
            var parsed = TypeRef.Parse(typeId);
            if (!parsed.Ok)
            {
                throw new ArgumentException(
                    "The Type ID '" + typeId + "' does not parse.", nameof(typeId));
            }
            return RefFor(parsed.Value!);
        }

        /// <summary>
        /// Propagates explicit-null admission through a root
        /// <c>"$ref": "#/$defs/&lt;TypeId&gt;"</c>: in draft 2020-12 the
        /// referenced schema applies to the instance in addition to any
        /// sibling keywords, so <c>null</c> validates only when the
        /// referenced type's emitted definition admits it. The mark is
        /// recorded whether the target fragment is already emitted or still
        /// pending, and chains through a target whose own fragment is a
        /// root <c>$ref</c> (an alias of an alias). A <c>$ref</c> whose
        /// tail is no Type ID is left as declared — its target is outside
        /// the registry and cannot be widened here.
        /// </summary>
        internal void PropagateNullable(JObject schema)
        {
            var reference = schema["$ref"] as JValue;
            if (reference is null || reference.Type != JTokenType.String)
            {
                return;
            }
            var target = (string)reference.Value!;
            if (!target.StartsWith(TypeSchemas.DefsRefPrefix, StringComparison.Ordinal))
            {
                return;
            }
            var parsed = TypeRef.Parse(target.Substring(TypeSchemas.DefsRefPrefix.Length));
            if (!parsed.Ok)
            {
                return;
            }
            var key = parsed.Value!.ToString();
            if (!_nullable.Add(key))
            {
                return;
            }
            RefFor(parsed.Value!);
            if (_defs[key] is JObject emitted)
            {
                TypeSchemas.ApplyNullable(emitted);
                PropagateNullable(emitted);
            }
        }

        /// <summary>
        /// Emits every registered reference into the definitions object —
        /// each fragment may register further references — and returns it,
        /// or null when no type was ever referenced.
        /// </summary>
        internal JObject? Build()
        {
            for (var i = 0; i < _pending.Count; i++)
            {
                var key = _pending[i].Key;
                var resolved = _registry.Resolve(_pending[i].Value);
                var fragment = resolved.Ok
                    ? TypeSchemas.EmitFragment(resolved.Value!, this)
                    : new JObject();
                _defs[key] = fragment;
                if (_nullable.Contains(key))
                {
                    TypeSchemas.ApplyNullable(fragment);
                    PropagateNullable(fragment);
                }
                if (resolved.Ok)
                {
                    TypeSchemas.ApplyDescription(fragment, resolved.Value!.Description);
                }
            }
            return _defs.Count == 0 ? null : _defs;
        }
    }
}
