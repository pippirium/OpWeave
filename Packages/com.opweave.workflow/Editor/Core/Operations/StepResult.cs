#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The outcome of one <see cref="IOperation.Execute"/> or
    /// <see cref="IOperation.Poll"/> call (SPEC §C2): a success, a suspended
    /// wait carrying the resume token the Runner persists in the StepWaiting
    /// journal record, or a failure carrying one <see cref="Diagnostic"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instances are created only through <see cref="Succeeded"/>,
    /// <see cref="Waiting"/> and <see cref="Failed"/>; <see cref="Kind"/>
    /// selects which payload members are meaningful —
    /// <see cref="ResumeToken"/>, <see cref="WaitingFor"/> and
    /// <see cref="PollIntervalMs"/> for <see cref="StepResultKind.Waiting"/>,
    /// <see cref="Error"/> for <see cref="StepResultKind.Failed"/>.
    /// </para>
    /// <para>
    /// <see cref="PollIntervalMs"/> is the requested delay between polls;
    /// a non-positive value is rejected because the wait would degenerate
    /// into a tight loop on the main thread (SPEC §C2 item 3).
    /// </para>
    /// </remarks>
    public readonly struct StepResult
    {
        private readonly JsonValue? _resumeToken;
        private readonly string? _waitingFor;
        private readonly Diagnostic? _error;

        private StepResult(
            StepResultKind kind,
            JsonValue? resumeToken,
            string? waitingFor,
            int pollIntervalMs,
            Diagnostic? error)
        {
            Kind = kind;
            _resumeToken = resumeToken;
            _waitingFor = waitingFor;
            PollIntervalMs = pollIntervalMs;
            _error = error;
        }

        /// <summary>Which of the three SPEC §C2 outcomes this result is.</summary>
        public StepResultKind Kind { get; }

        /// <summary>
        /// The <see cref="StepResultKind.Waiting"/> payload: the only place a
        /// Waitable Operation keeps state across a domain reload (SPEC §C2
        /// item 4). <see langword="null"/> for the other kinds.
        /// </summary>
        public JsonValue? ResumeToken => _resumeToken;

        /// <summary>
        /// The <see cref="StepResultKind.Waiting"/> payload: a short fixed
        /// English label of what the step is waiting for, journaled as
        /// StepWaiting <c>waitingFor</c> (SPEC §F2).
        /// </summary>
        public string? WaitingFor => _waitingFor;

        /// <summary>
        /// The <see cref="StepResultKind.Waiting"/> payload: the requested
        /// delay in milliseconds before the next <see cref="IOperation.Poll"/>;
        /// positive. <c>0</c> for the other kinds.
        /// </summary>
        public int PollIntervalMs { get; }

        /// <summary>
        /// The <see cref="StepResultKind.Failed"/> payload.
        /// <see langword="null"/> for the other kinds.
        /// </summary>
        public Diagnostic? Error => _error;

        /// <summary>A finished step (SPEC §C2).</summary>
        public static StepResult Succeeded() =>
            new StepResult(StepResultKind.Succeeded, null, null, 0, null);

        /// <summary>
        /// A suspended step; <paramref name="resumeToken"/> is persisted in the
        /// StepWaiting journal record and handed back through
        /// <see cref="StepContext.ResumeToken"/> on the next
        /// <see cref="IOperation.Poll"/> (SPEC §C2 items 3–4, §F2).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="resumeToken"/> or <paramref name="waitingFor"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="waitingFor"/> is empty.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="pollIntervalMs"/> is not positive.
        /// </exception>
        public static StepResult Waiting(
            JsonValue resumeToken, string waitingFor, int pollIntervalMs = 100)
        {
            if (resumeToken is null)
            {
                throw new ArgumentNullException(nameof(resumeToken));
            }
            if (waitingFor is null)
            {
                throw new ArgumentNullException(nameof(waitingFor));
            }
            if (waitingFor.Length == 0)
            {
                throw new ArgumentException(
                    "waitingFor must be a non-empty label.", nameof(waitingFor));
            }
            if (pollIntervalMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pollIntervalMs),
                    "pollIntervalMs must be positive.");
            }
            return new StepResult(
                StepResultKind.Waiting, resumeToken, waitingFor, pollIntervalMs, null);
        }

        /// <summary>A failed step carrying one <see cref="Diagnostic"/> (SPEC §E4).</summary>
        /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
        public static StepResult Failed(Diagnostic error)
        {
            if (error is null)
            {
                throw new ArgumentNullException(nameof(error));
            }
            return new StepResult(StepResultKind.Failed, null, null, 0, error);
        }
    }
}
