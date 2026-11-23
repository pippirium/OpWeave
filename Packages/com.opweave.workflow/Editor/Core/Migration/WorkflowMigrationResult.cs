#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Migration
{
    /// <summary>
    /// The outcome of a successful <see cref="WorkflowMigrator.Migrate"/> call
    /// (SPEC §A7 item 3): the migrated document JSON — a fresh tree; the caller's
    /// input is never mutated and no file is written — plus one
    /// <see cref="MigrationChange"/> per applied step in application order.
    /// </summary>
    internal sealed record WorkflowMigrationResult(
        JObject Document,
        IReadOnlyList<MigrationChange> Changes)
    {
        /// <summary>The migrated document; never null.</summary>
        public JObject Document { get; init; } =
            Document ?? throw new System.ArgumentNullException(nameof(Document));

        /// <summary>One entry per applied step; never null.</summary>
        public IReadOnlyList<MigrationChange> Changes { get; init; } =
            ReadOnlyCollections.CopyList(Changes);
    }
}
