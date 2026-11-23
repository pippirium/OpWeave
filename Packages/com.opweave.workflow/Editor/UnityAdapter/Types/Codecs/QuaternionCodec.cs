#nullable enable

using System;
using OpWeave.Core;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The CLR codec of <c>unity.Quaternion</c> (SPEC §B4):
    /// <c>{"x":0,"y":0,"z":0,"w":1}</c> ↔ <see cref="Quaternion"/>, read
    /// normalized (正規化して読む).
    /// </summary>
    /// <remarks>
    /// Local, reversible choices where the SPEC is silent: the read is
    /// normalized in double precision so finite inputs can never overflow the
    /// squared magnitude; a read whose squared magnitude is already within
    /// <see cref="Mathf.Approximately"/> of <c>1</c> is returned verbatim,
    /// which keeps <see cref="ShapeBoundClrCodec{T}.Normalize"/> exactly
    /// idempotent (SPEC §B7.1) — a second normalization could shift the
    /// lowest float bits; and a zero-magnitude read maps to the identity,
    /// matching Unity's own <c>Quaternion.Normalize</c> convention for
    /// degenerate input.
    /// </remarks>
    internal sealed class QuaternionCodec : ShapeBoundClrCodec<Quaternion>
    {
        private static readonly string[] s_components = { "x", "y", "z", "w" };

        internal QuaternionCodec(IValueCodec shape) : base(shape)
        {
        }

        /// <inheritdoc/>
        public override Quaternion FromJson(JsonValue value)
        {
            var obj = ClrCodecJson.RequireObject(value, "unity.Quaternion");
            var quaternion = new Quaternion(
                ClrCodecJson.RequiredComponent(obj, "unity.Quaternion", "x"),
                ClrCodecJson.RequiredComponent(obj, "unity.Quaternion", "y"),
                ClrCodecJson.RequiredComponent(obj, "unity.Quaternion", "z"),
                ClrCodecJson.RequiredComponent(obj, "unity.Quaternion", "w"));
            var sqrMagnitude =
                (double)quaternion.x * quaternion.x +
                (double)quaternion.y * quaternion.y +
                (double)quaternion.z * quaternion.z +
                (double)quaternion.w * quaternion.w;
            if (sqrMagnitude <= 0.0)
            {
                return Quaternion.identity;
            }
            if (Mathf.Approximately((float)sqrMagnitude, 1f))
            {
                return quaternion;
            }
            var inverse = 1.0 / Math.Sqrt(sqrMagnitude);
            return new Quaternion(
                (float)(quaternion.x * inverse),
                (float)(quaternion.y * inverse),
                (float)(quaternion.z * inverse),
                (float)(quaternion.w * inverse));
        }

        /// <inheritdoc/>
        public override JsonValue ToJson(Quaternion value) =>
            JsonValue.From(ClrCodecJson.ComponentsObject(
                s_components, new[] { value.x, value.y, value.z, value.w }));
    }
}
