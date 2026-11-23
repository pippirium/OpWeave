#nullable enable

using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// Converts a single workflow node JSON from <see cref="FromVersion"/> to the
    /// implementing Operation's own registered version (SPEC §A7 item 2): parameter
    /// keys/values and port names are rewritten in place. Standard and custom
    /// Operations use the same mechanism — an Operation class implements this
    /// interface alongside <c>IOperation</c> when its new version can upgrade nodes
    /// written for an older version.
    /// </summary>
    /// <remarks>
    /// Applied through the migration framework's internal lookup of registered
    /// Operation versions, only from <c>migrate_workflow</c> — never during validate
    /// or execute (SPEC §A7 item 3). A node whose version no registered
    /// <see cref="IOperationMigration"/> accepts is OW-OP-002; a migration that
    /// throws is OW-WF-007.
    /// </remarks>
    public interface IOperationMigration
    {
        /// <summary>The node <c>version</c> this migration accepts.</summary>
        int FromVersion { get; }

        /// <summary>Rewrites <paramref name="node"/> in place to the implementing version.</summary>
        void Migrate(JObject node);
    }
}
