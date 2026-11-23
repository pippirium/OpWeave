#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The reader classification of a journal line (SPEC §F0, §F2). This is the
    /// codec-level signal; the Run-level consequence (fold, drop of a broken
    /// last line, NeedsReview) is decided by the loading layer.
    /// </summary>
    public enum JournalLineStatus
    {
        /// <summary>The line decoded into a header or record.</summary>
        Decoded,

        /// <summary>
        /// An envelope line whose <c>type</c> is unknown and whose
        /// <c>required</c> is false — the only kind a reader may skip
        /// (SPEC §F2 item 2). Its <c>seq</c> still occupies the sequence.
        /// </summary>
        Skipped,

        /// <summary>
        /// A structurally valid line the reader must not interpret: an unknown
        /// <c>type</c> with <c>required":true</c>, or a header with an
        /// unknown/missing/mistyped <c>formatVersion</c> or
        /// <c>canonicalJsonVersion</c>. Never skippable — not even as the last
        /// line (SPEC §F2 items 2 and 4).
        /// </summary>
        NeedsReview,

        /// <summary>
        /// A line that violates the journal format itself: unparseable or
        /// non-object JSON, an envelope member outside the SPEC §F2 set, a
        /// missing or mistyped member, a <c>required</c> that is absent or not
        /// a boolean, a <c>seq</c> gap or duplicate, a <c>crc</c> mismatch or a
        /// known type carrying a <c>required</c> different from the SPEC §F2
        /// table. Only the loading layer may drop such a line, and only when
        /// it is the journal's last one (SPEC §F3 item 3).
        /// </summary>
        Corrupt,
    }
}
