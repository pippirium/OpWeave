#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using OpWeave.Core;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The thread-safe per-Run status snapshot of SPEC §H2 item 3 /
    /// §0.4: an immutable <see cref="RunSnapshot"/> per Run, replaced on
    /// every relevant §E8 event, that the observation commands
    /// (<c>get_run_status</c>, <c>list_runs</c>) read from any thread —
    /// including the Pipeline worker threads a
    /// <c>MainThreadRequired = false</c> command lands on — while the
    /// Editor's main thread is busy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Writes on the main thread, reads anywhere.</b> The tracker is
    /// constructed and fed on the main thread: every §E8 dispatch is
    /// main-thread synchronous after its journal write, and the event
    /// handler re-reads the durable state through the injected
    /// <paramref name="load"/> seam (the Run Store fold, which reaches
    /// the store's own mutable caches — main-thread-only by SPEC §0.4)
    /// and atomically replaces the immutable snapshot under the gate.
    /// The read members — <see cref="SnapshotOf"/>, <see cref="ListRuns"/>
    /// and <see cref="AwaitChange"/> — never reach the store: they only
    /// take the gate and hand out the last immutable snapshot, so a
    /// background caller can never touch mutable Core state (the
    /// ticket's pass condition and L-01's confirmed constraint).
    /// </para>
    /// <para>
    /// <b>What counts as an update.</b> The snapshot is replaced after
    /// every <see cref="IRunEvents.RunStateChanged"/> and
    /// <see cref="IRunEvents.StepStateChanged"/> — the journal records
    /// those events announce are the whole status projection needs.
    /// <see cref="IRunEvents.LogAppended"/> cannot be an update source:
    /// its payload carries no Run id, so the event cannot be attributed
    /// to a tracked Run (the §F6 read side is the journaled log read of
    /// <c>get_run_logs</c>, which never goes through this cache).
    /// Records that change no journaled state — <c>step_progress</c>,
    /// <c>cancel_requested</c>, <c>resource_*</c>, <c>approval_granted</c>
    /// — publish no event; a <c>cancel_requested</c> journaled through
    /// this service's own <c>cancel_workflow</c> is picked up by the
    /// main-thread <see cref="RefreshNow"/> that command performs.
    /// </para>
    /// <para>
    /// <b>Seeding, retained failures and reseeding (T-W3.2-08).</b>
    /// Construction subscribes first, then seeds every Run the store
    /// already holds through <paramref name="list"/> +
    /// <paramref name="load"/>, so a Run from an earlier session is
    /// observable immediately. The seed is complete only when the
    /// enumeration and every tracked Run's load succeeded — the tracker
    /// never discards a failure the store returned:
    /// </para>
    /// <list type="bullet">
    /// <item>a failed enumeration keeps its diagnostics in
    /// <see cref="_enumerationFailure"/>;</item>
    /// <item>a failed load for a Run the tracker knows but has no
    /// snapshot of keeps its diagnostics on the tracked entry — such an
    /// entry is <i>pending</i>;</item>
    /// <item>a load answering a definitive-absence verdict
    /// (OW-RUN-010 / OW-STORE-003) is the store's proof the Run is gone —
    /// the entry is dropped so the API's own verdicts answer, and the
    /// Run is not retried forever as a stale failure;</item>
    /// <item>a failed refresh of a Run that still holds a snapshot keeps
    /// serving the last durable state — the §E8 feed retries it, as
    /// before.</item>
    /// </list>
    /// <para>
    /// While the seed is incomplete the observation commands report the
    /// retained diagnostic — the store's own code and published meaning,
    /// never a new or re-mapped one: <see cref="ListRuns"/> fails with it
    /// rather than serving an empty or silently partial membership, and
    /// <see cref="SnapshotOf"/> carries it for the affected Run — or for
    /// any Run at all while the enumeration cannot prove membership — so
    /// an unproven runId is never answered as "unknown". The retained
    /// enumeration failure stays the tracker's answer through the whole
    /// recovered pass — while any listed Run's load is still pending no
    /// observation is a partial success or a premature OW-RUN-010. A
    /// runId proven absent by a completed enumeration still answers
    /// OW-RUN-010 (SPEC §J) through the caller.
    /// </para>
    /// <para>
    /// <b>Reseeding.</b> The failed enumeration and each pending Run's
    /// load are retried on the main thread when a later update arrives —
    /// every §E8 dispatch and <see cref="RefreshNow"/> run
    /// <see cref="Reseed"/> first — and when an observation request
    /// arrives: the read members queue <see cref="Reseed"/> onto the main
    /// thread through the injected <paramref name="dispatchToMainThread"/>
    /// (the production binding is <see cref="MainThreadDispatcher.Post"/>),
    /// so the request itself still touches no mutable Core state off the
    /// main thread. Once the retry succeeds the snapshot replaces the
    /// retained failure and the Runs become visible — no domain reload.
    /// </para>
    /// <para>
    /// <b>Waiting.</b> <see cref="AwaitChange"/> sleeps inside the gate
    /// on the calling thread — never on the main thread (L-01: the
    /// long-poll must block the Pipeline worker, not the Editor). Every
    /// replacement wakes all waiters; a waiter whose Run never changes
    /// times out and re-reads the unchanged snapshot — the
    /// <c>waitSeconds ≤ 20</c> contract's "returns on change or within
    /// the duration".
    /// </para>
    /// </remarks>
    internal sealed class RunStatusTracker : IDisposable
    {
        private readonly object _gate = new object();
        private readonly IRunEvents _events;
        private readonly Func<string, Result<RunSnapshot>> _load;
        private readonly Func<Result<IReadOnlyList<RunIndexEntry>>> _list;
        private readonly Action<Action> _dispatch;

        // The tracked Runs in enrolment order: the seed's store order
        // first, then first-seen order for Runs the events introduce —
        // a deterministic order the list_runs page preserves.
        private readonly Dictionary<string, TrackedRun> _runs =
            new Dictionary<string, TrackedRun>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        // Seed bookkeeping (T-W3.2-08): _enumerated flips on the first
        // completed enumeration, _enumerationFailure retains the last
        // failed enumeration's diagnostics — kept published until every
        // load a recovered enumeration introduced has answered — and
        // _pendingLoads counts the tracked Runs no snapshot was ever
        // loaded for. The seed is complete only when the enumeration
        // succeeded and no pending Run remains; while it is incomplete
        // the retained diagnostics are what the read members report.
        private bool _enumerated;
        private IReadOnlyList<Diagnostic>? _enumerationFailure;
        private int _pendingLoads;
        private bool _reseedPosted;
        private bool _disposed;

        /// <summary>
        /// Creates the tracker on the main thread: subscribes to the §E8
        /// dispatcher before seeding from the store, so no event can fall
        /// between subscription and seed.
        /// </summary>
        /// <param name="events">The SPEC §E8 dispatcher
        /// (<see cref="IRunEvents"/>).</param>
        /// <param name="load">The Run Store journal reconstruction —
        /// <c>IRunStore.Load</c> — invoked on the main thread only
        /// (event dispatch, <see cref="RefreshNow"/> and reseeding).</param>
        /// <param name="list">The Run Store listing — <c>IRunStore.List</c>
        /// — invoked on the main thread only, for the seed and every
        /// enumeration retry.</param>
        /// <param name="dispatchToMainThread">The main-thread work queue a
        /// failed seed uses to schedule its retry from observation
        /// requests arriving on any thread — the production binding is
        /// <see cref="MainThreadDispatcher.Post"/>. The queued callback is
        /// the only thing the read members ever hand a thread; it never
        /// runs under the caller's thread.</param>
        /// <exception cref="ArgumentNullException">
        /// Any argument is null.
        /// </exception>
        internal RunStatusTracker(
            IRunEvents events,
            Func<string, Result<RunSnapshot>> load,
            Func<Result<IReadOnlyList<RunIndexEntry>>> list,
            Action<Action>? dispatchToMainThread = null)
        {
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _list = list ?? throw new ArgumentNullException(nameof(list));
            _dispatch = dispatchToMainThread ?? MainThreadDispatcher.Post;
            _events.RunStateChanged += OnRunStateChanged;
            _events.StepStateChanged += OnStepStateChanged;
            Reseed();
        }

        /// <summary>
        /// The immutable snapshot currently held for
        /// <paramref name="runId"/> — or the retained store failure while
        /// the Run cannot be served, or <see langword="default"/> when the
        /// tracker provenly never saw that Run (the caller's OW-RUN-010) —
        /// safe from any thread.
        /// </summary>
        internal RunObservation SnapshotOf(string runId)
        {
            RequestReseed();
            lock (_gate)
            {
                if (_runs.TryGetValue(runId, out var tracked))
                {
                    if (tracked.Snapshot is { } snapshot)
                    {
                        return new RunObservation(
                            new RunStatusView(snapshot, tracked.Version), null);
                    }
                    // A tracked Run without a snapshot reports its
                    // retained load failure — a mid-seed pending entry
                    // falls back to the enumeration failure it was
                    // enrolled under, which the pass also keeps
                    // published as the tracker's answer until every
                    // listed load has answered (both null only inside
                    // the constructor's first seed, before the tracker
                    // can be observed).
                    return new RunObservation(
                        null, tracked.Failure ?? _enumerationFailure);
                }
                // A proven membership decides the verdict: while the
                // enumeration stands failed an unproven runId reports the
                // store's failure, never OW-RUN-010.
                if (_enumerationFailure is { } enumerationFailure)
                {
                    return new RunObservation(null, enumerationFailure);
                }
                return default;
            }
        }

        /// <summary>
        /// Every tracked Run's immutable snapshot in enrolment order —
        /// or the retained store failure while the seed is incomplete,
        /// so a successful listing is never empty or silently partial —
        /// safe from any thread.
        /// </summary>
        internal Result<IReadOnlyList<RunStatusView>> ListRuns()
        {
            RequestReseed();
            lock (_gate)
            {
                if (_enumerationFailure is { } enumerationFailure)
                {
                    return Result<IReadOnlyList<RunStatusView>>.Failure(
                        enumerationFailure);
                }
                var views = new List<RunStatusView>(_order.Count);
                for (var i = 0; i < _order.Count; i++)
                {
                    if (!_runs.TryGetValue(_order[i], out var tracked))
                    {
                        continue;
                    }
                    if (tracked.Snapshot is { } snapshot)
                    {
                        views.Add(new RunStatusView(snapshot, tracked.Version));
                    }
                    else if (tracked.Failure is { } failure)
                    {
                        return Result<IReadOnlyList<RunStatusView>>.Failure(
                            failure);
                    }
                    // A tracked Run without snapshot or failure is a
                    // construction-seed entry still being loaded — it
                    // cannot be observed, so it is simply not listed.
                }
                return Result<IReadOnlyList<RunStatusView>>.Success(views);
            }
        }

        /// <summary>
        /// Sleeps on the calling thread until <paramref name="runId"/>'s
        /// snapshot version differs from <paramref name="sinceVersion"/>
        /// or <paramref name="timeout"/> elapses — the §H2 item 3
        /// long-poll: a snapshot replacement wakes every waiter. Returns
        /// the latest view — possibly unchanged after a timeout — or null
        /// when no snapshot is held for the Run (never waits for a Run
        /// that has none). Safe from any thread.
        /// </summary>
        internal RunStatusView? AwaitChange(
            string runId, long sinceVersion, TimeSpan timeout)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            lock (_gate)
            {
                while (true)
                {
                    if (!_runs.TryGetValue(runId, out var tracked) ||
                        tracked.Snapshot is null)
                    {
                        return null;
                    }
                    if (tracked.Version != sinceVersion || _disposed)
                    {
                        return new RunStatusView(tracked.Snapshot, tracked.Version);
                    }
                    var remaining = timeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        return new RunStatusView(tracked.Snapshot, tracked.Version);
                    }
                    Monitor.Wait(_gate, remaining);
                }
            }
        }

        /// <summary>
        /// Re-reads <paramref name="runId"/>'s durable state into the
        /// snapshot now — the main-thread complement to the event feed,
        /// for a record that publishes no §E8 event (the §E6
        /// <c>cancel_requested</c>). Main thread only: the injected
        /// loader reaches the Run Store.
        /// </summary>
        internal void RefreshNow(string runId)
        {
            Reseed();
            ReloadIntoSnapshot(runId);
        }

        /// <summary>Drops the §E8 subscriptions and wakes every waiter —
        /// domain teardown and test fixtures.</summary>
        public void Dispose()
        {
            _events.RunStateChanged -= OnRunStateChanged;
            _events.StepStateChanged -= OnStepStateChanged;
            lock (_gate)
            {
                _disposed = true;
                Monitor.PulseAll(_gate);
            }
        }

        private void OnRunStateChanged(RunStateChanged change)
        {
            Reseed();
            ReloadIntoSnapshot(change.RunId);
        }

        private void OnStepStateChanged(StepStateChanged change)
        {
            Reseed();
            ReloadIntoSnapshot(change.RunId);
        }

        // Queues one reseed onto the main thread while the seed stands
        // incomplete — the observation-request retry trigger of
        // T-W3.2-08. Called from the read members on any thread: it only
        // enqueues, so the caller still touches no mutable Core state.
        private void RequestReseed()
        {
            bool post;
            lock (_gate)
            {
                post = !_disposed && !_reseedPosted && SeedIncomplete;
                if (post)
                {
                    _reseedPosted = true;
                }
            }
            if (post)
            {
                _dispatch(PostedReseed);
            }
        }

        // The queued main-thread callback the dispatcher drains: the
        // flag clears once the pass runs so a request arriving while it
        // still fails can post the next retry.
        private void PostedReseed()
        {
            try
            {
                Reseed();
            }
            finally
            {
                lock (_gate)
                {
                    _reseedPosted = false;
                }
            }
        }

        // The seed is complete only when the enumeration succeeded and
        // every tracked Run's load did — anything less is a state the
        // observation commands must keep reporting, never serve.
        private bool SeedIncomplete =>
            !_enumerated || _enumerationFailure != null || _pendingLoads > 0;

        // The one enumeration-and-load pass, on the main thread only:
        // retries the failed enumeration first — a successful listing
        // enrolls the Runs the tracker never saw (pending entries that
        // keep the retained failure until their own load answers) — then
        // retries every tracked Run without a snapshot. The retained
        // enumeration failure stays published through the whole pass:
        // while any listed Run's load is still pending an observation
        // request on another thread keeps reporting it, never a partial
        // list and never OW-RUN-010; only once every load has answered
        // is the pass's membership proven and the failure retired.
        // Construction runs the same pass: its first enumeration is
        // unconditional.
        private void Reseed()
        {
            bool enumerate;
            lock (_gate)
            {
                enumerate = !_disposed &&
                    (!_enumerated || _enumerationFailure != null);
            }
            if (enumerate)
            {
                var listed = _list();
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _enumerated = true;
                    if (!listed.Ok)
                    {
                        _enumerationFailure = listed.Diagnostics;
                    }
                    else
                    {
                        // _enumerationFailure is deliberately not cleared
                        // here: the retained failure remains the
                        // tracker's answer until every load this
                        // enumeration introduced has completed below.
                        var retained = _enumerationFailure;
                        for (var i = 0; i < listed.Value!.Count; i++)
                        {
                            var runId = listed.Value[i].RunId;
                            if (!_runs.ContainsKey(runId))
                            {
                                _runs[runId] = new TrackedRun
                                {
                                    Failure = retained,
                                };
                                _order.Add(runId);
                                _pendingLoads++;
                            }
                        }
                    }
                }
                if (!listed.Ok)
                {
                    return;
                }
            }
            // Every tracked Run without a snapshot — newly listed or
            // previously failed — gets its load retried.
            var pending = new List<string>();
            lock (_gate)
            {
                for (var i = 0; i < _order.Count; i++)
                {
                    if (_runs.TryGetValue(_order[i], out var tracked) &&
                        tracked.Snapshot is null)
                    {
                        pending.Add(_order[i]);
                    }
                }
            }
            for (var i = 0; i < pending.Count; i++)
            {
                ReloadIntoSnapshot(pending[i]);
            }
            lock (_gate)
            {
                // Every load the pass owed has answered: the enumeration's
                // retained failure retires and the listed Runs now report
                // their own outcome — a snapshot or their own retained
                // load failure — while a runId absent from the proven
                // membership answers OW-RUN-010 again. A pass that ran no
                // enumeration leaves nothing to retire.
                _enumerationFailure = null;
            }
        }

        // The one write path: re-fold the Run's journal on the main
        // thread and replace the immutable snapshot under the gate,
        // bumping its version and waking the long-polls.
        private void ReloadIntoSnapshot(string runId)
        {
            var loaded = _load(runId);
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                if (!loaded.Ok)
                {
                    ApplyLoadFailure(runId, loaded.Diagnostics);
                    return;
                }
                if (!_runs.TryGetValue(runId, out var tracked))
                {
                    tracked = new TrackedRun();
                    _runs[runId] = tracked;
                    _order.Add(runId);
                }
                else if (tracked.Snapshot is null)
                {
                    _pendingLoads--;
                }
                tracked.Failure = null;
                tracked.Snapshot = loaded.Value!;
                tracked.Version++;
                Monitor.PulseAll(_gate);
            }
        }

        // Applies a failed load under the gate. A definitive-absence
        // verdict (OW-RUN-010 / OW-STORE-003) is the store's proof the
        // Run is gone: the entry is dropped so the API's own deleted /
        // unknown verdicts answer — a Run proven absent is never
        // retained as an unservable failure the listing must keep
        // reporting. Any other failure is retained on the entry while
        // the Run has no snapshot: get_run_status reports it instead of
        // "unknown run" and the next update or observation request
        // retries the load. A Run that still holds a snapshot keeps
        // serving it — the §E8 feed retries the refresh.
        private void ApplyLoadFailure(string runId, IReadOnlyList<Diagnostic> diagnostics)
        {
            if (IsAbsenceVerdict(diagnostics))
            {
                if (_runs.TryGetValue(runId, out var removed))
                {
                    _runs.Remove(runId);
                    _order.Remove(runId);
                    if (removed.Snapshot is null)
                    {
                        _pendingLoads--;
                    }
                    Monitor.PulseAll(_gate);
                }
                return;
            }
            if (!_runs.TryGetValue(runId, out var tracked))
            {
                tracked = new TrackedRun();
                _runs[runId] = tracked;
                _order.Add(runId);
            }
            if (tracked.Snapshot is null)
            {
                if (tracked.Failure is null)
                {
                    _pendingLoads++;
                }
                tracked.Failure = diagnostics;
            }
        }

        private static bool IsAbsenceVerdict(IReadOnlyList<Diagnostic> diagnostics)
        {
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var code = diagnostics[i].Code;
                if (code == ErrorCodes.OW_RUN_010 || code == ErrorCodes.OW_STORE_003)
                {
                    return true;
                }
            }
            return false;
        }

        // The replaceable record behind the gate: the immutable snapshot
        // (or the retained load failure while none exists) plus the
        // version the long-poll compares — swapped together, never
        // mutated in place.
        private sealed class TrackedRun
        {
            internal RunSnapshot? Snapshot;
            internal IReadOnlyList<Diagnostic>? Failure;
            internal long Version;
        }
    }

    /// <summary>
    /// One Run's immutable view out of <see cref="RunStatusTracker"/>: the
    /// journaled <see cref="RunSnapshot"/> and the monotonically
    /// increasing version that marks each replacement — the pair a
    /// <c>get_run_status</c> long-poll captures, waits on and re-reads.
    /// The struct is a copy of two read-only references; the snapshot
    /// behind it is immutable, so a view handed to a background thread
    /// stays a faithful, unchanging picture (SPEC §0.4).
    /// </summary>
    internal readonly struct RunStatusView
    {
        internal RunStatusView(RunSnapshot snapshot, long version)
        {
            Snapshot = snapshot;
            Version = version;
        }

        /// <summary>The immutable journaled state.</summary>
        internal RunSnapshot Snapshot { get; }

        /// <summary>The replacement counter — every newer snapshot of the
        /// same Run carries a larger value.</summary>
        internal long Version { get; }
    }

    /// <summary>
    /// The answer <see cref="RunStatusTracker.SnapshotOf"/> produces for a
    /// Run id (T-W3.2-08): <see cref="View"/> when an immutable snapshot
    /// is held, <see cref="Failure"/> — the retained store diagnostic —
    /// while the Run or the whole membership cannot be served, and
    /// neither when the Run is provenly unknown. At most one member is
    /// set.
    /// </summary>
    internal readonly struct RunObservation
    {
        internal RunObservation(
            RunStatusView? view, IReadOnlyList<Diagnostic>? failure)
        {
            View = view;
            Failure = failure;
        }

        /// <summary>The served snapshot, when one is held.</summary>
        internal RunStatusView? View { get; }

        /// <summary>The retained store failure reported while no
        /// snapshot can be served — the store's own diagnostic, never a
        /// re-mapped code.</summary>
        internal IReadOnlyList<Diagnostic>? Failure { get; }
    }
}
