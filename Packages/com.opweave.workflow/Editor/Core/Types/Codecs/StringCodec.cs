#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The built-in Codec for <c>core.String</c> (SPEC §B3): accepts JSON
    /// string tokens and applies <see cref="PortConstraints"/> — including the
    /// <c>enum</c> constraint that models enumerations without new types
    /// (SPEC §B3). String values are already in their Canonical JSON persisted
    /// form, so <see cref="Normalize"/> is the identity.
    /// </summary>
    /// <remarks>
    /// Internal: the public Codec contract is <see cref="IValueCodec"/> only;
    /// callers reach this implementation through
    /// <see cref="TypeDefinition.Codec"/>.
    /// </remarks>
    internal sealed class StringCodec : ITypeSchemaCodec
    {
        internal static readonly StringCodec Instance = new StringCodec();

        private StringCodec()
        {
        }

        /// <inheritdoc/>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs) => schema["type"] = "string";

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
            if (token.Type != JTokenType.String)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Value is not a JSON string."));
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
