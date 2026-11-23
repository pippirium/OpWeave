#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// The outcome of a SPEC §B6 assignability question: whether an output of
    /// the source type may be connected to an input of the target type, plus a
    /// fixed-English <see cref="Reason"/> when it may not.
    /// </summary>
    /// <remarks>
    /// <see cref="Reason"/> is null when <see cref="Assignable"/> is true and is
    /// always set on a negative answer; it explains the failed rule (for
    /// example the "unresolved class" case of §B6 rule 4) for diagnostics such
    /// as OW-TYPE-002.
    /// </remarks>
    public sealed record AssignResult(bool Assignable, string? Reason);
}
