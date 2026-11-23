#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// Line 1 of every journal: the <c>JournalHeader</c> of SPEC §F2, written
    /// once at Run creation —
    /// <c>{"formatVersion":1,"canonicalJsonVersion":1,"runId":...,"planHash":...}</c>
    /// in canonical member order. The header carries no envelope
    /// (<c>seq</c>/<c>crc</c>/...): its integrity is established by the version
    /// checks of SPEC §F2 item 4 and the planHash/plan.json comparison of the
    /// loading layer, not by a per-line checksum.
    /// </summary>
    /// <remarks>
    /// <see cref="FormatVersion"/> is the journal format version of SPEC §F2 and
    /// §N.1 — a reader that meets a header whose <c>formatVersion</c> or
    /// <c>canonicalJsonVersion</c> is unknown, missing or mistyped must not
    /// interpret the records and classifies the header as NeedsReview.
    /// <c>canonicalJsonVersion</c> is <see cref="CanonicalJson.Version"/>: the
    /// Canonical JSON version the journal's payloads are written in.
    /// </remarks>
    public sealed record JournalHeader(string RunId, string PlanHash)
    {
        /// <summary>The journal <c>formatVersion</c> this implementation reads and writes.</summary>
        public const int FormatVersion = 1;

        /// <summary>The <c>canonicalJsonVersion</c> this implementation writes.</summary>
        public const int CanonicalJsonVersion = CanonicalJson.Version;
    }
}
