#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The built-in Codec for <c>core.Bool</c> (SPEC §B3): accepts JSON boolean
    /// tokens only. Booleans are already in their Canonical JSON persisted
    /// form, so <see cref="Normalize"/> is the identity.
    /// </summary>
    internal sealed class BoolCodec : ITypeSchemaCodec
    {
        internal static readonly BoolCodec Instance = new BoolCodec();

        private BoolCodec()
        {
        }

        /// <inheritdoc/>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs) => schema["type"] = "boolean";

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
            if (token.Type != JTokenType.Boolean)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Value is not a JSON boolean."));
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
