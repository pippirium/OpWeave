#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// Origin of a registered Operation (SPEC §C3 origin rule): the defining
    /// assembly belongs to the <c>com.opweave.workflow</c> package
    /// (<see cref="Standard"/>) or to anything else (<see cref="Custom"/>).
    /// Origin is used only for extension permission/discovery, provenance display
    /// and search filtering; it never changes validation, execution or recovery
    /// rules (SPEC §C2 item 9).
    /// </summary>
    public enum OperationOrigin
    {
        Standard,
        Custom,
    }
}
