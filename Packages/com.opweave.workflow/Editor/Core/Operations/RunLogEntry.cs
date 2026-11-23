#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// One structured log line bound to a Run or a step (SPEC §F6):
    /// <c>{ts, level, step?, code?, message, data?}</c>. Entries come from the
    /// Runner, from Operations through <see cref="IRunLogSink"/> and from the
    /// Unity Console during a step; <see cref="Message"/> is fixed English
    /// text and must never carry secrets (SPEC §F6).
    /// </summary>
    public sealed record RunLogEntry(
        string Ts,
        Severity Level,
        int? Step,
        string? Code,
        string Message,
        JsonValue? Data = null)
    {
        private readonly string _ts =
            string.IsNullOrEmpty(Ts)
                ? throw new ArgumentException(
                    "A log entry must carry a timestamp.", nameof(Ts))
                : Ts;
        private readonly string _message =
            Message ?? throw new ArgumentNullException(nameof(Message));

        /// <summary>UTC ISO 8601 timestamp with milliseconds (SPEC §0.4).</summary>
        public string Ts
        {
            get => _ts;
            init => _ts =
                string.IsNullOrEmpty(value)
                    ? throw new ArgumentException(
                        "A log entry must carry a timestamp.", nameof(Ts))
                    : value;
        }

        /// <summary>The fixed English message (SPEC §F6).</summary>
        public string Message
        {
            get => _message;
            init => _message =
                value ?? throw new ArgumentNullException(nameof(Message));
        }
    }
}
