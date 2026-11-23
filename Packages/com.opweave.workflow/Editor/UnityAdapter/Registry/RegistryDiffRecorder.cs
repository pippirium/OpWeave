#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpWeave.Core;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Editor-side surface of SPEC §C3 rule 7: drives the Core
    /// <see cref="RegistrySnapshotStore"/> pass for each published build —
    /// the same Unity-free code the [Core] tests exercise — then reports
    /// the outcome through the one-line Console summary and the §E8
    /// <c>RegistryChanged</c> event, retains the result for the later
    /// get_registry_status / Registry tab (T-W3.2-02, T-W3.5-03), and
    /// refreshes <see cref="PreviousDefinitions"/> — the durable
    /// previous-definition data the D3 OW-OP-001 hint reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reporting follows the SPEC exactly: a trusted previous summary with
    /// a non-empty recorded batch produces the single Console summary line
    /// and — only when the ndjson append actually completed — the
    /// <c>RegistryChanged</c> dispatch (<see cref="RunEvents.PublishRegistryChanges"/>'
    /// s record-before-publish contract). A missing, corrupt or unreadable
    /// <c>registry.json</c> produces the explicit "previous snapshot
    /// unavailable" Console notice and no dispatch; a persistence failure
    /// produces a warning line and never fails the build.
    /// </para>
    /// <para>
    /// <see cref="Shared"/> is the production recorder: the Run Store root
    /// (<c>Library/OpWeave/</c>), the live Console and the installed
    /// runner host's §E8 dispatcher resolved lazily per call — the host is
    /// installed before the first rebuild runs the startup hook, while a
    /// recorder used without a host simply publishes nothing. Tests
    /// construct recorders over their own roots with captured seams.
    /// </para>
    /// </remarks>
    internal sealed class RegistryDiffRecorder
    {
        private readonly RegistrySnapshotStore _store;
        private readonly Func<RunEvents?> _events;
        private readonly Action<string> _consoleInfo;
        private readonly Action<string> _consoleWarning;

        /// <summary>The domain's production recorder over <c>Library/OpWeave/</c>.</summary>
        internal static RegistryDiffRecorder Shared { get; } = new RegistryDiffRecorder(
            Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "OpWeave"),
            static () => OpWeaveRunnerHost.Current?.Events);

        /// <summary>
        /// A recorder over an injected root and seams.
        /// <paramref name="events"/> resolves the §E8 dispatcher lazily —
        /// the runner host may not be installed yet when the first rebuild
        /// runs; a null resolution drops the dispatch (the record itself is
        /// already durable).
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="root"/> is null or empty.</exception>
        /// <exception cref="ArgumentNullException">Any delegate seam is null.</exception>
        internal RegistryDiffRecorder(
            string root,
            Func<RunEvents?> events,
            Action<string>? consoleInfo = null,
            Action<string>? consoleWarning = null,
            Func<string>? now = null)
        {
            _store = new RegistrySnapshotStore(root, now);
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _consoleInfo = consoleInfo ?? LogInfo;
            _consoleWarning = consoleWarning ?? LogWarning;
        }

        /// <summary>
        /// The last completed persistence pass — the latest registry diff a
        /// consumer (get_registry_status, the Registry tab) reads; null
        /// before this domain's first recorded build. The OW-OP-001
        /// hint's previous-definition data is
        /// <see cref="PreviousDefinitions"/>.
        /// </summary>
        internal RegistryDiffResult? Latest { get; private set; }

        /// <summary>
        /// The durable previous-definition data the D3 OW-OP-001
        /// "previously at <file>" hint reads through
        /// <c>WorkflowCompiler(changes:)</c>: every (id, version) whose
        /// most recent <c>registry-changes.ndjson</c> record is
        /// <see cref="RegistryChangeKinds.Removed"/>, recomputed from the
        /// persisted history on every <see cref="Record"/> — so a
        /// still-deleted Operation keeps its hint after any number of
        /// unrelated later builds, which <see cref="Latest"/> alone
        /// cannot answer (it holds only the last build's batch).
        /// </summary>
        internal IReadOnlyList<RegistryChange> PreviousDefinitions { get; private set; } =
            Array.Empty<RegistryChange>();

        /// <summary>The underlying store — the file layout's single owner.</summary>
        internal RegistrySnapshotStore Store => _store;

        /// <summary>
        /// Runs the SPEC §C3 rule-7 pass for the just-published
        /// <paramref name="snapshot"/> and reports it (see the class
        /// remarks). Returns the pass result, also retained as
        /// <see cref="Latest"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        internal RegistryDiffResult Record(RegistrySnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            var result = _store.RecordPublishedSnapshot(snapshot);
            Latest = result;
            PreviousDefinitions = _store.FindRemovedDefinitions();

            if (result.Previous is null)
            {
                // SPEC §C3 rule 7: never guess — say why no diff exists.
                _consoleWarning(
                    "OpWeave: previous registry snapshot is unavailable (" +
                    (result.UnavailableReason ?? "unknown reason") +
                    "); no registry diff was recorded.");
            }
            else if (result.Changes.Count != 0)
            {
                _consoleInfo(SummaryLine(result.Changes));
                if (result.ChangesRecorded)
                {
                    _events()?.PublishRegistryChanges(result.Changes);
                }
            }

            if (result.PersistenceError is not null)
            {
                _consoleWarning("OpWeave: " + result.PersistenceError);
            }
            return result;
        }

        private static void LogInfo(string line) => Debug.Log(line);

        private static void LogWarning(string line) => Debug.LogWarning(line);

        // The one-line Console summary of SPEC §C3 rule 7: per-kind counts of
        // the recorded batch, non-zero kinds only, in the kind order the
        // rule lists them.
        private static string SummaryLine(IReadOnlyList<RegistryChange> changes)
        {
            var builder = new StringBuilder("OpWeave registry: ");
            var first = true;
            Append(builder, changes, RegistryChangeKinds.Added, ref first);
            Append(builder, changes, RegistryChangeKinds.Removed, ref first);
            Append(builder, changes, RegistryChangeKinds.DescriptorChanged, ref first);
            Append(builder, changes, RegistryChangeKinds.ImplementationChanged, ref first);
            Append(builder, changes, RegistryChangeKinds.Failed, ref first);
            return builder.Append('.').ToString();
        }

        private static void Append(
            StringBuilder builder, IReadOnlyList<RegistryChange> changes, string kind, ref bool first)
        {
            var count = 0;
            for (var i = 0; i < changes.Count; i++)
            {
                if (changes[i].Kind == kind)
                {
                    count++;
                }
            }
            if (count == 0)
            {
                return;
            }
            if (!first)
            {
                builder.Append(", ");
            }
            first = false;
            builder.Append(count).Append(' ').Append(kind);
        }
    }
}
