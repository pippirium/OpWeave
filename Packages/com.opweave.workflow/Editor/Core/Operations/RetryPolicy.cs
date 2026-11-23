#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The retry contract of SPEC §E7: at most <see cref="MaxAttempts"/> attempts,
    /// starting at <see cref="InitialBackoffMs"/> milliseconds and doubling by
    /// <see cref="Multiplier"/> up to <see cref="MaxBackoffMs"/>. Automatic
    /// retry additionally requires a retryable diagnostic and the SPEC §F5
    /// re-execution verdict. Each check runs in both the backing-field
    /// initializer and the <c>init</c> accessor so record <c>with</c>
    /// expressions cannot bypass it.
    /// </summary>
    public sealed record RetryPolicy(
        int MaxAttempts = 1,
        int InitialBackoffMs = 500,
        double Multiplier = 2.0,
        int MaxBackoffMs = 10_000)
    {
        private readonly int _maxAttempts = CheckMaxAttempts(MaxAttempts);
        private readonly int _initialBackoffMs = CheckInitialBackoffMs(InitialBackoffMs);
        private readonly double _multiplier = CheckMultiplier(Multiplier);
        private readonly int _maxBackoffMs = CheckMaxBackoffMs(MaxBackoffMs);

        /// <summary>The maximum number of attempts including the first.</summary>
        public int MaxAttempts
        {
            get => _maxAttempts;
            init => _maxAttempts = CheckMaxAttempts(value);
        }

        /// <summary>The first retry delay in milliseconds.</summary>
        public int InitialBackoffMs
        {
            get => _initialBackoffMs;
            init => _initialBackoffMs = CheckInitialBackoffMs(value);
        }

        /// <summary>The backoff growth factor.</summary>
        public double Multiplier
        {
            get => _multiplier;
            init => _multiplier = CheckMultiplier(value);
        }

        /// <summary>The maximum retry delay in milliseconds.</summary>
        public int MaxBackoffMs
        {
            get => _maxBackoffMs;
            init => _maxBackoffMs = CheckMaxBackoffMs(value);
        }

        private static int CheckMaxAttempts(int maxAttempts) =>
            maxAttempts < 1
                ? throw new ArgumentOutOfRangeException(
                    nameof(MaxAttempts), maxAttempts, "Max attempts must be at least 1.")
                : maxAttempts;

        private static int CheckInitialBackoffMs(int initialBackoffMs) =>
            initialBackoffMs < 0
                ? throw new ArgumentOutOfRangeException(
                    nameof(InitialBackoffMs), initialBackoffMs,
                    "Initial backoff must not be negative.")
                : initialBackoffMs;

        private static double CheckMultiplier(double multiplier) =>
            double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0.0
                ? throw new ArgumentOutOfRangeException(
                    nameof(Multiplier), multiplier,
                    "Backoff multiplier must be a positive finite number.")
                : multiplier;

        private static int CheckMaxBackoffMs(int maxBackoffMs) =>
            maxBackoffMs < 0
                ? throw new ArgumentOutOfRangeException(
                    nameof(MaxBackoffMs), maxBackoffMs, "Max backoff must not be negative.")
                : maxBackoffMs;
    }
}
