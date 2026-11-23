#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// The policy abstraction the D6 permission check injects (SPEC §D6 item 1):
    /// the configured allow / ask / deny level for each §C4 side effect of one
    /// actor. Whitelist and registration decisions are deliberately absent —
    /// they are a separate stage and never remove an <c>ask</c> (SPEC §C0.2, §X.9).
    /// </summary>
    /// <remarks>
    /// <see cref="LevelFor"/> returns the configured level only. The mandatory
    /// floors — the §H4 stage-3 <c>code.write</c> lower bound of ask and the
    /// §H4 item 2 <c>destructiveRequiresAsk</c> floor — are applied by
    /// <see cref="PermissionEvaluator"/>, not by individual implementations.
    /// </remarks>
    public interface IPermissionPolicy
    {
        /// <summary>The actor this policy decides for.</summary>
        PolicyActor Actor { get; }

        /// <summary>
        /// The configured level for <paramref name="effect"/>. Effects outside the
        /// §C4 vocabulary have no configurable level and must resolve to
        /// <see cref="PermissionLevel.Deny"/>; <see cref="PermissionEvaluator"/>
        /// clamps them to Deny regardless of what an implementation returns.
        /// </summary>
        PermissionLevel LevelFor(string effect);

        /// <summary>
        /// <c>destructiveRequiresAsk</c> (SPEC §H4 item 2, default true): a destructive
        /// Operation whose Undo support is not Full is at least <see cref="PermissionLevel.Ask"/>.
        /// </summary>
        bool DestructiveRequiresAsk { get; }
    }
}
