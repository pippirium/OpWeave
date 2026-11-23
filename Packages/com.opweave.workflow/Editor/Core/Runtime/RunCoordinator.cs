#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The <see cref="IRunCoordinator"/> implementation of SPEC §E2 items 1–4:
    /// accepts Plans into the Run Store, keeps exactly one mutating Run per
    /// project, gates the Queued → Running transition on the Editor state and
    /// the <c>coordinator.lock</c> inter-process exclusion, verifies the used
    /// Operation fingerprints against the current Registry at start (SPEC §1.4)
    /// and empties <c>lock.json</c> before releasing the exclusion at Run end.
    /// </summary>
    /// <remarks>
    /// <para><b>Document hand-off.</b> SPEC §E2's
    /// <c>Accept(CompiledPlan, ActorKind, AcceptOptions)</c> cannot carry the
    /// <see cref="WorkflowDocument"/> the §F1 Run layout needs for its
    /// <c>workflow.json</c> audit copy — a deliberately minimal internal seam:
    /// the execute path (T-W2.1-04 wiring) hands the document it already parsed
    /// to <see cref="BindDocument"/> keyed by planId ahead of the SPEC member
    /// (or uses the one-call internal overload); the public member resolves the
    /// binding and completes the single acceptance path — an accept without a
    /// binding fails with OW-API-001 (SPEC §J, malformed request), never an
    /// exception and never a Run directory without its audit file.</para>
    /// <para><b>Persistence seam.</b> §F1's
    /// <c>IRunStore.CreateRun(runId, plan, doc)</c> signature likewise carries
    /// no actor, so the writer delegate supplied at construction binds to
    /// <see cref="FileRunStore"/>'s in-assembly
    /// <c>CreateRun(runId, plan, doc, actor)</c> overload — exactly the pattern
    /// that type established for the RunAccepted <c>actor</c> member.</para>
    /// <para><b>Human decisions.</b> <see cref="Approve"/>,
    /// <see cref="Reject"/> and <see cref="ResolveReview"/> implement the
    /// human-only decisions of SPEC §E2/§H4 (T-W1.8-09) — never reachable
    /// from an Agent-facing surface — and the §E2 item 5 approval-wait
    /// invalidation cancels a Run whose used Operations changed while it
    /// waited (<see cref="InvalidateStaleApprovals"/>).
    /// <see cref="Cancel"/> journals the §E6 item 2 request only; the
    /// next-Tick processing belongs to T-W1.8-05/-06. Run state beyond
    /// acceptance is held in memory; the journal remains the durable truth
    /// (SPEC §F1), <see cref="Get"/> answers from its reconstruction and
    /// journal-folded recovery is W4.</para>
    /// <para><b>Single writer.</b> Core runs on the main thread only, so no
    /// synchronization is taken (SPEC §0.4, AGENTS invariants).</para>
    /// </remarks>
    internal sealed class RunCoordinator : IRunCoordinator, IDisposable
    {
        /// <summary>The §E2 item 2 waiting-queue capacity.</summary>
        internal const int MaxQueuedRuns = 5;

        private readonly IRunStore _store;
        private readonly Func<string, CompiledPlan, WorkflowDocument, ActorKind, Result<Unit>> _createRun;
        private readonly IEditorState _editorState;
        private readonly IClock _clock;
        private readonly Func<RegistrySnapshot> _currentRegistry;
        private readonly Func<Result<CoordinatorRunLock>> _acquireLock;
        private readonly string _editorSessionId;
        private readonly RunEvents _events;

        // SPEC §F6 SG-08 write-path gate: the settings debugLogging flag as
        // a lazily read predicate — false/omitted drops a debug-level log
        // record before it ever reaches the journal (the Core log store),
        // while info-and-above records are journaled unchanged either way.
        private readonly Func<bool> _debugLogging;

        // The run loop's SPEC §E6 item 4 Run-end resource release, bound at
        // its construction (BindRunEndResourceCleanup): a human review
        // abort ends a NeedsReview Run outside the Tick, so the loop's own
        // terminal cleanup runs through this seam — never a duplicated
        // release path. Null only while no run loop drives this
        // coordinator; nothing in this process can then hold the Run's
        // resources.
        private Func<string, List<Diagnostic>, IReadOnlyList<Diagnostic>?>?
            _runEndResourceRelease;

        // The pre-terminal artifact seam of the §F1 durable completion path
        // (SPEC §F7): the single writer of the terminal run_finished record
        // invokes it exactly once per Run end BEFORE the record, for every
        // terminal outcome — Succeeded, Failed, Cancelled and the
        // store-failed end alike — so a refused Run Store write ends the Run
        // Failed with the OW-STORE-001 diagnostic on the record's summary,
        // never a terminal state whose change list is missing or stale.
        // Bound at wiring time (the UnityAdapter host registers the
        // changes.json writer); null means the Run owns no end-of-run
        // artifacts.
        private Func<string, IReadOnlyList<Diagnostic>?>? _runEndArtifacts;

        // The §E2 signature cannot carry the WorkflowDocument the §F1 Run
        // layout persists; the execute wiring binds it to the Plan's planId
        // ahead of the public Accept (see the class remarks).
        private readonly Dictionary<string, WorkflowDocument> _boundDocuments =
            new Dictionary<string, WorkflowDocument>(StringComparer.Ordinal);

        // Acceptance-ordered in-memory records; the durable truth is each
        // Run's journal (SPEC §F1).
        private readonly List<RunRecord> _runs = new List<RunRecord>();

        // The SPEC §E8 "logged and ignored" record for a subscriber fault
        // that has no Run journal to land in: a runless event with no
        // active Run, or a nested fault suppressed while a fault record
        // was already being logged (journaling it would publish
        // LogAppended again). A bounded in-memory log holding the newest
        // 100 entries in arrival order — the same RunLogEntry model, never
        // a second log type. Entries go in BEFORE the runless LogAppended
        // publish, so the fault is logged even with no listener or a
        // throwing one.
        private const int RunlessFaultLogCapacity = 100;
        private readonly List<RunLogEntry> _runlessFaults = new List<RunLogEntry>();

        /// <summary>
        /// Creates the coordinator. <paramref name="createRun"/> is the
        /// actor-carrying store entry point — the in-assembly
        /// <see cref="FileRunStore"/> overload of <see cref="IRunStore.CreateRun"/>;
        /// <paramref name="currentRegistry"/> supplies the live
        /// <see cref="RegistrySnapshot"/> for the §1.4 start-time fingerprint
        /// check; <paramref name="acquireLock"/> acquires the project-unique
        /// <c>coordinator.lock</c> (the wiring binds
        /// <c>CoordinatorRunLock.TryAcquire</c> over the Run Store root);
        /// <paramref name="editorSessionId"/> is the SessionState GUID stamped
        /// into lock.json and every journal record (SPEC §E2 item 3, §F2).
        /// <paramref name="events"/> is the SPEC §E8 dispatcher — a fresh one
        /// bound to this coordinator's fault-log seam when omitted;
        /// <paramref name="debugLogging"/> is the SPEC §F6 SG-08 write-path
        /// gate, lazily read — omitted means the SPEC default
        /// <c>debugLogging: false</c>.
        /// </summary>
        internal RunCoordinator(
            IRunStore store,
            Func<string, CompiledPlan, WorkflowDocument, ActorKind, Result<Unit>> createRun,
            IEditorState editorState,
            IClock clock,
            Func<RegistrySnapshot> currentRegistry,
            Func<Result<CoordinatorRunLock>> acquireLock,
            string editorSessionId,
            RunEvents? events = null,
            Func<bool>? debugLogging = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _createRun = createRun ?? throw new ArgumentNullException(nameof(createRun));
            _editorState = editorState ?? throw new ArgumentNullException(nameof(editorState));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _currentRegistry = currentRegistry ?? throw new ArgumentNullException(nameof(currentRegistry));
            _acquireLock = acquireLock ?? throw new ArgumentNullException(nameof(acquireLock));
            if (string.IsNullOrEmpty(editorSessionId))
            {
                throw new ArgumentException(
                    "The Editor session id must not be null or empty.", nameof(editorSessionId));
            }
            _editorSessionId = editorSessionId;
            _events = events ?? new RunEvents(AppendSubscriberFaultLog);
            _debugLogging = debugLogging ?? (static () => false);
        }

        /// <inheritdoc cref="IRunCoordinator.Accept"/>
        /// <remarks>
        /// The §E2 signature cannot carry the workflow document the §F1 Run
        /// layout persists, so it arrives through <see cref="BindDocument"/>
        /// ahead of this call (the document hand-off of the class remarks).
        /// An accept for a Plan with no bound document is a malformed request
        /// and fails with OW-API-001 (SPEC §J) as a Result, never an exception.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="plan"/> or <paramref name="options"/> is null.
        /// </exception>
        public Result<RunHandle> Accept(CompiledPlan plan, ActorKind actor, AcceptOptions options)
        {
            if (plan is null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (!_boundDocuments.TryGetValue(plan.PlanId, out var document))
            {
                return Failure(ErrorCodes.Create(
                    ErrorCodes.OW_API_001,
                    "The accept request carries no bound workflow document for the Plan."));
            }
            return AcceptCore(plan, document, actor, options);
        }

        /// <summary>
        /// The document hand-off of the class remarks (SPEC §E2 item 1): binds
        /// the <see cref="WorkflowDocument"/> the execute wiring parsed to a
        /// Plan ahead of the public
        /// <see cref="Accept(CompiledPlan, ActorKind, AcceptOptions)"/> call,
        /// so the §F1 Run layout gets its <c>workflow.json</c> audit copy.
        /// Bindings are keyed by <see cref="CompiledPlan.PlanId"/> and stay
        /// until the coordinator is discarded — a retryable rejection can be
        /// retried without rebinding, and rebinding replaces the document.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="plan"/> or <paramref name="document"/> is null.
        /// </exception>
        internal void BindDocument(CompiledPlan plan, WorkflowDocument document)
        {
            if (plan is null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }
            _boundDocuments[plan.PlanId] = document;
        }

        /// <summary>
        /// The one-call form of the document hand-off for the execute wiring
        /// and tests: binds <paramref name="document"/> to
        /// <paramref name="plan"/> and routes through the SPEC §E2
        /// <see cref="Accept(CompiledPlan, ActorKind, AcceptOptions)"/> — the
        /// single complete acceptance path.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="plan"/>, <paramref name="document"/> or
        /// <paramref name="options"/> is null.
        /// </exception>
        internal Result<RunHandle> Accept(
            CompiledPlan plan, WorkflowDocument document, ActorKind actor, AcceptOptions options)
        {
            BindDocument(plan, document);
            return Accept(plan, actor, options);
        }

        /// <summary>
        /// The run loop's construction-time bind of its SPEC §E6 item 4
        /// Run-end resource release — the same cleanup its own finishes
        /// perform: a human review abort ends a NeedsReview Run outside the
        /// Tick, so the loop's release still runs through this seam before
        /// the terminal record and the lock release — never a duplicated
        /// cleanup. The bound call appends the release phase's diagnostics
        /// to the given list and returns the first refused
        /// <c>resource_released</c> append's diagnostics — the §F1 refusal
        /// — or null. A second bind replaces the first (a rebuilt loop
        /// binds the same seam again).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="releaseRunResources"/> is null.
        /// </exception>
        internal void BindRunEndResourceCleanup(
            Func<string, List<Diagnostic>, IReadOnlyList<Diagnostic>?> releaseRunResources)
        {
            _runEndResourceRelease =
                releaseRunResources ?? throw new ArgumentNullException(nameof(releaseRunResources));
        }

        /// <summary>
        /// The wiring's bind of the pre-terminal artifact finalizer (SPEC
        /// §F7): the single run_finished writer calls it once per Run end,
        /// after the §E6 item 4 release every end path runs first and before
        /// the record itself. The bound call takes the Run id and returns the
        /// refusal diagnostics of a Run Store write that could not be made —
        /// the §F1 signal that turns the end Failed — or null. A second bind
        /// replaces the first.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="finalizeRunArtifacts"/> is null.
        /// </exception>
        internal void BindRunEndArtifactFinalizer(
            Func<string, IReadOnlyList<Diagnostic>?> finalizeRunArtifacts)
        {
            _runEndArtifacts =
                finalizeRunArtifacts ?? throw new ArgumentNullException(nameof(finalizeRunArtifacts));
        }

        // The complete acceptance path behind the SPEC §E2 member; the
        // document is non-null (the binding lookup or the overload's argument
        // checks guarantee it).
        private Result<RunHandle> AcceptCore(
            CompiledPlan plan, WorkflowDocument document, ActorKind actor, AcceptOptions options)
        {
            // SPEC §1.4 row 1: a Run can only start in Edit Mode; the execute
            // request is rejected retryable while that precondition fails.
            if (!_editorState.IsEditMode)
            {
                return Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_001, "The Editor is not in Edit Mode."));
            }
            // SPEC §D7 item 7 / §D0.1: an expectedPlanHash mismatch rejects
            // before anything is persisted.
            if (options.ExpectedPlanHash is { } expected &&
                !string.Equals(expected, plan.PlanHash, StringComparison.Ordinal))
            {
                return Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_003, "The expectedPlanHash does not match the Plan."));
            }
            // SPEC §E2 item 2: at most one mutating Run; queue: true waits in a
            // queue of at most MaxQueuedRuns.
            if (FindActive() is not null && !options.Queue)
            {
                return Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_002, "Another mutating Run is already active."));
            }
            var needsQueueSlot = plan.Approvals.Count == 0;
            if (needsQueueSlot && CountQueued() >= MaxQueuedRuns)
            {
                return Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_002, "The Run waiting queue is full."));
            }
            // SPEC §E2 items 2–3: a non-queued execute is refused while ANY
            // process holds the exclusion — the process-local run list cannot
            // see a second Editor's live owner — so the OS exclusion is probed
            // before anything is persisted. A granted probe is dropped with
            // Dispose, never Release: Release would empty lock.json and
            // destroy the recorded owner state the §F4 recovery reconciles.
            // queue: true keeps the §E2 item 2 waiting-queue behaviour and
            // never probes here.
            if (!options.Queue)
            {
                var probe = _acquireLock();
                if (!probe.Ok || probe.Value is null)
                {
                    return Result<RunHandle>.Failure(
                        probe.Diagnostics.Count == 0
                            ? LockHeldDiagnostics()
                            : probe.Diagnostics);
                }
                probe.Value.Dispose();
            }

            var runId = Identifiers.NewRunId();
            var created = _createRun(runId, plan, document, actor);
            if (!created.Ok)
            {
                return Result<RunHandle>.Failure(created.Diagnostics);
            }
            var state = needsQueueSlot ? RunState.Queued : RunState.AwaitingApproval;
            _runs.Add(new RunRecord(runId, plan, state));
            // SPEC §E8: the store create journaled run_accepted (seq 1)
            // before the in-memory record existed — the event follows the
            // journal write exactly like the funnel's dispatches.
            RecordSuppressedFaults(
                _events.PublishRunState(new RunStateChanged(runId, state)));
            return Result<RunHandle>.Success(new RunHandle(runId, state));
        }

        /// <inheritdoc cref="IRunCoordinator.Approve"/>
        /// <remarks>
        /// The human-only grant of SPEC §E2/§H4 — never reachable from an
        /// Agent-facing surface (SPEC §H4 item 5). Approve IS the
        /// AwaitingApproval → Queued decision: the human approves the Run's
        /// asks together, tied to the planHash (SPEC §H4 items 3–4), so one
        /// call journals one <c>approval_granted</c> record (required,
        /// Flushed) for every approval the Plan recorded — write-ahead,
        /// SPEC §E intro, the same per-record count the §F2 fold applies —
        /// then moves the Run to Queued. A decision on a Run in any other
        /// state asks for a transition the SPEC §E1 table does not contain
        /// — an implementation bug detected by throwing, before anything is
        /// journaled. Before granting, the recorded Operation fingerprints
        /// are re-verified against the current Registry (SPEC §E2 item 5): a
        /// mismatch cancels the Run with OW-RUN-011 — a human never
        /// approves code different from what was validated.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/> or <paramref name="approver"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The Run is not AwaitingApproval — the decision asks for a
        /// transition absent from the SPEC §E1 table, an implementation bug
        /// detected by throwing (§E1: unlisted transitions are exceptions).
        /// </exception>
        public Result<RunHandle> Approve(string runId, string approver)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            if (approver is null)
            {
                throw new ArgumentNullException(nameof(approver));
            }
            var run = Find(runId);
            if (run is null)
            {
                return Failure(ErrorCodes.Create(ErrorCodes.OW_RUN_010, "The Run is unknown."));
            }
            if (run.State != RunState.AwaitingApproval)
            {
                // SPEC §E1: the Run table allows no transition for an
                // approval decision in this state — the inapplicable
                // decision is an implementation bug, detected by throwing
                // before anything is journaled or changed.
                throw new InvalidOperationException(
                    $"Approve on a Run in state {run.State} asks for a transition the SPEC E1 table does not contain.");
            }
            var mismatched = FirstFingerprintMismatch(run.Plan, CurrentRegistry());
            if (mismatched is not null)
            {
                var diagnostics = new List<Diagnostic>();
                if (!CancelAwaitingForFingerprintMismatch(run, mismatched, diagnostics))
                {
                    // SPEC §F1: the cancellation's records never reached the
                    // store — the Run took the store-failure path; report
                    // the refusal, never a fabricated success.
                    return Result<RunHandle>.Failure(diagnostics);
                }
                return Result<RunHandle>.Success(
                    new RunHandle(run.RunId, run.State), diagnostics);
            }
            // SPEC §H4: the asks are approved together — one
            // approval_granted record per approval the Plan recorded, the
            // per-record count the §F2 fold maps back to Queued.
            var approvals = run.Plan.Approvals;
            for (var i = 0; i < approvals.Count; i++)
            {
                var granted = Append(
                    run, JournalRecordType.ApprovalGranted,
                    JsonValue.From(new JObject
                    {
                        ["approver"] = CredentialMask.MaskText(approver),
                    }));
                if (!granted.Ok)
                {
                    // SPEC §F1: the required decision record never reached
                    // the store — the approval never durably happened, so
                    // the Run takes the per-Run store-failure path and the
                    // refusal is still reported to the caller.
                    var diagnostics = new List<Diagnostic>();
                    diagnostics.AddRange(granted.Diagnostics);
                    FailRunOnStoreRefusal(run, granted.Diagnostics, diagnostics);
                    return Result<RunHandle>.Failure(diagnostics);
                }
            }
            // SPEC §E8: the approval_granted records are journaled above;
            // the Run only now reached Queued — the event reports the
            // decided state once, after the journal writes and the
            // in-memory transition.
            TransitionTo(run, RunState.Queued);
            return Result<RunHandle>.Success(new RunHandle(run.RunId, run.State));
        }

        /// <inheritdoc cref="IRunCoordinator.Reject"/>
        /// <remarks>
        /// The human-only rejection of SPEC §E2/§H4 — never reachable from
        /// an Agent-facing surface (SPEC §H4 item 5). The decision is
        /// journaled <c>approval_rejected</c> (required, Flushed) first;
        /// then every step — all still Pending — is journaled
        /// <c>step_skipped</c> (SPEC §E1) and the Run ends Cancelled with
        /// the OW-PERM-003 terminal summary (SPEC §J: approval not
        /// granted). A decision on a Run in any other state asks for a
        /// transition the SPEC §E1 table does not contain — the same
        /// implementation-bug detection <see cref="Approve"/> applies to
        /// an inapplicable decision.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/>, <paramref name="approver"/> or
        /// <paramref name="reason"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The Run is not AwaitingApproval — the decision asks for a
        /// transition absent from the SPEC §E1 table, an implementation bug
        /// detected by throwing (§E1: unlisted transitions are exceptions).
        /// </exception>
        public Result<RunHandle> Reject(string runId, string approver, string reason)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            if (approver is null)
            {
                throw new ArgumentNullException(nameof(approver));
            }
            if (reason is null)
            {
                throw new ArgumentNullException(nameof(reason));
            }
            var run = Find(runId);
            if (run is null)
            {
                return Failure(ErrorCodes.Create(ErrorCodes.OW_RUN_010, "The Run is unknown."));
            }
            if (run.State != RunState.AwaitingApproval)
            {
                // SPEC §E1: the Run table allows no transition for a
                // rejection decision in this state — the inapplicable
                // decision is an implementation bug, detected by throwing
                // before anything is journaled or changed.
                throw new InvalidOperationException(
                    $"Reject on a Run in state {run.State} asks for a transition the SPEC E1 table does not contain.");
            }
            var rejected = Append(
                run, JournalRecordType.ApprovalRejected,
                JsonValue.From(new JObject
                {
                    ["approver"] = CredentialMask.MaskText(approver),
                    ["reason"] = CredentialMask.MaskText(reason),
                }));
            if (!rejected.Ok)
            {
                var refused = new List<Diagnostic>();
                refused.AddRange(rejected.Diagnostics);
                FailRunOnStoreRefusal(run, rejected.Diagnostics, refused);
                return Result<RunHandle>.Failure(refused);
            }
            var diagnostic = ErrorCodes.Create(
                ErrorCodes.OW_PERM_003, "The required approval was rejected.");
            var diagnostics = new List<Diagnostic> { diagnostic };
            if (!FinishAwaitingCancelled(run, diagnostic, diagnostics))
            {
                // SPEC §F1: the rejection's records never reached the store
                // — the Run took the store-failure path; report the refusal,
                // never a fabricated success.
                return Result<RunHandle>.Failure(diagnostics);
            }
            return Result<RunHandle>.Success(
                new RunHandle(run.RunId, run.State), diagnostics);
        }

        /// <summary>
        /// Records the SPEC §E6 item 2 cancel request: a Flushed
        /// <c>cancel_requested</c> journal record that the run loop processes
        /// on the next Tick (T-W1.8-05/-06); the state itself does not change
        /// here. An unknown Run reports OW-RUN-010 and a terminal Run reports
        /// OW-RUN-014 with its state unchanged. A refusal of the required
        /// record is still reported to the caller — and the Run takes the
        /// SPEC §F1 store-failure path: ended Failed exactly once, never
        /// started or continued afterwards.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/> or <paramref name="reason"/> is null.
        /// </exception>
        public Result<RunHandle> Cancel(string runId, string reason)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            if (reason is null)
            {
                throw new ArgumentNullException(nameof(reason));
            }
            var run = Find(runId);
            if (run is null)
            {
                return Failure(ErrorCodes.Create(ErrorCodes.OW_RUN_010, "The Run is unknown."));
            }
            if (IsTerminal(run.State))
            {
                return Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_014, "The Run is in a state that cannot be cancelled."));
            }
            var data = JsonValue.From(
                new JObject { ["reason"] = CredentialMask.MaskText(reason) });
            var appended = Append(run, JournalRecordType.CancelRequested, data);
            if (!appended.Ok)
            {
                // SPEC §F1: the required cancel request never reached the
                // store — the Run cannot safely continue without its
                // journaled request, so it takes the same per-Run
                // store-failure path the loop gives a refused required
                // record: ended Failed exactly once, never started or
                // continued afterwards — a Running Run through the run
                // loop's §E6 item 4 Run-end cleanup. The refusal is still
                // reported to the caller.
                var diagnostics = new List<Diagnostic>();
                diagnostics.AddRange(appended.Diagnostics);
                FailRunOnStoreRefusal(run, appended.Diagnostics, diagnostics);
                return Result<RunHandle>.Failure(diagnostics);
            }
            run.CancelRequested = true;
            run.CancelReason = reason;
            return Result<RunHandle>.Success(new RunHandle(run.RunId, run.State));
        }

        /// <inheritdoc cref="IRunCoordinator.ResolveReview"/>
        /// <remarks>
        /// The human-only NeedsReview resolution of SPEC §E2/§E1 — never
        /// reachable from an Agent-facing surface (SPEC §H4 item 5). The
        /// decision is journaled <c>review_resolved</c> (required, Flushed)
        /// first — the durable truth the §F2 fold maps to the resolved
        /// state. <see cref="ReviewDecision.RetryStep"/> moves the Run back
        /// to Running and marks it for the run loop, which re-arms the
        /// reviewed step as a fresh attempt on the next Tick (the §E0
        /// NeedsReview → Running pair); <see cref="ReviewDecision.Abort"/>
        /// cancels the reviewed step (<c>step_cancelled</c>), skips every
        /// still-Pending step (SPEC §E1) and ends the Run Cancelled. A
        /// decision on a Run in any other state asks for a transition the
        /// SPEC §E1 table does not contain — the same implementation-bug
        /// detection <see cref="Approve"/> applies to an inapplicable
        /// decision.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The Run is not NeedsReview — the decision asks for a transition
        /// absent from the SPEC §E1 table, an implementation bug detected
        /// by throwing (§E1: unlisted transitions are exceptions).
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="decision"/> is outside the declared
        /// <see cref="ReviewDecision"/> values.
        /// </exception>
        public Result<RunHandle> ResolveReview(string runId, ReviewDecision decision)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            var run = Find(runId);
            if (run is null)
            {
                return Failure(ErrorCodes.Create(ErrorCodes.OW_RUN_010, "The Run is unknown."));
            }
            if (run.State != RunState.NeedsReview)
            {
                // SPEC §E1: the Run table allows no transition for a review
                // decision in this state — the inapplicable decision is an
                // implementation bug, detected by throwing before anything
                // is journaled or changed.
                throw new InvalidOperationException(
                    $"ResolveReview on a Run in state {run.State} asks for a transition the SPEC E1 table does not contain.");
            }
            if (decision == ReviewDecision.RetryStep)
            {
                var resolved = Append(
                    run, JournalRecordType.ReviewResolved,
                    JsonValue.From(new JObject { ["decision"] = "retry_step" }));
                if (!resolved.Ok)
                {
                    // SPEC §F1: the required decision record never reached
                    // the store — the resolution never durably happened, so
                    // the Run takes the per-Run store-failure path and the
                    // refusal is still reported to the caller.
                    var refused = new List<Diagnostic>();
                    refused.AddRange(resolved.Diagnostics);
                    FailRunOnStoreRefusal(run, resolved.Diagnostics, refused);
                    return Result<RunHandle>.Failure(refused);
                }
                run.PendingReviewRetry = true;
                TransitionTo(run, RunState.Running);
                return Result<RunHandle>.Success(
                    new RunHandle(run.RunId, RunState.Running));
            }
            if (decision == ReviewDecision.Abort)
            {
                var resolved = Append(
                    run, JournalRecordType.ReviewResolved,
                    JsonValue.From(new JObject { ["decision"] = "abort" }));
                if (!resolved.Ok)
                {
                    var refused = new List<Diagnostic>();
                    refused.AddRange(resolved.Diagnostics);
                    FailRunOnStoreRefusal(run, resolved.Diagnostics, refused);
                    return Result<RunHandle>.Failure(refused);
                }
                var diagnostic = ErrorCodes.Create(
                    ErrorCodes.OW_RUN_005, "The Run was cancelled.");
                var diagnostics = new List<Diagnostic> { diagnostic };
                if (!FinishReviewAbortedRun(run, diagnostic, diagnostics))
                {
                    // SPEC §F1: the abort's records never reached the store
                    // — the Run took the store-failure path; report the
                    // refusal, never a fabricated success.
                    return Result<RunHandle>.Failure(diagnostics);
                }
                return Result<RunHandle>.Success(
                    new RunHandle(run.RunId, run.State), diagnostics);
            }
            // SPEC §E1: a value outside the declared ReviewDecision set
            // asks for a transition the state table does not contain —
            // bug detection by throwing, before anything is journaled.
            throw new ArgumentOutOfRangeException(
                nameof(decision), decision, "Unknown review decision.");
        }

        /// <inheritdoc cref="IRunCoordinator.Get"/>
        /// <remarks>
        /// The journal is the durable truth (SPEC §F1), so the snapshot is
        /// the <see cref="IRunStore.Load"/> reconstruction exactly — a
        /// NeedsReview state included — and null when the store cannot
        /// produce one (an unknown Run or a store failure).
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="runId"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="runId"/> is outside the run identifier grammar —
        /// the argument-error contract of <see cref="IRunStore.Load"/>
        /// (SPEC §0.4).
        /// </exception>
        public RunSnapshot? Get(string runId)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            var loaded = _store.Load(runId);
            return loaded.Ok ? loaded.Value : null;
        }

        /// <summary>
        /// The journal reconstruction of <see cref="IRunStore.Load"/> with
        /// its diagnostics kept — the resume driver's read seam (SPEC §F4
        /// item 2). Unlike <see cref="Get"/> the OW-STORE-002 warnings and
        /// the missing-Run failure reach the caller.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="runId"/> is null.</exception>
        internal Result<RunSnapshot> LoadRun(string runId)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            return _store.Load(runId);
        }

        /// <summary>
        /// The SPEC §D0.3 protected set (T-W4.2-06): every tracked Run
        /// outside the terminal states — Queued, AwaitingApproval,
        /// Running (a step-level Waiting keeps its Run Running),
        /// Suspended and NeedsReview alike — appended to
        /// <paramref name="into"/> with the Plan whose used Operations,
        /// Types, Codecs and defining assemblies the definition-mutation
        /// guard compares mutation targets against. The tracked set is
        /// complete for this purpose: a definition-changing Operation can
        /// only execute inside a mutating Run, which this session's
        /// <c>coordinator.lock</c> exclusion already serializes against
        /// every foreign one (SPEC §E2 item 3) — and a recovered Run whose
        /// Plan could not be decoded stays NeedsReview with its lock
        /// residue intact (<see cref="AdoptRecoveredRun(RunSnapshot, CoordinatorRunLock?)"/>),
        /// which keeps every new mutating Run failing closed.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="into"/> is null.</exception>
        internal void CollectProtectedRuns(List<ProtectedRun> into)
        {
            if (into is null)
            {
                throw new ArgumentNullException(nameof(into));
            }
            for (var i = 0; i < _runs.Count; i++)
            {
                var run = _runs[i];
                if (!IsTerminal(run.State))
                {
                    into.Add(new ProtectedRun(run.RunId, run.Plan));
                }
            }
        }

        /// <summary>
        /// Re-attaches a journal-reconstructed Run to this coordinator —
        /// the Run-level half of the SPEC §F4 resume (T-W1.8-08's driver
        /// rebuilds the step projection on top). The tracked record mirrors
        /// the journaled state exactly: <see cref="RunSnapshot.State"/>,
        /// the next free <c>seq</c> (<see cref="RunSnapshot.LastSeq"/> + 1),
        /// a pending cancel request and the journaled review-retry mark.
        /// </summary>
        /// <remarks>
        /// <para>A Run in a lock-holding state — Running, Suspended,
        /// NeedsReview or a decided-but-unrecorded Cancelled — is adopted
        /// only while holding the <c>coordinator.lock</c> exclusion (SPEC
        /// §E2 item 3): acquisition is tried through the same injected seam
        /// the start gate uses; a live owner makes adoption fail closed.
        /// Inside the held section lock.json is verified before it is
        /// touched: a recorded owner naming a DIFFERENT Run id fails closed
        /// and the residue is left for the takeover work (T-W4.3-01) — never
        /// overwritten. Otherwise the owner record is refreshed to the
        /// current Editor session, the SPEC §F4 item 6 lock hand-over.
        /// A Queued or AwaitingApproval Run holds no exclusion (SPEC §E2).
        /// A Run whose Plan could not be decoded can never be driven: it is
        /// reported NeedsReview, untracked — the lock residue mechanism
        /// keeps new mutating Runs failing closed.</para>
        /// <para>The in-memory state for a decided-but-unrecorded Cancelled
        /// end is Running — the only non-terminal state whose §E1 table
        /// allows the completion the journal already decided.</para>
        /// <para>Adoption is idempotent: an already tracked Run answers
        /// with its current handle and nothing is re-attached.</para>
        /// </remarks>
        /// <remarks>
        /// <para><b>The offered exclusion (SPEC §F4 item 6, T-W4.3-01).</b> The
        /// recovery engine holds <c>coordinator.lock</c> for its whole pass — the
        /// hand-over the §F4 resume performs happens inside that same critical
        /// section, and re-acquiring here could never succeed anyway: the OS
        /// denies a second exclusive handle on the same file to anyone, including
        /// the holder. <paramref name="offeredLock"/> is that already-held handle:
        /// it is used in place of the acquisition seam, verified and refreshed
        /// exactly as an acquired one is, and attached to the adopted Run when the
        /// state holds the exclusion. Ownership is explicit: the coordinator owns
        /// the handle only once it attaches it (then <see cref="EndRun"/> releases
        /// and <see cref="Dispose"/> drops it like any Run-held exclusion); on
        /// every path that does not attach it — a Queued/AwaitingApproval Run, an
        /// undecodable Plan, a verification refusal, an already tracked Run — the
        /// caller keeps ownership and this method never disposes it.</para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        internal Result<RunHandle> AdoptRecoveredRun(
            RunSnapshot snapshot, CoordinatorRunLock? offeredLock = null)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            var runId = snapshot.RunId;
            var existing = Find(runId);
            if (existing is not null)
            {
                return Result<RunHandle>.Success(new RunHandle(runId, existing.State));
            }
            if (snapshot.Plan is null)
            {
                return Result<RunHandle>.Success(
                    new RunHandle(runId, RunState.NeedsReview),
                    ErrorCodes.Create(
                        ErrorCodes.OW_STORE_002,
                        "The Run's Plan cannot be decoded; the Run cannot be reconstructed."));
            }
            var state = snapshot.State;
            var holdsExclusion = !snapshot.Ended &&
                state != RunState.Queued && state != RunState.AwaitingApproval;
            CoordinatorRunLock? held = null;
            if (holdsExclusion)
            {
                // The offered handle is already inside the critical section (see
                // the remarks); an acquired one belongs to this method and every
                // refusal below disposes it.
                CoordinatorRunLock heldLock;
                var ownsLock = offeredLock is null;
                if (offeredLock is { } offered)
                {
                    heldLock = offered;
                }
                else
                {
                    var acquired = _acquireLock();
                    if (!acquired.Ok || acquired.Value is null)
                    {
                        return Result<RunHandle>.Failure(
                            acquired.Diagnostics.Count == 0
                                ? LockHeldDiagnostics()
                                : acquired.Diagnostics);
                    }
                    heldLock = acquired.Value;
                }
                var recorded = heldLock.ReadState();
                if (!recorded.Ok)
                {
                    if (ownsLock)
                    {
                        heldLock.Dispose();
                    }
                    var unreadable = new List<Diagnostic>
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_STORE_002,
                            "The recorded Run lock state cannot be read; the Run is not resumed."),
                    };
                    unreadable.AddRange(recorded.Diagnostics);
                    return Result<RunHandle>.Failure(unreadable);
                }
                if (recorded.Value is { } residue &&
                    !string.Equals(residue.RunId, runId, StringComparison.Ordinal))
                {
                    // Another Run owns the recorded lock state — overwriting
                    // it would destroy that Run's owner record (SPEC §E2).
                    if (ownsLock)
                    {
                        heldLock.Dispose();
                    }
                    return Result<RunHandle>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_RUN_002,
                        "The Run lock records a different Run; the Run is not resumed."));
                }
                // SPEC §F4 item 6: the exclusion passes to the recovered Run —
                // lock.json is refreshed to the current Editor session inside
                // the held critical section.
                var written = heldLock.WriteState(new RunLockState(
                    runId, _editorSessionId, UtcTimestamp.Format(_clock.UtcNow)));
                if (!written.Ok)
                {
                    if (ownsLock)
                    {
                        heldLock.Dispose();
                    }
                    return Result<RunHandle>.Failure(written.Diagnostics);
                }
                held = heldLock;
            }
            // The decided-but-unrecorded Cancelled journal adopts as Running:
            // the §E1 table has no -> Cancelled pair from Queued,
            // AwaitingApproval or a terminal state, and Running -> Cancelled
            // is exactly the completion the journaled decision owed.
            var adopted = snapshot.Ended || state != RunState.Cancelled
                ? state
                : RunState.Running;
            _runs.Add(new RunRecord(runId, snapshot.Plan, adopted)
            {
                NextSeq = snapshot.LastSeq + 1,
                CancelRequested = snapshot.CancelPending,
                CancelReason = snapshot.CancelReason,
                EndJournaled = snapshot.Ended,
                JournaledTerminal = snapshot.Ended ? snapshot.State : (RunState?)null,
                HeldLock = held,
                PendingReviewRetry =
                    (adopted == RunState.Running || adopted == RunState.Suspended) &&
                    ReviewRetryPending(snapshot),
            });
            return Result<RunHandle>.Success(new RunHandle(runId, adopted));
        }

        /// <summary>
        /// The SPEC §E2 item 4 start gate, invoked by the run loop each Tick:
        /// starts the oldest Queued Run only when no Run is
        /// Running/Suspended/NeedsReview, the Editor is in Edit Mode and
        /// neither compiling nor updating, and the coordinator.lock exclusion
        /// can be acquired — and only after the used Operations' recorded
        /// fingerprints still equal the current Registry (SPEC §1.4). Runs
        /// with a pending cancel request are skipped (their cancellation
        /// processing is the loop's). A live foreign lock owner leaves the
        /// head Queued — reported with OW-RUN-002 but not an error of this
        /// call. Once the exclusion is held, lock.json is read before any
        /// write (SPEC §E2 item 3): a recorded owner whose journal verifiably
        /// ended is stale residue and overwritten, while an unfinished
        /// recorded Run, one whose termination cannot be established or a
        /// corrupt lock.json fails closed with OW-RUN-002 — nothing is
        /// overwritten, nothing starts and the recorded Run is left for the
        /// recovery work (T-W1.8-08). A Registry fingerprint mismatch cancels
        /// the head with OW-RUN-011, the only non-Running transition the §E1
        /// table allows from Queued.
        /// </summary>
        /// <returns>
        /// Success with a null value when nothing starts, success with the
        /// started Run's handle (or the OW-RUN-011-cancelled handle plus the
        /// diagnostic), or failure on a persistence error — the Run then ends
        /// Failed on the SPEC §F1 store-failure path and is never retried.
        /// </returns>
        internal Result<RunHandle?> TryStartNext()
        {
            if (FindActive() is not null)
            {
                return Result<RunHandle?>.Success(null);
            }
            var next = FirstStartable();
            if (next is null)
            {
                return Result<RunHandle?>.Success(null);
            }
            if (!_editorState.IsEditMode || _editorState.IsCompiling || _editorState.IsUpdating)
            {
                return Result<RunHandle?>.Success(null);
            }

            var registry = _currentRegistry() ??
                throw new InvalidOperationException(
                    "The registry provider returned no snapshot.");
            var mismatched = FirstFingerprintMismatch(next.Plan, registry);
            if (mismatched is not null)
            {
                return CancelForFingerprintMismatch(next, mismatched);
            }

            var acquired = _acquireLock();
            if (!acquired.Ok || acquired.Value is null)
            {
                // SPEC §E2 item 3: an unobtainable lock means a live owner —
                // the queued Run waits; the denial is reported, not fatal.
                var diagnostics = acquired.Diagnostics.Count == 0
                    ? LockHeldDiagnostics()
                    : acquired.Diagnostics;
                return Result<RunHandle?>.Success(null, diagnostics);
            }
            var heldLock = acquired.Value;
            // SPEC §E2 item 3: inside the held critical section the recorded
            // owner state is read BEFORE any write. Empty means free and a
            // recorded Run whose journal verifiably reached a terminal state
            // is stale residue that may be overwritten; anything else — an
            // unfinished recorded Run, one whose termination cannot be
            // established or an unreadable lock.json — fails closed (the
            // "維持できない場合" (b) and "Editor 再起動後" rules): lock.json is
            // not overwritten, the queued Run does not start, OW-RUN-002 is
            // reported and the residue is left for the §F4 recovery work
            // (T-W1.8-08). Dispose drops only the OS handle — the recorded
            // state stays exactly as found.
            var recorded = heldLock.ReadState();
            if (!recorded.Ok)
            {
                heldLock.Dispose();
                var diagnostics = new List<Diagnostic>
                {
                    ErrorCodes.Create(
                        ErrorCodes.OW_RUN_002,
                        "The recorded Run lock state cannot be read; it is left for recovery."),
                };
                diagnostics.AddRange(recorded.Diagnostics);
                return Result<RunHandle?>.Success(null, diagnostics);
            }
            if (recorded.Value is { } residue)
            {
                // The recorded Run counts as ended only when the SPEC §F1
                // journal fold reconstructs a terminal state (Succeeded,
                // Failed or Cancelled — the §E1 set). IRunStore.Load already
                // classifies a malformed record payload, an unknown required
                // record and every other integrity failure as NeedsReview —
                // never an automatic success — so a non-terminal snapshot, a
                // load failure and a missing Run all fail closed the same way.
                var loaded = LoadRecordedRun(residue.RunId);
                if (!loaded.Ok || loaded.Value is not { } snapshot ||
                    !IsTerminal(snapshot.State))
                {
                    heldLock.Dispose();
                    var diagnostics = new List<Diagnostic>
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_RUN_002,
                            "The recorded Run is unfinished or its end cannot be verified; it is left for recovery."),
                    };
                    diagnostics.AddRange(loaded.Diagnostics);
                    return Result<RunHandle?>.Success(null, diagnostics);
                }
            }
            var written = heldLock.WriteState(new RunLockState(
                next.RunId, _editorSessionId, UtcTimestamp.Format(_clock.UtcNow)));
            if (!written.Ok)
            {
                // Abandoned-state disposal (SPEC §E2): lock.json keeps whatever
                // it held, exactly like an owner dying mid-write; the Run
                // stays Queued and the failure is reported for the next Tick.
                heldLock.Dispose();
                return Result<RunHandle?>.Failure(written.Diagnostics);
            }
            var started = Append(
                next, JournalRecordType.RunStarted,
                JsonValue.From(new JObject { ["reason"] = "start_conditions_met" }));
            if (!started.Ok)
            {
                // The journal shows no RunStarted, so the Run can never run —
                // yet it must not stay Queued for a later Tick's retry: SPEC
                // §F1 ends it Failed once through the per-Run store-failure
                // path. The freshly taken exclusion is still released first:
                // an orderly Release empties the recorded state; if even that
                // fails, Dispose drops the OS handle and leaves the abandoned
                // lock.json residue the §F4 recovery reconciles — never a
                // silently leaked in-process exclusion.
                if (!heldLock.Release().Ok)
                {
                    heldLock.Dispose();
                }
                var diagnostics = new List<Diagnostic>();
                diagnostics.AddRange(started.Diagnostics);
                FailRunOnStoreRefusal(next, started.Diagnostics, diagnostics);
                return Result<RunHandle?>.Failure(diagnostics);
            }
            next.HeldLock = heldLock;
            TransitionTo(next, RunState.Running);
            return Result<RunHandle?>.Success(new RunHandle(next.RunId, RunState.Running));
        }

        /// <summary>
        /// The run loop's end-of-Run seam: journals a Flushed
        /// <c>run_finished</c> record, empties lock.json and releases the
        /// coordinator.lock exclusion (SPEC §E2 item 3) and applies the
        /// terminal transition. The journal write precedes the in-memory
        /// transition (write-ahead, SPEC §E intro); when the release fails
        /// after the terminal record reached the store the Run is
        /// terminal-pending-release — the journaled state stands and the
        /// release is retried through <see cref="ReleaseEndedRunLocks"/>
        /// without writing a duplicate record.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="terminal"/> is not a terminal state or the
        /// transition is absent from the SPEC §E1 table — an implementation
        /// error, thrown as required by §E1.
        /// </exception>
        internal Result<Unit> EndRun(
            string runId, RunState terminal, JsonValue? outputs = null, JsonValue? summary = null)
        {
            var run = Find(runId);
            if (run is null)
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_010, "The Run is unknown."));
            }
            if (!IsTerminal(terminal) || !StateRules.CanTransition(run.State, terminal))
            {
                throw new ArgumentException(
                    $"Illegal RunState transition {run.State} -> {terminal}; the pair is absent from the SPEC E1 table.",
                    nameof(terminal));
            }
            if (!run.EndJournaled)
            {
                var finished = AppendRunFinished(
                    run, terminal,
                    outputs ?? JsonValue.From(new JObject()),
                    summary ?? JsonValue.From(new JObject()));
                if (!finished.Ok)
                {
                    return Result<Unit>.Failure(finished.Diagnostics);
                }
            }
            if (run.HeldLock is { } held)
            {
                var released = held.Release();
                if (!released.Ok)
                {
                    return Result<Unit>.Failure(released.Diagnostics);
                }
                run.HeldLock = null;
            }
            ApplyJournaledEnd(run, terminal);
            return Result<Unit>.Success(Unit.Value);
        }

        /// <summary>
        /// True when the Run's terminal record is already journaled — the
        /// mark of a failed <see cref="EndRun"/> that reached the release
        /// step: only the lock release is still owed (internal seam for the
        /// run loop's fail-safe split between an unwritten and a journaled
        /// <c>run_finished</c>).
        /// </summary>
        internal bool HasJournaledEnd(string runId) => Find(runId)?.EndJournaled ?? false;

        /// <summary>
        /// The terminal state the journaled <c>run_finished</c> record
        /// carries — the outcome the single writer actually made durable,
        /// which a refused artifact finalization may have replaced with
        /// Failed — or null while no terminal record reached the store.
        /// </summary>
        internal RunState? JournaledTerminalOf(string runId) =>
            Find(runId)?.JournaledTerminal;

        /// <summary>
        /// The run loop's journaled-end convergence for a failed
        /// <see cref="EndRun"/> that reached the store: applies the durable
        /// terminal the single writer recorded — never the requested
        /// outcome a refused artifact finalization replaced with Failed.
        /// Returns false for an unknown Run or one without a journaled end.
        /// </summary>
        internal bool ConvergeToJournaledEnd(string runId)
        {
            var run = Find(runId);
            if (run is null || run.JournaledTerminal is not { } durable)
            {
                return false;
            }
            ApplyJournaledEnd(run, durable);
            return true;
        }

        // The in-memory half of a journaled end: the state follows the
        // JOURNALED outcome (<see cref="RunRecord.JournaledTerminal"/>, or
        // <paramref name="fallback"/> when the end predates the writer).
        // The §E1 pair the recorded outcome names applies through
        // TransitionTo like every other transition; a forced SPEC §F1
        // outcome the table never names — a Failed pair exists only from
        // Running, so a NeedsReview, Queued or AwaitingApproval Run whose
        // artifact write the store refused cannot transition — is ASSIGNED
        // exactly like the store-failure end's mark, and the §E8 event still
        // follows the durable record.
        private void ApplyJournaledEnd(RunRecord run, RunState fallback)
        {
            var durable = run.JournaledTerminal ?? fallback;
            if (StateRules.CanTransition(run.State, durable))
            {
                TransitionTo(run, durable);
                return;
            }
            run.State = durable;
            RecordSuppressedFaults(
                _events.PublishRunState(new RunStateChanged(run.RunId, durable)));
        }

        // The single writer of the terminal run_finished record (SPEC
        // §F1/§F2): every end path — the run loop's Succeeded, Failed and
        // Cancelled ends through <see cref="EndRun"/>, the coordinator's own
        // cancelled ends the same way, the §E2 item 5 start-gate
        // cancellation and the SPEC §F1 store-failure end — reaches its
        // durable outcome through this append, and the Run's §F7 artifacts
        // are finalized inside it BEFORE the record, so a refused Run Store
        // write can still turn the end Failed with OW-STORE-001.
        //
        // The bound finalizer runs exactly once per Run end
        // (RunRecord.ArtifactsFinalized) — after the §E6 item 4 release every
        // caller performed first. A non-null return is a Run Store write
        // refusal: an end being written as anything but Failed becomes
        // Failed instead — the summary carries the refusal's diagnostic and
        // the replaced decision stays durable under "diagnostics" — while an
        // end already written as Failed keeps its decided diagnostic and
        // records the refusal alongside it. A refusal of the append itself
        // returns the store diagnostics and writes nothing:
        // RunRecord.EndJournaled guards the record, so nothing ever journals
        // run_finished twice.
        private Result<Unit> AppendRunFinished(
            RunRecord run, RunState terminal, JsonValue outputs, JsonValue summary)
        {
            if (run.EndJournaled)
            {
                return Result<Unit>.Success(Unit.Value);
            }
            var effective = terminal;
            var effectiveOutputs = outputs;
            var effectiveSummary = summary;
            if (!run.ArtifactsFinalized)
            {
                run.ArtifactsFinalized = true;
                var refusal = InvokeRunEndArtifacts(run.RunId);
                if (refusal is not null)
                {
                    effective = RunState.Failed;
                    effectiveOutputs = JsonValue.From(new JObject());
                    effectiveSummary = terminal == RunState.Failed
                        ? WithRefusalEvidence(summary, refusal)
                        : RefusedTerminalSummary(refusal, summary);
                }
            }
            var appended = Append(
                run, JournalRecordType.RunFinished,
                JsonValue.From(new JObject
                {
                    ["state"] = JsonNaming.ToSnakeCase(effective.ToString()),
                    ["outputs"] = effectiveOutputs.ToToken(),
                    ["summary"] = effectiveSummary.ToToken(),
                }));
            if (!appended.Ok)
            {
                return Result<Unit>.Failure(appended.Diagnostics);
            }
            run.EndJournaled = true;
            run.JournaledTerminal = effective;
            return Result<Unit>.Success(Unit.Value);
        }

        // The bound §F7 artifact finalizer as a §F1 refusal list: null when
        // the seam is unwired or the write reached the Run Store; a throwing
        // seam is a wiring defect reported like every other broken injected
        // delegate — still a Failed end, with OW-API-004 rather than the
        // store code the writer itself reports.
        private IReadOnlyList<Diagnostic>? InvokeRunEndArtifacts(string runId)
        {
            if (_runEndArtifacts is null)
            {
                return null;
            }
            try
            {
                return _runEndArtifacts(runId);
            }
            catch (Exception e)
            {
                return new[] { InternalError(e.GetType().FullName) };
            }
        }

        // The run_finished{failed} summary of a refused artifact write: the
        // refusal's own diagnostic is the decided cause; the caller's
        // intended outcome (its code/message) and phase diagnostics stay
        // durable under the additive "diagnostics" member, followed by the
        // refusal's remaining diagnostics.
        private static JsonValue RefusedTerminalSummary(
            IReadOnlyList<Diagnostic> refusal, JsonValue prior)
        {
            var error = refusal.Count == 0
                ? ErrorCodes.Create(
                    ErrorCodes.OW_STORE_001, "The Run artifacts could not be written.")
                : refusal[0];
            var summary = new JObject
            {
                ["code"] = error.Code,
                ["message"] = CredentialMask.MaskText(error.Message),
            };
            var extras = new JArray();
            if (prior.ToToken() is JObject decided)
            {
                if (decided["code"] is JValue { Value: string code } &&
                    decided["message"] is JValue { Value: string message })
                {
                    extras.Add(new JObject
                    {
                        ["code"] = code, ["message"] = message,
                    });
                }
                if (decided["diagnostics"] is JArray recorded)
                {
                    foreach (var entry in recorded)
                    {
                        extras.Add(entry);
                    }
                }
            }
            for (var i = 1; i < refusal.Count; i++)
            {
                extras.Add(new JObject
                {
                    ["code"] = refusal[i].Code,
                    ["message"] = CredentialMask.MaskText(refusal[i].Message),
                });
            }
            if (extras.Count != 0)
            {
                summary["diagnostics"] = extras;
            }
            return JsonValue.From(summary);
        }

        // The same refusal riding on an end already written as Failed: the
        // decided cause stays the record's diagnostic and the refused
        // artifact write joins the durable evidence under "diagnostics".
        private static JsonValue WithRefusalEvidence(
            JsonValue prior, IReadOnlyList<Diagnostic> refusal)
        {
            var summary = prior.ToToken() as JObject ?? new JObject();
            var recorded = summary["diagnostics"] as JArray ?? new JArray();
            for (var i = 0; i < refusal.Count; i++)
            {
                recorded.Add(new JObject
                {
                    ["code"] = refusal[i].Code,
                    ["message"] = CredentialMask.MaskText(refusal[i].Message),
                });
            }
            summary["diagnostics"] = recorded;
            return JsonValue.From(summary);
        }

        private static Diagnostic InternalError(string? detail) =>
            ErrorCodes.Create(
                ErrorCodes.OW_API_004,
                "An internal runner error occurred.",
                hint: detail);

        /// <summary>
        /// The release-only retry of <see cref="EndRun"/> for every Run
        /// whose terminal record is journaled and whose in-memory state
        /// already follows it but whose coordinator.lock exclusion a failed
        /// <see cref="CoordinatorRunLock.Release"/> still holds —
        /// terminal-pending-release. The run loop invokes it each Tick; no
        /// journal record is written (a second <c>run_finished</c> never
        /// is) and a still-failing release reports its diagnostics for the
        /// next Tick's retry.
        /// </summary>
        internal Result<Unit> ReleaseEndedRunLocks()
        {
            var diagnostics = new List<Diagnostic>();
            for (var i = 0; i < _runs.Count; i++)
            {
                var run = _runs[i];
                if (!run.EndJournaled || !IsTerminal(run.State) ||
                    run.HeldLock is not { } held)
                {
                    continue;
                }
                var released = held.Release();
                if (released.Ok)
                {
                    run.HeldLock = null;
                }
                else
                {
                    diagnostics.AddRange(released.Diagnostics);
                }
            }
            return Result<Unit>.Success(Unit.Value, diagnostics);
        }

        /// <summary>The tracked state of a known Run, or null (internal seam for the loop/tests).</summary>
        internal RunState? StateOf(string runId) => Find(runId)?.State;

        /// <summary>
        /// True while this coordinator holds any Run's
        /// <c>coordinator.lock</c> exclusion — including a terminal Run whose
        /// <see cref="CoordinatorRunLock.Release"/> has not succeeded yet. The
        /// recovery engine of SPEC §F4 asks before it takes a handle of its own:
        /// the handle the reload hand-off persisted IS the one a tracked Run
        /// already owns, and a second managed owner of one OS exclusion could
        /// close it through the wrong one (internal seam, T-W4.3-01).
        /// </summary>
        internal bool HoldsAnyRunLock()
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                if (_runs[i].HeldLock is not null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True when this coordinator holds the <c>coordinator.lock</c>
        /// exclusion on behalf of <paramref name="runId"/> — the SPEC §E2 item 2
        /// "実行中" question the Editor wiring asks to decide which Run's id
        /// lock.json still records as this session's owner (internal seam,
        /// T-W4.3-01).
        /// </summary>
        internal bool HoldsRunLock(string runId) => Find(runId)?.HeldLock is not null;

        /// <summary>
        /// True when <paramref name="held"/> IS the exclusion this coordinator
        /// holds for <paramref name="runId"/> — the identity check the recovery
        /// engine uses to learn whether its offered handle was attached by
        /// <see cref="AdoptRecoveredRun(RunSnapshot, CoordinatorRunLock?)"/> and
        /// ownership passed over, or whether the pass still owns it and must drop
        /// it (internal seam, T-W4.3-01).
        /// </summary>
        internal bool HoldsRunLock(string runId, CoordinatorRunLock held) =>
            Find(runId)?.HeldLock == held;

        /// <summary>The Plan of a known Run, or null (internal seam for the loop/tests).</summary>
        internal CompiledPlan? PlanOf(string runId) => Find(runId)?.Plan;

        /// <summary>
        /// The workflow document bound to a known Run's Plan at acceptance,
        /// or null — the run loop's authority for a <see cref="FromInput"/>
        /// port's SPEC §0.6 SG-06 static type (the bound workflow input's
        /// declared type).
        /// </summary>
        internal WorkflowDocument? DocumentOf(string runId) =>
            Find(runId) is { } run &&
                _boundDocuments.TryGetValue(run.Plan.PlanId, out var document)
                    ? document
                    : null;

        /// <summary>
        /// The run loop's write-ahead seam (SPEC §E4): appends one journal
        /// record under the Run's sequence, session and clock, with the SPEC
        /// §F2 table durability of the record's type — Flushed state records
        /// fsync before returning while Buffered records
        /// (<c>step_progress</c>, <c>log</c>) may wait for the next Flushed
        /// append. An unknown Run reports OW-RUN-010.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        internal Result<Unit> AppendRecord(
            string runId, JournalRecordType type, int? step, int? attempt, JsonValue data)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            var run = Find(runId);
            if (run is null)
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_010, "The Run is unknown."));
            }
            var record = new JournalRecord(
                run.NextSeq, UtcTimestamp.Format(_clock.UtcNow), _editorSessionId,
                type, step, attempt, data);
            return AppendJournalRecord(run, record, record.Durability);
        }

        /// <summary>
        /// Applies an in-memory Run transition the journal already recorded
        /// (write-ahead, SPEC §E intro): the record is appended first and this
        /// only follows the recorded state — every non-journaled transition
        /// goes through <see cref="TryStartNext"/> or <see cref="EndRun"/>.
        /// Returns false for an unknown Run.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The pair is absent from the SPEC §E1 table — an implementation
        /// error, thrown as required by §E1.
        /// </exception>
        internal bool Transition(string runId, RunState to)
        {
            var run = Find(runId);
            if (run is null)
            {
                return false;
            }
            TransitionTo(run, to);
            return true;
        }

        // The one place an in-memory Run transition meets the SPEC §E8
        // dispatch: the journaled record IS the event, delivered only after
        // the matching in-memory transition — a synchronous subscriber's
        // StateOf already sees the new state, never the previous one.
        private void TransitionTo(RunRecord run, RunState to)
        {
            run.State = StateRules.RequireTransition(run.State, to);
            RecordSuppressedFaults(
                _events.PublishRunState(new RunStateChanged(run.RunId, to)));
        }

        /// <summary>The active Run's handle, when one is Running/Suspended/NeedsReview.</summary>
        internal RunHandle? ActiveHandle()
        {
            var active = FindActive();
            return active is null ? null : new RunHandle(active.RunId, active.State);
        }

        /// <summary>True while a pending cancel request is recorded for the Run.</summary>
        internal bool HasPendingCancel(string runId) => Find(runId)?.CancelRequested ?? false;

        /// <summary>
        /// The reason text a pending <see cref="Cancel"/> recorded for the
        /// Run, or null — the run loop mirrors it onto the step records it
        /// writes while applying the §E6 item 2 processing.
        /// </summary>
        internal string? CancelReasonOf(string runId) => Find(runId)?.CancelReason;

        /// <summary>
        /// The refusal diagnostics of a Run marked for the SPEC §F1
        /// store-failure end, or null — the run loop finishes it through
        /// the same §E6 item 4 Run-end cleanup as every other terminal
        /// transition on the next Tick (internal seam for the loop,
        /// exactly like <see cref="CancelReasonOf"/>).
        /// </summary>
        internal IReadOnlyList<Diagnostic>? PendingStoreFailureOf(string runId) =>
            Find(runId)?.PendingStoreFailure;

        /// <summary>
        /// True while a journaled <c>review_resolved{retry_step}</c> waits
        /// for the run loop to re-arm the reviewed step as a fresh attempt
        /// (SPEC §E1: NeedsReview → Running is retry_step; internal seam for
        /// the loop, exactly like <see cref="HasPendingCancel"/>).
        /// </summary>
        internal bool HasPendingReviewRetry(string runId) =>
            Find(runId)?.PendingReviewRetry ?? false;

        /// <summary>
        /// Whether the journal's reconstruction leaves a
        /// <c>review_resolved{retry_step}</c> unconsumed: the decision named
        /// a NeedsReview step, and the step record that consumes it — the
        /// retry's own <c>step_started</c> — has not been journaled yet
        /// (SPEC §E1, §F4). A consumed decision never re-arms: once the
        /// retried attempt's record folds, the named step is no longer
        /// NeedsReview.
        /// </summary>
        internal static bool ReviewRetryPending(RunSnapshot snapshot)
        {
            if (snapshot.ReviewRetryStep is not { } retryStep)
            {
                return false;
            }
            var steps = snapshot.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Index == retryStep)
                {
                    return steps[i].State == StepState.NeedsReview;
                }
            }
            return false;
        }

        /// <summary>
        /// Consumes a pending <see cref="HasPendingReviewRetry">review-retry
        /// mark</see> — the run loop calls it once it has re-armed the step.
        /// </summary>
        internal void ClearReviewRetry(string runId)
        {
            var run = Find(runId);
            if (run is not null)
            {
                run.PendingReviewRetry = false;
            }
        }

        /// <summary>
        /// The SPEC §E2 item 5 approval-wait invalidation, invoked by the
        /// run loop each Tick: every AwaitingApproval Run whose Plan's used
        /// Operations no longer match the current Registry — deleted or
        /// changed since the Plan recorded their descriptor and
        /// implementation fingerprints — is ended Cancelled with OW-RUN-011
        /// (approved code is never swapped for different code). Every step —
        /// all still Pending — is journaled <c>step_skipped</c> first
        /// (SPEC §E1). The same check runs inside <see cref="Approve"/> at
        /// the decisive moment; this pass catches a change between
        /// decisions. Runs in every other state are untouched: the start
        /// gate checks Queued Runs (<see cref="TryStartNext"/>) and
        /// recovery checks resumed ones (SPEC §F4).
        /// </summary>
        /// <returns>
        /// Success with the gathered diagnostics — each cancelled Run adds
        /// its OW-RUN-011 — or failure on a persistence error (the affected
        /// Run then takes the SPEC §F1 store-failure path).
        /// </returns>
        internal Result<Unit> InvalidateStaleApprovals()
        {
            var diagnostics = new List<Diagnostic>();
            var failed = false;
            RegistrySnapshot? registry = null;
            for (var i = 0; i < _runs.Count; i++)
            {
                var run = _runs[i];
                if (run.State != RunState.AwaitingApproval)
                {
                    continue;
                }
                registry ??= CurrentRegistry();
                var mismatched = FirstFingerprintMismatch(run.Plan, registry);
                if (mismatched is not null)
                {
                    // A refused record ends that Run on the SPEC §F1
                    // store-failure path and the pass reports failure;
                    // unaffected Runs still run their invalidation check.
                    failed |= !CancelAwaitingForFingerprintMismatch(
                        run, mismatched, diagnostics);
                }
            }
            return failed
                ? Result<Unit>.Failure(diagnostics)
                : Result<Unit>.Success(Unit.Value, diagnostics);
        }

        /// <summary>
        /// The §E6 item 2 next-Tick processing for Runs the loop does not
        /// drive step-by-step: every Queued or AwaitingApproval Run with a
        /// journaled <c>cancel_requested</c> is ended Cancelled — its Plan
        /// steps were never started, so each is journaled
        /// <c>step_skipped</c> ("Pending → Skipped at Run end", SPEC §E1)
        /// before the terminal record. A Suspended Run's pending cancel is
        /// deliberately left pending: Operation calls are unsafe while the
        /// Editor-state gate has the Run suspended, so the request is
        /// processed once recovery resumes the Run (T-W1.8-08). A
        /// NeedsReview Run's cancel likewise waits for the human decision
        /// path (T-W1.8-09).
        /// </summary>
        /// <remarks>
        /// A required record the store refuses takes the SPEC §F1
        /// store-failure path for THAT Run — the same handling the run
        /// loop gives a refused required record: normal processing stops,
        /// no further step records are attempted and the Run ends Failed
        /// with the store diagnostic, so it can never reach
        /// <see cref="TryStartNext"/> and no journaled
        /// <c>step_skipped</c> is appended a second time.
        /// </remarks>
        internal Result<Unit> ProcessInactiveCancels()
        {
            var diagnostics = new List<Diagnostic>();
            for (var i = 0; i < _runs.Count; i++)
            {
                var run = _runs[i];
                if (!run.CancelRequested ||
                    (run.State != RunState.Queued && run.State != RunState.AwaitingApproval))
                {
                    continue;
                }
                var steps = run.Plan.Steps;
                IReadOnlyList<Diagnostic>? refused = null;
                for (var s = 0; s < steps.Count; s++)
                {
                    var skipped = AppendRecord(
                        run.RunId, JournalRecordType.StepSkipped, s, null,
                        JsonValue.From(new JObject { ["reason"] = "unreached" }));
                    if (!skipped.Ok)
                    {
                        refused = skipped.Diagnostics;
                        break;
                    }
                }
                if (refused is not null)
                {
                    // SPEC §F1: the required StepSkipped never reached the
                    // store — the journaled skips stand, no further step
                    // records are attempted (the run loop's recordSkips:
                    // false policy) and the Run ends Failed with the store
                    // diagnostic, never the cancelled outcome it was
                    // interrupted inside.
                    diagnostics.AddRange(refused);
                    FailRunOnStoreRefusal(run, refused, diagnostics);
                    continue;
                }
                var diagnostic = ErrorCodes.Create(
                    ErrorCodes.OW_RUN_005, "The Run was cancelled.");
                var ended = EndRun(
                    run.RunId, RunState.Cancelled,
                    JsonValue.From(new JObject()),
                    JsonValue.From(new JObject
                    {
                        ["code"] = diagnostic.Code,
                        ["message"] = CredentialMask.MaskText(diagnostic.Message),
                    }));
                if (!ended.Ok)
                {
                    diagnostics.AddRange(ended.Diagnostics);
                    if (run.EndJournaled)
                    {
                        // The FinishRun tail applied to a Run the loop
                        // does not drive: the terminal record is durable,
                        // so the in-memory state follows the journaled
                        // outcome.
                        ApplyJournaledEnd(run, RunState.Cancelled);
                    }
                    else
                    {
                        // The terminal record could not be written: the
                        // refusal takes the centralized §F1 store-failure
                        // path like every other refused required append —
                        // one run_finished{failed} attempt with the store
                        // diagnostic, then the in-memory Failed mark, so
                        // the Run never spins in Queued, never reaches
                        // TryStartNext and never rewrites the journaled
                        // skips. A refusal of that record is still
                        // reported and left for the §F4 recovery engine.
                        FailRunOnStoreRefusal(run, ended.Diagnostics, diagnostics);
                    }
                    continue;
                }
                diagnostics.Add(diagnostic);
            }
            return Result<Unit>.Success(Unit.Value, diagnostics);
        }

        // The SPEC §F1 store-failure end for a Run that cannot safely
        // continue after a required record the store refused — shared by
        // every site that cannot route the refusal through a RunExecution
        // the run loop drives: ProcessInactiveCancels' refused
        // step_skipped and unwritten terminal record, Cancel's refused
        // cancel_requested, TryStartNext's refused run_started,
        // CancelForFingerprintMismatch's refused terminal record, the
        // human-decision helpers' refused decision, step, release and
        // terminal records (Approve, Reject, ResolveReview,
        // InvalidateStaleApprovals) and — through the runId-keyed
        // overload — the run loop's own refused terminal append inside
        // FinishRun.
        //
        // A Running, Suspended or NeedsReview Run may hold SPEC §E6 item 4
        // open Run resources, so it is only MARKED here: the first
        // refusal's diagnostics are kept on the record and the forced
        // Failed outcome is assigned in memory at once — the Run is
        // never driven further, never reaches TryStartNext — while the
        // run loop finishes it on the next Tick through the same Run-end
        // cleanup as every other terminal transition: still-open
        // resources are released (resource_released journaled where the
        // store accepts it, the unreleased kept durable for the §F4
        // recovery engine) BEFORE the single terminal record, ahead of
        // any further Operation call. The in-memory mark is assigned
        // rather than routed through StateRules.RequireTransition
        // exactly like the direct end below — the §E1 table carries no
        // Queued/AwaitingApproval/Suspended/NeedsReview -> Failed pair
        // and this outcome is forced by the refused append, not a
        // transition the runner chose.
        //
        // A Queued or AwaitingApproval Run holds no resources and takes
        // the direct end: the terminal run_finished{failed} is journaled
        // once — RunRecord.EndJournaled guards the record — and the Run
        // is marked Failed in memory so it can never reach TryStartNext,
        // a later Tick's cancel processing or further step work; the §F2
        // fold maps the journaled record to the same state, so the
        // in-memory projection follows the journal. When the terminal
        // record itself cannot be written the Run is still marked Failed
        // — never left spinning in Queued or Running — and the journal
        // is left for the §F4 recovery engine, exactly like FinishRun's
        // unwritten-terminal tail. A Run still holding the exclusion (a
        // Running Run whose cancel request was refused) keeps its
        // HeldLock: once the end is journaled the still-held release is
        // retried through ReleaseEndedRunLocks — the same
        // terminal-pending-release seam EndRun establishes.
        private void FailRunOnStoreRefusal(
            RunRecord run, IReadOnlyList<Diagnostic> errors, List<Diagnostic> diagnostics)
        {
            if (run.PendingStoreFailure is not null ||
                run.State == RunState.Running || run.State == RunState.Suspended ||
                run.State == RunState.NeedsReview)
            {
                // The marked Run is finished by the run loop's Run-end
                // cleanup on the next Tick — the terminal record never
                // precedes the §E6 item 4 resource release.
                run.PendingStoreFailure ??= errors;
                run.State = RunState.Failed;
                return;
            }
            var error = errors.Count == 0 ? null : errors[0];
            AppendStoreFailureEnd(
                run,
                JsonValue.From(error is null
                    ? new JObject()
                    : new JObject
                    {
                        ["code"] = error.Code,
                        ["message"] = CredentialMask.MaskText(error.Message),
                    }),
                diagnostics);
        }

        /// <summary>
        /// The run loop's entry into the per-Run store-failure end for a
        /// Run it drives: a terminal append FinishRun could not journal
        /// takes the same SPEC §F1 path as every other refused required
        /// record — the Running Run is marked and the next Tick finishes
        /// it through the §E6 item 4 cleanup ahead of
        /// <see cref="EndStoreFailedRun"/>'s single run_finished{failed}
        /// and the still-held exclusion's release through
        /// <see cref="ReleaseEndedRunLocks"/>; a refusal of that record
        /// is reported and left for the §F4 recovery engine. An unknown
        /// Run is ignored.
        /// </summary>
        internal void FailRunOnStoreRefusal(
            string runId, IReadOnlyList<Diagnostic> errors, List<Diagnostic> diagnostics)
        {
            var run = Find(runId);
            if (run is not null)
            {
                FailRunOnStoreRefusal(run, errors, diagnostics);
            }
        }

        /// <summary>
        /// The deferred terminal half of the SPEC §F1 store-failure end
        /// for a marked Run — the run loop invokes it AFTER releasing the
        /// Run's still-open §E6 item 4 resources, so every
        /// resource_released the store accepted is journaled before the
        /// single terminal record. The mark is consumed: the end is
        /// journaled once and never retried — a refused append leaves the
        /// journal for the §F4 recovery engine (T-W1.8-08) exactly like
        /// the direct path's unwritten-terminal tail — and a still-held
        /// exclusion is left for <see cref="ReleaseEndedRunLocks"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="summary"/> is null.</exception>
        internal void EndStoreFailedRun(
            string runId, JsonValue summary, List<Diagnostic> diagnostics)
        {
            if (summary is null)
            {
                throw new ArgumentNullException(nameof(summary));
            }
            var run = Find(runId);
            if (run is null)
            {
                return;
            }
            run.PendingStoreFailure = null;
            AppendStoreFailureEnd(run, summary, diagnostics);
        }

        // The terminal half of the store-failure end: the single
        // run_finished{failed} is journaled once — RunRecord.EndJournaled
        // guards the record — and the forced Failed outcome is assigned,
        // never routed through StateRules.RequireTransition (the §E1
        // table's only -> Failed pair is Running -> Failed). Shared by
        // the direct end of a resource-free Run and the loop-driven
        // finish of a marked one.
        private void AppendStoreFailureEnd(
            RunRecord run, JsonValue summary, List<Diagnostic> diagnostics)
        {
            var journaledHere = false;
            if (!run.EndJournaled)
            {
                var appended = AppendRunFinished(
                    run, RunState.Failed,
                    JsonValue.From(new JObject()), summary);
                if (!appended.Ok)
                {
                    diagnostics.AddRange(appended.Diagnostics);
                }
                else
                {
                    journaledHere = true;
                }
            }
            run.State = RunState.Failed;
            if (journaledHere)
            {
                // SPEC §E8: the forced Failed outcome is only an event once
                // its run_finished{failed} record is durable — after the
                // in-memory transition, like every state dispatch.
                RecordSuppressedFaults(_events.PublishRunState(
                    new RunStateChanged(run.RunId, RunState.Failed)));
            }
        }

        /// <summary>
        /// Drops every OS exclusion this coordinator holds — the
        /// abandoned-state semantics of a crash or teardown
        /// (<see cref="CoordinatorRunLock.Dispose"/>): lock.json is not
        /// emptied, so the recorded owner state stays for the §F4 recovery
        /// engine. The orderly per-Run end is <see cref="EndRun"/>.
        /// </summary>
        public void Dispose()
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                var run = _runs[i];
                run.HeldLock?.Dispose();
                run.HeldLock = null;
            }
        }

        // The SPEC §E2 item 5 cancellation of an AwaitingApproval Run: the
        // OW-RUN-011 diagnostic leads the reported diagnostics and rides on
        // the terminal record's summary; the step skips and the terminal
        // record go through FinishAwaitingCancelled, whose outcome decides
        // whether the cancellation's records were durably written.
        private bool CancelAwaitingForFingerprintMismatch(
            RunRecord run, PlanStep step, List<Diagnostic> diagnostics)
        {
            var diagnostic = ErrorCodes.Create(
                ErrorCodes.OW_RUN_011,
                "An Operation used by the Plan was deleted or changed since the Plan was validated.",
                nodeId: step.NodeId);
            diagnostics.Add(diagnostic);
            return FinishAwaitingCancelled(run, diagnostic, diagnostics);
        }

        // The cancelled end of an inactive (Queued or AwaitingApproval) Run
        // decided by a human rejection or the §E2 item 5 invalidation:
        // every Plan step — all still Pending — is journaled step_skipped
        // (SPEC §E1) before the terminal run_finished carries the deciding
        // diagnostic's code; the in-memory transition follows the journaled
        // record. A refused step record or terminal append takes the SPEC
        // §F1 store-failure path: the Run ends Failed once, never the
        // interrupted Cancelled outcome and never a retrying Queued spin.
        // Returns true only when the decision's records were durably
        // written — the terminal record journaled (a still-held exclusion
        // is retried by ReleaseEndedRunLocks); false reports the §F1
        // store-failure end so the caller returns the refusal, never a
        // fabricated success.
        private bool FinishAwaitingCancelled(
            RunRecord run, Diagnostic diagnostic, List<Diagnostic> diagnostics)
        {
            var steps = run.Plan.Steps;
            IReadOnlyList<Diagnostic>? refused = null;
            for (var s = 0; s < steps.Count; s++)
            {
                var skipped = AppendRecord(
                    run.RunId, JournalRecordType.StepSkipped, s, null,
                    JsonValue.From(new JObject { ["reason"] = "unreached" }));
                if (!skipped.Ok)
                {
                    refused = skipped.Diagnostics;
                    break;
                }
            }
            if (refused is not null)
            {
                diagnostics.AddRange(refused);
                FailRunOnStoreRefusal(run, refused, diagnostics);
                return false;
            }
            var ended = EndRun(
                run.RunId, RunState.Cancelled,
                JsonValue.From(new JObject()),
                JsonValue.From(new JObject
                {
                    ["code"] = diagnostic.Code,
                    ["message"] = CredentialMask.MaskText(diagnostic.Message),
                }));
            if (!ended.Ok)
            {
                diagnostics.AddRange(ended.Diagnostics);
                if (run.EndJournaled)
                {
                    // The terminal record IS durable — the in-memory state
                    // follows the journaled outcome (a still-held
                    // exclusion is retried by ReleaseEndedRunLocks); the
                    // decision's records were written, so the outcome
                    // stands.
                    ApplyJournaledEnd(run, RunState.Cancelled);
                    return true;
                }
                // The terminal record could not be written: the refusal
                // takes the centralized §F1 store-failure path like every
                // other refused required append — one run_finished{failed}
                // attempt with the store diagnostic, then the in-memory
                // Failed mark, so the Run is never left in
                // AwaitingApproval to be approved or invalidated again.
                // A refusal of that record is still reported and left
                // for the §F4 recovery engine.
                FailRunOnStoreRefusal(run, ended.Diagnostics, diagnostics);
                return false;
            }
            return true;
        }

        // The cancelled end of a NeedsReview Run after a journaled
        // review_resolved{abort} (SPEC §E1/§E0): the step consequences read
        // the journal's own fold — the store's Load reconstruction, the
        // durable truth — so the step recorded NeedsReview is journaled
        // step_cancelled, every still-Pending step step_skipped and every
        // already-terminal step left exactly as recorded; the run loop's
        // §E6 item 4 release then frees the still-open Run resources
        // before the terminal run_finished carries the OW-RUN-005
        // cancellation summary and the lock release. A Load that cannot
        // reconstruct takes the SPEC §F1
        // store-failure path instead: ending the Run Cancelled without the
        // step consequences would record a terminal state whose Pending
        // steps were never journaled step_skipped (SPEC §E1: Pending steps
        // become Skipped when the Run ends), so the Run is marked Failed
        // with the load diagnostics and the run loop finishes it — never
        // the Cancelled outcome. A refused step record or terminal append
        // takes the same §F1 path. Returns true only when the decision's
        // records were durably written — the terminal record journaled (a
        // still-held exclusion is retried by ReleaseEndedRunLocks); false
        // reports the §F1 store-failure end so the caller returns the
        // refusal, never a fabricated success.
        private bool FinishReviewAbortedRun(
            RunRecord run, Diagnostic diagnostic, List<Diagnostic> diagnostics)
        {
            var loaded = LoadRecordedRun(run.RunId);
            if (!loaded.Ok || loaded.Value is not { } snapshot)
            {
                // SPEC §F1: the journal cannot be reconstructed, so the
                // step consequences can never be decided — ending the Run
                // Cancelled now would record a terminal state whose Pending
                // steps were never journaled step_skipped (SPEC §E1). The
                // Run takes the per-Run store-failure path with the load
                // diagnostics: marked Failed once, finished by the run
                // loop's §E6 item 4 cleanup.
                diagnostics.AddRange(loaded.Diagnostics);
                FailRunOnStoreRefusal(run, loaded.Diagnostics, diagnostics);
                return false;
            }
            var recorded = new Dictionary<int, StepSnapshot>();
            foreach (var step in snapshot.Steps)
            {
                recorded[step.Index] = step;
            }
            var steps = run.Plan.Steps;
            IReadOnlyList<Diagnostic>? refused = null;
            for (var i = 0; i < steps.Count; i++)
            {
                var state = recorded.TryGetValue(i, out var snap)
                    ? snap.State
                    : StepState.Pending;
                Result<Unit> appended;
                if (state == StepState.NeedsReview)
                {
                    appended = AppendRecord(
                        run.RunId, JournalRecordType.StepCancelled, i, snap!.Attempt,
                        JsonValue.From(new JObject { ["reason"] = "aborted" }));
                }
                else if (state == StepState.Pending)
                {
                    appended = AppendRecord(
                        run.RunId, JournalRecordType.StepSkipped, i, null,
                        JsonValue.From(new JObject { ["reason"] = "unreached" }));
                }
                else
                {
                    continue;
                }
                if (!appended.Ok)
                {
                    refused = appended.Diagnostics;
                    break;
                }
            }
            if (refused is not null)
            {
                diagnostics.AddRange(refused);
                FailRunOnStoreRefusal(run, refused, diagnostics);
                return false;
            }
            // SPEC §E6 item 4: a NeedsReview Run may still hold open Run
            // resources — the abort ends it outside the Tick, so the run
            // loop's own terminal release runs here, before the terminal
            // record and the lock release (the Tick never drives the Run
            // again once it is Cancelled); the phase's diagnostics ride on
            // the terminal record's summary exactly like FinishRun's. A
            // refused resource_released takes the §F1 store-failure path
            // like every other refused required append: marked Failed once
            // and finished by the run loop's cleanup on the next Tick.
            var releaseDiagnostics = new List<Diagnostic>();
            var releaseRefusal =
                _runEndResourceRelease?.Invoke(run.RunId, releaseDiagnostics);
            diagnostics.AddRange(releaseDiagnostics);
            if (releaseRefusal is not null)
            {
                FailRunOnStoreRefusal(run, releaseRefusal, diagnostics);
                return false;
            }
            var ended = EndRun(
                run.RunId, RunState.Cancelled,
                JsonValue.From(new JObject()),
                JsonValue.From(RunLoop.TerminalSummary(diagnostic, releaseDiagnostics)));
            if (!ended.Ok)
            {
                diagnostics.AddRange(ended.Diagnostics);
                if (run.EndJournaled)
                {
                    // The terminal record IS durable — the in-memory state
                    // follows the journaled outcome (a still-held
                    // exclusion is retried by ReleaseEndedRunLocks); the
                    // decision's records were written, so the outcome
                    // stands.
                    ApplyJournaledEnd(run, RunState.Cancelled);
                    return true;
                }
                // The terminal record could not be written: the Run takes
                // the centralized SPEC §F1 store-failure path like every
                // other refused required append — marked Failed once so
                // the run loop finishes it through the §E6 item 4 cleanup
                // and the still-held exclusion is retried by
                // ReleaseEndedRunLocks once the end is journaled.
                FailRunOnStoreRefusal(run, ended.Diagnostics, diagnostics);
                return false;
            }
            return true;
        }

        // The injected live Registry snapshot — the §1.4 fingerprint checks
        // of TryStartNext, Approve and InvalidateStaleApprovals share this
        // non-null guarantee.
        private RegistrySnapshot CurrentRegistry() =>
            _currentRegistry() ??
                throw new InvalidOperationException(
                    "The registry provider returned no snapshot.");

        /// <summary>
        /// The Editor session id stamped into lock.json and every journal
        /// record — the value SPEC §F4 item 4's restart check compares with
        /// a reconstructed journal's recorded <c>session</c>.
        /// </summary>
        internal string EditorSessionId => _editorSessionId;

        /// <summary>
        /// The SPEC §F4 item 3 (R4) resume-time check — the remaining-steps
        /// half of the §1.4 fingerprint comparisons: every Plan step the
        /// resumed Run may still drive must still resolve in the current
        /// Registry with the descriptor and implementation fingerprints the
        /// Plan recorded (SPEC §C3 rule 6, §D7). A step the journal already
        /// closed (Succeeded, Failed, Cancelled or Skipped) never runs
        /// again, so only the remaining — non-terminal — steps are compared;
        /// removal, change and failed registration are the same verdict,
        /// standard and custom alike. Returns the first mismatched step, or
        /// null when every remaining step still matches.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="snapshot"/> is null or its Plan is.
        /// </exception>
        internal PlanStep? FirstRemainingFingerprintMismatch(RunSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (snapshot.Plan is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            var closed = new HashSet<int>();
            var steps = snapshot.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].State is StepState.Succeeded or StepState.Failed or
                    StepState.Cancelled or StepState.Skipped)
                {
                    closed.Add(steps[i].Index);
                }
            }
            return FirstFingerprintMismatch(snapshot.Plan, CurrentRegistry(), closed);
        }

        private Result<RunHandle?> CancelForFingerprintMismatch(RunRecord run, PlanStep step)
        {
            var diagnostic = ErrorCodes.Create(
                ErrorCodes.OW_RUN_011,
                "An Operation used by the Plan was deleted or changed since the Plan was validated.",
                nodeId: step.NodeId);
            if (!run.EndJournaled)
            {
                var finished = AppendRunFinished(
                    run, RunState.Cancelled,
                    JsonValue.From(new JObject()),
                    JsonValue.From(new JObject
                    {
                        ["code"] = diagnostic.Code,
                        ["message"] = CredentialMask.MaskText(diagnostic.Message),
                    }));
                if (!finished.Ok)
                {
                    // SPEC §F1: the required terminal record never reached the
                    // store — the Run ends Failed through the per-Run
                    // store-failure path (one terminal run_finished{failed}
                    // attempt, then the in-memory Failed mark), never left
                    // Queued for the start gate.
                    var diagnostics = new List<Diagnostic>();
                    diagnostics.AddRange(finished.Diagnostics);
                    FailRunOnStoreRefusal(run, finished.Diagnostics, diagnostics);
                    return Result<RunHandle?>.Failure(diagnostics);
                }
            }
            // The in-memory state follows the JOURNALED outcome: a refused
            // artifact write made the end Failed — the forced §F1 outcome,
            // never the cancelled transition the decision asked for.
            ApplyJournaledEnd(run, RunState.Cancelled);
            return Result<RunHandle?>.Success(
                new RunHandle(run.RunId, run.State), diagnostic);
        }

        // The §1.4 start-time check: every used Operation must still resolve
        // in the current Registry with identical descriptor and
        // implementation fingerprints (SPEC §C3 rule 6 values stored on the
        // Plan steps, SPEC §D7). <paramref name="closedSteps"/> skips the
        // indexes the journal already closed — the SPEC §F4 item 3 resume
        // check compares only the remaining steps.
        private static PlanStep? FirstFingerprintMismatch(
            CompiledPlan plan, RegistrySnapshot registry, HashSet<int>? closedSteps = null)
        {
            var steps = plan.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (closedSteps is not null && closedSteps.Contains(step.Index))
                {
                    continue;
                }
                var registered = registry.Find(step.OperationId, step.Version);
                if (registered is null ||
                    !string.Equals(registered.DescriptorHash, step.DescriptorHash, StringComparison.Ordinal) ||
                    !string.Equals(registered.ImplementationHash, step.ImplementationHash, StringComparison.Ordinal))
                {
                    return step;
                }
            }
            return null;
        }

        private Result<Unit> Append(RunRecord run, JournalRecordType type, JsonValue data)
        {
            var record = new JournalRecord(
                run.NextSeq, UtcTimestamp.Format(_clock.UtcNow), _editorSessionId,
                type, Step: null, Attempt: null, data);
            return AppendJournalRecord(run, record, Durability.Flushed);
        }

        /// <summary>The SPEC §E8 dispatcher — subscribers register through the host.</summary>
        internal RunEvents Events => _events;

        /// <summary>
        /// The bounded runless subscriber-fault record (SPEC §E8): the
        /// newest <see cref="RunlessFaultLogCapacity"/> entries of faults
        /// no journal could hold, in arrival order. Internal test seam —
        /// <c>OpWeave.Core.Tests</c> reaches it through InternalsVisibleTo.
        /// </summary>
        internal IReadOnlyList<RunLogEntry> RunlessSubscriberFaults => _runlessFaults;

        // The single funnel every journal append goes through: the SPEC
        // §F6 debug gate first, then the store write, the sequence spend
        // and the SPEC §E8 dispatch — after the record exists (SPEC §E8).
        private Result<Unit> AppendJournalRecord(
            RunRecord run, JournalRecord record, Durability durability)
        {
            if (SuppressDebug(record))
            {
                // SPEC §F6 SG-08: a debug-level record is dropped before it
                // reaches the Core log store — no journal line, no event
                // and no sequence number spent.
                return Result<Unit>.Success(Unit.Value);
            }
            var appended = _store.Append(run.RunId, record, durability);
            if (appended.Ok)
            {
                run.NextSeq++;
                NotifyAppended(run, record);
            }
            return appended;
        }

        // SPEC §F6 SG-08: the write-path debug gate — a log record whose
        // level is "debug" reaches the Core log store only while the
        // injected debugLogging policy permits; info and above are never
        // gated.
        private bool SuppressDebug(JournalRecord record) =>
            record.Type == JournalRecordType.Log &&
            !_debugLogging() &&
            record.Data.ToToken() is JObject data &&
            data["level"] is JValue { Value: string level } &&
            string.Equals(level, "debug", StringComparison.Ordinal);

        // SPEC §E8: the journaled record IS the event — dispatched after
        // the append, carrying the state the SPEC §F2 fold gives it. Run
        // state records (run_started, run_suspended, approval_rejected,
        // review_resolved, run_finished and the step_needs_review Run
        // transition) dispatch through the matching in-memory transition
        // instead — TransitionTo and EndRun's tails — so a synchronous
        // subscriber's StateOf never sees the previous state. Records
        // that mean no state change (cancel_requested, step_progress,
        // resource_*, approval_granted — Approve reports the decided
        // Queued transition itself) and the dropped debug record produce
        // no event.
        private void NotifyAppended(RunRecord run, JournalRecord record)
        {
            switch (record.Type)
            {
                case JournalRecordType.StepStarted:
                    PublishStep(run, record, StepState.Running, record.Attempt);
                    return;
                case JournalRecordType.StepWaiting:
                    PublishStep(run, record, StepState.Waiting, record.Attempt);
                    return;
                case JournalRecordType.StepSucceeded:
                    PublishStep(run, record, StepState.Succeeded, record.Attempt);
                    return;
                case JournalRecordType.StepFailed:
                    NotifyStepFailed(run, record);
                    return;
                case JournalRecordType.StepCancelled:
                    PublishStep(run, record, StepState.Cancelled, record.Attempt);
                    return;
                case JournalRecordType.StepSkipped:
                    PublishStep(run, record, StepState.Skipped, attempt: null);
                    return;
                case JournalRecordType.StepNeedsReview:
                    PublishStep(run, record, StepState.NeedsReview, record.Attempt);
                    return;
                case JournalRecordType.Log:
                    NotifyLogAppended(run, record);
                    return;
            }
        }

        // The §F2 fold mapping of step_failed: a record carrying a decided
        // retry leaves the step Running at the pending attempt; without
        // the retry member the failure is terminal.
        private void NotifyStepFailed(RunRecord run, JournalRecord record)
        {
            var next = record.Data.ToToken() is JObject data &&
                data["retry"] is JObject retry
                    ? retry["nextAttempt"]
                    : null;
            if (next is { Type: JTokenType.Integer } && next.Value<long>() >= 1)
            {
                PublishStep(run, record, StepState.Running, (int)next.Value<long>());
                return;
            }
            PublishStep(run, record, StepState.Failed, record.Attempt);
        }

        private void PublishStep(
            RunRecord run, JournalRecord record, StepState state, int? attempt)
        {
            if (record.Step is not { } index || NodeIdOf(run.Plan, index) is not { } nodeId)
            {
                // A step record without a step index, or an index outside
                // the Plan — defensive only: every writer above carries a
                // valid step.
                return;
            }
            RecordSuppressedFaults(_events.PublishStepState(
                new StepStateChanged(run.RunId, index, nodeId, state, attempt)));
        }

        private static string? NodeIdOf(CompiledPlan plan, int stepIndex)
        {
            var steps = plan.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Index == stepIndex)
                {
                    return steps[i].NodeId;
                }
            }
            return null;
        }

        // The journaled log record surfaces through LogAppended as the
        // single Core RunLogEntry (SPEC §F6): ts and step from the
        // envelope, level/code/message/data from the payload. A
        // debug-level record exists only while the SG-08 policy permits
        // it, and the §0.6 Severity set has no member to carry it — such a
        // record stays journaled without a LogAppended event.
        private void NotifyLogAppended(RunRecord run, JournalRecord record)
        {
            if (record.Data.ToToken() is not JObject data ||
                data["message"] is not JValue { Value: string message } ||
                data["level"] is not JValue { Value: string level } ||
                !TryParseLogSeverity(level, out var severity))
            {
                return;
            }
            RecordSuppressedFaults(_events.PublishLog(run.RunId, new RunLogEntry(
                record.Ts,
                severity,
                record.Step,
                data["code"] is JValue { Value: string code } ? code : null,
                message,
                data["data"] is { } token ? JsonValue.From(token) : null)));
        }

        // The wire level of SPEC §F6 log records: the §0.4 snake_case
        // Severity names.
        private static bool TryParseLogSeverity(string? wire, out Severity severity)
        {
            foreach (Severity candidate in Enum.GetValues(typeof(Severity)))
            {
                if (string.Equals(
                        wire, JsonNaming.ToSnakeCase(candidate.ToString()),
                        StringComparison.Ordinal))
                {
                    severity = candidate;
                    return true;
                }
            }
            severity = default;
            return false;
        }

        // The SPEC §E8 fault record: one warning log record per thrown
        // subscriber exception, journaled on the Run the event belongs to.
        // A runless event (RegistryChanged) records against the active Run
        // when one exists; with no active Run there is no journal to append
        // to, so §E8's "logged" half is the bounded runless fault log —
        // the same §F6 warning entry (the single RunLogEntry model, never
        // a second log type) lands there BEFORE the dispatcher's
        // LogAppended publish, so the fault stays logged with no listener
        // or a throwing one. A LogAppended listener that throws on the
        // fallback entry is itself a subscriber fault: the dispatch
        // returns it suppressed and it lands in the same bounded log —
        // never republished, so nothing recurses and no exception escapes.
        // The nested case is symmetric: a listener that throws on the
        // journaled fault record's own LogAppended returns suppressed to
        // NotifyLogAppended, which records it in the same bounded log.
        private Result<Unit> AppendSubscriberFaultLog(string? runId, Exception fault)
        {
            var target = runId ?? ActiveHandle()?.RunId;
            if (target is null)
            {
                var entry = SubscriberFaultEntry(fault);
                AppendRunlessFault(entry);
                RecordSuppressedFaults(_events.PublishLog(null, entry));
                return Result<Unit>.Success(Unit.Value);
            }
            var payload = new JObject
            {
                ["level"] = "warning",
                ["message"] = "A run event subscriber threw an exception.",
                ["data"] = new JObject
                {
                    ["exception"] = fault.GetType().FullName ?? fault.GetType().Name,
                },
            };
            return AppendRecord(target, JournalRecordType.Log, null, null, JsonValue.From(payload));
        }

        // The one §F6 warning entry shape every subscriber-fault record
        // takes — journaled, runless-logged or both.
        private RunLogEntry SubscriberFaultEntry(Exception fault) =>
            new RunLogEntry(
                UtcTimestamp.Format(_clock.UtcNow),
                Severity.Warning,
                null,
                null,
                "A run event subscriber threw an exception.",
                JsonValue.From(new JObject
                {
                    ["exception"] = fault.GetType().FullName ?? fault.GetType().Name,
                }));

        private void AppendRunlessFault(RunLogEntry entry)
        {
            if (_runlessFaults.Count == RunlessFaultLogCapacity)
            {
                _runlessFaults.RemoveAt(0);
            }
            _runlessFaults.Add(entry);
        }

        // The suppressed-fault half of every §E8 publish: a subscriber
        // fault the dispatcher returned because a fault record was
        // already being logged can never take the journal path — the
        // append would publish LogAppended again — so it is recorded in
        // the same bounded log through the single RunLogEntry fault
        // model, never republished (no recursion, no exception escapes).
        private void RecordSuppressedFaults(IReadOnlyList<Exception>? suppressed)
        {
            if (suppressed is null)
            {
                return;
            }
            for (var i = 0; i < suppressed.Count; i++)
            {
                AppendRunlessFault(SubscriberFaultEntry(suppressed[i]));
            }
        }

        private RunRecord? Find(string runId)
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                if (string.Equals(_runs[i].RunId, runId, StringComparison.Ordinal))
                {
                    return _runs[i];
                }
            }
            return null;
        }

        private RunRecord? FindActive()
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                var state = _runs[i].State;
                if (state == RunState.Running || state == RunState.Suspended ||
                    state == RunState.NeedsReview)
                {
                    return _runs[i];
                }
            }
            return null;
        }

        private RunRecord? FirstStartable()
        {
            for (var i = 0; i < _runs.Count; i++)
            {
                var run = _runs[i];
                if (run.State == RunState.Queued && !run.CancelRequested)
                {
                    return run;
                }
            }
            return null;
        }

        private int CountQueued()
        {
            var count = 0;
            for (var i = 0; i < _runs.Count; i++)
            {
                if (_runs[i].State == RunState.Queued)
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsTerminal(RunState state) =>
            state == RunState.Succeeded || state == RunState.Failed || state == RunState.Cancelled;

        // The recorded runId of a lock.json residue is untrusted persisted
        // state: a value outside the run identifier grammar makes
        // IRunStore.Load throw ArgumentException — corrupt recorded state
        // (OW-STORE-002), which fails closed exactly like an unloadable Run.
        private Result<RunSnapshot> LoadRecordedRun(string runId)
        {
            try
            {
                return _store.Load(runId);
            }
            catch (ArgumentException)
            {
                return Result<RunSnapshot>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_002,
                    "The recorded Run identifier is not a run identifier."));
            }
        }

        private static Result<RunHandle> Failure(Diagnostic diagnostic) =>
            Result<RunHandle>.Failure(diagnostic);

        // SPEC §E2 item 3: an unobtainable exclusion always means a live
        // owner — the single shared OW-RUN-002 report.
        private static Diagnostic[] LockHeldDiagnostics() =>
            new[]
            {
                ErrorCodes.Create(
                    ErrorCodes.OW_RUN_002,
                    "The Run lock is held by a live owner; a live owner's lock is never taken."),
            };

        private sealed class RunRecord
        {
            internal RunRecord(string runId, CompiledPlan plan, RunState state)
            {
                RunId = runId;
                Plan = plan;
                State = state;
                NextSeq = 2; // RunAccepted is seq 1, written by the store create.
            }

            internal string RunId { get; }
            internal CompiledPlan Plan { get; }
            internal RunState State { get; set; }
            internal long NextSeq { get; set; }
            internal CoordinatorRunLock? HeldLock { get; set; }
            internal bool EndJournaled { get; set; }
            // The terminal state the journaled run_finished record carries —
            // the outcome the single writer made durable, which a refused
            // §F7 artifact write may have replaced with Failed.
            internal RunState? JournaledTerminal { get; set; }
            // The §F7 artifact finalizer ran for this Run's end — exactly
            // once per end, whichever end path reaches the single writer.
            internal bool ArtifactsFinalized { get; set; }
            internal bool CancelRequested { get; set; }
            internal string? CancelReason { get; set; }
            // A journaled review_resolved{retry_step} waiting for the run
            // loop to re-arm the reviewed step as a fresh attempt.
            internal bool PendingReviewRetry { get; set; }
            // The refusal diagnostics of a marked store-failure end — the
            // run loop finishes the Run's §E6 item 4 cleanup before the
            // single terminal record (SPEC §F1).
            internal IReadOnlyList<Diagnostic>? PendingStoreFailure { get; set; }
        }
    }
}
