#nullable enable

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// How far an Operation's effects can be reverted through the Unity Undo
    /// system (SPEC §C1, §G3).
    /// </summary>
    public enum UndoSupport
    {
        None,
        Partial,
        Full,
    }
}
