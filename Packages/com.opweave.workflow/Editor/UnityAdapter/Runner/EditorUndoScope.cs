#nullable enable

using System;
using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The per-step Undo group of SPEC §G3 item 1: the Runner opens one scope
    /// around every Execute/Poll attempt (SPEC §E4 step 5) so the step's
    /// Undoable changes collapse into a single Undo entry named
    /// <c>OpWeave: &lt;workflowId&gt;/&lt;nodeId&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The author contract this imposes on an Operation (SPEC §G3 items
    /// 1–3).</b> The group is opened and closed here, so an Operation never
    /// calls <see cref="Undo.IncrementCurrentGroup"/> or
    /// <see cref="Undo.CollapseUndoOperations"/> — it registers its changes
    /// into whichever group is current when <c>Execute</c>/<c>Poll</c> starts.
    /// Every Scene change goes through an Undo API
    /// (<c>Undo.RegisterCreatedObjectUndo</c>, <c>Undo.RecordObject</c>,
    /// <c>Undo.AddComponent</c>, <c>Undo.DestroyObjectImmediate</c>,
    /// <c>Undo.SetTransformParent</c> or
    /// <c>SerializedObject.ApplyModifiedProperties</c>), which is also what
    /// marks the Scene dirty. Creating, moving or deleting an <i>asset</i> is
    /// never undoable, so such an Operation declares its Descriptor
    /// <c>Undo</c> as <c>None</c> or <c>Partial</c> and lets the SPEC §F7
    /// change records carry what happened instead.
    /// </para>
    /// </remarks>
    internal sealed class EditorUndoScope : IDisposable
    {
        private readonly int _group;

        private EditorUndoScope(int group) => _group = group;

        /// <summary>
        /// Opens the group: <see cref="Undo.IncrementCurrentGroup"/>, then
        /// <see cref="Undo.SetCurrentGroupName"/> — closing collapses the
        /// whole group into one Undo record (SPEC §G3 item 1).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        internal static IDisposable Open(string name)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return new EditorUndoScope(group);
        }

        /// <summary>
        /// Collapses the step's Undo records into the named group. The pending
        /// <c>Undo.RecordObject</c> snapshots are flushed first: left pending,
        /// they would be written after the collapse and split the step across
        /// two Undo steps instead of the one SPEC §G3 item 1 requires.
        /// </summary>
        public void Dispose()
        {
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(_group);
        }
    }
}
