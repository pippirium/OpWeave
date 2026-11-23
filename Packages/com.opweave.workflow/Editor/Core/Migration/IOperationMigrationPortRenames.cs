#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Migration
{
    /// <summary>
    /// The Registry-internal declaration of the external port renames one
    /// Operation-version migration step performs (SPEC §A7 item 2). An
    /// Operation class implements this interface alongside
    /// <see cref="IOperationMigration"/> when its migration renames ports the
    /// surrounding workflow references — the node-only public
    /// <see cref="IOperationMigration.Migrate"/> signature cannot carry them
    /// (SPEC §A7), so the registered-version metadata supplies them without
    /// widening the public extension API (SPEC §C0.1).
    /// </summary>
    /// <remarks>
    /// The §A7 contract requires an explicit declaration for every Operation
    /// that carries a migration: an empty list means "this step renames no
    /// ports" and an Operation that declares nothing fails closed — the
    /// framework refuses the step rather than leaving stale workflow port
    /// references behind.
    /// </remarks>
    internal interface IOperationMigrationPortRenames
    {
        /// <summary>
        /// The external port renames the implementing Operation's migration
        /// step performs; never null — an empty list declares "no renames".
        /// </summary>
        IReadOnlyList<OperationPortRename> PortRenames { get; }
    }
}
