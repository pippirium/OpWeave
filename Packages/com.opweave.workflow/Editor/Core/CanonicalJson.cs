#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The single Canonical JSON v1 implementation (SPEC §0.3): every hash, equality
    /// comparison and plan.json byte in OpWeave derives from <see cref="Serialize"/> and
    /// <see cref="Sha256"/> output — UTF-8 without BOM or whitespace, object keys ordered by
    /// UTF-16 code unit ordinal, RFC 8785 (§3.2.2.2) string escaping, Int64/Double typed
    /// numbers and explicit null preserved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Version"/> is the integer <c>canonicalJsonVersion</c> stored in plan.json
    /// and JournalHeader (SPEC §0.3): it versions the canonicalization algorithm itself and
    /// is a planHash input, distinct from <c>formatVersion</c> and <c>schemaVersion</c>.
    /// </para>
    /// <para>
    /// <see cref="Serialize(JToken)"/> is a pure function over an already validated JSON
    /// value model; a <see cref="JToken"/> that cannot occur in JSON text (non-finite
    /// doubles, integers outside Int64, unpaired surrogates, dates, bytes, comments or
    /// <c>undefined</c>) violates the method contract and raises
    /// <see cref="ArgumentException"/> rather than producing bytes. Text-level inputs are
    /// validated by <see cref="Parse"/>, whose failed <see cref="Result{T}"/>s carry
    /// SG-02 internal diagnostics (empty code, payload-free reason, offset and JsonPath)
    /// that the owning layer maps to an existing OW-* code exactly once.
    /// </para>
    /// </remarks>
    public static class CanonicalJson
    {
        /// <summary>The <c>canonicalJsonVersion</c> of this canonicalization (SPEC §0.3).</summary>
        public const int Version = 1;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// The Canonical JSON v1 form of <paramref name="token"/> (SPEC §0.3). The return
        /// value's UTF-8 encoding is the normative byte sequence.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="token"/> contains values outside the JSON value model validated
        /// by <see cref="Parse"/>: Int64-range overflow, non-finite or non-double floats,
        /// invalid Unicode or non-JSON token kinds.
        /// </exception>
        public static string Serialize(JToken token)
        {
            if (token is null)
            {
                throw new ArgumentNullException(nameof(token));
            }
            var builder = new StringBuilder(256);
            SerializeValue(token, builder);
            return builder.ToString();
        }

        /// <summary>
        /// Lowercase-hex SHA-256 of the canonical UTF-8 bytes (SPEC §0.3 item 3); the hash
        /// behind JsonValue/plan/descriptor digests.
        /// </summary>
        public static string Sha256(JToken token)
        {
            var bytes = s_utf8.GetBytes(Serialize(token));
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }
            return ToLowerHex(hash);
        }

        /// <summary>
        /// Strict text-level parse (SPEC §0.3, §0.6 SG-02): rejects duplicate keys,
        /// comments, leading zeros, <c>+</c> signs, lone surrogates and every other JSON
        /// grammar violation in the text itself, never by delegating to Newtonsoft
        /// defaults. Failure diagnostics are internal (empty <see cref="Diagnostic.Code"/>);
        /// the caller maps them to OW-WF-001, OW-STORE-002 or OW-CFG-001 exactly once.
        /// </summary>
        internal static Result<JToken> Parse(string json) => CanonicalJsonReader.Parse(json);

        // Iterative serializer: output depth follows the token, not the call stack, so a
        // deeply nested parse result cannot overflow the thread stack.
        private static void SerializeValue(JToken token, StringBuilder builder)
        {
            var stack = new Stack<WriteFrame>();
            WriteAtomOrOpen(token, builder, stack);
            while (stack.Count != 0)
            {
                var frame = stack.Peek();
                if (frame.MoveNext(out var name, out var child))
                {
                    if (!frame.First)
                    {
                        builder.Append(',');
                    }
                    frame.First = false;
                    if (name is not null)
                    {
                        AppendEscaped(builder, name);
                        builder.Append(':');
                    }
                    WriteAtomOrOpen(child!, builder, stack);
                }
                else
                {
                    builder.Append(frame.IsObject ? '}' : ']');
                    stack.Pop();
                }
            }
        }

        private static void WriteAtomOrOpen(JToken token, StringBuilder builder, Stack<WriteFrame> stack)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    builder.Append('{');
                    stack.Push(WriteFrame.ForObject((JObject)token));
                    return;
                case JTokenType.Array:
                    builder.Append('[');
                    stack.Push(WriteFrame.ForArray((JArray)token));
                    return;
            }
            AppendScalar(token, builder);
        }

        private static void AppendScalar(JToken token, StringBuilder builder)
        {
            switch (token.Type)
            {
                case JTokenType.String:
                    AppendEscaped(builder, RequiredString((JValue)token));
                    return;
                case JTokenType.Integer:
                    builder.Append(CanonicalJsonNumber.FormatInt64(ExtractInt64((JValue)token)));
                    return;
                case JTokenType.Float:
                    var formatted = CanonicalJsonNumber.FormatDouble(ExtractDouble((JValue)token));
                    if (!formatted.Ok)
                    {
                        throw InvalidToken("JSON float token is not a finite binary64 value.");
                    }
                    builder.Append(formatted.Value);
                    return;
                case JTokenType.Boolean:
                    builder.Append((bool)((JValue)token).Value! ? "true" : "false");
                    return;
                case JTokenType.Null:
                    builder.Append("null");
                    return;
                default:
                    throw InvalidToken(
                        "JSON token of type " + token.Type.ToString() +
                        " is outside the Canonical JSON value model.");
            }
        }

        private static string RequiredString(JValue token) =>
            token.Value as string
            ?? throw InvalidToken("JSON string token has no value.");

        private static long ExtractInt64(JValue token)
        {
            switch (token.Value)
            {
                case null:
                    throw InvalidToken("JSON integer token has no value.");
                case long value:
                    return value;
                case BigInteger bigInteger:
                    if (bigInteger < new BigInteger(long.MinValue) ||
                        bigInteger > new BigInteger(long.MaxValue))
                    {
                        throw InvalidToken("JSON integer token is out of the Int64 range.");
                    }
                    return (long)bigInteger;
                case ulong unsigned:
                    if (unsigned > long.MaxValue)
                    {
                        throw InvalidToken("JSON integer token is out of the Int64 range.");
                    }
                    return (long)unsigned;
                default:
                    return Convert.ToInt64(token.Value, CultureInfo.InvariantCulture);
            }
        }

        private static double ExtractDouble(JValue token)
        {
            switch (token.Value)
            {
                case null:
                    throw InvalidToken("JSON float token has no value.");
                case double value:
                    return value;
                case float single:
                    return single;
                case decimal precise:
                    return (double)precise;
                default:
                    return Convert.ToDouble(token.Value, CultureInfo.InvariantCulture);
            }
        }

        // RFC 8785 §3.2.2.2 escaping (SPEC §0.3 item 2): quote, reverse solidus, the five
        // short-form control characters and other U+0000..U+001F as lowercase \uXXXX.
        // Everything else — "/", U+007F and non-ASCII included — is emitted literally.
        private static void AppendEscaped(StringBuilder builder, string value)
        {
            builder.Append('"');
            var i = 0;
            while (i < value.Length)
            {
                var c = value[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\r': builder.Append("\\r"); break;
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u");
                            builder.Append(s_hexDigits[c >> 12 & 0xF]);
                            builder.Append(s_hexDigits[c >> 8 & 0xF]);
                            builder.Append(s_hexDigits[c >> 4 & 0xF]);
                            builder.Append(s_hexDigits[c & 0xF]);
                        }
                        else if (char.IsHighSurrogate(c))
                        {
                            if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                            {
                                throw InvalidToken("JSON text contains an unpaired surrogate.");
                            }
                            builder.Append(c);
                            builder.Append(value[i + 1]);
                            i += 2;
                            continue;
                        }
                        else if (char.IsLowSurrogate(c))
                        {
                            throw InvalidToken("JSON text contains an unpaired surrogate.");
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
                i++;
            }
            builder.Append('"');
        }

        private const string s_hexDigits = "0123456789abcdef";

        private static ArgumentException InvalidToken(string reason) => new ArgumentException(reason);

        private static string ToLowerHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = s_hexDigits[bytes[i] >> 4];
                chars[i * 2 + 1] = s_hexDigits[bytes[i] & 0xF];
            }
            return new string(chars);
        }

        // One open container on the serializer stack: an array enumerating items in order
        // or an object enumerating properties in UTF-16 code-unit ordinal key order
        // (StringComparer.Ordinal, SPEC §0.3 item 1).
        private sealed class WriteFrame
        {
            private readonly IEnumerator<JToken>? _items;
            private readonly IEnumerator<JProperty>? _properties;

            private WriteFrame(bool isObject, IEnumerator<JToken>? items, IEnumerator<JProperty>? properties)
            {
                IsObject = isObject;
                _items = items;
                _properties = properties;
            }

            public bool IsObject { get; }
            public bool First = true;

            public static WriteFrame ForArray(JArray array) =>
                new WriteFrame(false, array.Children().GetEnumerator(), null);

            public static WriteFrame ForObject(JObject obj) =>
                new WriteFrame(true, null,
                    obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).GetEnumerator());

            public bool MoveNext(out string? name, out JToken? value)
            {
                if (_items is not null)
                {
                    name = null;
                    if (_items.MoveNext())
                    {
                        value = _items.Current;
                        return true;
                    }
                    value = null;
                    return false;
                }
                if (_properties!.MoveNext())
                {
                    name = _properties.Current.Name;
                    value = _properties.Current.Value;
                    return true;
                }
                name = null;
                value = null;
                return false;
            }
        }
    }
}
