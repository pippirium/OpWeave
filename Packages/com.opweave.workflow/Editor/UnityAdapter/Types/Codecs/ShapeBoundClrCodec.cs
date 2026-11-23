#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Base of the OpWeave.UnityAdapter CLR codecs of the standard unity.*
    /// value types (SPEC §B4, T-W2.1-05): each pairs the type's Core
    /// JSON-shape Codec (T-W1.4-05), which stays the single authority for
    /// <see cref="IValueCodec.Validate"/>, with the CLR conversion of
    /// <see cref="IClrValueCodec{T}"/> (SPEC §B7).
    /// </summary>
    /// <remarks>
    /// Per SPEC §B7.1 item 2 a CLR codec's normalization must be
    /// Canonical-JSON byte-identical to <c>ToJson(FromJson(value))</c>;
    /// <see cref="Normalize"/> implements exactly that, so the persisted
    /// form of every unity.* value type is its own CLR value serialized
    /// back — deterministic, idempotent and side-effect free. Components
    /// travel through <see cref="float"/>, the precision of the CLR structs
    /// themselves: an input component written with more precision than a
    /// float carries normalizes to the nearest representable value, which is
    /// the value the Unity runtime actually holds.
    /// </remarks>
    internal abstract class ShapeBoundClrCodec<T> : IClrValueCodec<T>
    {
        private readonly IValueCodec _shape;

        /// <summary>The shape Codec this CLR codec validates through.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="shape"/> is null.</exception>
        protected ShapeBoundClrCodec(IValueCodec shape) =>
            _shape = shape ?? throw new ArgumentNullException(nameof(shape));

        /// <inheritdoc/>
        /// <remarks>Delegated verbatim to the Core shape Codec (SPEC §0.6 SG-10).</remarks>
        public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
            _shape.Validate(value, constraints);

        /// <inheritdoc/>
        /// <remarks>
        /// The SPEC §B7.1 item 2 contract — Canonical JSON of
        /// <c>ToJson(FromJson(value))</c> — implemented literally.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public JsonValue Normalize(JsonValue value) => ToJson(FromJson(value));

        /// <inheritdoc/>
        public abstract T FromJson(JsonValue value);

        /// <inheritdoc/>
        public abstract JsonValue ToJson(T value);
    }
}
