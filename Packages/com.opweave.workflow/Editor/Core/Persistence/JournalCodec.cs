#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The NDJSON codec of the append-only journal (SPEC §F2, §F0, §0.6 SG-12):
    /// line 1 is the <see cref="JournalHeader"/> and every following line is a
    /// <see cref="JournalRecord"/> envelope whose <c>crc</c> is the
    /// CRC-32/ISO-HDLC of the line's Canonical JSON bytes without the
    /// <c>crc</c> member, written as 8 lowercase hex digits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WriteHeader"/> and <see cref="WriteRecord"/> return the line
    /// text without a terminator; the store joins lines with <c>'\n'</c>. The
    /// writer emits the SPEC §F2 table <c>required</c> value of each record
    /// type and never writes the reserved <c>frame</c>/<c>iteration</c>
    /// members (v1 values are always 0) or <c>sourcePath</c> (SPEC §N.1,
    /// §N.5.5). <c>seq</c> is caller-assigned; the reader enforces the
    /// starts-at-1, steps-by-1 rule against <paramref name="expectedSeq"/> in
    /// <see cref="ReadRecord"/>. <see cref="IsBrokenLine"/> exposes the
    /// structural half of the read — the "broken" line of SPEC §F3 item 3 —
    /// for the loading layer's droppable-tail decision.
    /// </para>
    /// <para>
    /// The reader classifies instead of throwing (SPEC §F2 items 2-4): an
    /// unknown <c>type</c> with <c>required:false</c> is
    /// <see cref="JournalLineStatus.Skipped"/>, an unknown type with
    /// <c>required:true</c>, a checksum-valid record whose reserved
    /// <c>frame</c>/<c>iteration</c> is nonzero (SPEC §N.1: a value only a
    /// future format v1 cannot interpret can write), or a header whose
    /// versions are unknown/missing/mistyped is
    /// <see cref="JournalLineStatus.NeedsReview"/>,
    /// and every format violation — unparseable JSON, an unknown member, a
    /// missing or mistyped member, an absent or non-boolean
    /// <c>required</c>, a <c>seq</c> gap or duplicate, a <c>crc</c> mismatch, a
    /// known type whose <c>required</c> differs from the SPEC §F2 table — is
    /// <see cref="JournalLineStatus.Corrupt"/>. Which classification becomes
    /// the Run's OW-STORE-002/NeedsReview outcome, and whether a Corrupt last
    /// line may be dropped (SPEC §F3 item 3), belongs to the loading layer.
    /// Failure diagnostics follow the SG-02 internal convention (empty
    /// <see cref="Diagnostic.Code"/>, payload-free fixed English, JsonPath
    /// relative to the line root) for the owning layer to map exactly once.
    /// </para>
    /// </remarks>
    public static class JournalCodec
    {
        private const string FormatVersionMember = "formatVersion";
        private const string CanonicalJsonVersionMember = "canonicalJsonVersion";
        private const string RunIdMember = "runId";
        private const string PlanHashMember = "planHash";

        private const string SeqMember = "seq";
        private const string TsMember = "ts";
        private const string SessionMember = "session";
        private const string TypeMember = "type";
        private const string RequiredMember = "required";
        private const string StepMember = "step";
        private const string FrameMember = "frame";
        private const string IterationMember = "iteration";
        private const string AttemptMember = "attempt";
        private const string DataMember = "data";
        private const string CrcMember = "crc";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private static readonly HashSet<string> s_headerMembers = new HashSet<string>(
            StringComparer.Ordinal)
        {
            FormatVersionMember, CanonicalJsonVersionMember, RunIdMember, PlanHashMember,
        };

        private static readonly HashSet<string> s_recordMembers = new HashSet<string>(
            StringComparer.Ordinal)
        {
            SeqMember, TsMember, SessionMember, TypeMember, RequiredMember,
            StepMember, FrameMember, IterationMember, AttemptMember,
            DataMember, CrcMember,
        };

        /// <summary>
        /// Encodes the journal's line 1 (SPEC §F2): the four-member header
        /// object, Canonical JSON, no trailing newline.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="header"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <see cref="JournalHeader.RunId"/> or <see cref="JournalHeader.PlanHash"/>
        /// is null or empty.
        /// </exception>
        public static string WriteHeader(JournalHeader header)
        {
            if (header is null)
            {
                throw new ArgumentNullException(nameof(header));
            }
            if (string.IsNullOrEmpty(header.RunId))
            {
                throw new ArgumentException(
                    "JournalHeader.RunId must not be null or empty.", nameof(header));
            }
            if (string.IsNullOrEmpty(header.PlanHash))
            {
                throw new ArgumentException(
                    "JournalHeader.PlanHash must not be null or empty.", nameof(header));
            }
            var line = new JObject
            {
                [FormatVersionMember] = JournalHeader.FormatVersion,
                [CanonicalJsonVersionMember] = JournalHeader.CanonicalJsonVersion,
                [RunIdMember] = header.RunId,
                [PlanHashMember] = header.PlanHash,
            };
            return CanonicalJson.Serialize(line);
        }

        /// <summary>
        /// Encodes one journal envelope line (SPEC §F2): the record members in
        /// Canonical JSON order with <c>crc</c> appended, no trailing newline.
        /// The <c>required</c> member is the SPEC §F2 table value of
        /// <see cref="JournalRecord.Type"/>; the reserved
        /// <c>frame</c>/<c>iteration</c> members are never written (v1 values
        /// are always 0).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="record"/>, <see cref="JournalRecord.Ts"/>,
        /// <see cref="JournalRecord.Session"/> or <see cref="JournalRecord.Data"/>
        /// is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// The record is outside the v1 persisted contract: <see cref="JournalRecord.Seq"/>
        /// below 1, a negative <see cref="JournalRecord.Step"/>/
        /// <see cref="JournalRecord.Attempt"/>, a non-zero
        /// <see cref="JournalRecord.Frame"/>/<see cref="JournalRecord.Iteration"/>,
        /// an empty <see cref="JournalRecord.Ts"/>/
        /// <see cref="JournalRecord.Session"/>, a <see cref="JournalRecord.Data"/>
        /// that is not a JSON object, or a <see cref="JournalRecord.Type"/>
        /// outside the SPEC §F2 table.
        /// </exception>
        public static string WriteRecord(JournalRecord record)
        {
            if (record is null)
            {
                throw new ArgumentNullException(nameof(record));
            }
            if (record.Seq < 1)
            {
                throw new ArgumentException(
                    "JournalRecord.Seq must be at least 1.", nameof(record));
            }
            if (record.Step is < 0)
            {
                throw new ArgumentException(
                    "JournalRecord.Step must be a non-negative step index.", nameof(record));
            }
            if (record.Attempt is < 0)
            {
                throw new ArgumentException(
                    "JournalRecord.Attempt must be a non-negative attempt number.", nameof(record));
            }
            if (record.Frame != 0 || record.Iteration != 0)
            {
                throw new ArgumentException(
                    "JournalRecord.Frame/Iteration are reserved and must be 0 in v1.",
                    nameof(record));
            }
            if (string.IsNullOrEmpty(record.Ts))
            {
                throw new ArgumentException(
                    "JournalRecord.Ts must not be null or empty.", nameof(record));
            }
            if (string.IsNullOrEmpty(record.Session))
            {
                throw new ArgumentException(
                    "JournalRecord.Session must not be null or empty.", nameof(record));
            }
            var info = JournalRecordTypes.Of(record.Type);
            var data = DataToken(record);
            var line = new JObject
            {
                [SeqMember] = record.Seq,
                [TsMember] = record.Ts,
                [SessionMember] = record.Session,
                [TypeMember] = info.WireName,
                [RequiredMember] = info.Required,
            };
            if (record.Step is { } step)
            {
                line[StepMember] = step;
            }
            if (record.Attempt is { } attempt)
            {
                line[AttemptMember] = attempt;
            }
            line[DataMember] = data;
            line[CrcMember] = LineCrc(line);
            return CanonicalJson.Serialize(line);
        }

        /// <summary>
        /// Decodes journal line 1 (SPEC §F2 items 1 and 4). A structurally valid
        /// header whose <c>formatVersion</c>/<c>canonicalJsonVersion</c> is
        /// unknown, missing or mistyped — or whose member set is not the v1
        /// four — is <see cref="JournalLineStatus.NeedsReview"/>, never
        /// interpreted and never auto-migrated. Only a JSON text failure is
        /// <see cref="JournalLineStatus.Corrupt"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        public static JournalHeaderRead ReadHeader(string line)
        {
            if (line is null)
            {
                throw new ArgumentNullException(nameof(line));
            }
            var parsed = CanonicalJson.Parse(line);
            if (!parsed.Ok)
            {
                return CorruptHeader(parsed.Diagnostics[0]);
            }
            if (parsed.Value is not JObject header)
            {
                return NeedsReviewHeader(
                    "A journal header must be a JSON object.", "$");
            }
            foreach (var property in header.Properties())
            {
                if (!s_headerMembers.Contains(property.Name))
                {
                    return NeedsReviewHeader(
                        "A journal header has a member outside the v1 set.", "$");
                }
            }
            if (!IsVersion(header[FormatVersionMember], JournalHeader.FormatVersion) ||
                !IsVersion(header[CanonicalJsonVersionMember], JournalHeader.CanonicalJsonVersion))
            {
                return NeedsReviewHeader(
                    "A journal header version is unknown, missing or mistyped.", "$");
            }
            var runId = StringMember(header[RunIdMember]);
            if (runId is null || runId.Length == 0)
            {
                return NeedsReviewHeader(
                    "A journal header runId is missing, empty or not a JSON string.", "$.*");
            }
            var planHash = StringMember(header[PlanHashMember]);
            if (planHash is null || planHash.Length == 0)
            {
                return NeedsReviewHeader(
                    "A journal header planHash is missing, empty or not a JSON string.", "$.*");
            }
            return new JournalHeaderRead(
                JournalLineStatus.Decoded, new JournalHeader(runId, planHash), null);
        }

        /// <summary>
        /// Decodes one journal envelope line (SPEC §F2 items 1-3). The caller
        /// tracks the sequence: <paramref name="expectedSeq"/> is 1 for the
        /// first record line and grows by 1 for every following line —
        /// including <see cref="JournalLineStatus.Skipped"/> ones, which still
        /// occupy a sequence number.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        public static JournalRecordRead ReadRecord(string line, long expectedSeq)
        {
            if (line is null)
            {
                throw new ArgumentNullException(nameof(line));
            }
            var parsed = CanonicalJson.Parse(line);
            if (!parsed.Ok)
            {
                return CorruptRecord(parsed.Diagnostics[0]);
            }
            if (parsed.Value is not JObject envelope)
            {
                return CorruptRecord(
                    Error("A journal record line must be a JSON object.", "$"));
            }
            if (ReadEnvelope(envelope, out var envelopeFailure) is not { } parts)
            {
                return CorruptRecord(envelopeFailure!);
            }
            // ReadEnvelope leaves only positional and integrity decisions —
            // the expected seq, the crc value and the table's required flag —
            // for this point on a structurally complete envelope.
            if (parts.Seq != expectedSeq)
            {
                return CorruptRecord(
                    Error("A journal record seq has a gap or a duplicate.", "$.*"));
            }
            if (!string.Equals(parts.Crc, LineCrc(envelope), StringComparison.Ordinal))
            {
                return CorruptRecord(
                    Error("A journal record crc does not match its Canonical JSON bytes.", "$.*"));
            }
            if (parts.Frame.GetValueOrDefault() != 0 || parts.Iteration.GetValueOrDefault() != 0)
            {
                return new JournalRecordRead(
                    JournalLineStatus.NeedsReview, null,
                    Error("A journal record has a nonzero reserved frame or iteration.", "$.*"));
            }
            if (!JournalRecordTypes.TryParse(parts.TypeName, out var type))
            {
                return parts.Required
                    ? new JournalRecordRead(
                        JournalLineStatus.NeedsReview, null,
                        Error("A journal record has an unknown type with required true.", "$.*"))
                    : new JournalRecordRead(JournalLineStatus.Skipped, null, null);
            }
            var info = JournalRecordTypes.Of(type);
            if (parts.Required != info.Required)
            {
                return CorruptRecord(
                    Error("A journal record required flag differs from the SPEC F2 table.", "$.*"));
            }
            var record = new JournalRecord(
                parts.Seq, parts.Ts, parts.Session, type, parts.Step, parts.Attempt,
                JsonValue.From(parts.Data), parts.Frame ?? 0, parts.Iteration ?? 0);
            return new JournalRecordRead(JournalLineStatus.Decoded, record, null);
        }

        /// <summary>
        /// Whether a record line is "broken" in the SPEC §F3 item 3 sense —
        /// it cannot be read back as a v1 envelope at all: unparseable JSON,
        /// a non-object value or a damaged member. SPEC §F2 item 2 routes the
        /// missing or non-boolean <c>required</c> member — like any other
        /// damaged required envelope member — here as record corruption, so
        /// only such a line may be the interrupted-write tail the loading
        /// layer may drop. A complete envelope whose failure is positional
        /// or integrity — a seq gap or duplicate, a crc mismatch, a
        /// checksum-valid record whose <c>required</c> disagrees with the
        /// SPEC §F2 table — is a well-formed record, never a torn write.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        internal static bool IsBrokenLine(string line)
        {
            if (line is null)
            {
                throw new ArgumentNullException(nameof(line));
            }
            var parsed = CanonicalJson.Parse(line);
            if (!parsed.Ok || parsed.Value is not JObject envelope)
            {
                return true;
            }
            return ReadEnvelope(envelope, out _) is null;
        }

        // The structural half of a v1 record line (SPEC §F2), extracted by
        // ReadEnvelope: the known member set and every required member
        // present and correctly typed, before the positional and integrity
        // checks (expected seq, crc value, the table's required flag) that
        // ReadRecord applies next.
        private sealed class Envelope
        {
            internal long Seq;
            internal string Ts = "";
            internal string Session = "";
            internal string TypeName = "";
            internal bool Required;
            internal string Crc = "";
            internal JObject Data = new JObject();
            internal int? Step;
            internal int? Attempt;
            internal int? Frame;
            internal int? Iteration;
        }

        // The v1 envelope's structural validation, shared by ReadRecord and
        // IsBrokenLine in the original check order: a JObject that fails here
        // is the "broken" line of SPEC §F3 item 3 while one that passes
        // leaves only integrity failures — a seq gap or duplicate, a crc
        // mismatch, a table-required disagreement — which are never a torn
        // write. Returns the typed members on success, the Corrupt
        // diagnostic on failure.
        private static Envelope? ReadEnvelope(JObject envelope, out Diagnostic? failure)
        {
            foreach (var property in envelope.Properties())
            {
                if (!s_recordMembers.Contains(property.Name))
                {
                    failure = Error(
                        "A journal record has a member outside the v1 envelope.", "$");
                    return null;
                }
            }
            if (!IntegerMember(envelope[SeqMember], out var seq) || seq < 1)
            {
                failure = Error(
                    "A journal record seq is missing or not a positive integer.", "$.*");
                return null;
            }
            var ts = StringMember(envelope[TsMember]);
            if (ts is null || ts.Length == 0)
            {
                failure = Error(
                    "A journal record ts is missing, empty or not a JSON string.", "$.*");
                return null;
            }
            var session = StringMember(envelope[SessionMember]);
            if (session is null || session.Length == 0)
            {
                failure = Error(
                    "A journal record session is missing, empty or not a JSON string.", "$.*");
                return null;
            }
            var typeName = StringMember(envelope[TypeMember]);
            if (typeName is null)
            {
                failure = Error(
                    "A journal record type is missing or not a JSON string.", "$.*");
                return null;
            }
            if (envelope[RequiredMember] is not JValue requiredValue ||
                requiredValue.Type != JTokenType.Boolean)
            {
                failure = Error(
                    "A journal record required flag is missing or not a JSON boolean.", "$.*");
                return null;
            }
            var crc = StringMember(envelope[CrcMember]);
            if (crc is null)
            {
                failure = Error(
                    "A journal record crc is missing or not a JSON string.", "$.*");
                return null;
            }
            var data = envelope[DataMember];
            if (data is null || data.Type != JTokenType.Object)
            {
                failure = Error(
                    "A journal record data is missing or not a JSON object.", "$.*");
                return null;
            }
            if (!OptionalIndex(envelope[StepMember], out var step) ||
                !OptionalIndex(envelope[AttemptMember], out var attempt) ||
                !OptionalIndex(envelope[FrameMember], out var frame) ||
                !OptionalIndex(envelope[IterationMember], out var iteration))
            {
                failure = Error(
                    "A journal record index member is not a non-negative integer.", "$.*");
                return null;
            }
            failure = null;
            return new Envelope
            {
                Seq = seq,
                Ts = ts,
                Session = session,
                TypeName = typeName,
                Required = (bool)requiredValue.Value!,
                Crc = crc,
                Data = (JObject)data,
                Step = step,
                Attempt = attempt,
                Frame = frame,
                Iteration = iteration,
            };
        }

        private static JToken DataToken(JournalRecord record)
        {
            var data = (record.Data ?? throw new ArgumentNullException(nameof(record)))
                .ToToken();
            if (data.Type != JTokenType.Object)
            {
                throw new ArgumentException(
                    "JournalRecord.Data must be a JSON object.", nameof(record));
            }
            return data;
        }

        // The SPEC §0.6 SG-12 checksum: CRC-32/ISO-HDLC over the UTF-8 bytes of
        // the line's Canonical JSON without the crc member, 8 lowercase hex
        // digits. Reads a JObject that must not carry crc yet.
        private static string LineCrc(JObject line)
        {
            var withoutCrc = (JObject)line.DeepClone();
            withoutCrc.Remove(CrcMember);
            return Crc32.ToLowerHex(
                Crc32.Compute(s_utf8.GetBytes(CanonicalJson.Serialize(withoutCrc))));
        }

        // The member's token must be a JSON string; member names and values are
        // never echoed into diagnostics (SG-02 payload-free convention). An
        // empty string still counts as the member's string form — an empty
        // type name is an unknown type, an empty crc fails the checksum.
        private static string? StringMember(JToken? token) =>
            token is JValue value && value.Type == JTokenType.String &&
                value.Value is string text
                ? text
                : null;

        private static bool IsVersion(JToken? token, int expected) =>
            token is JValue value && value.Type == JTokenType.Integer &&
                value.Value is long number && number == expected;

        private static bool IntegerMember(JToken? token, out long integer)
        {
            if (token is JValue value && value.Type == JTokenType.Integer &&
                value.Value is long number)
            {
                integer = number;
                return true;
            }
            integer = 0;
            return false;
        }

        // An optional envelope index member: absent -> null, a non-negative
        // integer in Int32 range -> the value, anything else is a format
        // violation reported by the caller.
        private static bool OptionalIndex(JToken? token, out int? index)
        {
            index = null;
            if (token is null)
            {
                return true;
            }
            if (token is JValue value && value.Type == JTokenType.Integer &&
                value.Value is long number && number >= 0 && number <= int.MaxValue)
            {
                index = (int)number;
                return true;
            }
            return false;
        }

        private static JournalHeaderRead CorruptHeader(Diagnostic diagnostic) =>
            new JournalHeaderRead(JournalLineStatus.Corrupt, null, diagnostic);

        private static JournalHeaderRead NeedsReviewHeader(string message, string path) =>
            new JournalHeaderRead(JournalLineStatus.NeedsReview, null, Error(message, path));

        private static JournalRecordRead CorruptRecord(Diagnostic diagnostic) =>
            new JournalRecordRead(JournalLineStatus.Corrupt, null, diagnostic);

        // Internal diagnostic convention of the SG-02 reader (SPEC §0.6): empty
        // code, payload-free fixed English, JsonPath; the Run Store layer maps
        // to OW-STORE-002 exactly once before publishing.
        private static Diagnostic Error(string message, string path) =>
            new Diagnostic(string.Empty, Severity.Error, message, JsonPath: path);
    }
}
