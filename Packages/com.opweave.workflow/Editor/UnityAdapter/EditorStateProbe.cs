#nullable enable

using System;
using OpWeave.Core.Runtime;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Editor-state probe of SPEC §G1 item 2: reports to the Runner and
    /// to Requirement evaluators whether the Editor is in Play Mode
    /// (<see cref="EditorApplication.isPlayingOrWillChangePlaymode"/>),
    /// compiling (<see cref="EditorApplication.isCompiling"/>), updating
    /// assets (<see cref="EditorApplication.isUpdating"/>), building a player
    /// (<see cref="BuildPipeline.isBuildingPlayer"/>) or running in batch
    /// mode (<see cref="Application.isBatchMode"/>), and implements the Core
    /// <see cref="IEditorState"/> abstraction (SPEC §E3 item 1) on top of
    /// them. Every flag is read live on each access.
    /// </summary>
    /// <remarks>
    /// The flag readers are injected as delegates so the live Unity API
    /// surface sits in exactly one place — the parameterless constructor —
    /// and tests can drive every reported state with test doubles.
    /// </remarks>
    public sealed class EditorStateProbe : IEditorState
    {
        private readonly Func<bool> _isPlayingOrWillChangePlaymode;
        private readonly Func<bool> _isCompiling;
        private readonly Func<bool> _isUpdating;
        private readonly Func<bool> _isBuildingPlayer;
        private readonly Func<bool> _isBatchMode;

        /// <summary>The probe over the live Unity Editor flags.</summary>
        public EditorStateProbe()
            : this(
                static () => EditorApplication.isPlayingOrWillChangePlaymode,
                static () => EditorApplication.isCompiling,
                static () => EditorApplication.isUpdating,
                static () => BuildPipeline.isBuildingPlayer,
                static () => Application.isBatchMode)
        {
        }

        /// <summary>
        /// The probe over caller-supplied flag readers — the test seam that
        /// lets a fixture toggle every reported state (SPEC §G1 test).
        /// </summary>
        /// <exception cref="ArgumentNullException">Any reader is null.</exception>
        public EditorStateProbe(
            Func<bool> isPlayingOrWillChangePlaymode,
            Func<bool> isCompiling,
            Func<bool> isUpdating,
            Func<bool> isBuildingPlayer,
            Func<bool> isBatchMode)
        {
            _isPlayingOrWillChangePlaymode = isPlayingOrWillChangePlaymode
                ?? throw new ArgumentNullException(nameof(isPlayingOrWillChangePlaymode));
            _isCompiling = isCompiling ?? throw new ArgumentNullException(nameof(isCompiling));
            _isUpdating = isUpdating ?? throw new ArgumentNullException(nameof(isUpdating));
            _isBuildingPlayer = isBuildingPlayer ?? throw new ArgumentNullException(nameof(isBuildingPlayer));
            _isBatchMode = isBatchMode ?? throw new ArgumentNullException(nameof(isBatchMode));
        }

        /// <summary>
        /// The Editor is playing or transitioning into Play Mode
        /// (<see cref="EditorApplication.isPlayingOrWillChangePlaymode"/>).
        /// </summary>
        public bool IsPlayingOrWillChangePlaymode => _isPlayingOrWillChangePlaymode();

        /// <inheritdoc cref="IEditorState.IsEditMode"/>
        public bool IsEditMode => !_isPlayingOrWillChangePlaymode();

        /// <inheritdoc cref="IEditorState.IsCompiling"/>
        public bool IsCompiling => _isCompiling();

        /// <inheritdoc cref="IEditorState.IsUpdating"/>
        public bool IsUpdating => _isUpdating();

        /// <summary><see cref="BuildPipeline.isBuildingPlayer"/>.</summary>
        public bool IsBuildingPlayer => _isBuildingPlayer();

        /// <summary><see cref="Application.isBatchMode"/>.</summary>
        public bool IsBatchMode => _isBatchMode();
    }
}
