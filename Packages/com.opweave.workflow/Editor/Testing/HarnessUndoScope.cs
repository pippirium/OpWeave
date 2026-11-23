#nullable enable

using System;
using UnityEditor;

namespace OpWeave.Testing
{
    /// <summary>
    /// The per-step Undo group of SPEC §G3 item 1 around one harness
    /// <c>Execute</c>, so a harness run leaves the Editor's Undo stack in the
    /// state a Run's step does: the step's own Undo records collapsed into one
    /// entry named <c>OpWeave: &lt;operationId&gt;/&lt;nodeId&gt;</c>, with the
    /// group that was current before the step untouched.
    /// </summary>
    /// <remarks>
    /// The sequence is the one SPEC §E4 step 5 opens the step's scope with —
    /// increment, take the new group number, name it, then flush the pending
    /// <c>Undo.RecordObject</c> snapshots and collapse the group on close —
    /// written here rather than borrowed because the Runner's own scope is an
    /// internal type of the Runner assembly and SPEC §0.5 keeps test support
    /// off its internals. An interrupted run reaches <see cref="Dispose"/>
    /// like any other: the crash state an interrupted run models lives in
    /// the journal (a <c>step_started</c> with no outcome record), while an
    /// Editor that died mid-step left no open Undo group at all.
    /// </remarks>
    internal sealed class HarnessUndoScope : IDisposable
    {
        private readonly int _group;
        private bool _closed;

        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        internal HarnessUndoScope(string name)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }
            Undo.IncrementCurrentGroup();
            _group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
        }

        /// <summary>
        /// Collapses the step's Undo records into the named group. The pending
        /// snapshots are flushed first: left pending, they would land after the
        /// collapse and split the step across two Undo steps instead of the one
        /// SPEC §G3 item 1 requires.
        /// </summary>
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(_group);
        }
    }
}
