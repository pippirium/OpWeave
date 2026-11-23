#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>The filesystem kind of one <see cref="OpWeaveSetupItem"/> of the
    /// SPEC §M.3 setup layout.</summary>
    public enum OpWeaveSetupItemKind
    {
        /// <summary>A directory (created with <see cref="System.IO.Directory.CreateDirectory(string)"/>).</summary>
        Folder,

        /// <summary>A file whose content the setup owns.</summary>
        File,
    }
}
