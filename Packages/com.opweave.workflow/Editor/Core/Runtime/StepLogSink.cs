#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The step-bound <see cref="IRunLogSink"/> behind
    /// <see cref="StepContext.Log"/> (SPEC §F6): each Operation
    /// <see cref="Write"/> becomes a <c>log</c> journal record — optional
    /// information, <c>required: false</c>, Buffered per the SPEC §F2 table —
    /// carrying the step index in the record envelope and
    /// <c>{level, code?, message, data?}</c> in its data payload.
    /// </summary>
    /// <remarks>
    /// Appends are best-effort: a Buffered write that the store refuses is
    /// dropped, because the next required record's append surfaces the same
    /// store failure as OW-STORE-001 at the state-changing boundary it
    /// actually threatens (SPEC §F1). Before anything is persisted the sink
    /// applies the SPEC §F6 mask (<see cref="CredentialMask"/>): known
    /// credential keys in <c>data</c> and URL userinfo in <c>data</c> and
    /// the message collapse to <c>"***"</c>; the remaining "no secrets
    /// through ctx.Log" contract — free-text secrets no key or URL shape
    /// reveals — stays with the Operation (SPEC §F6).
    /// </remarks>
    internal sealed class StepLogSink : IRunLogSink
    {
        private readonly string _runId;
        private readonly int _step;
        private readonly Func<string, JournalRecordType, int?, JsonValue, Result<Unit>> _append;

        /// <summary>
        /// Binds the sink to <paramref name="runId"/>/<paramref name="step"/>;
        /// <paramref name="append"/> is the coordinator's record-append seam.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/> or <paramref name="append"/> is null.
        /// </exception>
        internal StepLogSink(
            string runId,
            int step,
            Func<string, JournalRecordType, int?, JsonValue, Result<Unit>> append)
        {
            _runId = runId ?? throw new ArgumentNullException(nameof(runId));
            _step = step;
            _append = append ?? throw new ArgumentNullException(nameof(append));
        }

        /// <inheritdoc cref="IRunLogSink.Write"/>
        public void Write(
            Severity level, string message, string? code = null, JsonValue? data = null)
        {
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            Append(level.ToString(), message, code, data);
        }

        /// <summary>
        /// The Runner-internal debug channel of SPEC §F6 SG-08: writes one
        /// <c>log</c> record with <c>level: "debug"</c> — a level the §0.6
        /// <see cref="Severity"/> set deliberately does not expose to
        /// Operations — journaled only while the coordinator's injected
        /// <c>debugLogging</c> policy permits; otherwise the write is
        /// dropped before it reaches the Core log store.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
        internal void WriteDebug(string message, string? code = null, JsonValue? data = null)
        {
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            Append("debug", message, code, data);
        }

        private void Append(string levelName, string message, string? code, JsonValue? data)
        {
            var payload = new JObject
            {
                ["level"] = JsonNaming.ToSnakeCase(levelName),
                ["message"] = CredentialMask.MaskText(message),
            };
            if (code is not null)
            {
                payload["code"] = code;
            }
            if (data is not null)
            {
                payload["data"] = CredentialMask.MaskToken(data.ToToken());
            }
            _append(_runId, JournalRecordType.Log, _step, JsonValue.From(payload));
        }
    }
}
