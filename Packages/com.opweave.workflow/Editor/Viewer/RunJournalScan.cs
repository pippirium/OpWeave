#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Persistence;

namespace OpWeave.Viewer
{
    /// <summary>
    /// The Runs-tab journal read (SPEC §H5): one pass over a Run's
    /// <c>journal.ndjson</c> collecting the per-step timing the step list's
    /// "所要時間" needs and the journaled <c>log</c> rows. The journal is the
    /// Run's single source of truth (SPEC §F1), so the window reads it
    /// rather than any in-memory copy.
    /// </summary>
    /// <remarks>
    /// The failure rules mirror the §F7 change-list reader
    /// (<c>JournaledChanges</c>): a record that cannot be decoded inside the
    /// journal reports the failure instead of a silently partial view, and
    /// the SPEC §F3 item 3 droppable tail — a broken LAST line whose record
    /// never committed — ends the scan with what was already read.
    /// </remarks>
    internal sealed class RunJournalScan
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private RunJournalScan(
            Dictionary<int, StepJournal> steps,
            List<RunViewerLogRow> logs,
            ReviewJournal? latestReview,
            string? failure)
        {
            Steps = steps;
            Logs = logs;
            LatestReview = latestReview;
            Failure = failure;
        }

        /// <summary>The per-step journal evidence, keyed by Plan step index.</summary>
        internal Dictionary<int, StepJournal> Steps { get; }

        /// <summary>The journaled log rows in journal order.</summary>
        internal List<RunViewerLogRow> Logs { get; }

        /// <summary>
        /// The latest <c>step_needs_review</c> record's reason — the
        /// "理由" the SPEC §H5 NeedsReview panel shows — or null when the
        /// journal records none.
        /// </summary>
        internal ReviewJournal? LatestReview { get; }

        /// <summary>Why the journal could not be read whole, or null when it could.</summary>
        internal string? Failure { get; }

        /// <summary>
        /// The reason one <c>step_needs_review</c> record journals (SPEC
        /// §E1): the step the record belongs to and the diagnostic code and
        /// message it carries — the same <c>{error, message}</c> payload
        /// every writer of the record emits.
        /// </summary>
        internal sealed class ReviewJournal
        {
            internal ReviewJournal(int? step, string? error, string? message)
            {
                Step = step;
                Error = error;
                Message = message;
            }

            /// <summary>The envelope's step index, or null when the record carries none.</summary>
            internal int? Step { get; }

            /// <summary>The payload's <c>error</c> code, or null.</summary>
            internal string? Error { get; }

            /// <summary>The payload's <c>message</c>, or null.</summary>
            internal string? Message { get; }
        }

        /// <summary>
        /// The per-step timing of one Plan step: the first
        /// <c>step_started</c> timestamp of each attempt (keyed by the §F2
        /// attempt number), the latest attempt that ever started and the
        /// timestamp of the step's latest record — the bounds the step row's
        /// elapsed time is measured between.
        /// </summary>
        internal sealed class StepJournal
        {
            private readonly Dictionary<int, string> _startedByAttempt =
                new Dictionary<int, string>();

            /// <summary>The first <c>step_started</c> timestamp of each attempt.</summary>
            internal IReadOnlyDictionary<int, string> StartedByAttempt => _startedByAttempt;

            /// <summary>The highest attempt a <c>step_started</c> opened, or null.</summary>
            internal int? LatestStartedAttempt { get; private set; }

            /// <summary>The timestamp of the step's latest journal record, or null.</summary>
            internal string? LastTs { get; private set; }

            /// <summary>Folds one journal record of this step into the timing.</summary>
            internal void Observe(JournalRecord record)
            {
                LastTs = record.Ts;
                if (record.Type != JournalRecordType.StepStarted)
                {
                    return;
                }
                var attempt = record.Attempt ?? 1;
                if (!_startedByAttempt.ContainsKey(attempt))
                {
                    _startedByAttempt.Add(attempt, record.Ts);
                }
                LatestStartedAttempt = attempt;
            }
        }

