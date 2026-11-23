#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The outcome of reading a journal's line 1 (SPEC §F2). <see cref="Header"/>
    /// is non-null exactly when <see cref="Status"/> is
    /// <see cref="JournalLineStatus.Decoded"/>; <see cref="Diagnostic"/> is
    /// non-null on <see cref="JournalLineStatus.NeedsReview"/> and
    /// <see cref="JournalLineStatus.Corrupt"/>.
    /// </summary>
    /// <remarks>
    /// The diagnostic follows the SG-02 internal convention (SPEC §0.6): an
    /// empty <see cref="Diagnostic.Code"/>, a payload-free fixed-English
    /// message and a JsonPath relative to the line's own root. The Run Store
    /// layer maps it to OW-STORE-002 exactly once.
    /// </remarks>
    public sealed record JournalHeaderRead(
        JournalLineStatus Status,
        JournalHeader? Header,
        Diagnostic? Diagnostic);
}
