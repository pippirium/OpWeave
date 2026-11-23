#nullable enable

using OpWeave.Core;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The CLR codec of <c>unity.Vector4</c> (SPEC §B4):
    /// <c>{"x":0,"y":0,"z":0,"w":0}</c> ↔ <see cref="Vector4"/>.
    /// </summary>
    internal sealed class Vector4Codec : ShapeBoundClrCodec<Vector4>
    {
        private static readonly string[] s_components = { "x", "y", "z", "w" };

        internal Vector4Codec(IValueCodec shape) : base(shape)
        {
        }

        /// <inheritdoc/>
        public override Vector4 FromJson(JsonValue value)
        {
            var obj = ClrCodecJson.RequireObject(value, "unity.Vector4");
            return new Vector4(
                ClrCodecJson.RequiredComponent(obj, "unity.Vector4", "x"),
                ClrCodecJson.RequiredComponent(obj, "unity.Vector4", "y"),
                ClrCodecJson.RequiredComponent(obj, "unity.Vector4", "z"),
                ClrCodecJson.RequiredComponent(obj, "unity.Vector4", "w"));
        }

        /// <inheritdoc/>
        public override JsonValue ToJson(Vector4 value) =>
            JsonValue.From(ClrCodecJson.ComponentsObject(
                s_components, new[] { value.x, value.y, value.z, value.w }));
    }
}
