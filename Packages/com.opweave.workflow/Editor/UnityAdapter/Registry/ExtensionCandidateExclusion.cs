#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// A candidate the <see cref="IExtensionExclusionPredicate"/> rejected
    /// before instantiation (SPEC §C3 rule 5). Intentional policy exclusions
    /// are not registration errors ("許可設定による意図的除外は登録エラーに
    /// しない"), so they are carried separately from the failure records —
    /// the Registry state reporting of T-W2.9-01 consumes this list.
    /// </summary>
    /// <param name="Candidate">The excluded candidate (metadata only; it was never constructed).</param>
    /// <param name="Reason">
    /// The fixed-English reason the predicate returned (e.g. which DiscoveryMode
    /// rule excluded it); null only for a predicate that declined to say.
    /// </param>
    internal sealed record ExtensionCandidateExclusion(
        ExtensionCandidate Candidate,
        string? Reason);
}
