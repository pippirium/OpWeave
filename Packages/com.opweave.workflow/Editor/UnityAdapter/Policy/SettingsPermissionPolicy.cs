#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The real <see cref="IPermissionPolicy"/> the D6 check injects: it feeds the
    /// <c>policy.agent</c> / <c>policy.human</c> maps and
    /// <c>destructiveRequiresAsk</c> of a loaded <see cref="OpWeaveSettings"/>
    /// (SPEC §H4, §H4.1). The §H4 stage floors are not applied here — every
    /// <see cref="IPermissionPolicy"/> shares them through
    /// <see cref="PermissionEvaluator"/>. Whitelist membership is not consulted:
    /// whitelist rejection and side-effect approval are separate stages
    /// (SPEC §C0.2, §X.9), so an <c>ask</c> is never skipped.
    /// </summary>
    public sealed class SettingsPermissionPolicy : IPermissionPolicy
    {
        private readonly IReadOnlyDictionary<string, PermissionLevel> _levels;

        private SettingsPermissionPolicy(
            PolicyActor actor,
            IReadOnlyDictionary<string, PermissionLevel> levels,
            bool destructiveRequiresAsk)
        {
            Actor = actor;
            _levels = levels;
            DestructiveRequiresAsk = destructiveRequiresAsk;
        }

        /// <summary>
        /// Builds the policy for <paramref name="actor"/> from loaded settings.
        /// A failed settings load never reaches this type: the §H4.1
        /// settings-error state (standard Operations only, no new Runs) is handled
        /// before any Run is validated.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
        public static SettingsPermissionPolicy For(PolicyActor actor, OpWeaveSettings settings)
        {
            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            var configured = actor == PolicyActor.Agent ? settings.AgentPolicy : settings.HumanPolicy;
            var levels = new Dictionary<string, PermissionLevel>(StringComparer.Ordinal);
            foreach (var pair in configured)
            {
                levels[pair.Key] = ToPermissionLevel(pair.Value);
            }
            return new SettingsPermissionPolicy(
                actor,
                ReadOnlyCollections.CopyMap(levels),
                settings.DestructiveRequiresAsk);
        }

        /// <inheritdoc/>
        public PolicyActor Actor { get; }

        /// <inheritdoc/>
        public bool DestructiveRequiresAsk { get; }

        /// <inheritdoc/>
        public PermissionLevel LevelFor(string effect)
        {
            if (effect is null)
            {
                throw new ArgumentNullException(nameof(effect));
            }
            // Closed §C4 vocabulary: an effect with no configured level is denied.
            return _levels.TryGetValue(effect, out var level) ? level : PermissionLevel.Deny;
        }

        private static PermissionLevel ToPermissionLevel(PolicyLevel level) => level switch
        {
            PolicyLevel.Allow => PermissionLevel.Allow,
            PolicyLevel.Ask => PermissionLevel.Ask,
            PolicyLevel.Deny => PermissionLevel.Deny,
            _ => PermissionLevel.Deny,
        };
    }
}
