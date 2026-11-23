#nullable enable

using System;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The closed outcome of a PrefabContentFingerprint computation
    /// (SPEC §G6.1): either a <see cref="PrefabFingerprint"/> was produced,
    /// or the content could not be normalized (<see cref="Unknown"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="Unknown"/> is not an error for the caller — it is the
    /// fail-closed answer the SPEC demands whenever the content cannot be
    /// proven identical across a save boundary (unsupported property,
    /// Missing Script, unresolvable reference, unnormalizable state). It
    /// maps to <c>ReconcileResult.Unknown</c> → NeedsReview, never to a
    /// cheaper hash or an automatic Completed (SPEC §G6.1 禁止, §F5).
    /// </remarks>
    public abstract record PrefabFingerprintResult
    {
        private PrefabFingerprintResult()
        {
        }

        /// <summary>The content normalized successfully.</summary>
        public sealed record Computed(PrefabFingerprint Fingerprint) : PrefabFingerprintResult
        {
            private readonly PrefabFingerprint _fingerprint =
                Fingerprint ?? throw new ArgumentNullException(nameof(Fingerprint));

            /// <summary>The computed v1 fingerprint.</summary>
            public PrefabFingerprint Fingerprint
            {
                get => _fingerprint;
                init => _fingerprint = value ?? throw new ArgumentNullException(nameof(Fingerprint));
            }
        }

        /// <summary>
        /// The content could not be normalized; <see cref="Reason"/> is
        /// fixed English text for the reconciliation diagnostic.
        /// </summary>
        public sealed record Unknown(string Reason) : PrefabFingerprintResult
        {
            private readonly string _reason =
                string.IsNullOrEmpty(Reason)
                    ? throw new ArgumentException(
                        "An Unknown outcome must carry a reason.", nameof(Reason))
                    : Reason;

            /// <summary>Why the fingerprint could not be computed, as fixed English text.</summary>
            public string Reason
            {
                get => _reason;
                init => _reason =
                    string.IsNullOrEmpty(value)
                        ? throw new ArgumentException(
                            "An Unknown outcome must carry a reason.", nameof(Reason))
                        : value;
            }
        }
    }
}
