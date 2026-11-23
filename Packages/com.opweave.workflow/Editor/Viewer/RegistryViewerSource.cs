#nullable enable

using System;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;

namespace OpWeave.Viewer
{
    /// <summary>
    /// Read model for the Registry tab of <see cref="RunViewerWindow"/> (SPEC
    /// §H5 item 2). The tab is strictly read-only over the publication seam:
    /// the atomically published <see cref="RegistrySnapshot"/> plus its stale
    /// flag (<see cref="RegistryPublication"/>) and the retained
    /// <see cref="RegistryBuildResult"/> (<see cref="RegistryBuildHost.Latest"/>)
    /// — it never writes Registry state, never evaluates permission settings
    /// and offers no control that could admit an excluded extension (SPEC §C3
    /// rule 5). The only action it performs is the one-time
    /// <see cref="OpWeaveProjectSetup"/> entry point of SPEC §M.3 and the
    /// source-open navigation SPEC §H5 requires for custom Operations.
    /// </summary>
    /// <remarks>
    /// The seams exist so tests can bind an isolated
    /// <see cref="RegistryPublication"/> and <see cref="RegistryBuildResult"/>
    /// plus a scratch project root for setup; production uses the shared
    /// publication, the last completed build and the real project layout.
    /// </remarks>
    internal sealed class RegistryViewerSource
    {
        private readonly RegistryPublication _publication;
        private readonly Func<RegistryBuildResult?> _latestBuild;
        private readonly Func<RegistryDiffResult?> _latestDiff;
        private readonly Func<IRunEvents?> _events;
        private readonly Func<bool> _isSetupComplete;
        private readonly Func<OpWeaveSetupResult> _runSetup;
        private readonly Action<OpWeaveSetupResult> _logSetup;
        private readonly Func<string?, bool> _openSource;

        internal RegistryViewerSource(
            RegistryPublication publication,
            Func<RegistryBuildResult?> latestBuild,
            Func<RegistryDiffResult?> latestDiff,
            Func<IRunEvents?> events,
            Func<bool> isSetupComplete,
            Func<OpWeaveSetupResult> runSetup,
            Action<OpWeaveSetupResult> logSetup,
            Func<string?, bool> openSource)
        {
            _publication = publication ?? throw new ArgumentNullException(nameof(publication));
            _latestBuild = latestBuild ?? throw new ArgumentNullException(nameof(latestBuild));
            _latestDiff = latestDiff ?? throw new ArgumentNullException(nameof(latestDiff));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _isSetupComplete = isSetupComplete ?? throw new ArgumentNullException(nameof(isSetupComplete));
            _runSetup = runSetup ?? throw new ArgumentNullException(nameof(runSetup));
            _logSetup = logSetup ?? throw new ArgumentNullException(nameof(logSetup));
            _openSource = openSource ?? throw new ArgumentNullException(nameof(openSource));
        }

        /// <summary>The shared publication, retained build and project layout.</summary>
        internal static RegistryViewerSource Production => new RegistryViewerSource(
            RegistryPublication.Shared,
            () => RegistryBuildHost.Latest,
            () => RegistryDiffRecorder.Shared.Latest,
            () => OpWeaveRunnerHost.Current?.Events,
            OpWeaveProjectSetup.IsSetupComplete,
            OpWeaveProjectSetup.Run,
            OpWeaveProjectSetup.LogResult,
            RegistrySourceOpener.Open);

        /// <summary>The atomically published snapshot; null before the first build.</summary>
        internal RegistrySnapshot? Snapshot => _publication.Snapshot;

        /// <summary>
        /// SPEC §E8 stale flag: true before the first successful build, after a
        /// failed compilation or while a rebuild is in flight — the displayed
        /// snapshot is then the previous good one, never an intermediate.
        /// </summary>
        internal bool Stale => _publication.Stale;

        /// <summary>
        /// The retained result of the last completed build (SPEC §H5): settings
        /// diagnostics, the recorded diff, registered TypeProviders and the
        /// admitted extension packages. Null when no build has completed.
        /// </summary>
        internal RegistryBuildResult? Build => _latestBuild();

        /// <summary>
        /// The recorder's retained §C3 rule-7 result — read here rather than
        /// through <see cref="Build"/>.<c>Diff</c> because the recorder stores
        /// it before <c>RegistryChanged</c> dispatches while the retained
        /// result's <c>Diff</c> attaches only once the record pass returns,
        /// so this keeps the diff rows consistent with the snapshot a
        /// refresh sees.
        /// </summary>
        internal RegistryDiffResult? Diff => _latestDiff();

        /// <summary>The §E8 event dispatcher the tab refreshes on.</summary>
        internal IRunEvents? Events => _events();

        /// <summary>SPEC §M.3 completion check over the real project layout.</summary>
        internal bool IsSetupComplete => _isSetupComplete();

        /// <summary>
        /// The SPEC §M.3 setup action: creates the recommended layout without
        /// overwriting existing files; the result is logged through the same
        /// seam the menu item uses.
        /// </summary>
        internal OpWeaveSetupResult RunSetup()
        {
            var result = _runSetup();
            _logSetup(result);
            return result;
        }

        /// <summary>Opens a project-relative source file; false when it cannot be resolved.</summary>
        internal bool OpenSource(string? sourceFile) => _openSource(sourceFile);
    }
}
