#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// A computed PrefabContentFingerprint (SPEC §G6.1, §0.6 SG-12): the
    /// normalized structural <see cref="JsonValue"/> document and its
    /// version. <see cref="Hash"/> is the lowercase-hex SHA-256 of the
    /// document's Canonical JSON bytes and is the only value compared for
    /// Prefab content identity (SPEC §F5).
    /// </summary>
    public sealed record PrefabFingerprint(int Version, JsonValue Document)
    {
        private readonly int _version =
            Version > 0
                ? Version
                : throw new ArgumentOutOfRangeException(
                    nameof(Version), Version, "Fingerprint version must be >= 1.");

        private readonly JsonValue _document =
            Document ?? throw new ArgumentNullException(nameof(Document));

        /// <summary>The SPEC §G6.1 fingerprint format version (v1).</summary>
        public int Version
        {
            get => _version;
            init => _version = value > 0
                ? value
                : throw new ArgumentOutOfRangeException(
                    nameof(Version), value, "Fingerprint version must be >= 1.");
        }

        /// <summary>
        /// The normalized structure document (SG-12:
        /// hierarchy/components/overrides/references). Its
        /// <see cref="JsonValue.CanonicalText"/> is the golden-byte form.
        /// </summary>
        public JsonValue Document
        {
            get => _document;
            init => _document = value ?? throw new ArgumentNullException(nameof(Document));
        }

        /// <summary>
        /// Lowercase-hex SHA-256 of the Canonical JSON bytes of
        /// <see cref="Document"/> — the fingerprint compared by
        /// Prefab reconciliation (SPEC §G6.1, §0.3 item 3).
        /// </summary>
        public string Hash => _document.Sha256;
    }
}
