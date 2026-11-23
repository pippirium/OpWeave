#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The result of one <see cref="RunRetention"/> pass (SPEC §F8):
    /// <see cref="Candidates"/> is the selected deletion set — the oldest
    /// finished Runs past either limit, in oldest-first deletion order —
    /// and <see cref="Deleted"/> is the subset the delegated tombstone
    /// writer actually confirmed deleted. While no provider is bound or
    /// one of its deletions is refused, <see cref="Deleted"/> stays
    /// shorter than <see cref="Candidates"/>: a selected Run is never
    /// reported as successful cleanup.
    /// </summary>
    public sealed record RunRetentionOutcome(
        IReadOnlyList<string> Candidates,
        IReadOnlyList<string> Deleted);
}
