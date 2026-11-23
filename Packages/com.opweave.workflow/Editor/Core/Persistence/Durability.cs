#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The journal write durability class of SPEC §F1 and the per-record-type
    /// column of §F2. <see cref="Flushed"/> means the record reaches disk
    /// (<c>Flush(flushToDisk: true)</c>) before the write returns;
    /// <see cref="Buffered"/> records are written with the next Flushed record
    /// or the explicit flush hook (SPEC §F3 item 1).
    /// </summary>
    public enum Durability
    {
        Buffered,
        Flushed,
    }
}
