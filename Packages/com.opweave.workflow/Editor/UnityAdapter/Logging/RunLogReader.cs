#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Persistence;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The read side of the SPEC §F6 Run log contract: reads the
    /// <c>log</c> journal records of one Run from the Run Store and returns
    /// them as <see cref="SanitizedRunLogEntry"/> — the already sanitized
    /// data the downstream Agent API (<c>get_run_logs</c>, T-W3.2-04) serves,
    /// available before that API exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read-only.</b> The whole read is file access on
    /// <c>&lt;root&gt;/Runs/&lt;runId&gt;/journal.ndjson</c> — the same
    /// journal the Run Store owns — touching no mutable Core state, so it is
    /// safe off the main thread (SPEC §0.4 / §H2 item 3; the read pattern of
    /// <see cref="JournaledChanges"/>). Nothing is written, folded or
    /// repaired: what the journal committed is what is returned.
    /// </para>
    /// <para>
    /// <b>The §F6 SG-08 filter.</b> A <c>"debug"</c>-level entry reaches the
    /// output only when <paramref name="includeDebug"/> carries the current
    /// <c>debugLogging</c> setting (SPEC §F6 — debug is recorded to the API
    /// response only while the setting permits). Records a disabled setting
    /// journaled earlier stay journaled — §F6 adds nothing retroactively —
    /// but this contract serves them only under the live policy.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> An unknown <paramref name="runId"/> is
    /// OW-RUN-010. A journal that cannot be read, an undecodable header, a
    /// mid-journal record failure or a <c>log</c> record whose payload does
    /// not carry the §F6 <c>{level, message}</c> members ends the read with
    /// OW-STORE-002 — the entries decoded so far are returned with the
    /// diagnostic, never silently skipped. The one exception is SPEC §F3
    /// item 3: a broken last line is an interrupted write whose record never
    /// committed, dropped with an OW-STORE-002 <em>warning</em>.
    /// </para>
    /// <para>
    /// <b>Already sanitized.</b> Every entry's <c>message</c>, <c>code</c>
    /// and <c>data</c> pass through <see cref="CredentialMask"/> on the way
    /// out — a defensive re-mask on top of the write-path masking inside
    /// <see cref="OpWeave.Core.Runtime.StepLogSink"/>, so a credential can
    /// never leave the Run Store through this contract whatever recorded it.
    /// Free-text secrets no key or URL shape reveals stay unidentifiable
    /// (SPEC §F6).
    /// </para>
    /// </remarks>
    public static class RunLogReader
    {
        private const string RunsDirectoryName = "Runs";
        private const string JournalFileName = "journal.ndjson";

        private const string UlidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const int UlidLength = 26;

        // The journal is UTF-8 without a BOM and invalid bytes are never
        // tolerated (SPEC §F2) — the same strictness JournaledChanges reads
        // with, so a damaged file reports as unreadable evidence.
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// The SPEC §F1 journal path of Run <paramref name="runId"/> —
        /// <c>&lt;root&gt;/Runs/&lt;runId&gt;/journal.ndjson</c>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="runStoreRoot"/> or <paramref name="runId"/> is null
        /// or empty.
        /// </exception>
        public static string JournalPathOf(string runStoreRoot, string runId)
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
                    "A Run log read must carry its Run id.", nameof(runId));
            }
            return Path.Combine(runStoreRoot, RunsDirectoryName, runId, JournalFileName);
        }

        /// <summary>
        /// Reads the journaled <c>log</c> records of Run
        /// <paramref name="runId"/> in journal order as
        /// <see cref="SanitizedRunLogEntry"/> values (see the class remarks
        /// for the filter, sanitization and fail-closed rules).
        /// </summary>
        /// <param name="includeDebug">
        /// Whether <c>"debug"</c>-level entries take part — the caller's
        /// current <c>debugLogging</c> setting (SPEC §F6 SG-08). Omitted or
        /// <see langword="false"/> drops them from the output exactly like
        /// the §F6 default.
        /// </param>
        /// <returns>
        /// The decoded entries — possibly with an OW-STORE-002 diagnostic
        /// when the read could not finish — or a failure when the Run does
        /// not exist (OW-RUN-010) or the journal cannot be interpreted
        /// (OW-STORE-002).
        /// </returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="runStoreRoot"/> is null or empty, or
        /// <paramref name="runId"/> is not a <c>"run_" + ULID</c> identifier
        /// (SPEC §0.4) — the strict grammar also keeps path separators and
        /// traversal out of the Run directory name.
        /// </exception>
        public static Result<IReadOnlyList<SanitizedRunLogEntry>> Read(
            string runStoreRoot, string runId, bool includeDebug = false)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.",
                    nameof(runStoreRoot));
            }
            RequireRunId(runId);
            var journal = JournalPathOf(runStoreRoot, runId);
            if (!File.Exists(journal))
            {
                return Result<IReadOnlyList<SanitizedRunLogEntry>>.Failure(
                    ErrorCodes.Create(
                        ErrorCodes.OW_RUN_010, "The Run does not exist."));
            }
            if (!TryReadLines(journal, out var lines, out var readFailure))
            {
                return Result<IReadOnlyList<SanitizedRunLogEntry>>.Failure(
                    ErrorCodes.Create(ErrorCodes.OW_STORE_002, readFailure!));
            }
            if (lines.Count == 0)
            {
                return Result<IReadOnlyList<SanitizedRunLogEntry>>.Failure(
                    ErrorCodes.Create(
                        ErrorCodes.OW_STORE_002,
                        "The Run journal is empty, so it carries no readable header."));
            }
            // Line 1 is the header (SPEC §F2 item 1): an unknown or
            // unreadable one means the records that follow are never
            // interpreted (SPEC §F2 item 4).
            var header = JournalCodec.ReadHeader(lines[0]);
            if (header.Status != JournalLineStatus.Decoded)
            {
                return Result<IReadOnlyList<SanitizedRunLogEntry>>.Failure(
                    ErrorCodes.Create(
                        ErrorCodes.OW_STORE_002,
                        header.Diagnostic?.Message ??
                            "The Run journal header could not be read."));
            }
            var entries = new List<SanitizedRunLogEntry>();
            var diagnostics = new List<Diagnostic>();
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
                    if (read.Record!.Type == JournalRecordType.Log &&
                        !TryProject(read.Record, includeDebug, entries))
                    {
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_STORE_002,
                            "A Run journal log record carries no readable " +
                                "level or message member."));
                        break;
                    }
                    continue;
                }
                if (read.Status == JournalLineStatus.Skipped)
                {
                    continue;
                }
                if (read.Status == JournalLineStatus.Corrupt && last &&
                    JournalCodec.IsBrokenLine(lines[i]))
                {
                    // The droppable tail of SPEC §F3 item 3: an interrupted
                    // write whose record never committed — dropped with an
                    // OW-STORE-002 warning, the decoded prefix standing as
                    // the whole durable truth.
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_STORE_002,
                        read.Diagnostic?.Message ??
                            "The last Run journal record could not be read.",
                        severity: Severity.Warning));
                    break;
                }
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_002,
                    read.Diagnostic?.Message ??
                        "A Run journal record could not be read."));
                break;
            }
            return diagnostics.Count == 0
                ? Result<IReadOnlyList<SanitizedRunLogEntry>>.Success(entries)
                : Result<IReadOnlyList<SanitizedRunLogEntry>>.Success(entries, diagnostics);
        }

        // One journaled log record → its sanitized output entry. The §F6
        // payload shape is {level, message, code?, data?}: a level or message
        // that is missing or not a JSON string is recorded state this reader
        // cannot interpret — it fails the projection rather than guess.
        // Returns false only for that unreadable-payload case; a "debug"
        // entry filtered out by the SG-08 policy simply produces nothing.
        private static bool TryProject(
            JournalRecord record, bool includeDebug,
            List<SanitizedRunLogEntry> entries)
        {
            var data = record.Data.ToToken() as JObject;
            if (data?["level"] is not JValue { Value: string level } ||
                data["message"] is not JValue { Value: string message })
            {
                return false;
            }
            if (string.Equals(level, "debug", StringComparison.Ordinal) &&
                !includeDebug)
            {
                return true;
            }
            entries.Add(new SanitizedRunLogEntry(
                record.Seq,
                record.Ts,
                level,
                record.Step,
                data["code"] is JValue { Value: string code }
                    ? CredentialMask.MaskText(code)
                    : null,
                CredentialMask.MaskText(message),
                data["data"] is { } payload
                    ? JsonValue.From(CredentialMask.MaskToken(payload))
                    : null));
            return true;
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

        // runId is "run_" + ULID (SPEC §0.4) — the FileRunStore grammar,
        // mirrored here so the directory the reader opens can never escape
        // the Runs layout.
        private static void RequireRunId(string runId)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            if (!IsRunId(runId))
            {
                throw new ArgumentException(
                    "runId must be a \"run_\" + ULID identifier.", nameof(runId));
            }
        }

        private static bool IsRunId(string? runId)
        {
            if (runId is null ||
                runId.Length != Identifiers.RunIdPrefix.Length + UlidLength ||
                !runId.StartsWith(Identifiers.RunIdPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            for (var i = Identifiers.RunIdPrefix.Length; i < runId.Length; i++)
            {
                if (UlidAlphabet.IndexOf(runId[i]) < 0)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
