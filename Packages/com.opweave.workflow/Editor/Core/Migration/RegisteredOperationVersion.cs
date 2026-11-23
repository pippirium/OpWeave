#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Migration
{
    /// <summary>
    /// Port name and direction for a particular Operation version transition.
    /// The Registry must declare all external port renames for its migration;
    /// IOperationMigration itself keeps the SPEC §A7 public node-only signature.
    /// </summary>
    internal enum OperationPortKind
    {
        Input,
        Output,
        Control,
    }

    internal sealed record OperationPortRename(
        OperationPortKind Kind, string From, string To);

    /// <summary>
    /// One registered Operation version, optional migration, and explicit
    /// port renames to be applied to the surrounding workflow connections
    /// when the prior version is upgraded. For an Operation with a migration,
    /// PortRenames must be explicitly non-null: empty means no port renames;
    /// null means the Registry has not declared compatibility and fails closed.
    /// </summary>
    internal sealed record RegisteredOperationVersion(
        int Version,
        IOperationMigration? Migration,
        IReadOnlyList<OperationPortRename>? PortRenames = null);
}
