#nullable enable

using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §F7 automatic recorder: Unity reports every asset import,
    /// deletion and move it performed through
    /// <c>AssetPostprocessor.OnPostprocessAllAssets</c>, and this is the package's
    /// listener for it. What it reports is attributed to the step whose attempt the
    /// executing Run last opened (<see cref="ChangeSetTracker"/>), so a change a
    /// custom Operation forgot to record through <c>ctx.Changes</c> still reaches
    /// the Run's change list — the reason §F7 requires this second channel at all.
    /// </summary>
    /// <remarks>
    /// The callback exists for the whole domain and fires on the Editor's own
    /// imports too; it does nothing but hand the four lists over while no Run is
    /// executing. Nothing here may throw into the import pipeline.
    /// </remarks>
    internal sealed class AssetChangePostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths) =>
            ChangeSetTracker.ReportAssetChanges(
                importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
    }
}
