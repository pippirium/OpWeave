#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// One workflow node as the D6 permission check sees it (SPEC §D6 item 1):
    /// its actual side effects resolved under §C4 (including any
    /// <c>IEffectResolver</c> output) plus the two Descriptor facts the
    /// <c>destructiveRequiresAsk</c> floor needs.
    /// </summary>
    public sealed record PermissionTarget(
        string NodeId,
        IReadOnlyList<string> Effects,
        bool Destructive,
        bool UndoIsFull);
}
