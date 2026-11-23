#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The built-in Codec for <c>core.Json</c> (SPEC §B3): accepts every JSON
    /// value, including the explicit <c>null</c> that no other type admits
    /// without <c>PortDescriptor.Nullable</c> (SPEC §A5, §B0.1).
    /// </summary>
    /// <remarks>
    /// <c>core.Json</c> is the single exception to typed normalization
    /// (SPEC §B7 item 4, §D0.2): values are untyped, so the written tokens are
    /// preserved — <c>1</c> and <c>1e0</c> stay distinct canonical forms — and
    /// <see cref="Normalize"/> is the identity.
    /// </remarks>
    internal sealed class JsonCodec : ITypeSchemaCodec
    {
        internal static readonly JsonCodec Instance = new JsonCodec();

        private JsonCodec()
        {
        }

        /// <inheritdoc/>
        /// <remarks>Emits nothing: <c>core.Json</c> accepts every JSON value, so its fragment is <c>{}</c>.</remarks>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs)
        {
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
            CodecConstraints.Check(value.ToToken(), constraints, diagnostics);
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
