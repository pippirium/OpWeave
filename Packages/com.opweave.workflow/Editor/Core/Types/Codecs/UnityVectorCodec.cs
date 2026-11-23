#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The JSON-shape Codec of the unity.* struct types of SPEC §B4 —
    /// <c>unity.Vector2</c>, <c>unity.Vector3</c>, <c>unity.Vector4</c>,
    /// <c>unity.Quaternion</c> and <c>unity.Euler</c>: a JSON object carrying
    /// exactly the declared named components, each a Double position of
    /// decision Q3 (integer tokens are accepted and normalized to Double).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every declared component is required and unknown members are rejected
    /// (the strict-shape rule the SPEC applies to InputSource objects; a
    /// Vector3 written <c>{"x":1,"y":2}</c> fails on the missing
    /// <c>z</c> — SPEC §B7 test 1). <c>unity.Euler</c> is a distinct type from
    /// <c>unity.Vector3</c> even though the JSON shape is identical; the type
    /// system keeps them non-assignable (SPEC §B4).
    /// </para>
    /// <para>
    /// <see cref="Normalize"/> converts each component's integer token to its
    /// Double token, so <c>{"x":0,"y":1,"z":0}</c> persists as
    /// <c>{"x":0e0,"y":1e0,"z":0e0}</c> (SPEC §B7 items 1–2). NaN and Infinity
    /// never reach the Codec — the strict Canonical-JSON reader rejects them.
    /// </para>
    /// </remarks>
    internal sealed class UnityVectorCodec : ITypeSchemaCodec
    {
        internal static readonly UnityVectorCodec Vector2 = new UnityVectorCodec("unity.Vector2", "x", "y");
        internal static readonly UnityVectorCodec Vector3 = new UnityVectorCodec("unity.Vector3", "x", "y", "z");
        internal static readonly UnityVectorCodec Vector4 = new UnityVectorCodec("unity.Vector4", "x", "y", "z", "w");
        internal static readonly UnityVectorCodec Quaternion = new UnityVectorCodec("unity.Quaternion", "x", "y", "z", "w");
        internal static readonly UnityVectorCodec Euler = new UnityVectorCodec("unity.Euler", "x", "y", "z");

        private readonly string _typeId;
        private readonly string[] _components;
        private readonly HashSet<string> _componentSet;

        private UnityVectorCodec(string typeId, params string[] components)
        {
            _typeId = typeId;
            _components = components;
            _componentSet = new HashSet<string>(components, StringComparer.Ordinal);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Every component is a required <c>number</c> and unknown members
        /// are rejected — the same strict shape <see cref="Validate"/>
        /// enforces.
        /// </remarks>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs)
        {
            var properties = new JObject();
            var required = new JArray();
            for (var i = 0; i < _components.Length; i++)
            {
                properties[_components[i]] = new JObject { ["type"] = "number" };
                required.Add(_components[i]);
            }
            schema["type"] = "object";
            schema["properties"] = properties;
            schema["required"] = required;
            schema["additionalProperties"] = false;
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
            if (token.Type != JTokenType.Object)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    $"Value is not a {_typeId} object."));
                CodecConstraints.Check(token, constraints, diagnostics);
                return diagnostics;
            }
            var obj = (JObject)token;
            for (var i = 0; i < _components.Length; i++)
            {
                var component = _components[i];
                if (!obj.TryGetValue(component, out var member))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        $"{_typeId} value is missing the '{component}' component.",
                        jsonPath: "$." + component));
                }
                else if (member!.Type != JTokenType.Integer && member.Type != JTokenType.Float)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        $"{_typeId} component '{component}' is not a JSON number.",
                        jsonPath: "$." + component));
                }
            }
            foreach (var property in obj.Properties())
            {
                if (!_componentSet.Contains(property.Name))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        $"{_typeId} value has an unknown member '{property.Name}'.",
                        jsonPath: "$." + property.Name));
                }
            }
            CodecConstraints.Check(token, constraints, diagnostics);
            return diagnostics;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public JsonValue Normalize(JsonValue value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var token = value.ToToken();
            if (token.Type != JTokenType.Object)
            {
                return value;
            }
            var obj = (JObject)token;
            var changed = false;
            for (var i = 0; i < _components.Length; i++)
            {
                var member = obj[_components[i]];
                if (member is not null && member.Type == JTokenType.Integer)
                {
                    var integer = (long)((JValue)member).Value!;
                    obj[_components[i]] = new JValue((double)integer);
                    changed = true;
                }
            }
            return changed ? JsonValue.From(obj) : value;
        }
    }
}
