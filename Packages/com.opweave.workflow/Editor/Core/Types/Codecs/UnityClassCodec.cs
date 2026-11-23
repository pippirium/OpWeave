#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The Codec attached to the built-in Unity class-id types of SPEC §B5
    /// (<c>unity.Object</c>, <c>unity.Component</c>, <c>unity.Animator</c>, …).
    /// Class IDs name Unity classes — they exist so Ref generic arguments and
    /// declared base types resolve inside the registry; they are not value
    /// types and never carry a JSON value, so every value fails with
    /// OW-TYPE-003.
    /// </summary>
    internal sealed class UnityClassCodec : ITypeSchemaCodec
    {
        internal static readonly UnityClassCodec Instance = new UnityClassCodec();

        private UnityClassCodec()
        {
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Class IDs never carry a JSON value, so the fragment is
        /// <c>{"enum":[]}</c>, which no instance satisfies.
        /// </remarks>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs) =>
            schema["enum"] = new JArray();

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            return new[]
            {
                ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Class identifiers name Unity classes and do not carry JSON values."),
            };
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
