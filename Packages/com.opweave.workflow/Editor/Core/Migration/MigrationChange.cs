#nullable enable

namespace OpWeave.Core.Migration
{
    /// <summary>Which migration step a <see cref="MigrationChange"/> records.</summary>
    internal enum MigrationChangeKind
    {
        /// <summary>A document <c>schemaVersion</c> step applied by an <see cref="IWorkflowMigration"/>.</summary>
        SchemaVersion,

        /// <summary>A node <c>version</c> step applied by an <see cref="IOperationMigration"/>.</summary>
        OperationVersion,
    }

    /// <summary>
    /// One applied migration step (SPEC §A7 item 3): <c>migrate_workflow</c> returns
    /// the migrated JSON together with this change list. For
    /// <see cref="MigrationChangeKind.SchemaVersion"/> entries <see cref="NodeId"/> and
    /// <see cref="OperationId"/> are null and the versions are schemaVersion strings;
    /// for <see cref="MigrationChangeKind.OperationVersion"/> entries they identify the
    /// node and Operation and the versions are node version numbers.
    /// </summary>
    internal sealed record MigrationChange(
        MigrationChangeKind Kind,
        string? NodeId,
        string? OperationId,
        string FromVersion,
        string ToVersion);
}
