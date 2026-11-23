#nullable enable

using System;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The immutable JSON value held by every Core public model (SPEC §0.1 F-07, §0.4).
    /// Explicit JSON <c>null</c> is a valid <see cref="JsonValue"/> whose
    /// <see cref="CanonicalText"/> is <c>"null"</c>; a missing parameter is represented by
    /// the caller's own absence marker, never by a <c>null</c> <see cref="JsonValue"/>
    /// reference or a serialized <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instance stores a normalized <see cref="JToken"/> tree — parsed back from
    /// <see cref="CanonicalText"/>, so keys are in UTF-16 code-unit ordinal order and
    /// numbers carry only the Int64/Double typed forms of SPEC §0.3 — that is never
    /// exposed. <see cref="From(JToken)"/> duplicates the caller's token before
    /// canonicalizing it, and <see cref="ToToken"/> returns a fresh
    /// <see cref="JToken.DeepClone"/> on every call; neither direction lets a caller
    /// observe or mutate the held tree. <see cref="JToken"/> therefore stays a boundary
    /// representation copied in and out (SPEC §0.4).
    /// </para>
    /// <para>
    /// <see cref="Parse"/> goes through the strict Canonical JSON reader, so text-level
    /// failures are SG-02 internal diagnostics — an empty <see cref="Diagnostic.Code"/>,
    /// a payload-free reason carrying the offset, and a JsonPath whose object member
    /// steps are <c>*</c> wildcards — returned unmapped inside the failed
    /// <see cref="Result{T}"/> for the owning layer to map exactly once (SPEC §0.6
    /// SG-02). <see cref="From(JToken)"/> instead takes an already materialized token;
    /// tokens outside the JSON value model (non-finite doubles, Int64-range overflow,
    /// unpaired surrogates, dates, bytes, comments, <c>undefined</c>) violate the
    /// <see cref="CanonicalJson.Serialize"/> contract and raise
    /// <see cref="ArgumentException"/>.
    /// </para>
    /// </remarks>
    public sealed class JsonValue : IEquatable<JsonValue>
    {
        private readonly JToken _token;

        private JsonValue(JToken token, string canonicalText, string sha256)
        {
            _token = token;
            CanonicalText = canonicalText;
            Sha256 = sha256;
        }

        /// <summary>
        /// Duplicates <paramref name="token"/>, canonicalizes the copy and holds the
        /// normalized form (SPEC §0.4). Later mutation of the caller's token cannot
        /// reach the <see cref="JsonValue"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="token"/> is outside the JSON value model validated by
        /// <see cref="CanonicalJson.Serialize"/>.
        /// </exception>
        public static JsonValue From(JToken token)
        {
            if (token is null)
            {
                throw new ArgumentNullException(nameof(token));
            }
            var canonicalText = CanonicalJson.Serialize(token.DeepClone());
            return FromCanonicalText(canonicalText);
        }

        /// <summary>
        /// Strictly parses <paramref name="json"/> into a <see cref="JsonValue"/>
        /// (SPEC §0.4). Malformed text returns a failed <see cref="Result{T}"/> carrying
        /// the reader's SG-02 internal diagnostic unchanged — never an exception for
        /// JSON-level errors.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        public static Result<JsonValue> Parse(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }
            var parsed = CanonicalJson.Parse(json);
            if (!parsed.Ok)
            {
                return Result<JsonValue>.Failure(parsed.Diagnostics);
            }
            return Result<JsonValue>.Success(
                FromCanonicalText(CanonicalJson.Serialize(parsed.Value!)));
        }

        /// <summary>
        /// Wraps a string as a JSON string <see cref="JsonValue"/> so the SG-05
        /// builder signature <c>JsonValue? @default</c> accepts plain string
        /// literals (SPEC §C2 example: <c>@default: "none"</c>). A
        /// <see langword="null"/> string produces the explicit JSON
        /// <c>null</c> value.
        /// </summary>
        public static implicit operator JsonValue(string? value) =>
            From(value is null ? JValue.CreateNull() : new JValue(value));

        /// <summary>The Canonical JSON v1 form of the value (SPEC §0.3).</summary>
        public string CanonicalText { get; }

        /// <summary>
        /// Lowercase-hex SHA-256 of <see cref="CanonicalText"/>'s UTF-8 bytes
        /// (SPEC §0.3 item 3).
        /// </summary>
        public string Sha256 { get; }

        /// <summary>
        /// A fresh clone of the held token (SPEC §0.4). Each call returns a new tree;
        /// mutating a returned token never changes this <see cref="JsonValue"/>.
        /// </summary>
        public JToken ToToken() => _token.DeepClone();

        /// <summary>Equality by <see cref="CanonicalText"/> ordinal match (SPEC §0.4).</summary>
        public bool Equals(JsonValue? other) =>
            other is not null &&
            string.Equals(CanonicalText, other.CanonicalText, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as JsonValue);

        public override int GetHashCode() =>
            StringComparer.Ordinal.GetHashCode(CanonicalText);

        // Rebuilds the normalized tree from canonical text produced by this assembly;
        // serializer output always satisfies the strict reader, so the parse cannot fail.
        private static JsonValue FromCanonicalText(string canonicalText)
        {
            var normalized = CanonicalJson.Parse(canonicalText).Value!;
            return new JsonValue(normalized, canonicalText, CanonicalJson.Sha256(normalized));
        }
    }
}
