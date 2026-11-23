#nullable enable

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The step-bound recorder behind <see cref="StepContext.Changes"/>
    /// (SPEC §0.6 SG-06, §F7). Each method appends one §F7 element —
    /// <c>{step, nodeId, action, target: {kind, path, guid?, globalId?}, undoable}</c>
    /// — to the Run's change list; the step and node identity are supplied by
    /// the implementing context, not by the caller.
    /// </summary>
    public interface IChangeRecorder
    {
        /// <summary>Records that the step created <paramref name="target"/>.</summary>
        void Created(ChangeTarget target, bool undoable);

        /// <summary>Records that the step modified <paramref name="target"/>.</summary>
        void Modified(ChangeTarget target, bool undoable);

        /// <summary>Records that the step deleted <paramref name="target"/>.</summary>
        void Deleted(ChangeTarget target, bool undoable);

        /// <summary>Records that the step moved or renamed <paramref name="target"/>.</summary>
        void Moved(ChangeTarget target, bool undoable);

        /// <summary>Records that the step saved <paramref name="target"/> to disk.</summary>
        void Saved(ChangeTarget target, bool undoable);
    }
}
