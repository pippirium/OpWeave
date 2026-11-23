#nullable enable

using System;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The Descriptor lookup every <see cref="OperationNode"/> port definition
    /// goes through (SPEC §H6.2): the current §C3
    /// <see cref="RegistrySnapshot"/> read through
    /// <see cref="RegistryPublication.Shared"/> — the single atomic
    /// publication point the search index, the GUI and the Runner share —
    /// or the snapshot a test installs through <see cref="SnapshotSource"/>.
    /// </summary>
    internal static class GraphToolkitRegistry
    {
        private static Func<RegistrySnapshot?> s_snapshotSource =
            static () => RegistryPublication.Shared.Snapshot;

        /// <summary>
        /// The snapshot provider <see cref="FindOperation"/> reads. Production
        /// keeps the <see cref="RegistryPublication"/> default; tests
        /// substitute an in-memory snapshot and restore it afterwards.
        /// </summary>
        /// <exception cref="ArgumentNullException">The assigned provider is null.</exception>
        internal static Func<RegistrySnapshot?> SnapshotSource
        {
            get => s_snapshotSource;
            set => s_snapshotSource = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// The Descriptor registered for <paramref name="operationId"/> at
        /// <paramref name="version"/> in the current snapshot, or null when
        /// none is registered — including before the first successful §C3
        /// build publishes.
        /// </summary>
        internal static OperationDescriptor? FindOperation(string operationId, int version) =>
            s_snapshotSource()?.Find(operationId, version)?.Descriptor;
    }
}
