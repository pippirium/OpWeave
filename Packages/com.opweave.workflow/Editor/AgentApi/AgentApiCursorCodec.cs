#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// Issues and verifies the opaque continuation cursors of SPEC §H3 item 3.
    /// A cursor is <c>base64url(canonicalJSON payload ‖ 16-byte HMAC-SHA256 tag)</c>
    /// where the payload is <c>{"v":1,"ctx":&lt;context digest&gt;,"off":&lt;item offset&gt;}</c>
    /// for a plain page position and additionally
    /// <c>"seg":&lt;UTF-16 segment offset&gt;,"dat":&lt;hex SHA-256 of the segmented canonical text&gt;</c>
    /// while a response is paging through the Canonical JSON of one oversized
    /// response envelope: the format is private to this codec, so a cursor stays an opaque
    /// string to Agents (local, reversible implementation choice — SPEC fixes
    /// only opacity and the OW-API-002 failure for invalid cursors).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The HMAC is keyed per codec instance — a random key is drawn at service
    /// construction, so a cursor survives neither a domain reload nor a restart
    /// and cannot be forged or mutated: any decode failure, tag mismatch, version
    /// mismatch, malformed payload or context mismatch resolves to the single
    /// OW-API-002 diagnostic (SPEC §J).
    /// </para>
    /// <para>
    /// The context digest binds a cursor to the request that produced it, so a
    /// cursor replayed against different command arguments is rejected instead
    /// of resuming an unrelated listing at a wrong offset.
    /// </para>
    /// </remarks>
    internal sealed class AgentApiCursorCodec
    {
        private const int PayloadVersion = 1;
        private const int TagBytes = 16;
        private const int KeyBytes = 32;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);
        private static readonly char[] s_hex = "0123456789abcdef".ToCharArray();

        private readonly byte[] _key;

        /// <summary>A codec keyed by <paramref name="key"/>; tests inject a fixed
        /// key for determinism while production draws a random one.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
        internal AgentApiCursorCodec(byte[] key)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (key.Length == 0)
            {
                throw new ArgumentException("The cursor key must not be empty.", nameof(key));
            }
            _key = (byte[])key.Clone();
        }

        /// <summary>A codec with a freshly drawn random key.</summary>
        internal static AgentApiCursorCodec Random() => new AgentApiCursorCodec(RandomBytes(KeyBytes));

        /// <summary>
        /// Issues the cursor that resumes <paramref name="context"/> at item
        /// <paramref name="offset"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
        internal string Issue(string context, int offset)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            return Issue(new JObject
            {
                ["v"] = PayloadVersion,
                ["ctx"] = ContextDigest(context),
                ["off"] = offset,
            });
        }

        /// <summary>
        /// Issues the cursor that resumes <paramref name="context"/> inside the
        /// Canonical-JSON segments of the oversized response envelope at
        /// <paramref name="itemOffset"/>: the next segment starts at UTF-16
        /// offset <paramref name="segmentOffset"/> of the canonical text whose
        /// lower-hex SHA-256 is <paramref name="dataDigest"/>. The digest is
        /// replayed against freshly serialized data on resume, so the cursor
        /// dies with OW-API-002 the moment the content it paginates changes.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="context"/> or <paramref name="dataDigest"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="itemOffset"/> is negative or
        /// <paramref name="segmentOffset"/> is below 1 (a segment cursor always
        /// resumes strictly inside the text; offset 0 is the first response and
        /// carries no cursor).
        /// </exception>
        internal string IssueSegment(
            string context, int itemOffset, int segmentOffset, string dataDigest)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (dataDigest is null)
            {
                throw new ArgumentNullException(nameof(dataDigest));
            }
            if (itemOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(itemOffset));
            }
            if (segmentOffset < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentOffset));
            }
            return Issue(new JObject
            {
                ["v"] = PayloadVersion,
                ["ctx"] = ContextDigest(context),
                ["off"] = itemOffset,
                ["seg"] = segmentOffset,
                ["dat"] = dataDigest,
            });
        }

        private string Issue(JObject payload)
        {
            var body = s_utf8.GetBytes(CanonicalJson.Serialize(payload));
            var wire = new byte[body.Length + TagBytes];
            Buffer.BlockCopy(body, 0, wire, 0, body.Length);
            Buffer.BlockCopy(Tag(body), 0, wire, body.Length, TagBytes);
            return ToBase64Url(wire);
        }

        /// <summary>
        /// Verifies <paramref name="cursor"/> for <paramref name="expectedContext"/>
        /// and returns its resume position. Every failure — undecodable, tampered,
        /// wrong version or shape, or issued for another context — is the single
        /// OW-API-002 diagnostic (SPEC §J); a null or empty cursor is invalid too
        /// (the absence of a cursor is expressed by passing
        /// <see langword="null"/> to the caller, which never reaches this method).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="expectedContext"/> is null.
        /// </exception>
        internal Result<AgentApiCursor> Decode(string? cursor, string expectedContext)
        {
            if (expectedContext is null)
            {
                throw new ArgumentNullException(nameof(expectedContext));
            }
            var failure = Result<AgentApiCursor>.Failure(AgentApiErrors.InvalidCursor());
            if (string.IsNullOrEmpty(cursor))
            {
                return failure;
            }
            var wire = FromBase64Url(cursor!);
            if (wire is null || wire.Length <= TagBytes)
            {
                return failure;
            }
            var bodyLength = wire.Length - TagBytes;
            var body = new byte[bodyLength];
            Buffer.BlockCopy(wire, 0, body, 0, bodyLength);
            if (!TagEquals(body, wire, bodyLength))
            {
                return failure;
            }
            var parsed = JsonValue.Parse(s_utf8.GetString(body));
            if (!parsed.Ok || parsed.Value!.ToToken() is not JObject payload)
            {
                return failure;
            }
            if (payload["v"] is not JValue version ||
                version.Type != JTokenType.Integer ||
                version.Value<long>() != PayloadVersion ||
                payload["ctx"] is not JValue context ||
                context.Type != JTokenType.String ||
                payload["off"] is not JValue offset ||
                offset.Type != JTokenType.Integer)
            {
                return failure;
            }
            var resumeOffset = offset.Value<long>();
            if (resumeOffset < 0 || resumeOffset > int.MaxValue)
            {
                return failure;
            }
            // The segment pair is all-or-nothing: "seg" (next UTF-16 offset into
            // the segmented canonical text) and "dat" (its hex SHA-256) appear
            // together or the payload is malformed.
            var segment = payload["seg"];
            var digest = payload["dat"];
            long segmentOffset = 0;
            string? dataDigest = null;
            if (segment is not null || digest is not null)
            {
                if (segment is not JValue segmentValue ||
                    segmentValue.Type != JTokenType.Integer ||
                    digest is not JValue digestValue ||
                    digestValue.Type != JTokenType.String)
                {
                    return failure;
                }
                segmentOffset = segmentValue.Value<long>();
                if (segmentOffset < 0 || segmentOffset > int.MaxValue)
                {
                    return failure;
                }
                dataDigest = digestValue.Value<string?>();
                if (dataDigest is null)
                {
                    return failure;
                }
            }
            if (!string.Equals(
                    context.Value<string?>(), ContextDigest(expectedContext),
                    StringComparison.Ordinal))
            {
                return failure;
            }
            return Result<AgentApiCursor>.Success(
                new AgentApiCursor((int)resumeOffset, (int)segmentOffset, dataDigest));
        }

        /// <summary>
        /// The lower-hex SHA-256 of <paramref name="context"/>: the opaque binding
        /// stored in the payload — it matches only when a cursor is replayed
        /// against identical request material and keeps the raw arguments out of
        /// the cursor itself.
        /// </summary>
        internal static string ContextDigest(string context)
        {
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(s_utf8.GetBytes(context));
            }
            var chars = new char[hash.Length * 2];
            for (var i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = s_hex[hash[i] >> 4];
                chars[i * 2 + 1] = s_hex[hash[i] & 0xF];
            }
            return new string(chars);
        }

        /// <summary>
        /// The lower-hex SHA-256 of <paramref name="text"/> — the digest a segment
        /// cursor binds to the Canonical JSON it paginates, so a replay against
        /// changed data fails its check.
        /// </summary>
        internal static string TextDigest(string text) => ContextDigest(text);

        private byte[] Tag(byte[] body)
        {
            byte[] full;
            using (var hmac = new HMACSHA256(_key))
            {
                full = hmac.ComputeHash(body);
            }
            var tag = new byte[TagBytes];
            Buffer.BlockCopy(full, 0, tag, 0, TagBytes);
            return tag;
        }

        // Constant-work comparison: the loop never exits early, so the tag check
        // leaks no prefix information (SPEC §H3 requires rejection only; this is
        // the standard verification form).
        private bool TagEquals(byte[] body, byte[] wire, int bodyLength)
        {
            var tag = Tag(body);
            var difference = 0;
            for (var i = 0; i < TagBytes; i++)
            {
                difference |= tag[i] ^ wire[bodyLength + i];
            }
            return difference == 0;
        }

        private static string ToBase64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[]? FromBase64Url(string text)
        {
            var builder = new StringBuilder(text.Length + 4);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '-')
                {
                    builder.Append('+');
                }
                else if (c == '_')
                {
                    builder.Append('/');
                }
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                         (c >= '0' && c <= '9'))
                {
                    builder.Append(c);
                }
                else
                {
                    return null;
                }
            }
            if (builder.Length % 4 == 1)
            {
                return null;
            }
            while (builder.Length % 4 != 0)
            {
                builder.Append('=');
            }
            try
            {
                return Convert.FromBase64String(builder.ToString());
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return bytes;
        }
    }

    /// <summary>
    /// The verified resume position carried by a decoded cursor: either a plain
    /// item offset or, while a response pages through the Canonical JSON of one
    /// oversized response envelope, the item plus the segment offset and the
    /// envelope's digest (<see cref="AgentApiCursorCodec.Decode"/>).
    /// </summary>
    internal readonly struct AgentApiCursor
    {
        internal AgentApiCursor(int itemOffset, int segmentOffset, string? dataDigest)
        {
            ItemOffset = itemOffset;
            SegmentOffset = segmentOffset;
            DataDigest = dataDigest;
        }

        /// <summary>The item index a page resumes at — also the item whose
        /// canonical text a segment cursor is paging through.</summary>
        internal int ItemOffset { get; }

        /// <summary>The UTF-16 offset into the segmented Canonical JSON text the
        /// next segment starts at; 0 on a plain item cursor.</summary>
        internal int SegmentOffset { get; }

        /// <summary>The lower-hex SHA-256 of the segmented Canonical JSON text;
        /// null on a plain item cursor.</summary>
        internal string? DataDigest { get; }

        /// <summary><c>true</c> when the cursor resumes inside the Canonical-JSON
        /// segments of one oversized payload rather than at an item boundary.</summary>
        internal bool IsSegment => DataDigest is not null;
    }
}
