#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The built-in Codec for <c>core.Int</c> (SPEC §B3): accepts integer
    /// tokens only. Per decision Q3 (SPEC §B7 item 3) a fraction or exponent
    /// token such as <c>1.0</c> is never coerced into an Int64 and fails with
    /// OW-TYPE-003; integer tokens are already Int64-bounded and canonical, so
    /// <see cref="Normalize"/> is the identity.
    /// </summary>
    /// <remarks>
    /// Out-of-Int64 integer tokens cannot reach this Codec: the Canonical
    /// JSON reader rejects them at parse time (SPEC §B7 item 5, §0.3 CJ-14)
    /// before any <see cref="JsonValue"/> exists.
    /// </remarks>
    internal sealed class IntCodec : ITypeSchemaCodec
    {
        internal static readonly IntCodec Instance = new IntCodec();

        private IntCodec()
        {
        }

        /// <inheritdoc/>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs) => schema["type"] = "integer";

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
            if (token.Type != JTokenType.Integer)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Value is not a JSON integer."));
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
