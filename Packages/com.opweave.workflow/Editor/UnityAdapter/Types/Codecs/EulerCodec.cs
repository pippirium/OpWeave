#nullable enable

using OpWeave.Core;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The CLR codec of <c>unity.Euler</c> (SPEC §B4): a rotation in degrees
    /// as <c>{"x":0,"y":90,"z":0}</c> ↔ <see cref="Vector3"/>. A distinct
    /// Codec for a distinct type — <c>unity.Euler</c> is not
    /// <c>unity.Vector3</c> even though the JSON shape is identical.
    /// </summary>
    internal sealed class EulerCodec : ShapeBoundClrCodec<Vector3>
    {
        private static readonly string[] s_components = { "x", "y", "z" };

        internal EulerCodec(IValueCodec shape) : base(shape)
        {
        }

        /// <inheritdoc/>
        public override Vector3 FromJson(JsonValue value)
        {
            var obj = ClrCodecJson.RequireObject(value, "unity.Euler");
            return new Vector3(
                ClrCodecJson.RequiredComponent(obj, "unity.Euler", "x"),
                ClrCodecJson.RequiredComponent(obj, "unity.Euler", "y"),
                ClrCodecJson.RequiredComponent(obj, "unity.Euler", "z"));
        }

        /// <inheritdoc/>
        public override JsonValue ToJson(Vector3 value) =>
            JsonValue.From(ClrCodecJson.ComponentsObject(s_components, new[] { value.x, value.y, value.z }));
    }
}
