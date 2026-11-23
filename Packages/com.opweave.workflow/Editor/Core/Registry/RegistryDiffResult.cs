#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// The outcome of one SPEC §C3 rule-7 persistence pass: what the new
    /// build recorded as its diff against the previously persisted
    /// <c>registry.json</c> — or why it recorded nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Previous"/> is null exactly when the persisted summary
    /// could not be trusted — the file missing, unreadable or malformed —
    /// and <see cref="UnavailableReason"/> then carries the fixed English
    /// reason surfaces (get_registry_status, the Registry tab, Console)
    /// report alongside <see cref="PreviousSnapshot"/> =
    /// <c>"unavailable"</c> (SPEC §C3 rule 7). No diff is computed in that
    /// case: <see cref="Changes"/> is empty — never a synthetic
    /// all-Operations-added record.
    /// </para>
    /// <para>
    /// <see cref="Changes"/> is the batch appended to
    /// <c>registry-changes.ndjson</c>: the computed
    /// added/removed/descriptorChanged/implementationChanged records plus
    /// this build's <see cref="RegistrySnapshot.Failures"/>, in
    /// <see cref="RegistryDiff"/> order followed by snapshot failure order.
    /// <see cref="ChangesRecorded"/> is true only when the append actually
    /// completed — the §E8 <c>RegistryChanged</c> publish contract requires
    /// the change record written first.
    /// </para>
    /// <para>
    /// <see cref="PersistenceError"/> carries the fixed English description
    /// of a failed append or a failed <c>registry.json</c> replace — a
    /// degraded pass, not a build failure: the snapshot is published
    /// regardless (SPEC §C3 rule 7 permits the new Registry), and the next
    /// build simply finds the previous summary unavailable or stale.
    /// </para>
    /// </remarks>
    public sealed record RegistryDiffResult(
        PersistedRegistrySnapshot? Previous,
        PersistedRegistrySnapshot Current,
        IReadOnlyList<RegistryChange> Changes,
        bool ChangesRecorded,
        string? UnavailableReason,
        string? PersistenceError)
    {
        /// <summary>The previously persisted summary, or null when unavailable.</summary>
        public PersistedRegistrySnapshot? Previous { get; init; } = Previous;

        /// <summary>The persisted form of the snapshot just published.</summary>
        public PersistedRegistrySnapshot Current { get; init; } =
            Current ?? throw new ArgumentNullException(nameof(Current));

        /// <summary>
        /// The build's recorded batch — empty when the previous summary was
        /// unavailable or nothing changed and no candidate failed.
        /// </summary>
        public IReadOnlyList<RegistryChange> Changes { get; init; } =
            CheckChanges(Changes);

        /// <summary>
        /// The <c>previousSnapshot</c> status of SPEC §C3 rule 7:
        /// <c>"available"</c> or <c>"unavailable"</c>.
        /// </summary>
        public string PreviousSnapshot => Previous is null ? "unavailable" : "available";

        private static IReadOnlyList<RegistryChange> CheckChanges(
            IReadOnlyList<RegistryChange> changes)
        {
            var copy = ReadOnlyCollections.CopyList(changes);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Change entries must not be null.", nameof(changes));
                }
            }
            return copy;
        }
    }
}
