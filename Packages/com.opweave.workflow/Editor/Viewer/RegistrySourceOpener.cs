#nullable enable

using UnityEditor;

namespace OpWeave.Viewer
{
    /// <summary>
    /// Opens the project-relative source file a custom Operation was
    /// registered from (SPEC §H5 item 2: カスタムはソースを開ける). The
    /// stored path comes from <c>[CallerFilePath]</c> attribution at build
    /// time and is always project-relative ("Assets/…" or "Packages/…"), so
    /// it resolves as a <see cref="MonoScript"/> through the AssetDatabase;
    /// anything else — a moved or deleted file, an absolute path — reports
    /// failure instead of navigating to the wrong asset.
    /// </summary>
    internal static class RegistrySourceOpener
    {
        internal static bool Open(string? sourceFile)
        {
            if (string.IsNullOrEmpty(sourceFile))
            {
                return false;
            }
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(sourceFile!);
            if (script is null)
            {
                return false;
            }
            return AssetDatabase.OpenAsset(script);
        }
    }
}
