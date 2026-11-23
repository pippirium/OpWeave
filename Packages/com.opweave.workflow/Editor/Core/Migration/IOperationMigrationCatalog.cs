#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Migration
{
    /// <summary>
    /// The internal (non-public) lookup of registered Operation versions the
    /// migration framework resolves node versions against (SPEC §A7 item 2). The
    /// future Operation Registry snapshot provides the implementation; the public
    /// extension surface never exposes it (SPEC §C0.1).
    /// </summary>
    internal interface IOperationMigrationCatalog
    {
        /// <summary>
        /// Every registered version of <paramref name="operationId"/> in ascending
        /// <see cref="RegisteredOperationVersion.Version"/> order, or null when the
        /// Operation is not registered.
        /// </summary>
        IReadOnlyList<RegisteredOperationVersion>? RegisteredVersions(string operationId);
    }
}
