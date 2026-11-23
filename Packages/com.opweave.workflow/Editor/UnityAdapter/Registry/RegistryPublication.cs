#nullable enable

using System;
using OpWeave.Core.Registry;
using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The atomic publication point of SPEC §C3 step 8: the single reference
    /// the search index, the GUI and the Runner read the current
    /// <see cref="RegistrySnapshot"/> through. Publication swaps one
    /// immutable state — snapshot plus stale flag — so a reader always sees a
    /// complete snapshot and never an intermediate build state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The published snapshot already carries its own unique-per-build
    /// generation ID (SPEC §C3 rule 6), so the same identity is visible to
    /// every consumer from the moment of the swap. The holder is plain
    /// managed state: it dies with the domain and the next domain's startup
    /// rebuild publishes a fresh snapshot (SPEC §1.4).
    /// </para>
    /// <para>
    /// <see cref="Shared"/> is the production instance the startup rebuild
    /// and the runner host's registry provider share; tests construct their
    /// own instances so no test order can leak state. A holder starts
    /// <see cref="Stale"/> with no snapshot — Runs stay refused until the
    /// first successful build proves a consistent compile (SPEC §C3 rule 9).
    /// </para>
    /// <para>
    /// A failed script compile inside a live domain produces no reload, so
    /// no rebuild can run to store the stale flag; <see cref="Stale"/>
    /// therefore ORs the stored flag with the injected live compile-failure
    /// signal — <see cref="EditorUtility.scriptCompilationFailed"/>, the
    /// same source the §C3 stage-1 check reads — and <see cref="Shared"/>
    /// reports the stale Registry of SPEC §C3 rule 9 without waiting for a
    /// rebuild. Clearing stays on the normal rebuild/publish path only: a
    /// successful later compile reloads the domain, a fresh holder starts
    /// stale and the startup build publishes over it — the signal never
    /// flips a still-published old snapshot back to fresh by itself.
    /// </para>
    /// </remarks>
    internal sealed class RegistryPublication
    {
        // One immutable state object per publish: snapshot+stale move
        // together, so a concurrent reader can only observe a complete
        // pre- or post-build state (SPEC §C3 step 8 "中間状態は見せない").
        private sealed record State(RegistrySnapshot? Snapshot, bool Stale);

        private volatile State _state = new State(null, Stale: true);

        private readonly Func<bool> _scriptCompilationFailed;

        /// <summary>The domain's production publication point.</summary>
        internal static RegistryPublication Shared { get; } =
            new RegistryPublication(static () => EditorUtility.scriptCompilationFailed);

        /// <summary>
        /// A publication holder. <paramref name="scriptCompilationFailed"/>
        /// is the live compile-failure signal <see cref="Stale"/> ORs onto
        /// the stored flag; tests omit it so a holder reports only what a
        /// rebuild stored.
        /// </summary>
        internal RegistryPublication(Func<bool>? scriptCompilationFailed = null) =>
            _scriptCompilationFailed = scriptCompilationFailed ?? (static () => false);

        /// <summary>
        /// The last atomically published snapshot — the "old" Registry kept
        /// while a later build is stale (SPEC §C3 rule 9) — or null until the
        /// first successful build.
        /// </summary>
        internal RegistrySnapshot? Snapshot => _state.Snapshot;

        /// <summary>
        /// True until the first successful build, while the compile
        /// consistency check fails — the stale Registry of SPEC §C3 rule 9 —
        /// and while the live compile-failure signal is set, so a failed
        /// compile in a live domain marks the publication stale without a
        /// rebuild.
        /// </summary>
        internal bool Stale => _state.Stale || _scriptCompilationFailed();

        /// <summary>
        /// Publishes <paramref name="snapshot"/> atomically: one reference
        /// swap makes the complete snapshot — generation ID included —
        /// visible to every consumer and clears the stale mark.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        internal void Publish(RegistrySnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            _state = new State(snapshot, Stale: false);
        }

        /// <summary>
        /// Marks the current state stale without touching the last published
        /// snapshot (SPEC §C3 rule 9: Unity keeps running the old assemblies,
        /// so the old Registry stays in place, marked stale).
        /// </summary>
        internal void MarkStale() =>
            _state = new State(_state.Snapshot, Stale: true);
    }
}
