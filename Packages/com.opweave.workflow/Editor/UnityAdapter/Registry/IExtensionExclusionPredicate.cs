#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The pre-instantiation exclusion seam of SPEC §C3 rule 5: consulted once
    /// per candidate <em>before</em> any extension constructor may run. The
    /// DiscoveryMode axis (§C0.2, §H4.1) plugs in here —
    /// <see cref="SettingsExtensionExclusion"/> carries the loaded
    /// <c>auto</c>/<c>whitelist</c> mode and the <c>disabledOperations</c>
    /// deny list; <see cref="StandardOnlyExtensionExclusion"/> covers the
    /// settings-error build.
    /// </summary>
    /// <remarks>
    /// The predicate sees only metadata: the candidate's
    /// <see cref="System.Type"/> is never passed through
    /// <see cref="System.Activator"/> by the caller — a returning predicate
    /// must not construct it either (providers are stateless/deterministic
    /// contracts, SPEC §C0.1). Exclusion is a policy decision, not a
    /// registration error: excluded candidates are reported separately
    /// (SPEC §C3 "許可設定による意図的除外は登録エラーにしない").
    /// </remarks>
    internal interface IExtensionExclusionPredicate
    {
        /// <summary>
        /// Returns a fixed-English exclusion reason when
        /// <paramref name="candidate"/> must not be instantiated, or null to
        /// admit it. Implementations must be deterministic — the same
        /// candidate must yield the same verdict within a build.
        /// </summary>
        string? Exclude(ExtensionCandidate candidate);
    }
}
