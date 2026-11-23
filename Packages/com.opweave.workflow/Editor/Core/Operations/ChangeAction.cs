#nullable enable

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The <c>action</c> value of a SPEC §F7 change element: what a step did to
    /// its <see cref="ChangeTarget"/>. Serialized as snake_case
    /// (<c>created</c>, <c>modified</c>, <c>deleted</c>, <c>moved</c>,
    /// <c>saved</c>) in the changes record.
    /// </summary>
    public enum ChangeAction
    {
        /// <summary>The target was created by the step.</summary>
        Created,

        /// <summary>An existing target was modified by the step.</summary>
        Modified,

        /// <summary>The target was deleted by the step.</summary>
        Deleted,

        /// <summary>The target was moved or renamed by the step.</summary>
        Moved,

        /// <summary>The target was saved to disk by the step.</summary>
        Saved,
    }
}
