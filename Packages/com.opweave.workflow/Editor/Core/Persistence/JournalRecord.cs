#nullable enable

using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// One decoded journal record of SPEC §F2 — the semantic content of an
    /// envelope line
    /// <c>{"seq","ts","session","type","required","step"?,"frame"?,"iteration"?,"attempt"?,"data","crc"}</c>.
    /// The wire-only <c>crc</c> checksum is computed and verified by
    /// <see cref="JournalCodec"/> and is not part of the record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Required"/> and <see cref="Durability"/> are the SPEC §F2
    /// table values of <see cref="Type"/>: a writer must emit the table
    /// <c>required</c> and honor the table durability, so they are derived here
    /// rather than stored independently.
    /// </para>
    /// <para>
    /// <see cref="Frame"/> and <see cref="Iteration"/> are the SPEC §N.1
    /// reservations for future control-flow records: v1 writers never emit
    /// them (they are always 0), and <see cref="JournalCodec.WriteRecord"/>
    /// rejects a non-zero value. The reserved <c>sourcePath</c> member of SPEC
    /// §N.5.5 (5) has no v1 wire presence at all — a line carrying it is not a
    /// v1 envelope and fails decoding.
    /// </para>
    /// </remarks>
    public sealed record JournalRecord(
        long Seq,
        string Ts,
        string Session,
        JournalRecordType Type,
        int? Step,
        int? Attempt,
        JsonValue Data,
        int Frame = 0,
        int Iteration = 0)
    {
        /// <summary>The SPEC §F2 <c>required</c> value of <see cref="Type"/>.</summary>
        public bool Required => JournalRecordTypes.Of(Type).Required;

        /// <summary>The SPEC §F2 durability class of <see cref="Type"/>.</summary>
        public Durability Durability => JournalRecordTypes.Of(Type).Durability;

        /// <summary>
        /// The SPEC §F6 <c>{level, code?, message}</c> payload of a
        /// <see cref="JournalRecordType.Log"/> record as raw wire strings —
        /// <paramref name="level"/> keeps the wire word so a <c>debug</c>
        /// entry the §0.6 Severity set has no member for still reads. False
        /// when the record is not a <c>log</c> record or its payload lacks
        /// the string <c>level</c>/<c>message</c> members; the caller then
        /// has no readable entry rather than a partial one.
        /// </summary>
        internal bool TryReadLog(out string level, out string? code, out string message)
        {
            level = string.Empty;
            code = null;
            message = string.Empty;
            if (Type != JournalRecordType.Log ||
                Data.ToToken() is not JObject data ||
                data["level"] is not JValue { Value: string levelValue } ||
                data["message"] is not JValue { Value: string messageValue })
            {
                return false;
            }
            level = levelValue;
            code = data["code"] is JValue { Value: string codeValue }
                ? codeValue
                : null;
            message = messageValue;
            return true;
        }
    }
}
