#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Migration;
using OpWeave.Core.Registry;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The production <see cref="IOperationMigrationCatalog"/>: projects one
    /// live <see cref="RegistrySnapshot"/> into the ordered registered-version
    /// list the §A7 migration framework resolves node versions against.
    /// A registered Operation whose live implementation carries
    /// <see cref="IOperationMigration"/> contributes that migration —
    /// <see cref="RegisteredOperationVersion.Version"/> is the version the
    /// migration lands on and <see cref="IOperationMigration.FromVersion"/>
    /// the node version it accepts; the port-rename metadata comes from the
    /// same implementation's internal
    /// <see cref="IOperationMigrationPortRenames"/> declaration. An
    /// implementation that declares a migration but no port-renames list
    /// produces a null <see cref="RegisteredOperationVersion.PortRenames"/>,
    /// so the framework fails closed (OW-WF-007) instead of silently
    /// leaving stale port references.
    /// </summary>
    internal sealed class OperationMigrationCatalog : IOperationMigrationCatalog
    {
        private static readonly IReadOnlyList<OperationPortRename> s_noRenames =
            Array.AsReadOnly(Array.Empty<OperationPortRename>());

        private readonly RegistrySnapshot _snapshot;

        internal OperationMigrationCatalog(RegistrySnapshot snapshot)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        /// <inheritdoc/>
        public IReadOnlyList<RegisteredOperationVersion>? RegisteredVersions(string operationId)
        {
            if (operationId is null)
            {
                throw new ArgumentNullException(nameof(operationId));
            }
            List<RegisteredOperationVersion>? versions = null;
            var operations = _snapshot.Operations;
            // The snapshot's canonical order is (id, version ascending), so
            // this filtered projection is already the ascending contract.
            for (var i = 0; i < operations.Count; i++)
            {
                var registered = operations[i];
                if (!string.Equals(
                        registered.Descriptor.Id, operationId, StringComparison.Ordinal))
                {
                    continue;
                }
                var migration = registered.Implementation as IOperationMigration;
                var renames = registered.Implementation as IOperationMigrationPortRenames;
                versions ??= new List<RegisteredOperationVersion>();
                versions.Add(new RegisteredOperationVersion(
                    registered.Descriptor.Version,
                    migration,
                    migration is null ? s_noRenames : renames?.PortRenames));
            }
            return versions;
        }
    }
}
