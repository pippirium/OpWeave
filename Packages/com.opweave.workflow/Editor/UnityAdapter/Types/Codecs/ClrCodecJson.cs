#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The shared JSON↔component plumbing of the unity.* CLR codecs
    /// (T-W2.1-05): reading a required numeric member of a component object
    /// and building the canonical component object written back.
    /// </summary>
    /// <remarks>
    /// <see cref="IClrValueCodec{T}.FromJson"/> is only invoked on values the
    /// Codec's <see cref="IValueCodec.Validate"/> already accepted (SPEC §B7),
    /// but the conversion still fails loudly on a malformed or
    /// single-precision-overflowing value instead of producing a silent
    /// CLR value — a Codec rejection that surfaces as OW-TYPE-003 before a
    /// Plan exists and OW-RUN-007 at run time (SPEC §B7.1 item 4).
    /// </remarks>
    internal static class ClrCodecJson
    {
        /// <summary>
        /// The value as a JSON object.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The value is not a JSON object.</exception>
        internal static JObject RequireObject(JsonValue value, string typeId)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            if (value.ToToken() is not JObject obj)
            {
                throw new InvalidOperationException($"A {typeId} value must be a JSON object.");
            }
            return obj;
        }

        /// <summary>
        /// A required numeric member of a component object as a
        /// <see cref="float"/>: integer tokens convert exactly like the
        /// Double-position rule of decision Q3 (SPEC §B7), and a Double that
        /// overflows single precision is rejected rather than silently
        /// becoming an infinity.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The member is absent, not a JSON number, or overflows <see cref="float"/>.
        /// </exception>
        internal static float RequiredComponent(JObject obj, string typeId, string name)
        {
            var member = obj[name];
            if (member is null || (member.Type != JTokenType.Integer && member.Type != JTokenType.Float))
            {
                throw new InvalidOperationException(
                    $"A {typeId} value is missing the '{name}' component or it is not a JSON number.");
            }
            var component = (float)member.ToObject<double>();
            if (float.IsNaN(component) || float.IsInfinity(component))
            {
                throw new InvalidOperationException(
                    $"The '{name}' component of a {typeId} value overflows single precision.");
            }
            return component;
        }

        /// <summary>
        /// The component object the codecs write back: each named component
        /// as its <see cref="float"/>-widened Double token, which
        /// <see cref="CanonicalJson"/> then serializes deterministically
        /// (SPEC §0.3). A non-finite CLR component fails the
        /// <see cref="JsonValue.From"/> call that wraps this object — the
        /// Codec rejection path of SPEC §B7.1 item 4.
        /// </summary>
        internal static JObject ComponentsObject(string[] names, float[] components)
        {
            var obj = new JObject();
            for (var i = 0; i < names.Length; i++)
            {
                obj[names[i]] = new JValue((double)components[i]);
            }
            return obj;
        }
    }
}
