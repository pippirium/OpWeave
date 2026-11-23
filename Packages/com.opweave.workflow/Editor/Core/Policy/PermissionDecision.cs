#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// The permission outcome for one node (SPEC §D6 item 1): the effects at
    /// <see cref="PermissionLevel.Ask"/> that go on the Plan's approval list and
    /// the effects at <see cref="PermissionLevel.Deny"/> that produce OW-PERM-001.
    /// Allowed effects are absent from both lists.
    /// </summary>
    public sealed record PermissionDecision(
        string NodeId,
        IReadOnlyList<string> ApprovalEffects,
        IReadOnlyList<string> DeniedEffects);
}
