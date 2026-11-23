#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Compiler;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The settings-derived policy environment one workflow validation or Run
    /// admission binds (SPEC §H4.1, decision D-R6): the configured
    /// <see cref="OpWeave.Core.IntegrityLevel"/> the §D7 planHash provenance
    /// carries verbatim, the §C4/§H4 per-actor permission policy the §D6
    /// stage consults and the <c>allowedPaths</c> its path checks judge by —
    /// exactly the material <see cref="UnityWorkflowCompiler.Create"/>'s
    /// <c>policyFor</c>/<c>allowedPaths</c> and provenance inputs read.
    /// </summary>
    /// <remarks>
    /// <see cref="OpWeaveRunnerHost"/> produces one environment per settings
    /// re-read, so a tightened or repaired settings file binds the next
    /// validation just as it binds the next Run acceptance, and the load's
    /// OW-CFG warnings ride on the <see cref="Result{T}"/> wrapper rather
    /// than on this value. Keeping the projection inside the adapter is also
    /// the §H4 item 5 boundary: the Agent-facing assembly binds this seam
    /// and never names the settings model itself.
    /// </remarks>
    internal sealed class RunPolicyEnvironment
    {
        private readonly OpWeaveSettings _settings;

        /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
        internal RunPolicyEnvironment(OpWeaveSettings settings) =>
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        /// <summary>
        /// The configured <see cref="OpWeave.Core.IntegrityLevel"/> —
        /// <see cref="IntegrityLevel.Full"/> is carried verbatim so the Build
        /// Plan stage rejects it with OW-OP-011, never downgraded (SPEC §0.1
        /// F-05).
        /// </summary>
        internal IntegrityLevel IntegrityLevel => _settings.IntegrityLevel;

        /// <summary>
        /// The <c>allowedPaths</c> the §G1 <c>path_writable</c>/§D6 path
        /// checks of this validation judge by.
        /// </summary>
        internal IReadOnlyList<string> AllowedPaths => _settings.AllowedPaths;

        /// <summary>
        /// The §C4/§H4 permission policy for <paramref name="actor"/> — the
        /// <see cref="SettingsPermissionPolicy"/> projection of the loaded
        /// policy maps (Agent <c>code.write</c> is already floored to ask by
        /// the load, SPEC §0.6 SG-18).
        /// </summary>
        internal IPermissionPolicy PolicyFor(ActorKind actor) =>
            SettingsPermissionPolicy.For(
                actor == ActorKind.Agent ? PolicyActor.Agent : PolicyActor.Human,
                _settings);
    }
}
