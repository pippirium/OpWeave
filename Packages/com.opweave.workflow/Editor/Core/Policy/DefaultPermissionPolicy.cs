#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// The <see cref="IPermissionPolicy"/> holding the SPEC §C4 default table
    /// and the §H4 actor defaults: the policy a settings-free pipeline injects
    /// until the real settings source lands (T-W3.4-01 feeds
    /// <c>OpWeaveSettings</c> through <c>SettingsPermissionPolicy</c>). The
    /// §H4 floors — the <c>code.write</c> lower bound of ask and
    /// <c>destructiveRequiresAsk</c> — are applied by
    /// <see cref="PermissionEvaluator"/>, not duplicated here.
    /// </summary>
    public sealed class DefaultPermissionPolicy : IPermissionPolicy
    {
        // SPEC §C4 table: the Agent asks on asset.move, asset.delete,
        // code.write and package.modify and allows everything else.
        private static readonly IReadOnlyDictionary<string, PermissionLevel> s_agent =
            ReadOnlyCollections.CopyMap(new Dictionary<string, PermissionLevel>(
                StringComparer.Ordinal)
            {
                [EffectNames.Read] = PermissionLevel.Allow,
                [EffectNames.SceneLoad] = PermissionLevel.Allow,
                [EffectNames.SceneModify] = PermissionLevel.Allow,
                [EffectNames.SceneSave] = PermissionLevel.Allow,
                [EffectNames.AssetCreate] = PermissionLevel.Allow,
                [EffectNames.AssetModify] = PermissionLevel.Allow,
                [EffectNames.AssetMove] = PermissionLevel.Ask,
                [EffectNames.AssetDelete] = PermissionLevel.Ask,
                [EffectNames.CodeTemplate] = PermissionLevel.Allow,
                [EffectNames.CodeWrite] = PermissionLevel.Ask,
                [EffectNames.Compile] = PermissionLevel.Allow,
                [EffectNames.PackageModify] = PermissionLevel.Ask,
            });

        // SPEC §H4 stage 1 human column and the §H4.1 settings example: mostly
        // allow, with delete / code.write / package.modify at ask.
        private static readonly IReadOnlyDictionary<string, PermissionLevel> s_human =
            ReadOnlyCollections.CopyMap(new Dictionary<string, PermissionLevel>(
                StringComparer.Ordinal)
            {
                [EffectNames.Read] = PermissionLevel.Allow,
                [EffectNames.SceneLoad] = PermissionLevel.Allow,
                [EffectNames.SceneModify] = PermissionLevel.Allow,
                [EffectNames.SceneSave] = PermissionLevel.Allow,
                [EffectNames.AssetCreate] = PermissionLevel.Allow,
                [EffectNames.AssetModify] = PermissionLevel.Allow,
                [EffectNames.AssetMove] = PermissionLevel.Allow,
                [EffectNames.AssetDelete] = PermissionLevel.Ask,
                [EffectNames.CodeTemplate] = PermissionLevel.Allow,
                [EffectNames.CodeWrite] = PermissionLevel.Ask,
                [EffectNames.Compile] = PermissionLevel.Allow,
                [EffectNames.PackageModify] = PermissionLevel.Ask,
            });

        /// <summary>The §C4/§H4 default policy for the Agent.</summary>
        public static readonly DefaultPermissionPolicy Agent =
            new DefaultPermissionPolicy(PolicyActor.Agent, s_agent);

        /// <summary>The §H4 default policy for a human.</summary>
        public static readonly DefaultPermissionPolicy Human =
            new DefaultPermissionPolicy(PolicyActor.Human, s_human);

        /// <summary>The default policy for <paramref name="actor"/>.</summary>
        public static DefaultPermissionPolicy For(PolicyActor actor) =>
            actor == PolicyActor.Agent ? Agent : Human;

        private readonly IReadOnlyDictionary<string, PermissionLevel> _levels;

        private DefaultPermissionPolicy(
            PolicyActor actor, IReadOnlyDictionary<string, PermissionLevel> levels)
        {
            Actor = actor;
            _levels = levels;
        }

        /// <inheritdoc/>
        public PolicyActor Actor { get; }

        /// <inheritdoc/>
        public bool DestructiveRequiresAsk => true;

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
    }
}
