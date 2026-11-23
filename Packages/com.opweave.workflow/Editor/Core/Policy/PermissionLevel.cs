#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// One level of the SPEC §C4/§H4 permission vocabulary: <see cref="Allow"/> runs
    /// without approval, <see cref="Ask"/> requires human approval before the Run
    /// proceeds, and <see cref="Deny"/> rejects validation with OW-PERM-001.
    /// </summary>
    public enum PermissionLevel
    {
        Allow,
        Ask,
        Deny,
    }
}