        /// <summary>
        /// Reads the journal at <paramref name="journalPath"/>. Never throws:
        /// an unreadable file or record is reported through
        /// <see cref="Failure"/>, because this runs on the display path —
        /// the lists returned with a set <see cref="Failure"/> are provably
        /// partial and the window marks them so.
        /// </summary>
        internal static RunJournalScan Read(string journalPath)
        {
            var steps = new Dictionary<int, StepJournal>();
            var logs = new List<RunViewerLogRow>();
            if (!TryReadLines(journalPath, out var lines, out var failure))
            {
                return new RunJournalScan(steps, logs, null, failure);
            }
            if (lines.Count == 0)
            {
                return new RunJournalScan(
                    steps, logs, null,
                    "The Run journal is empty, so it carries no readable header.");
            }
            // Line 1 is the header (SPEC §F2): an unknown or unreadable one
            // means the records that follow are never interpreted.
            var header = JournalCodec.ReadHeader(lines[0]);
            if (header.Status != JournalLineStatus.Decoded)
            {
                return new RunJournalScan(
                    steps, logs, null,
                    header.Diagnostic?.Message ??
                        "The Run journal header could not be read.");
            }

            ReviewJournal? review = null;
            var expectedSeq = 1L;
            for (var i = 1; i < lines.Count; i++)
            {
                var last = i == lines.Count - 1;
                var read = JournalCodec.ReadRecord(lines[i], expectedSeq);
                // Every envelope spends its sequence position, a skipped or
                // refused one included (SPEC §F2) — the same accounting the
                // Run Store's fold does.
                if (read.Status != JournalLineStatus.Corrupt)
                {
                    expectedSeq++;
                }
                if (read.Status == JournalLineStatus.Decoded)
                {
                    Observe(steps, logs, read.Record!);
                    if (read.Record!.Type == JournalRecordType.StepNeedsReview)
                    {
                        review = ReviewOf(read.Record!);
                    }
                }
                else if (read.Status == JournalLineStatus.Skipped)
                {
                    continue;
                }
                else if (!(read.Status == JournalLineStatus.Corrupt && last &&
                           JournalCodec.IsBrokenLine(lines[i])))
                {
                    // The droppable tail of SPEC §F3 item 3 is the only line
                    // this reader may pass over.
                    return new RunJournalScan(
                        steps, logs, review,
                        read.Diagnostic?.Message ??
                            "A Run journal record could not be read.");
                }
            }
            return new RunJournalScan(steps, logs, review, null);
        }

        // One decoded record's contribution: a step record feeds the step's
        // timing (any record carrying the §F2 step index belongs to that
        // step's timeline), a log record a row — with the RunLogEntry shape
        // of SPEC §F6: level/code/message from the payload, ts and step from
        // the envelope.
        private static void Observe(
            Dictionary<int, StepJournal> steps,
            List<RunViewerLogRow> logs,
            JournalRecord record)
        {
            if (record.Step is { } index)
            {
                if (!steps.TryGetValue(index, out var step))
                {
                    step = new StepJournal();
                    steps.Add(index, step);
                }
                step.Observe(record);
            }
            if (record.TryReadLog(out var level, out var code, out var message))
            {
                logs.Add(new RunViewerLogRow(
                    record.Ts, record.Step, level, code, message));
            }
        }

        // The reason a step_needs_review record carries: the {error,
        // message} payload every writer emits and the envelope's step
        // index — read raw, never guessed: a missing member stays null.
        private static ReviewJournal ReviewOf(JournalRecord record)
        {
            string? error = null;
            string? message = null;
            if (record.Data.ToToken() is JObject data)
            {
                if (data["error"] is JValue { Value: string errorValue })
                {
                    error = errorValue;
                }
                if (data["message"] is JValue { Value: string messageValue })
                {
                    message = messageValue;
                }
            }
            return new ReviewJournal(record.Step, error, message);
        }

        private static bool TryReadLines(
            string path, out List<string> lines, out string? failure)
        {
            lines = new List<string>();
            failure = null;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e) when (
                e is IOException || e is UnauthorizedAccessException ||
                e is System.Security.SecurityException || e is NotSupportedException)
            {
                failure = "The Run journal could not be read.";
                return false;
            }
            try
            {
                foreach (var line in Utf8.GetString(bytes).Split('\n'))
                {
                    if (line.Length != 0)
                    {
                        lines.Add(line);
                    }
                }
                return true;
            }
            catch (DecoderFallbackException)
            {
                lines.Clear();
                failure = "The Run journal is not valid UTF-8.";
                return false;
            }
        }
    }
}
