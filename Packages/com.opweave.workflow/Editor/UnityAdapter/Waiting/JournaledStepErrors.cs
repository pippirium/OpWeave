#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Persistence;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The restart-durable read-back of one step's SPEC §F6 Unity-Console
    /// error entries from its Run's journal — where the §G4 wait's error
    /// channel gets the import errors a real Editor restart makes
    /// unreachable any other way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SPEC §F6 journals every Console Warning-and-above produced while a
    /// step runs as that step's <c>log</c> record — <c>data.source</c> is
    /// <c>"unity_console"</c> and <c>level</c> is the mapped severity — so
    /// the same Console error line <see cref="ImportErrorCapture"/> keeps
    /// in SessionState is also written to the journal, and the journal is
    /// the one channel that survives a full Editor restart (SessionState
    /// dies with the session; a domain reload is only the lesser case).
    /// Reading the journal back — never an in-memory hand-off — is what
    /// lets an import error logged after the last persisted resume token
    /// still reach the step's OW-UNITY-008 under SPEC §F4 recovery.
    /// </para>
    /// <para>
    /// The read follows <see cref="JournaledChanges"/>'s discipline: the
    /// journal is decoded line-by-line with <see cref="JournalCodec"/>
    /// over strictly decoded UTF-8, every readable envelope spends its
    /// sequence position, and only the SPEC §F3 item 3 droppable tail is
    /// passed over. A line that cannot be read back ends the read:
    /// recorded state that fails interpretation already marks the Run
    /// NeedsReview on load, so the wait never resumes to judge a partial
    /// answer — but what the journal already yielded stays, because a
    /// Buffered tail can trail a step the journal otherwise finished.
    /// </para>
    /// </remarks>
    internal static class JournaledStepErrors
    {
        private const string LevelMember = "level";
        private const string MessageMember = "message";
        private const string DataMember = "data";
        private const string SourceMember = "source";

        private const string ErrorLevel = "error";

        // The journal is written as UTF-8 without a BOM and invalid bytes
        // are never tolerated (SPEC §F2); the same strictness here means a
        // damaged file contributes nothing instead of a mangled message.
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private static readonly IReadOnlyList<string> ConsoleSources =
            new[] { ConsoleLogCapture.ConsoleSource };

        /// <summary>
        /// The <c>message</c> of every <c>log</c> record the journal at
        /// <paramref name="journalPath"/> attributes to step
        /// <paramref name="stepIndex"/> through the §F6
        /// <c>"unity_console"</c> channel at error level — the Console
        /// Error, Assert and Exception entries the step produced, in
        /// journal order. Never throws: a missing or unreadable journal
        /// contributes nothing, because the wait path is not where a
        /// store failure is judged.
        /// </summary>
        internal static List<string> Read(string? journalPath, int stepIndex) =>
            Read(journalPath, stepIndex, ConsoleSources);

        /// <summary>
        /// The <c>message</c> of every <c>log</c> record the journal at
        /// <paramref name="journalPath"/> attributes to step
        /// <paramref name="stepIndex"/> through one of
        /// <paramref name="sources"/>' channels at error level, in journal
        /// order — the §F6 Console channel for the import wait, the
        /// <c>unity_compile</c> channel (and the Console's copy of the
        /// same errors) for the compile wait.
        /// </summary>
        internal static List<string> Read(
            string? journalPath, int stepIndex, IReadOnlyList<string> sources)
        {
            var messages = new List<string>();
            if (string.IsNullOrEmpty(journalPath) ||
                !TryReadLines(journalPath!, out var lines))
            {
                return messages;
            }
            // Line 1 is the header (§F2 item 1): a journal whose header
            // cannot be read yields nothing — the records that follow an
            // unreadable header are never interpreted.
            if (lines.Count == 0 ||
                JournalCodec.ReadHeader(lines[0]).Status != JournalLineStatus.Decoded)
            {
                return messages;
            }

            var expectedSeq = 1L;
            for (var i = 1; i < lines.Count; i++)
            {
                var last = i == lines.Count - 1;
                var read = JournalCodec.ReadRecord(lines[i], expectedSeq);
                // Every readable envelope spends its sequence position —
                // a Skipped or refused one included (§F2) — the same
                // accounting the Run Store's fold applies.
                if (read.Status != JournalLineStatus.Corrupt)
                {
                    expectedSeq++;
                }
                if (read.Status == JournalLineStatus.Decoded)
                {
                    if (TryMessage(read.Record!, stepIndex, sources, out var message))
                    {
                        messages.Add(message!);
                    }
                }
                else if (read.Status == JournalLineStatus.Skipped ||
                         (read.Status == JournalLineStatus.Corrupt && last &&
                          JournalCodec.IsBrokenLine(lines[i])))
                {
                    // A Skipped unknown-optional record carries nothing
                    // for this reader; the droppable tail of SPEC §F3
                    // item 3 is the interrupted write whose record never
                    // committed, so what was read before it still is the
                    // whole durable truth.
                    continue;
                }
                else
                {
                    // A record that cannot be read back — NeedsReview or
                    // middle corruption — ends the read; the journal is
                    // then the Run Store's problem (its Load marks the
                    // Run NeedsReview), never the wait's.
                    break;
                }
            }
            return messages;
        }

        // The §F6 attribution of one record: a `log` record bound to the
        // step and written at error level through one of the caller's
        // channels — the journaled counterpart of the error lines the
        // §G4 wait's capture keeps in its mirror.
        private static bool TryMessage(
            JournalRecord record, int stepIndex, IReadOnlyList<string> sources,
            out string? message)
        {
            message = null;
            if (record.Type != JournalRecordType.Log || record.Step != stepIndex ||
                record.Data.ToToken() is not JObject data ||
                data[LevelMember] is not JValue { Value: ErrorLevel } ||
                data[DataMember] is not JObject channel ||
                channel[SourceMember] is not JValue { Value: string source } ||
                !sources.Contains(source) ||
                data[MessageMember] is not JValue { Value: string text })
            {
                return false;
            }
            message = text;
            return true;
        }

        // The strict byte-level line split (JournaledChanges' discipline):
        // a half-written last line reaches the codec as a line like any
        // other, while a file that is absent or unreadable is simply no
        // durable evidence.
        private static bool TryReadLines(string journalPath, out List<string> lines)
        {
            lines = new List<string>();
            byte[] bytes;
            try
            {
                if (!File.Exists(journalPath))
                {
                    return false;
                }
                bytes = File.ReadAllBytes(journalPath);
            }
            catch (Exception e) when (
                e is IOException || e is UnauthorizedAccessException ||
                e is System.Security.SecurityException || e is NotSupportedException)
            {
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
            }
            catch (DecoderFallbackException)
            {
                lines.Clear();
                return false;
            }
            return true;
        }
    }
}
