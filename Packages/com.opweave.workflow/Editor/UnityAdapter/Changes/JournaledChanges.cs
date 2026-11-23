#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The durable half of a Run's SPEC §F7 change list: every element a step
    /// already recorded is journaled on that attempt's §F2 records —
    /// <c>step_succeeded</c> always, <c>step_waiting</c>,
    /// <c>step_failed</c> and <c>step_cancelled</c> whenever the attempt's
    /// cumulative list is non-empty (the 2026-11-08 attempt-durability
    /// decision). The Run's journal therefore holds the change list of
    /// everything it finished <em>and</em> the durable interim changes of an
    /// attempt a domain reload interrupted — the record
    /// <see cref="ChangeSetTracker"/> reads back when a reconstruction reopens
    /// the Run, and the only evidence that survives a domain reload (§F intro:
    /// the journal is the Run's truth, <c>changes.json</c> is its §F7
    /// projection).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why read at all.</b> The tracker owns its list in memory, so the host
    /// a resumed Run gets after a reload would otherwise finalize a list
    /// holding only the steps that ran after it, and §F7 asks that Run for its
    /// whole list at Run end. Reading the journal — never an in-memory
    /// hand-off — is also what makes the restored elements the same elements
    /// the journal already promised the review, with the same
    /// <c>step</c>/<c>nodeId</c>/<c>attempt</c> attribution.
    /// </para>
    /// <para>
    /// <b>The union over attempts.</b> Each attempt's LAST journaled
    /// <c>changes</c> is its durable list — a later record of the same attempt
    /// replaces the earlier one wholesale, so nothing is double-counted and an
    /// interrupted attempt contributes its last durable interim list. An
    /// attempt's identical elements merge once; identical elements of
    /// different attempts stay separate, exactly as §F7 prescribes for
    /// <c>changes.json</c>. A record without the optional member (a
    /// pre-decision journal or an empty list) reads as the attempt's empty
    /// list; a <c>step_succeeded</c> without a readable <c>changes</c> array
    /// fails the read, because the writer always emits that member.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> An element this reader cannot interpret is never
    /// skipped silently: the reader reports the failure instead, and the
    /// tracker then refuses to write a list it knows is partial (SPEC §F1 —
    /// the consumer gets the store diagnostic rather than a change list a
    /// manual rollback would trust and act on). Only the SPEC §F3 item 3
    /// droppable tail ends the read without a failure, because a last line
    /// that cannot be read back at all is an interrupted write whose record
    /// never committed.
    /// </para>
    /// </remarks>
    internal static class JournaledChanges
    {
        private const string RunsDirectoryName = "Runs";
        private const string JournalFileName = "journal.ndjson";

        private const string ChangesMember = "changes";

        // The journal is written as UTF-8 without a BOM and with invalid bytes
        // never tolerated (SPEC §F2); the same strictness here means a damaged
        // file reads as unreadable evidence, not as evidence with a mangled
        // member.
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// The <c>journal.ndjson</c> path of Run <paramref name="runId"/> under
        /// the SPEC §F1 layout — the sibling of
        /// <see cref="ChangeSetFile.PathOf"/>'s <c>changes.json</c>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="runStoreRoot"/> or <paramref name="runId"/> is null
        /// or empty.
        /// </exception>
        internal static string PathOf(string runStoreRoot, string runId)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.",
                    nameof(runStoreRoot));
            }
            if (string.IsNullOrEmpty(runId))
            {
                throw new ArgumentException(
                    "A Run change list must carry its Run id.", nameof(runId));
            }
            return Path.Combine(runStoreRoot, RunsDirectoryName, runId, JournalFileName);
        }

        /// <summary>
        /// The union over attempts of each attempt's latest durable change
        /// list in <paramref name="path"/>, in journal order (SPEC §F7).
        /// </summary>
        /// <param name="path">The Run's journal path.</param>
        /// <param name="failure">
        /// Null when the journal was read whole; otherwise why it could not
        /// be, in which case the returned list is incomplete and must never be
        /// finalized as the Run's change list.
        /// </param>
        /// <returns>
        /// The journaled elements — empty for a Run whose steps recorded
        /// nothing. Never throws: an unreadable journal is reported through
        /// <paramref name="failure"/>, because this runs on the step path.
        /// </returns>
        internal static List<ChangeRecord> Read(string path, out string? failure)
        {
            var records = new List<ChangeRecord>();
            if (!TryReadLines(path, out var lines, out failure))
            {
                return records;
            }
            if (lines.Count == 0)
            {
                failure = "The Run journal is empty, so it carries no readable header.";
                return records;
            }
            // Line 1 is the header (§F2 item 1): an unknown or unreadable one
            // means the records that follow are never interpreted.
            var header = JournalCodec.ReadHeader(lines[0]);
            if (header.Status != JournalLineStatus.Decoded)
            {
                failure = header.Diagnostic?.Message ??
                    "The Run journal header could not be read.";
                return records;
            }

            // Each attempt's latest durable list, keyed by the (step, attempt)
            // pair SPEC §F2 names an attempt with and emitted in the order the
            // attempts first journal one of their change-carrying records —
            // journal order, since the runner is sequential. A later record of
            // the same attempt replaces the list wholesale (§F2 item 2).
            var order = new List<AttemptKey>();
            var latestByAttempt = new Dictionary<AttemptKey, List<ChangeRecord>>();
            var expectedSeq = 1L;
            for (var i = 1; i < lines.Count; i++)
            {
                var last = i == lines.Count - 1;
                var read = JournalCodec.ReadRecord(lines[i], expectedSeq);
                // Every envelope spends its sequence position, a skipped or
                // refused one included (§F2) — the same accounting the Run
                // Store's fold does, so a seq gap here is the gap the store
                // would report too.
                if (read.Status != JournalLineStatus.Corrupt)
                {
                    expectedSeq++;
                }
                if (read.Status == JournalLineStatus.Decoded)
                {
                    if (!TryReadChanges(
                            read.Record!, order, latestByAttempt, out failure))
                    {
                        return records;
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
                    // this reader may pass over: its record never committed,
                    // so what was read before it still is the whole durable
                    // truth.
                    failure = read.Diagnostic?.Message ??
                        "A Run journal record could not be read.";
                    return records;
                }
            }
            for (var i = 0; i < order.Count; i++)
            {
                var list = latestByAttempt[order[i]];
                // §F7: identical elements of one attempt merge once. The same
                // elements of different attempts keep their own entries —
                // the key keeps them apart all the way into changes.json.
                var seen = new HashSet<ChangeRecord>();
                for (var j = 0; j < list.Count; j++)
                {
                    if (seen.Add(list[j]))
                    {
                        records.Add(list[j]);
                    }
                }
            }
            failure = null;
            return records;
        }

        // The (step, attempt) pair of one attempt — the §F2 attempt identity.
        private readonly struct AttemptKey : IEquatable<AttemptKey>
        {
            private readonly int _step;
            private readonly int _attempt;

            internal AttemptKey(int step, int attempt)
            {
                _step = step;
                _attempt = attempt;
            }

            internal int Attempt => _attempt;

            public bool Equals(AttemptKey other) =>
                _step == other._step && _attempt == other._attempt;

            public override bool Equals(object? obj) =>
                obj is AttemptKey other && Equals(other);

            public override int GetHashCode() =>
                (_step * 397) ^ _attempt;
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

        // One record's contribution to its attempt's durable list. The four
        // change-carrying record types of SPEC §F2 take part; everything else
        // leaves the lists untouched. A record without the optional member
        // reads as the attempt's empty list (pre-decision journals and empty
        // lists) — except step_succeeded, whose member the writer always
        // emits, so a missing or unreadable one is an unreadable record
        // rather than "a step that changed nothing".
        private static bool TryReadChanges(
            JournalRecord record,
            List<AttemptKey> order,
            Dictionary<AttemptKey, List<ChangeRecord>> latestByAttempt,
            out string? failure)
        {
            failure = null;
            var carriesChanges = record.Type == JournalRecordType.StepWaiting ||
                record.Type == JournalRecordType.StepSucceeded ||
                record.Type == JournalRecordType.StepFailed ||
                record.Type == JournalRecordType.StepCancelled;
            if (!carriesChanges)
            {
                return true;
            }
            var source = JournalRecordTypes.Of(record.Type).WireName +
                " journal record";
            // The envelope names the attempt the record belongs to; a missing
            // one reads as the first attempt, the same default the element
            // reader applies to a pre-decision element without its own
            // `attempt` member.
            var key = new AttemptKey(record.Step ?? -1, record.Attempt ?? 1);
            var list = new List<ChangeRecord>();
            var member = (record.Data.ToToken() as JObject)?[ChangesMember];
            if (member is null || member.Type == JTokenType.Null)
            {
                // No durable interim changes — the pre-decision journal and
                // the empty-list form of the optional member. A
                // step_succeeded without a readable member is unreadable
                // instead: the writer always emits it, even empty, so its
                // absence drops that step's whole list.
                if (record.Type == JournalRecordType.StepSucceeded)
                {
                    failure = "A step_succeeded journal record carries no " +
                        "readable changes member.";
                    return false;
                }
            }
            else if (member is JArray changes)
            {
                foreach (var element in changes)
                {
                    if (!ChangeRecordJson.TryReadElement(
                            element, key.Attempt, source,
                            out var change, out failure))
                    {
                        return false;
                    }
                    list.Add(change!);
                }
            }
            else
            {
                // A changes member of another type is recorded state that
                // cannot be interpreted.
                failure = $"A {source} carries a changes member that is not an array.";
                return false;
            }
            if (!latestByAttempt.ContainsKey(key))
            {
                order.Add(key);
            }
            latestByAttempt[key] = list;
            return true;
        }
    }
}
