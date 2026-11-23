#nullable enable

using System;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The closed verdict of comparing a stored Prefab fingerprint
    /// snapshot against live content at recovery time (SPEC §G6.1, §F5):
    /// the content provably matches, provably differs, or the comparison
    /// cannot be made.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Matched"/> may feed a Completed verdict; both
    /// <see cref="Mismatched"/> and <see cref="Unknown"/> are reconciliation
    /// Unknown (NeedsReview) — a differing fingerprint is never NotApplied
    /// for content-saving Operations, and a snapshot that cannot be
    /// compared never downgrades to a cheaper check (SPEC §G6.1 禁止).
    /// </remarks>
    public abstract record PrefabFingerprintCheck
    {
        private PrefabFingerprintCheck()
        {
        }

        /// <summary>
        /// The live content's fingerprint equals the snapshot's expected
        /// fingerprint under the same version and Unity version (SPEC §G6.1
        /// item 7).
        /// </summary>
        public sealed record Matched : PrefabFingerprintCheck;

        /// <summary>
        /// A fingerprint was computed but differs from the expected one.
        /// </summary>
        public sealed record Mismatched : PrefabFingerprintCheck;

        /// <summary>
        /// The comparison cannot be made — the snapshot's
        /// <c>prefabFingerprintVersion</c> is absent/unknown/different, the
        /// recorded Unity version differs, or the live content cannot be
        /// fingerprinted. <see cref="Reason"/> is fixed English text.
        /// </summary>
        public sealed record Unknown(string Reason) : PrefabFingerprintCheck
        {
            private readonly string _reason =
                string.IsNullOrEmpty(Reason)
                    ? throw new ArgumentException(
                        "An Unknown verdict must carry a reason.", nameof(Reason))
                    : Reason;

            /// <summary>Why the comparison cannot be made, as fixed English text.</summary>
            public string Reason
            {
                get => _reason;
                init => _reason =
                    string.IsNullOrEmpty(value)
                        ? throw new ArgumentException(
                            "An Unknown verdict must carry a reason.", nameof(Reason))
                        : value;
            }
        }
    }
}
