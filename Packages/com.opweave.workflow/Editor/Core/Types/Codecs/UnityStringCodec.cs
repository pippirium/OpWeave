#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The JSON-shape Codec of the unity.* string types of SPEC §B3 —
    /// <c>unity.AssetPath</c>, <c>unity.TypeName</c> and
    /// <c>unity.HierarchyPath</c>: a JSON string that must additionally match a
    /// fixed pattern. Core knows only the JSON shape (SPEC §0.6 SG-10); CLR
    /// conversion lives in OpWeave.UnityAdapter (SPEC §B4).
    /// </summary>
    /// <remarks>
    /// Strings are already their Canonical-JSON persisted form, so
    /// <see cref="Normalize"/> is the identity.
    /// </remarks>
    internal sealed class UnityStringCodec : ITypeSchemaCodec
    {
        /// <summary>Codec of <c>unity.AssetPath</c> — a string matching <c>^(Assets|Packages)/</c>.</summary>
        internal static readonly UnityStringCodec AssetPath = new UnityStringCodec(
            UnityShapePatterns.AssetPath,
            "Value is not an asset path; asset paths start with 'Assets/' or 'Packages/'.");

        /// <summary>Codec of <c>unity.TypeName</c> — a fully qualified type name.</summary>
        internal static readonly UnityStringCodec TypeName = new UnityStringCodec(
            UnityShapePatterns.TypeName,
            "Value is not a fully qualified type name.");

        /// <summary>Codec of <c>unity.HierarchyPath</c> — a '/Root/Child' scene path.</summary>
        internal static readonly UnityStringCodec HierarchyPath = new UnityStringCodec(
            UnityShapePatterns.HierarchyPath,
            "Value is not a hierarchy path; expected the '/Root/Child' form.");

        private readonly Regex _pattern;
        private readonly string _reject;

        private UnityStringCodec(Regex pattern, string reject)
        {
            _pattern = pattern;
            _reject = reject;
        }

        /// <inheritdoc/>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs)
        {
            schema["type"] = "string";
            schema["pattern"] = TypeSchemas.ToSchemaPattern(_pattern.ToString());
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
            if (token.Type != JTokenType.String ||
                !_pattern.IsMatch((string)((JValue)token).Value!))
            {
                diagnostics.Add(ErrorCodes.Create(ErrorCodes.OW_TYPE_003, _reject));
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
            return value;
        }
    }
}
