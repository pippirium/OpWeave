#nullable enable

using OpWeave.Core;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The CLR codec of <c>unity.Vector2</c> (SPEC §B4):
    /// <c>{"x":0,"y":0}</c> ↔ <see cref="Vector2"/>.
    /// </summary>
    internal sealed class Vector2Codec : ShapeBoundClrCodec<Vector2>
    {
        private static readonly string[] s_components = { "x", "y" };

        internal Vector2Codec(IValueCodec shape) : base(shape)
        {
        }

        /// <inheritdoc/>
        public override Vector2 FromJson(JsonValue value)
        {
            var obj = ClrCodecJson.RequireObject(value, "unity.Vector2");
            return new Vector2(
                ClrCodecJson.RequiredComponent(obj, "unity.Vector2", "x"),
                ClrCodecJson.RequiredComponent(obj, "unity.Vector2", "y"));
        }

        /// <inheritdoc/>
        public override JsonValue ToJson(Vector2 value) =>
            JsonValue.From(ClrCodecJson.ComponentsObject(s_components, new[] { value.x, value.y }));
    }
}
