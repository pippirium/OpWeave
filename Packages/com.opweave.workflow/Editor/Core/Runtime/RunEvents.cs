#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The SPEC §E8 dispatcher behind <see cref="IRunEvents"/>: the
    /// <see cref="RunCoordinator"/> invokes the Publish methods immediately
    /// after each journal write that carries a state change or a log entry,
    /// on the main thread and inside the write-ahead order — every handler
    /// observes the journaled record already durable. Core is called from
    /// the main thread only, so delivery is synchronous with no locking
    /// (SPEC §0.4, AGENTS invariants).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A subscriber's exception is caught per handler, so one throwing
    /// handler never blocks the remaining subscribers and never stops the
    /// Run (SPEC §E8). Each fault is then recorded through the injected
    /// <paramref name="subscriberFault"/> seam — a <c>log</c> journal
    /// record (<c>required: false</c>, Buffered) on the Run the event
    /// belongs to — which is itself a normal log append and therefore
    /// fires <see cref="LogAppended"/> again. A subscriber that also throws
    /// on the fault record cannot recurse without bound: while a fault is
    /// being logged, further subscriber faults never re-enter the seam
    /// (<see cref="_loggingFault"/>) — the dispatch returns them to the
    /// publishing call, which records them in the coordinator's bounded
    /// fault log without republishing.
    /// </para>
    /// <para>
    /// <see cref="PublishRegistryChanges"/> is the SPEC §C3 rule 7 entry
    /// point: the Registry-diff publication calls it when a new snapshot
    /// exposes changes. Its faults carry no Run — the coordinator's seam
    /// journals them against the active Run when one exists, else records
    /// them in its bounded runless fault log and surfaces them through
    /// <see cref="LogAppended"/> as a runless <see cref="RunLogEntry"/>
    /// (a runless event has no journal to record into, and SPEC §E8 still
    /// requires the fault logged).
    /// </para>
    /// </remarks>
    internal sealed class RunEvents : IRunEvents
    {
        private readonly Func<string?, Exception, Result<Unit>>? _subscriberFault;
        private bool _loggingFault;

        /// <summary>
        /// Creates the dispatcher. <paramref name="subscriberFault"/> is the
        /// seam that journals one warning <c>log</c> record per thrown
        /// subscriber exception (SPEC §E8/§F6); null drops faults silently —
        /// the coordinator always wires its own append seam.
        /// </summary>
        internal RunEvents(Func<string?, Exception, Result<Unit>>? subscriberFault = null) =>
            _subscriberFault = subscriberFault;

        /// <inheritdoc cref="IRunEvents.RunStateChanged"/>
        public event Action<RunStateChanged>? RunStateChanged;

        /// <inheritdoc cref="IRunEvents.StepStateChanged"/>
        public event Action<StepStateChanged>? StepStateChanged;

        /// <inheritdoc cref="IRunEvents.LogAppended"/>
        public event Action<RunLogEntry>? LogAppended;

        /// <inheritdoc cref="IRunEvents.RegistryChanged"/>
        public event Action<IReadOnlyList<RegistryChange>>? RegistryChanged;

        /// <summary>
        /// Delivers a Run-state change (internal seam — after the journal
        /// write); the suppressed-fault return contract of
        /// <see cref="PublishLog"/> applies.
        /// </summary>
        internal IReadOnlyList<Exception>? PublishRunState(RunStateChanged change) =>
            Dispatch(RunStateChanged, change, change.RunId);

        /// <summary>
        /// Delivers a step-state change (internal seam — after the journal
        /// write); the suppressed-fault return contract of
        /// <see cref="PublishLog"/> applies.
        /// </summary>
        internal IReadOnlyList<Exception>? PublishStepState(StepStateChanged change) =>
            Dispatch(StepStateChanged, change, change.RunId);

        /// <summary>
        /// Delivers a journaled <c>log</c> record (internal seam — after the
        /// journal write); a null <paramref name="runId"/> carries the
        /// runless §E8 subscriber-fault entry no journal ever held.
        /// Returns the subscriber faults suppressed while a fault record
        /// was already being logged — never republished by the dispatcher,
        /// so a runless fallback publish can record them in the bounded
        /// fault log; null when none were suppressed.
        /// </summary>
        internal IReadOnlyList<Exception>? PublishLog(string? runId, RunLogEntry entry) =>
            Dispatch(LogAppended, entry, runId);

        /// <summary>
        /// Delivers a SPEC §C3 rule 7 Registry diff through the same
        /// mechanism (internal seam — the diff publication calls it after
        /// recording <c>registry-changes.ndjson</c>); the suppressed-fault
        /// return contract of <see cref="PublishLog"/> applies.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="changes"/> is null.</exception>
        internal IReadOnlyList<Exception>? PublishRegistryChanges(IReadOnlyList<RegistryChange> changes)
        {
            if (changes is null)
            {
                throw new ArgumentNullException(nameof(changes));
            }
            // SPEC §C3 rule 7: the caller publishes only AFTER the change
            // record is written to registry-changes.ndjson (T-W2.2-04's
            // persisted diff) — never before it and never for build
            // failures alone.
            return Dispatch(RegistryChanged, changes, null);
        }

        // SPEC §E8: every handler runs in registration order; a throwing
        // handler is caught, never blocks the remaining handlers and never
        // stops the Run. The collected faults are journaled once the whole
        // list ran, so a fault log's own LogAppended delivery cannot
        // interleave mid-dispatch. Returns the faults suppressed while a
        // fault record was already being logged — the caller decides their
        // record; null means no fault or every fault reached the seam.
        private IReadOnlyList<Exception>? Dispatch<T>(Action<T>? handlers, T payload, string? runId)
        {
            if (handlers is null)
            {
                return null;
            }
            List<Exception>? faults = null;
            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(payload);
                }
                catch (Exception exception)
                {
                    (faults ??= new List<Exception>()).Add(exception);
                }
            }
            if (faults is null || _loggingFault)
            {
                return faults;
            }
            ReportFaults(runId, faults);
            return null;
        }

        // The §E8 "logged and ignored": one warning log record per thrown
        // exception, through the coordinator's append seam. Dispatch
        // suppresses this reporting while it runs (the _loggingFault
        // guard) and hands the suppressed faults back to the publisher —
        // a subscriber that throws on the fault record's own LogAppended
        // produces no further fault records — and a throwing seam is
        // contained exactly like a throwing subscriber.
        private void ReportFaults(string? runId, List<Exception> faults)
        {
            if (_subscriberFault is null)
            {
                return;
            }
            _loggingFault = true;
            try
            {
                for (var i = 0; i < faults.Count; i++)
                {
                    try
                    {
                        _subscriberFault(runId, faults[i]);
                    }
                    catch (Exception)
                    {
                        // The fault-log seam is internal code; a failure is
                        // ignored exactly like the subscriber fault itself.
                    }
                }
            }
            finally
            {
                _loggingFault = false;
            }
        }
    }
}
