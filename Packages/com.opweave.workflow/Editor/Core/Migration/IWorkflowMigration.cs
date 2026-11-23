#nullable enable

using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// One step in a schemaVersion migration chain (SPEC §A7, §N.3 item 4): converts a
    /// whole workflow document JSON from the <see cref="From"/> version to the
    /// <see cref="To"/> version. Chained by the migration framework until the document
    /// reaches the current major version; never invoked during validate or execute —
    /// migrations are applied only through <c>migrate_workflow</c> (SPEC §A7 item 3).
    /// </summary>
    /// <remarks>
    /// The returned <see cref="JObject"/> is authoritative: it replaces the input
    /// document as the next chain input and its <c>schemaVersion</c> must equal
    /// <see cref="To"/>. A null return, a thrown exception, a mismatched produced
    /// version or an unresolvable chain is an OW-WF-007 migration failure; a version
    /// no registered migration reaches is OW-WF-002.
    /// </remarks>
    public interface IWorkflowMigration
    {
        /// <summary>The document <c>schemaVersion</c> this migration accepts.</summary>
        string From { get; }

        /// <summary>The <c>schemaVersion</c> the migrated document declares.</summary>
        string To { get; }

        /// <summary>Converts <paramref name="doc"/> from <see cref="From"/> to <see cref="To"/>.</summary>
        JObject Migrate(JObject doc);
    }
}
