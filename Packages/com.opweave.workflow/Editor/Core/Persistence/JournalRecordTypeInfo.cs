#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// One row of the SPEC §F2 record-type table: the wire name written in the
    /// envelope's <c>"type"</c> member, whether the record is
    /// <c>"required": true</c> (changes Run/Step state, approvals, results or
    /// Run resources — a reader that cannot interpret it must not skip it) and
    /// its write <see cref="Persistence.Durability"/> class.
    /// </summary>
    public sealed record JournalRecordTypeInfo(
        JournalRecordType Type,
        string WireName,
        bool Required,
        Durability Durability);
}
