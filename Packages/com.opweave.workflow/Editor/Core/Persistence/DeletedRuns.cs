#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The deletion tombstone file of SPEC §0.6 SG-16 and §F8:
    /// <c>Library/OpWeave/deleted-runs.ndjson</c>, one Canonical-JSON
    /// record per permanently deleted Run — <c>{"runId", "deletedAt",
    /// "reason", "crc"}</c>, newline-terminated — persisting the
    /// deleted-vs-never-created distinction the <c>Runs/</c> tree itself
    /// can no longer express once the directory is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is append-only under the Run Store inter-process
    /// exclusion and survives every Run it names: tombstones are never
    /// subject to the §F8 retention bounds, and a tombstoned runId is
    /// never reused. A deletion commits its tombstone — the SPEC §F3
    /// item 2 atomic replace — before the Run directory or index entry
    /// may be removed, so an interruption mid-delete still proves the
    /// Run deleted and a recovery sweep finishes the physical removal.
    /// </para>
    /// <para>
    /// The commit protocol is what makes an interrupted append provable.
    /// The whole committed content — every earlier record plus the new
    /// one — is written to a same-directory temp file, fsync'd and
    /// published over <c>deleted-runs.ndjson</c> by the durable rename
    /// of <see cref="DurableRename"/>: a crash anywhere in that sequence
    /// leaves the previous committed file, the durably published new
    /// committed file or an orphaned temp file, never a torn committed
    /// file, so the bytes on disk are always exactly what one commit
    /// fsync'd and the residue of an interrupted append lives only in
    /// the temp file reads never open.
    /// Because the POSIX publish lands the rename in the directory
    /// before its fsync can complete, a committed-looking record is not
    /// by itself a durable one: a retried delete, the idempotent
    /// re-delete and the recovery sweep each re-establish durability at
    /// the point of reliance through <see cref="EnsureDurable"/> —
    /// re-publishing the observed committed content — before any
    /// removal; a failed flush or publish commits nothing the
    /// destructive half may rely on.
    /// </para>
    /// <para>
    /// Because committed content is produced only by the atomic replace,
    /// every byte of the file must prove it: every line is
    /// '\n'-terminated — the last byte included — and carries the strict
    /// record contract — a runId in the <c>"run_" + ULID</c> grammar, a
    /// UTC ISO-8601 millisecond <c>deletedAt</c> and a <c>reason</c> of
    /// <c>retention</c> or <c>manual</c> — plus its own integrity check:
    /// <c>crc</c> is the SPEC §0.6 SG-12 CRC-32/ISO-HDLC of the record's
    /// Canonical JSON without the <c>crc</c> member, 8 lowercase hex
    /// digits, the same checksum the §F2 journal lines carry. Content
    /// that cannot prove it was committed — unreadable, empty, an
    /// unterminated or undecodable tail, a malformed or mistyped record,
    /// a missing or mismatched <c>crc</c> — is the OW-STORE-002
    /// corruption SPEC §0.6 SG-16 maps to a safe stop: an unterminated
    /// tail could be a truncated committed record as plausibly as an
    /// incomplete append, so it is never dropped — the store fails
    /// closed rather than answer OW-RUN-010 for a Run it cannot prove
    /// was never deleted.
    /// </para>
    /// <para>
    /// Internal to Core: the tombstone store is owned by
    /// <see cref="FileRunStore"/> alone — the SPEC §0.6 SG-16 rule that
    /// no second copy may exist outside the Run Store.
    /// </para>
    /// </remarks>
    internal static class DeletedRuns
    {
        internal const string FileName = "deleted-runs.ndjson";
        internal const string ReasonRetention = "retention";
        internal const string ReasonManual = "manual";

        private const string RunIdMember = "runId";
        private const string DeletedAtMember = "deletedAt";
        private const string ReasonMember = "reason";
        private const string CrcMember = "crc";
        private const string DeletedAtFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        private const string TempSuffix = ".opweave-tmp";
        private const int MaxReplaceAttempts = 5;
        private const int ReplaceRetryDelayMilliseconds = 15;

        private const string UlidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const int UlidLength = 26;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        /// <summary>The deletion reasons SG-16 admits on a tombstone record.</summary>
        internal static bool IsKnownReason(string? reason) =>
            reason is ReasonRetention or ReasonManual;

        // The Canonical-JSON tombstone record of one deleted Run, without
        // the record terminator — one line of deleted-runs.ndjson. The
        // crc member is the SPEC §0.6 SG-12 checksum of the record
        // without it — the same integrity member the §F2 journal lines
        // carry — so damage that keeps a record schema-valid is still
        // provable corruption.
        internal static string Line(string runId, string deletedAt, string reason)
        {
            var record = new JObject
            {
                [RunIdMember] = runId,
                [DeletedAtMember] = deletedAt,
                [ReasonMember] = reason,
            };
            record[CrcMember] = LineCrc(record);
            return CanonicalJson.Serialize(record);
        }

        /// <summary>
        /// Reads every committed tombstone id of
        /// <paramref name="tombstonePath"/>: a missing file is the empty
        /// set, and every byte of a present file must be committed
        /// tombstone records — the atomic replace of
        /// <see cref="Append"/> produces nothing else, so an unreadable
        /// file, an empty file, an unterminated tail or any line outside
        /// the record contract (a truncated committed record included)
        /// is unprovable and OW-STORE-002 — the deleted-vs-never-created
        /// distinction fails closed, never silently empty.
        /// </summary>
        internal static Result<HashSet<string>> ReadIds(string tombstonePath)
        {
            byte[] raw;
            try
            {
                if (!File.Exists(tombstonePath))
                {
                    return Result<HashSet<string>>.Success(
                        new HashSet<string>(StringComparer.Ordinal));
                }
                raw = File.ReadAllBytes(tombstonePath);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Corruption("The deletion tombstone file could not be read.");
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            return IsCommittedContent(raw, ids)
                ? Result<HashSet<string>>.Success(ids)
                : Corruption("The deletion tombstone file is corrupt.");
        }

        /// <summary>
        /// The tombstone commit of SPEC §0.6 SG-16: the committed content
        /// plus the record of <paramref name="runId"/> is fsync'd to a
        /// same-directory temp file and published over
        /// <paramref name="tombstonePath"/> by the durable rename of
        /// <see cref="DurableRename"/> — the durable step that must
        /// complete before every destructive half of the delete. The
        /// caller holds the Run Store exclusion; existing content is
        /// revalidated first and the same unprovable content the read
        /// path rejects is OW-STORE-002 — never carried into the next
        /// commit, never destroyed. An interrupted append leaves only an
        /// orphan temp file (a later append truncates it); a write or
        /// flush that cannot persist is OW-STORE-001 and commits nothing
        /// — no committed-looking record is left for a retry or the
        /// sweep to rely on — and the caller must not delete anything.
        /// </summary>
        /// <param name="publishStep">
        /// The internal test seam for the durable publish: when set it
        /// replaces the <see cref="DurableRename.Move"/> call, so tests
        /// record the durable commit's ordering against the destructive
        /// half and inject a publish failure deterministically — a
        /// conforming seam performs the durable publish itself.
        /// Production callers pass null.
        /// </param>
        internal static Result<Unit> Append(
            string tombstonePath,
            string runId,
            string reason,
            Func<FileStream, FileStream>? streamWrapper,
            Action<string, string>? publishStep)
        {
            byte[] committed;
            bool exists;
            try
            {
                exists = File.Exists(tombstonePath);
                committed = exists
                    ? File.ReadAllBytes(tombstonePath)
                    : Array.Empty<byte>();
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return StoreFailure();
            }
            if (exists && !IsCommittedContent(committed, null))
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_002,
                    "The deletion tombstone file is corrupt."));
            }
            var line = s_utf8.GetBytes(
                Line(runId, UtcTimestamp.Now(), reason) + "\n");
            var bytes = new byte[committed.Length + line.Length];
            Buffer.BlockCopy(committed, 0, bytes, 0, committed.Length);
            Buffer.BlockCopy(line, 0, bytes, committed.Length, line.Length);
            return Publish(tombstonePath, bytes, streamWrapper, publishStep);
        }

        /// <summary>
        /// Re-establishes the durability of the CURRENT committed
        /// tombstone file — file content and directory entry — at the
        /// point a destructive step relies on it: the POSIX durable
        /// rename lands the new directory entry before the containing
        /// directory's fsync can complete, so a record that survived a
        /// failed publish attempt is visible as committed without its
        /// entry being durable, and commitment can never be assumed from
        /// the file merely being present and readable. The observed
        /// committed bytes are re-published through the same atomic
        /// commit as <see cref="Append"/> — identical bytes to the
        /// same-directory temp file, fsync'd, then durable-renamed over
        /// the committed path — which makes the current content durable
        /// on every platform. The caller holds the Run Store exclusion.
        /// Fails closed: a missing or unreadable file and any failed
        /// write, flush or publish is OW-STORE-001 — nothing may be
        /// removed — and content that cannot prove it was committed is
        /// the same OW-STORE-002 the read and append paths report.
        /// </summary>
        /// <param name="publishStep">
        /// The internal test seam for the durable publish — the same
        /// contract as <see cref="Append"/>'s. Production callers pass
        /// null.
        /// </param>
        internal static Result<Unit> EnsureDurable(
            string tombstonePath,
            Func<FileStream, FileStream>? streamWrapper,
            Action<string, string>? publishStep)
        {
            byte[] committed;
            try
            {
                if (!File.Exists(tombstonePath))
                {
                    return StoreFailure(
                        "The committed deletion tombstone file is missing.");
                }
                committed = File.ReadAllBytes(tombstonePath);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return StoreFailure(
                    "The committed deletion tombstone file could not be read.");
            }
            if (!IsCommittedContent(committed, null))
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_002,
                    "The deletion tombstone file is corrupt."));
            }
            return Publish(tombstonePath, committed, streamWrapper, publishStep);
        }

        // The durable tail of the tombstone commit, shared by Append
        // (new committed content) and EnsureDurable (the observed
        // committed content re-published): the bytes are written to the
        // same-directory temp file and fsync'd, then published over the
        // committed path by the durable rename — the directory entry
        // durable, not only the file contents (SPEC §F3 item 2 /
        // §0.6 SG-16). The publish is retried a bounded number of times
        // on the transient Win32 sharing failures an external reader
        // (indexer, virus scanner) causes — the same policy the Run
        // caches' atomic replace follows. A write, flush or publish
        // that cannot persist is OW-STORE-001 and commits nothing the
        // destructive half may rely on.
        private static Result<Unit> Publish(
            string tombstonePath,
            byte[] bytes,
            Func<FileStream, FileStream>? streamWrapper,
            Action<string, string>? publishStep)
        {
            var temp = tombstonePath + TempSuffix;
            try
            {
                using (var stream = OpenStream(temp, streamWrapper))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                TryDeleteTemp(temp);
                return StoreFailure();
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (publishStep is { } publish)
                    {
                        publish(temp, tombstonePath);
                    }
                    else
                    {
                        DurableRename.Move(temp, tombstonePath);
                    }
                    return Result<Unit>.Success(Unit.Value);
                }
                catch (Exception e) when (IsIoFailure(e))
                {
                    if (!IsTransientReplaceFailure(e) ||
                        attempt >= MaxReplaceAttempts)
                    {
                        TryDeleteTemp(temp);
                        return StoreFailure();
                    }
                    Thread.Sleep(ReplaceRetryDelayMilliseconds);
                }
            }
        }

        // The tombstone's own temp-file handle — FileShare.None keeps the
        // uncommitted bytes private until the atomic replace publishes
        // them; the optional test seam wraps the stream before the write
        // and fsync go through it (same shape as the journal seam).
        private static FileStream OpenStream(
            string tempPath, Func<FileStream, FileStream>? streamWrapper)
        {
            var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            try
            {
                return streamWrapper is { } wrap ? wrap(stream) : stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // The strict committed-content contract of the atomic protocol:
        // non-empty, every line '\n'-terminated — the last byte
        // included — and every line a valid tombstone record. Anything
        // else cannot prove it was committed: an unterminated tail is a
        // truncated committed record as plausibly as an incomplete
        // append, so the read never drops it — the file fails closed.
        private static bool IsCommittedContent(byte[] raw, HashSet<string>? ids)
        {
            if (raw.Length == 0 || raw[raw.Length - 1] != (byte)'\n')
            {
                return false;
            }
            var start = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] != (byte)'\n')
                {
                    continue;
                }
                if (!IsRecordLine(Decode(raw, start, i - start), out var runId))
                {
                    return false;
                }
                ids?.Add(runId!);
                start = i + 1;
            }
            return true;
        }

        // One committed tombstone line: a JSON object whose runId is a
        // "run_" + ULID identifier, deletedAt a UTC ISO-8601 millisecond
        // timestamp, reason one of the SG-16 values and crc matching the
        // record's own Canonical JSON — the SG-16 "CRC相当" damage a
        // schema-valid record alone would not expose. Extra members
        // carry no deleted-vs-never-created meaning and are ignored.
        private static bool IsRecordLine(string? line, out string? runId)
        {
            runId = null;
            if (line is null)
            {
                return false;
            }
            var parsed = CanonicalJson.Parse(line);
            if (!parsed.Ok || parsed.Value is not JObject obj)
            {
                return false;
            }
            var id = StringField(obj, RunIdMember);
            var deletedAt = StringField(obj, DeletedAtMember);
            var reason = StringField(obj, ReasonMember);
            var crc = StringField(obj, CrcMember);
            if (!IsRunId(id) || !IsDeletedAt(deletedAt) ||
                !IsKnownReason(reason) || crc is null ||
                !string.Equals(crc, LineCrc(obj), StringComparison.Ordinal))
            {
                return false;
            }
            runId = id;
            return true;
        }

        // The SPEC §0.6 SG-12 checksum: CRC-32/ISO-HDLC over the UTF-8
        // bytes of the record's Canonical JSON without the crc member,
        // 8 lowercase hex digits — the same convention as the §F2
        // journal lines.
        private static string LineCrc(JObject record)
        {
            var withoutCrc = (JObject)record.DeepClone();
            withoutCrc.Remove(CrcMember);
            return Crc32.ToLowerHex(
                Crc32.Compute(s_utf8.GetBytes(CanonicalJson.Serialize(withoutCrc))));
        }

        private static bool IsDeletedAt(string? value) =>
            value is not null && DateTimeOffset.TryParseExact(
                value, DeletedAtFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);

        private static string? Decode(byte[] raw, int start, int length)
        {
            if (length > 0 && raw[start + length - 1] == (byte)'\r')
            {
                length--;
            }
            try
            {
                return s_utf8.GetString(raw, start, length);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        private static string? StringField(JObject? obj, string name) =>
            obj?[name] is JValue value && value.Type == JTokenType.String &&
                value.Value is string text
                ? text
                : null;

        // The same "run_" + ULID grammar FileRunStore.RequireRunId
        // enforces — the tombstone's runId must be a real Run identifier.
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

        // The transient Win32 codes the replace step can surface while an
        // external reader briefly holds a file: ERROR_ACCESS_DENIED (5),
        // ERROR_SHARING_VIOLATION (32) and ERROR_UNABLE_TO_REMOVE_REPLACED
        // (1175) — the same set the Run caches' atomic replace retries.
        private static bool IsTransientReplaceFailure(Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException))
            {
                return false;
            }
            var code = e.HResult & 0xFFFF;
            return code == 5 || code == 32 || code == 1175;
        }

        private static void TryDeleteTemp(string temp)
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception e) when (
                e is IOException || e is UnauthorizedAccessException ||
                e is System.Security.SecurityException || e is NotSupportedException)
            {
            }
        }

        private static bool IsIoFailure(Exception e) =>
            e is IOException || e is UnauthorizedAccessException ||
            e is NotSupportedException || e is System.Security.SecurityException;

        private static Result<Unit> StoreFailure() => StoreFailure(
            "The deletion tombstone could not be persisted.");

        private static Result<Unit> StoreFailure(string message) =>
            Result<Unit>.Failure(ErrorCodes.Create(
                ErrorCodes.OW_STORE_001, message));

        private static Result<HashSet<string>> Corruption(string message) =>
            Result<HashSet<string>>.Failure(ErrorCodes.Create(
                ErrorCodes.OW_STORE_002, message));
    }
}
