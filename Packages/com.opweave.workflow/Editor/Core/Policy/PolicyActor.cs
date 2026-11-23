#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// The actor a permission policy decides for (SPEC §H4): the Agent or a human
    /// running a workflow from the GUI.
    /// </summary>
    public enum PolicyActor
    {
        Agent,
        Human,
    }
}
