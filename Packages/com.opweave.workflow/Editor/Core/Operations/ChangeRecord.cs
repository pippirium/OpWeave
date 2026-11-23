#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// One element of a Run's change list (SPEC §F7):
    /// <c>{step, attempt, nodeId, action, target: {kind, path, guid?, globalId?}, undoable}</c>
    /// — the review and manual-rollback record produced through
    /// <see cref="IChangeRecorder"/> and by the automatic asset-change tracking.
    /// </summary>
    /// <remarks>
    /// <see cref="Attempt"/> is the attempt that made the change — the same number
    /// the element's §F2 journal envelope carries. It is part of the element
    /// because one Run's change list keeps the attempts apart: the same action on
    /// the same target in two attempts is two elements (SPEC §F7, 2026-11-08
    /// attempt-durability decision), while repeats within one attempt stay one.
    /// </remarks>
    public sealed record ChangeRecord(
        int Step,
        int Attempt,
        string NodeId,
        ChangeAction Action,
        ChangeTarget Target,
        bool Undoable)
    {
        private readonly string _nodeId =
            string.IsNullOrEmpty(NodeId)
                ? throw new ArgumentException(
                    "A change record must carry its node id.", nameof(NodeId))
                : NodeId;
        private readonly ChangeTarget _target =
            Target ?? throw new ArgumentNullException(nameof(Target));

        /// <summary>The id of the WF node whose step made the change.</summary>
        public string NodeId
        {
            get => _nodeId;
            init => _nodeId =
                string.IsNullOrEmpty(value)
                    ? throw new ArgumentException(
                        "A change record must carry its node id.", nameof(NodeId))
                    : value;
        }

        /// <summary>What the change applies to.</summary>
        public ChangeTarget Target
        {
            get => _target;
            init => _target =
                value ?? throw new ArgumentNullException(nameof(Target));
        }
    }
}
