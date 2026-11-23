#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The step-bound <see cref="IRunResourceRegistry"/> behind the
    /// <see cref="StepContext"/> resource cast (SPEC §E6 item 4): each
    /// <see cref="Acquire"/>/<see cref="Release"/> becomes a Flushed
    /// <c>resource_acquired</c>/<c>resource_released</c> journal record
    /// (<c>required: true</c>, SPEC §F2) and updates the Run's tracked
    /// resource list under the same (kind, data) pairing rule the §F2 fold
    /// applies when it rebuilds <see cref="RunSnapshot.OpenResources"/>.
    /// </summary>
    /// <remarks>
    /// Every tracked resource carries two independent facts:
    /// <see cref="TrackedRunResource.PhysicallyHeld"/> — true until the
    /// Operation or the release seam released the object in this process —
    /// and <see cref="TrackedRunResource.Journal"/>: Unjournaled (the
    /// <c>resource_acquired</c> append was refused), Acquired (journaled)
    /// or ReleaseRecorded (the <c>resource_released</c> is durable). The
    /// journal write precedes the bookkeeping update exactly like the
    /// other step records (write-ahead). A refused append is not thrown
    /// into the Operation's code path: the first failure is recorded on
    /// <see cref="StoreFailure"/> and the run loop takes the SPEC §F1
    /// store-failure path at the attempt boundary — a required record that
    /// never became durable ends the Run Failed, never a silently
    /// untracked resource. A refused <c>resource_acquired</c> still leaves
    /// the Operation holding the object: the entry stays tracked
    /// Unjournaled, released through the seam exactly once at Run end but
    /// never paired with a <c>resource_released</c> record — without the
    /// acquisition record the release would be an unpaired record the §F2
    /// fold refuses. A refused <c>resource_released</c> leaves the entry
    /// Acquired — outstanding in the fold for the §F4 recovery engine —
    /// and the release seam is never called for it again in this process:
    /// the Operation already released the object.
    /// <para>
    /// <b>Two scopes, two queries.</b> Every entry knows which step acquired
    /// it, so <see cref="IsOpen"/> answers the §F5 question of the step this
    /// registry is bound to — SPEC §F5's Completed row needs the same
    /// step's own resource (同じステップの Run リソース), never an
    /// identically named acquisition of a sibling step — while
    /// <see cref="IsHeldByRun"/> and <see cref="Release"/> keep the Run-wide
    /// scope of the §E6 item 4 pairing rule, which is how
    /// <c>unity.prefab.save_contents</c> closes the contents its predecessor
    /// step opened (§G6 item 5).
    /// </para>
    /// </remarks>
    internal sealed class StepResourceRegistry : IRunResourceRegistry
    {
        private readonly string _runId;
        private readonly int _step;
        private readonly List<TrackedRunResource> _resources;
        private readonly Func<JournalRecordType, int?, JsonValue, Result<Unit>> _append;

        /// <summary>
        /// Binds the registry to step <paramref name="step"/> of Run
        /// <paramref name="runId"/>; <paramref name="resources"/> is the
        /// Run-level tracked-resource list the run end releases from and
        /// <paramref name="append"/> is the coordinator's step-record seam
        /// (runId-bound).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/>, <paramref name="resources"/> or
        /// <paramref name="append"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="runId"/> is empty — a scope with no Run would name
        /// no acquisition.
        /// </exception>
        internal StepResourceRegistry(
            string runId,
            int step,
            List<TrackedRunResource> resources,
            Func<JournalRecordType, int?, JsonValue, Result<Unit>> append)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            if (runId.Length == 0)
            {
                throw new ArgumentException(
                    "A Run resource scope must name a Run.", nameof(runId));
            }
            _runId = runId;
            _step = step;
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _append = append ?? throw new ArgumentNullException(nameof(append));
        }

        /// <inheritdoc cref="IRunResourceRegistry.AcquisitionScope"/>
        public string AcquisitionScope => RunResourceScope.Of(_runId, _step);

        /// <summary>
        /// The diagnostics of the first journal append the store refused —
        /// the run loop surfaces the §F1 store-failure path at the attempt
        /// boundary.
        /// </summary>
        internal IReadOnlyList<Diagnostic>? StoreFailure { get; private set; }

        /// <inheritdoc cref="IRunResourceRegistry.Acquire"/>
        public void Acquire(string kind, JsonValue? data)
        {
            CheckKind(kind);
            var appended = _append(
                JournalRecordType.ResourceAcquired, _step,
                JsonValue.From(ResourceData(kind, data)));
            if (!appended.Ok)
            {
                StoreFailure ??= appended.Diagnostics;
            }
            // Whatever the store did, the Operation holds the object now.
            _resources.Add(new TrackedRunResource(
                _step, kind, data,
                appended.Ok
                    ? RunResourceJournalState.Acquired
                    : RunResourceJournalState.Unjournaled));
        }

        /// <inheritdoc cref="IRunResourceRegistry.IsOpen"/>
        /// <exception cref="ArgumentException">
        /// <paramref name="kind"/> is null or empty (the same fail-fast the
        /// two journaling members apply — a query with no kind cannot match
        /// an entry either, but a caller that got here with an empty kind has
        /// a bug worth surfacing).
        /// </exception>
        public RunResourceOpenState IsOpen(string kind, JsonValue? data)
        {
            CheckKind(kind);
            var resources = _resources;
            var undetermined = false;
            for (var i = 0; i < resources.Count; i++)
            {
                var entry = resources[i];
                if (!entry.PhysicallyHeld ||
                    !string.Equals(entry.Kind, kind, StringComparison.Ordinal) ||
                    !SameData(entry.Data, data))
                {
                    continue;
                }
                // SPEC §F5: only the step's own acquisition is its evidence. An
                // entry the §F2 fold rebuilt without an acquiring step cannot
                // be attributed, and §F5 turns that into Unknown rather than
                // into Completed or NotApplied.
                if (entry.Step == _step)
                {
                    return RunResourceOpenState.OpenByThisStep;
                }
                undetermined |= entry.Step is null;
            }
            return undetermined
                ? RunResourceOpenState.Undetermined
                : RunResourceOpenState.NotOpen;
        }

        /// <inheritdoc cref="IRunResourceRegistry.IsHeldByRun"/>
        /// <exception cref="ArgumentException">
        /// <paramref name="kind"/> is null or empty.
        /// </exception>
        public bool IsHeldByRun(string kind, JsonValue? data)
        {
            CheckKind(kind);
            return FindHeld(kind, data) is not null;
        }

        /// <inheritdoc cref="IRunResourceRegistry.Release"/>
        /// <exception cref="ArgumentException">
        /// No held (kind, data) resource matches — the release would journal
        /// an unpaired <c>resource_released</c> the §F2 fold refuses.
        /// </exception>
        public void Release(string kind, JsonValue? data)
        {
            CheckKind(kind);
            var entry = FindHeld(kind, data);
            if (entry is null)
            {
                throw new ArgumentException(
                    "The Run resource was never acquired or is already released.",
                    nameof(kind));
            }
            // The Operation released the object itself: physically gone in
            // this process, so the release seam is never called for it
            // again. An Unjournaled acquisition can never pair with a
            // release record — nothing is journaled for it.
            entry.PhysicallyHeld = false;
            if (entry.Journal != RunResourceJournalState.Acquired)
            {
                return;
            }
            var appended = _append(
                JournalRecordType.ResourceReleased, _step,
                JsonValue.From(ResourceData(kind, data)));
            if (!appended.Ok)
            {
                // The entry stays Acquired — outstanding in the fold for
                // the §F4 recovery engine — while the released object is
                // never re-released in this process.
                StoreFailure ??= appended.Diagnostics;
                return;
            }
            entry.Journal = RunResourceJournalState.ReleaseRecorded;
        }

        // The §F2 pairing rule, mirrored from the fold: the first still-held
        // entry of the list with the same kind and equal data.
        private TrackedRunResource? FindHeld(string kind, JsonValue? data)
        {
            for (var i = 0; i < _resources.Count; i++)
            {
                var entry = _resources[i];
                if (entry.PhysicallyHeld &&
                    string.Equals(entry.Kind, kind, StringComparison.Ordinal) &&
                    SameData(entry.Data, data))
                {
                    return entry;
                }
            }
            return null;
        }

        internal static bool SameData(JsonValue? first, JsonValue? second) =>
            first is null ? second is null : first.Equals(second);

        private static void CheckKind(string kind)
        {
            if (string.IsNullOrEmpty(kind))
            {
                throw new ArgumentException(
                    "A Run resource kind must not be null or empty.", nameof(kind));
            }
        }

        private static JObject ResourceData(string kind, JsonValue? data) =>
            new JObject
            {
                ["kind"] = kind,
                ["data"] = data is null ? JValue.CreateNull() : data.ToToken(),
            };
    }

    /// <summary>
    /// The journal side of one tracked Run resource — the durable half of
    /// the SPEC §E6 item 4 release model.
    /// </summary>
    internal enum RunResourceJournalState
    {
        /// <summary>The required <c>resource_acquired</c> append was
        /// refused — nothing durable can ever pair with a release record,
        /// so none is ever journaled for the entry.</summary>
        Unjournaled,

        /// <summary><c>resource_acquired</c> is durable; no matching
        /// <c>resource_released</c> yet — the fold's open entry.</summary>
        Acquired,

        /// <summary>The pairing <c>resource_released</c> is durable —
        /// the entry is fully closed.</summary>
        ReleaseRecorded,
    }

    /// <summary>
    /// One Run resource tracked by the run loop — an acquired (kind, data)
    /// pair carrying the two independent facts of the SPEC §E6 item 4
    /// release model: <see cref="PhysicallyHeld"/> is true until the
    /// Operation or the release seam released the object in this process
    /// (a release seam failure leaves it set — the object was never
    /// released), and <see cref="Journal"/> is the durable state of its
    /// journal records. <see cref="Step"/> is the index of the step that
    /// acquired it — for an entry rebuilt from the §F2 fold, the <c>step</c>
    /// of its <c>resource_acquired</c> record — or <see langword="null"/>
    /// when that step was never recorded, the one state no step may claim.
    /// </summary>
    internal sealed class TrackedRunResource
    {
        internal TrackedRunResource(
            int? step, string kind, JsonValue? data, RunResourceJournalState journal)
        {
            Step = step;
            Kind = kind;
            Data = data;
            Journal = journal;
            PhysicallyHeld = true;
        }

        internal int? Step { get; }
        internal string Kind { get; }
        internal JsonValue? Data { get; }

        /// <summary>True while this process still holds the object — the
        /// release seam is called for it exactly once, at Run end.</summary>
        internal bool PhysicallyHeld;

        /// <summary>The durable journal state of the acquisition.</summary>
        internal RunResourceJournalState Journal;

        /// <summary>True for an entry the §F2 fold rebuilt rather than an
        /// acquisition this process performed (SPEC §E6 item 4's "released after
        /// a reload"): this session never saw the object, so a release the seam
        /// refuses for it reports that its physical state cannot be proven, not
        /// that an object this session is holding could not be released. Only
        /// the seam's own success or the durable release evidence of the
        /// resource's handler ever settles such an acquisition.</summary>
        internal bool FromRecoveredProjection;
    }

    /// <summary>
    /// The one text form of an acquisition scope — the Run and the step a
    /// <c>resource_acquired</c> belongs to (SPEC §E6 item 4). Both halves of a
    /// session-side ownership binding name the acquisition through this single
    /// function: the step's own registry when it records the object it opened,
    /// and the release handler when it looks that record back up from the Run id
    /// and the <c>step</c> of the acquisition's own journal record, so the two
    /// can never drift apart. A SPEC §0.4 runId carries no ':', so the Run and
    /// the step of the text cannot be confused. The scope is an identity only:
    /// it is never journaled and never part of a record payload, which leaves
    /// the §F2 record format and the (kind, data) pairing rule untouched.
    /// </summary>
    internal static class RunResourceScope
    {
        internal static string Of(string runId, int step) =>
            runId + ":" + step.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
