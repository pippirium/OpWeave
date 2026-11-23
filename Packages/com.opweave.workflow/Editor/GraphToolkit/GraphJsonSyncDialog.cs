#nullable enable

using UnityEditor;
using UnityEngine;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The production prompt behind the SPEC §H7.3 choice "reload from JSON
    /// / overwrite JSON with graph": a modal
    /// <see cref="EditorUtility.DisplayDialogComplex"/> shown to the human.
    /// In batch mode — where no modal answer exists — every prompt resolves
    /// to <see cref="GraphJsonSyncChoice.Cancel"/> so an automatic path can
    /// never silently overwrite the JSON (SPEC §0.6 SG-17).
    /// </summary>
    internal static class GraphJsonSyncDialog
    {
        /// <summary>
        /// Shows the conflict dialog for <paramref name="check"/> and maps
        /// the answer to a <see cref="GraphJsonSyncChoice"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="check"/> is null.</exception>
        internal static GraphJsonSyncChoice ShowConflictPrompt(GraphJsonSyncCheck check)
        {
            if (check is null)
            {
                throw new System.ArgumentNullException(nameof(check));
            }
            if (Application.isBatchMode)
            {
                return GraphJsonSyncChoice.Cancel;
            }
            var detail = check.State == GraphJsonSyncState.Missing
                ? "the file no longer exists"
                : check.State == GraphJsonSyncState.InvalidJson
                    ? "the file no longer parses as a workflow document"
                    : check.State == GraphJsonSyncState.IoError
                        ? "the file could not be read"
                        : check.State == GraphJsonSyncState.UnemittableGraph
                            ? "the graph carries content it cannot re-emit unambiguously"
                            : "the file was changed outside the editor";
            var answer = EditorUtility.DisplayDialogComplex(
                "OpWeave workflow JSON changed",
                "The JSON source of truth this graph was last synced with diverged: " +
                detail + ".\n\nReload from JSON discards unsaved graph changes; " +
                "Overwrite JSON writes the graph over the file.",
                "Reload from JSON",
                "Cancel",
                "Overwrite JSON");
            return answer switch
            {
                0 => GraphJsonSyncChoice.ReloadJson,
                2 => GraphJsonSyncChoice.OverwriteJson,
                _ => GraphJsonSyncChoice.Cancel,
            };
        }
    }
}
