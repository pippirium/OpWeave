#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SG-18 settings gate of SPEC §0.6 and §H4.1 ("設定エラーのときの動作":
    /// 新しい Run は受け付けない): while the settings file is in the error
    /// state the gate refuses the accept with the loader's own OW-CFG-001 /
    /// OW-CFG-002 diagnostics — before any side effect and with no dedicated
    /// RUN code (SG-18). A readable file admits the Run; its OW-CFG-003/004/005
    /// warnings ride on the admission result so the caller can surface them.
    /// </summary>
    internal sealed class SettingsErrorGate : IRunAcceptanceGate
    {
        private readonly Func<Result<OpWeaveSettings>> _loadSettings;

        /// <summary>
        /// <paramref name="loadSettings"/> re-reads the settings file per
        /// accept, so a file the human fixed unblocks Runs without an Editor
        /// restart.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="loadSettings"/> is null.</exception>
        internal SettingsErrorGate(Func<Result<OpWeaveSettings>> loadSettings) =>
            _loadSettings = loadSettings ?? throw new ArgumentNullException(nameof(loadSettings));

        /// <summary>
        /// The <see cref="OpWeaveSettings"/> the most recent successful
        /// admission loaded and validated — the runner host records it for
        /// the Run that admission accepted, so the Run's runtime
        /// <c>path_writable</c> checks are judged by the same snapshot
        /// (SPEC §H4.1/D-R6: a failed load leaves the previous value and
        /// refuses the Run, never a default fallback).
        /// </summary>
        internal OpWeaveSettings? LastValidated { get; private set; }

        /// <inheritdoc cref="IRunAcceptanceGate.Admit"/>
        public Result<Unit> Admit(RunAcceptanceRequest request)
        {
            var settings = _loadSettings();
            if (!settings.Ok)
            {
                return Result<Unit>.Failure(settings.Diagnostics);
            }
            LastValidated = settings.Value;
            return Result<Unit>.Success(Unit.Value, settings.Diagnostics);
        }
    }
}
