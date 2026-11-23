#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The JSON-shape Codec of <c>unity.Color</c> (SPEC §B4): either the object
    /// form <c>{"r":1,"g":1,"b":1,"a":1}</c> or the string form
    /// <c>"#RRGGBBAA"</c>. Components are Double positions of decision Q3, so
    /// integer tokens are accepted and normalized to Double.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The object form follows the same strict shape as the other unity.*
    /// struct codecs: all four components required, no unknown members. The
    /// string form accepts eight hex digits in either case; the SPEC writes
    /// only the eight-digit form, so six-digit <c>#RRGGBB</c> input is
    /// rejected.
    /// </para>
    /// <para>
    /// Local, reversible choice where the SPEC is silent: both spellings of
    /// the same color normalize to the <em>object</em> form
    /// (<see cref="IValueCodec.Normalize"/> must give spellings the type
    /// treats as equal one canonical persisted form, SPEC §B7.1), so
    /// <c>"#FF0000FF"</c> and <c>{"r":1,"g":0,"b":0,"a":1}</c> persist as the
    /// identical Canonical JSON <c>{"a":1e0,"b":0e0,"g":0e0,"r":1e0}</c>.
    /// The object form is the only shape that can carry the arbitrary Double
    /// components the type admits, so the string form cannot be canonical.
    /// </para>
    /// </remarks>
    internal sealed class UnityColorCodec : ITypeSchemaCodec
    {
        internal static readonly UnityColorCodec Instance = new UnityColorCodec();

        private static readonly string[] s_components = { "r", "g", "b", "a" };
        private static readonly HashSet<string> s_componentSet =
            new HashSet<string>(s_components, StringComparer.Ordinal);

        private UnityColorCodec()
        {
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The two accepted spellings share one fragment: <c>type</c> admits
        /// object and string, the object keywords describe the
        /// <c>{"r":…,"g":…,"b":…,"a":…}</c> form and apply only to objects,
        /// while <c>pattern</c> describes the <c>"#RRGGBBAA"</c> form and
        /// applies only to strings (SPEC §B8; no <c>oneOf</c> in the subset).
        /// </remarks>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs)
        {
            var properties = new JObject();
            var required = new JArray();
            for (var i = 0; i < s_components.Length; i++)
            {
                properties[s_components[i]] = new JObject { ["type"] = "number" };
                required.Add(s_components[i]);
            }
            schema["type"] = new JArray("object", "string");
            schema["properties"] = properties;
            schema["required"] = required;
            schema["additionalProperties"] = false;
            schema["pattern"] = TypeSchemas.ToSchemaPattern(UnityShapePatterns.ColorHex.ToString());
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var diagnostics = new List<Diagnostic>();
            var token = value.ToToken();
            switch (token.Type)
            {
                case JTokenType.String:
                    if (!UnityShapePatterns.ColorHex.IsMatch((string)((JValue)token).Value!))
                    {
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_TYPE_003,
                            "Value is not a '#RRGGBBAA' color string."));
                    }
                    break;
                case JTokenType.Object:
                    ValidateObject((JObject)token, diagnostics);
                    break;
                default:
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "Value is neither a unity.Color object nor a '#RRGGBBAA' string."));
                    break;
            }
            CodecConstraints.Check(token, constraints, diagnostics);
            return diagnostics;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// A string token is outside the JSON value model (cannot occur for values the
        /// strict reader produced).
        /// </exception>
        public JsonValue Normalize(JsonValue value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var token = value.ToToken();
            if (token.Type == JTokenType.String)
            {
                var text = (string)((JValue)token).Value!;
                if (UnityShapePatterns.ColorHex.IsMatch(text))
                {
                    var obj = new JObject();
                    for (var i = 0; i < 4; i++)
                    {
                        var channel = byte.Parse(
                            text.Substring(1 + 2 * i, 2),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture);
                        obj.Add(s_components[i], new JValue(channel / 255.0));
                    }
                    return JsonValue.From(obj);
                }
                return value;
            }
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                var changed = false;
                for (var i = 0; i < s_components.Length; i++)
                {
                    var member = obj[s_components[i]];
                    if (member is not null && member.Type == JTokenType.Integer)
                    {
                        var integer = (long)((JValue)member).Value!;
                        obj[s_components[i]] = new JValue((double)integer);
                        changed = true;
                    }
                }
                return changed ? JsonValue.From(obj) : value;
            }
            return value;
        }

        private static void ValidateObject(JObject obj, List<Diagnostic> diagnostics)
        {
            for (var i = 0; i < s_components.Length; i++)
            {
                var component = s_components[i];
                if (!obj.TryGetValue(component, out var member))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        $"unity.Color value is missing the '{component}' component.",
                        jsonPath: "$." + component));
                }
                else if (member!.Type != JTokenType.Integer && member.Type != JTokenType.Float)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        $"unity.Color component '{component}' is not a JSON number.",
                        jsonPath: "$." + component));
                }
            }
            foreach (var property in obj.Properties())
            {
                if (!s_componentSet.Contains(property.Name))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        $"unity.Color value has an unknown member '{property.Name}'.",
                        jsonPath: "$." + property.Name));
                }
            }
        }
    }
}
