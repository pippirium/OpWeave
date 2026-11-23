#nullable enable

using OpWeave.Core;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The CLR codec of <c>unity.Vector3</c> (SPEC §B4):
    /// <c>{"x":0,"y":0,"z":0}</c> ↔ <see cref="Vector3"/>.
    /// </summary>
    internal sealed class Vector3Codec : ShapeBoundClrCodec<Vector3>
    {
        private static readonly string[] s_components = { "x", "y", "z" };

        internal Vector3Codec(IValueCodec shape) : base(shape)
        {
        }

        /// <inheritdoc/>
        public override Vector3 FromJson(JsonValue value)
        {
            var obj = ClrCodecJson.RequireObject(value, "unity.Vector3");
            return new Vector3(
                ClrCodecJson.RequiredComponent(obj, "unity.Vector3", "x"),
                ClrCodecJson.RequiredComponent(obj, "unity.Vector3", "y"),
                ClrCodecJson.RequiredComponent(obj, "unity.Vector3", "z"));
        }

        /// <inheritdoc/>
        public override JsonValue ToJson(Vector3 value) =>
            JsonValue.From(ClrCodecJson.ComponentsObject(s_components, new[] { value.x, value.y, value.z }));
    }
}
