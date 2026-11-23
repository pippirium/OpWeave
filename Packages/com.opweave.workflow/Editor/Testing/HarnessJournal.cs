#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Persistence;

namespace OpWeave.Testing
{
    /// <summary>
    /// The SPEC §F2 record list one harness run produces (SPEC §M.6 item 1).
    /// A harness run has no Run directory — there is no Plan to write and no
    /// <c>planHash</c> to verify, and SPEC §M.6 runs the Operation precisely
    /// <em>without</em> a workflow — so the records it would journal are kept in
    /// order on the result instead of appended to a Run Store journal. Each one
    /// is a real <see cref="JournalRecord"/>: the same type, the same
    /// <c>step</c>/<c>attempt</c>/<c>data</c> members and the same
    /// <c>required</c>/durability class the SPEC §F2 table fixes for its type,
    /// so <see cref="JournalCodec.WriteRecord"/> encodes it byte for byte as a
    /// journal line.
    /// </summary>
    /// <remarks>
    /// The write order follows SPEC §E4: <c>step_started</c> with the Preflight
    /// snapshot and the inputs digest is produced before any side effect, and
    /// only then the outcome record. A <c>log</c> record from the SPEC §0.6
    /// <see cref="OpWeave.Core.Operations.StepContext.Log"/> sink sits wherever
    /// the Operation wrote it.
    /// </remarks>
    internal sealed class HarnessJournal
    {
        private readonly string _session;
        private readonly List<JournalRecord> _records = new List<JournalRecord>();
        private long _seq;

        /// <summary>
        /// Creates an empty journal whose records carry
        /// <paramref name="session"/> as their <c>session</c> envelope member —
        /// the harness's own session identity, never the Runner host's Editor
        /// session id.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="session"/> is null or empty.
        /// </exception>
        internal HarnessJournal(string session)
        {
            if (string.IsNullOrEmpty(session))
            {
                throw new ArgumentException(
                    "The harness journal session must not be null or empty.", nameof(session));
            }
            _session = session;
        }

        /// <summary>The records produced so far, in write order.</summary>
        internal IReadOnlyList<JournalRecord> Records => _records;

        /// <summary>Appends one step record with the SPEC §F2 durability of its type.</summary>
        internal void AppendStep(JournalRecordType type, int step, int attempt, JObject data)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            Append(type, step, attempt, JsonValue.From(data));
        }

        /// <summary>
        /// The append seam the Core <c>StepLogSink</c> writes its §F6 <c>log</c>
        /// records through; it always admits, because a harness journal cannot
        /// fail the way a store does.
        /// </summary>
        internal Result<Unit> AppendFromLogSink(
            string runId, JournalRecordType type, int? step, JsonValue data)
        {
            Append(type, step, null, data);
            return Result<Unit>.Success(Unit.Value);
        }

        /// <summary>
        /// The step's journaled state (SPEC §E1) read back from the records,
        /// the way the SPEC §F2 fold derives it from a journal: the last
        /// state-bearing step record wins and the optional <c>log</c> records
        /// never change it. A journal with no step record at all is
        /// <see cref="StepState.Pending"/> — the step was never started.
        /// </summary>
        internal static StepState JournaledStateOf(IReadOnlyList<JournalRecord> records)
        {
            if (records is null)
            {
                throw new ArgumentNullException(nameof(records));
            }
            for (var i = records.Count - 1; i >= 0; i--)
            {
                switch (records[i].Type)
                {
                    case JournalRecordType.StepStarted:
                        return StepState.Running;
                    case JournalRecordType.StepSucceeded:
                        return StepState.Succeeded;
                    case JournalRecordType.StepFailed:
                        return StepState.Failed;
                    case JournalRecordType.StepWaiting:
                        return StepState.Waiting;
                    case JournalRecordType.StepCancelled:
                        return StepState.Cancelled;
                    case JournalRecordType.StepSkipped:
                        return StepState.Skipped;
                    case JournalRecordType.StepNeedsReview:
                        return StepState.NeedsReview;
                    default:
                        // log and resource records carry no step state; the
                        // fold reads them without moving the step.
                        continue;
                }
            }
            return StepState.Pending;
        }

        private void Append(
            JournalRecordType type, int? step, int? attempt, JsonValue data)
        {
            _seq++;
            _records.Add(new JournalRecord(
                _seq,
                UtcTimestamp.Format(DateTimeOffset.UtcNow),
                _session,
                type,
                step,
                attempt,
                data));
        }
    }
}
