#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The outcome of one <see cref="IWaitCondition.Check"/> call (SPEC §G4):
    /// <see cref="WaitStatusKind.Pending"/> with the resume token the next
    /// check continues from, <see cref="WaitStatusKind.Done"/>, or
    /// <see cref="WaitStatusKind.Failed"/> carrying one
    /// <see cref="Diagnostic"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instances are created only through <see cref="Pending"/>,
    /// <see cref="Done"/> and <see cref="Failed"/>;
    /// <see cref="WaitStatusKind"/> selects which payload member is
    /// meaningful — <see cref="ResumeToken"/> for
    /// <see cref="WaitStatusKind.Pending"/>, <see cref="Error"/> for
    /// <see cref="WaitStatusKind.Failed"/>.
    /// </para>
    /// <para>
    /// The Pending payload is the condition's wait state rewritten: a
    /// condition restores only from the resume token (SPEC §G4), so the
    /// caller — a Waitable Operation's <see cref="IOperation.Poll"/> — hands
    /// this token to <see cref="StepResult.Waiting"/> so the next check sees
    /// the same state again, across a domain reload.
    /// </para>
    /// </remarks>
    public readonly struct WaitStatus
    {
        private readonly JsonValue? _resumeToken;
        private readonly Diagnostic? _error;

        private WaitStatus(
            WaitStatusKind kind, JsonValue? resumeToken, Diagnostic? error)
        {
            Kind = kind;
            _resumeToken = resumeToken;
            _error = error;
        }

        /// <summary>Which of the three SPEC §G4 verdicts this status is.</summary>
        public WaitStatusKind Kind { get; }

        /// <summary>
        /// The <see cref="WaitStatusKind.Pending"/> payload: the resume token
        /// the next <see cref="IWaitCondition.Check"/> continues from —
        /// <see langword="null"/> for the other kinds.
        /// </summary>
        public JsonValue? ResumeToken => _resumeToken;

        /// <summary>
        /// The <see cref="WaitStatusKind.Failed"/> payload.
        /// <see langword="null"/> for the other kinds.
        /// </summary>
        public Diagnostic? Error => _error;

        /// <summary>
        /// A still-waiting verdict; <paramref name="resumeToken"/> is the
        /// token the next check is called with.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="resumeToken"/> is null.
        /// </exception>
        public static WaitStatus Pending(JsonValue resumeToken)
        {
            if (resumeToken is null)
            {
                throw new ArgumentNullException(nameof(resumeToken));
            }
            return new WaitStatus(WaitStatusKind.Pending, resumeToken, null);
        }

        /// <summary>A completed wait (SPEC §G4).</summary>
        public static WaitStatus Done() =>
            new WaitStatus(WaitStatusKind.Done, null, null);

        /// <summary>A failed wait carrying one <see cref="Diagnostic"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
        public static WaitStatus Failed(Diagnostic error)
        {
            if (error is null)
            {
                throw new ArgumentNullException(nameof(error));
            }
            return new WaitStatus(WaitStatusKind.Failed, null, error);
        }
    }
}
