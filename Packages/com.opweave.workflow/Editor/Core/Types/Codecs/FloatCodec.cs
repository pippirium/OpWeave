#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The built-in Codec for <c>core.Float</c> (SPEC §B3, decision Q3):
    /// accepts finite binary64 tokens <em>and integer tokens</em>, which the
    /// Codec converts to Double. <see cref="Normalize"/> therefore maps the
    /// integer token <c>0</c> and the decimal token <c>0.0</c> to the same
    /// canonical persisted form <c>0e0</c> (SPEC §B7 items 1–2).
    /// </summary>
    /// <remarks>
    /// <see cref="Validate"/> can only see values that already survived the
    /// strict reader, so NaN and Infinity never reach this Codec; integer
    /// tokens are Int64-bounded by construction. Integers beyond Double
    /// precision convert with the usual round-to-nearest loss — the specified
    /// behaviour of a Double position, identical to having written the
    /// fraction token directly (SPEC §B7 item 1).
    /// </remarks>
    internal sealed class FloatCodec : ITypeSchemaCodec
    {
        internal static readonly FloatCodec Instance = new FloatCodec();

        private FloatCodec()
        {
        }

        /// <inheritdoc/>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs) => schema["type"] = "number";

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
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Value is not a JSON number."));
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
            if (token.Type != JTokenType.Integer)
            {
                return value;
            }
            var integer = (long)((JValue)token).Value!;
            return JsonValue.From(new JValue((double)integer));
        }
    }
}
