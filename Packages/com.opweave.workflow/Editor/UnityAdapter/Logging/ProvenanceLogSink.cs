#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The <see cref="IRunLogSink"/> the Unity StepContext exposes as
    /// <c>ctx.Log</c> for a step whose Operation registered with
    /// <see cref="OperationOrigin.Custom"/> (SPEC §F6): every entry the
    /// Operation writes is stamped with the §F6 provenance members —
    /// <c>operationId</c> and <c>origin</c> — inside the entry's
    /// <c>data</c>, the only member of the
    /// <c>RunLogEntry { ts, level, step?, code?, message, data? }</c> shape
    /// that can carry them.
    /// </summary>
    /// <remarks>
    /// An Operation's own <c>data</c> is preserved: object members merge into
    /// the stamped object and the provenance members overwrite any same-named
    /// member, so the recorded provenance is the registration's, never the
    /// Operation's claim. A non-object <c>data</c> value is nested under the
    /// <c>data</c> member of the stamped object rather than dropped.
    /// </remarks>
    internal sealed class ProvenanceLogSink : IRunLogSink
    {
        /// <summary>The §C3 wire value of a custom <see cref="OperationOrigin"/>.</summary>
        internal const string CustomOrigin = "custom";

        private readonly IRunLogSink _inner;
        private readonly string _operationId;

        /// <summary>
        /// Wraps the step's <paramref name="inner"/> sink for the custom
        /// Operation <paramref name="operationId"/>; the stamped
        /// <c>origin</c> is always <see cref="CustomOrigin"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="operationId"/> is null or empty.</exception>
        internal ProvenanceLogSink(IRunLogSink inner, string operationId)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (string.IsNullOrEmpty(operationId))
            {
                throw new ArgumentException(
                    "A provenance log sink must carry its Operation id.",
                    nameof(operationId));
            }
            _operationId = operationId;
        }

        /// <inheritdoc cref="IRunLogSink.Write"/>
        public void Write(
            Severity level, string message, string? code = null, JsonValue? data = null) =>
            _inner.Write(level, message, code, Stamp(data));

        /// <summary>
        /// The stamped form of the Runner-internal debug channel (SPEC §F6
        /// SG-08, <see cref="StepLogSink.WriteDebug"/>): a step-bound debug
        /// entry on a custom Operation's step carries the same
        /// <c>operationId</c>/<c>origin</c> provenance as every other entry
        /// the step produces. A sink that is not the journaled
        /// <see cref="StepLogSink"/> drops the write like a failed append —
        /// a log-path fault never stops the Run (SPEC §E8).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
        internal void WriteDebug(string message, string? code = null, JsonValue? data = null)
        {
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            if (_inner is StepLogSink step)
            {
                step.WriteDebug(message, code, Stamp(data));
            }
        }

        // The §F6 stamp: the Operation's own data object merged with the
        // provenance members written last, so a member named operationId or
        // origin in the Operation's data can never falsify the recorded
        // provenance. A non-object data value is nested under "data"; an
        // absent one produces the provenance object alone.
        private JsonValue Stamp(JsonValue? data)
        {
            JObject stamped;
            switch (data?.ToToken())
            {
                case null:
                    stamped = new JObject();
                    break;
                case JObject members:
                    stamped = new JObject(members);
                    break;
                case { } token:
                    stamped = new JObject { ["data"] = token };
                    break;
            }
            stamped["operationId"] = _operationId;
            stamped["origin"] = CustomOrigin;
            return JsonValue.From(stamped);
        }
    }
}
