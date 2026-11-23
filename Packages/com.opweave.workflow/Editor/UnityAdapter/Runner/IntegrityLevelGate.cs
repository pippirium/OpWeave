#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The integrity-level refusal inside the SG-18 acceptance chain (SPEC
    /// §0.6): a configured <c>extensions.integrityLevel</c> of
    /// <see cref="IntegrityLevel.Full"/> — a valid settings value, never a
    /// settings error — refuses every new Run with OW-OP-011 before any side
    /// effect, the "Run 受付時に OW-OP-011" row of the SPEC §H4.1 loading
    /// table. v1 carries no full-verification algorithm (SPEC §C0.2 and §X.8
    /// keep it the single open item) and the refusal never downgrades to the
    /// weak level (SPEC §0.1 F-05, §M.5).
    /// </summary>
    /// <remarks>
    /// The gate judges the settings snapshot the <see cref="SettingsErrorGate"/>
    /// validated for the same accept — the host keeps it immediately after the
    /// settings gate, so the Run is refused by exactly the snapshot its
    /// admission validated (the same binding the §G1 <c>path_writable</c>
    /// checks read). A failed settings load already refused the Run inside the
    /// settings gate, so this gate only ever runs with a validated snapshot;
    /// absent one there is no configured <c>full</c> to refuse on and it
    /// admits.
    /// </remarks>
    internal sealed class IntegrityLevelGate : IRunAcceptanceGate
    {
        private readonly SettingsErrorGate _settingsGate;

        /// <exception cref="ArgumentNullException"><paramref name="settingsGate"/> is null.</exception>
        internal IntegrityLevelGate(SettingsErrorGate settingsGate) =>
            _settingsGate = settingsGate ?? throw new ArgumentNullException(nameof(settingsGate));

        /// <inheritdoc cref="IRunAcceptanceGate.Admit"/>
        public Result<Unit> Admit(RunAcceptanceRequest request) =>
            _settingsGate.LastValidated?.IntegrityLevel == IntegrityLevel.Full
                ? Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_011,
                    "IntegrityLevel=full execution is not supported in this version."))
                : Result<Unit>.Success(Unit.Value);
    }
}
