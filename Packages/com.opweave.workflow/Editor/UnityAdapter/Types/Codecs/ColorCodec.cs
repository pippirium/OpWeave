#nullable enable

using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The CLR codec of <c>unity.Color</c> (SPEC §B4):
    /// <c>{"r":1,"g":1,"b":1,"a":1}</c> or <c>"#RRGGBBAA"</c> ↔
    /// <see cref="Color"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ToJson"/> always writes the object form — the only shape
    /// that carries arbitrary component values, and the persisted canonical
    /// form the Core shape Codec already normalizes both spellings to
    /// (SPEC §B7.1).
    /// </remarks>
    internal sealed class ColorCodec : ShapeBoundClrCodec<Color>
    {
        private static readonly string[] s_components = { "r", "g", "b", "a" };

        internal ColorCodec(IValueCodec shape) : base(shape)
        {
        }

        /// <inheritdoc/>
        public override Color FromJson(JsonValue value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var token = value.ToToken();
            switch (token.Type)
            {
                case JTokenType.Object:
                    var obj = (JObject)token;
                    return new Color(
                        ClrCodecJson.RequiredComponent(obj, "unity.Color", "r"),
                        ClrCodecJson.RequiredComponent(obj, "unity.Color", "g"),
                        ClrCodecJson.RequiredComponent(obj, "unity.Color", "b"),
                        ClrCodecJson.RequiredComponent(obj, "unity.Color", "a"));
                case JTokenType.String:
                    if (TryParseHex((string)((JValue)token).Value!, out var color))
                    {
                        return color;
                    }
                    throw new InvalidOperationException(
                        "A unity.Color string must be a '#RRGGBBAA' value.");
                default:
                    throw new InvalidOperationException(
                        "A unity.Color value must be a JSON object or a '#RRGGBBAA' string.");
            }
        }

        /// <inheritdoc/>
        public override JsonValue ToJson(Color value) =>
            JsonValue.From(ClrCodecJson.ComponentsObject(
                s_components, new[] { value.r, value.g, value.b, value.a }));

        // The eight-digit hex spelling of SPEC §B4; the JSON-shape Codec is
        // the acceptance authority, so a failure here only guards unvalidated
        // input.
        private static bool TryParseHex(string text, out Color color)
        {
            color = default;
            if (text.Length != 9 || text[0] != '#')
            {
                return false;
            }
            var channels = new float[4];
            for (var i = 0; i < 4; i++)
            {
                if (!byte.TryParse(
                        text.Substring(1 + 2 * i, 2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var channel))
                {
                    return false;
                }
                channels[i] = channel / 255f;
            }
            color = new Color(channels[0], channels[1], channels[2], channels[3]);
            return true;
        }
    }
}
