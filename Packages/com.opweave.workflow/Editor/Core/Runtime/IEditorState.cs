#nullable enable

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The Editor-state probe of SPEC §E2 item 4 and §E3 item 4: the Unity
    /// wiring implements it over <c>EditorApplication</c> flags so Core never
    /// touches Unity types.
    /// </summary>
    public interface IEditorState
    {
        /// <summary>
        /// The Editor is in Edit Mode — not playing and not transitioning into
        /// Play Mode (SPEC §1.4).
        /// </summary>
        bool IsEditMode { get; }

        /// <summary><c>EditorApplication.isCompiling</c> (SPEC §E2 item 4).</summary>
        bool IsCompiling { get; }

        /// <summary><c>EditorApplication.isUpdating</c> (SPEC §E2 item 4).</summary>
        bool IsUpdating { get; }
    }
}
