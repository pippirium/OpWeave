#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The Core execution loop of SPEC §E3 and the write-ahead step sequence
    /// of SPEC §E4: advances the one active mutating Run inside each
    /// <see cref="Tick"/>, taking the next step only from the Plan's
    /// <see cref="PlanStep.Transitions"/> table (never <c>index + 1</c>),
    /// honouring the per-tick budget (<see cref="DefaultTickBudgetMs"/>,
    /// setting <c>tickBudgetMs</c>) and the Editor-state gate, and journaling
    /// every state change before the side effect it records.
    /// </summary>
    /// <remarks>
    /// <para><b>Unity seams.</b> The loop touches Unity only through the
    /// injected delegates: <paramref name="resolveOperation"/> returns the
    /// live <see cref="IOperation"/> for a step (resolved per call — waiting
    /// state lives only in the resume token, SPEC §C2 item 4),
    /// <paramref name="requirements"/> answers the §G1 runtime Requirement
    /// and dynamic-path checks (the UnityAdapter evaluators, T-W2.1-02),
    /// <paramref name="openUndoScope"/> opens the named G3 undo group
    /// (<c>OpWeave: &lt;workflowId&gt;/&lt;nodeId&gt;</c>), and
    /// <paramref name="opWeaveInitiatedBusy"/> reports whether an in-progress
    /// compile/import was triggered by an OpWeave node — the §1.4 Waiting
    /// vs Suspended distinction.</para>
    /// <para><b>Budget.</b> The tick budget is a duration, so SPEC §0.4
    /// measures it on a monotonic timer (<see cref="IMonotonicTimer"/> —
    /// <see cref="System.Diagnostics.Stopwatch"/> in production, a fake in
    /// [Core] tests): the wall clock can jump backwards or forwards and
    /// must never stretch or exhaust the 50 ms budget. The injected
    /// <see cref="IClock"/> still supplies journal timestamps and the
    /// <c>nextPollAt</c> / <c>deadline</c> instants. The budget is
    /// consulted before each step action; an Immediate step in flight is
    /// never interrupted (SPEC §E6 item 1) — an exhausted budget simply
    /// defers the NEXT action to the next Tick.</para>
    /// <para><b>E4 write-ahead.</b> The journal order inside one step is
    /// Preflight → <c>step_started</c>{attempt, snapshot, inputsDigest}
    /// Flushed → undo scope → Execute → <c>step_succeeded</c> or
    /// <c>step_waiting</c> Flushed → undo close; Polls reuse E4 steps 5–8
    /// only. A <c>not_compiling</c> Requirement wait (2026-11-06 owner
    /// decision) writes no StepStarted, emits no diagnostic and ends the
    /// step's Tick work; the step is re-evaluated from E4 step 1 next Tick.
    /// <c>step_progress</c> (<c>required: false</c>, Buffered) is emitted when
    /// a Waiting step's reported <c>waitingFor</c> label changes, at most once
    /// per <see cref="ProgressIntervalMs"/>; <c>percent</c> is omitted because
    /// the v1 contract carries no progress channel beyond the label.</para>
    /// <para><b>SPEC §E6/§E7.</b> A Waiting step's deadline — the step-start
    /// instant plus <see cref="PlanStep.TimeoutSeconds"/>, journaled UTC on
    /// <c>step_waiting</c> and never reset by a reload — calls
    /// <see cref="IOperation.Cancel"/> once and then fails through the §E7
    /// gate with OW-RUN-004; an Immediate step cannot be interrupted, so an
    /// overrun is a warning <c>log</c> record only. A journaled
    /// <c>cancel_requested</c> is processed at the head of the next Tick:
    /// a Waiting step is cancelled through <see cref="IOperation.Cancel"/>
    /// and journaled <c>step_cancelled</c>, a Pending step ends with the
    /// Run (completed changes are never reverted — the §F7 list stays the
    /// rollback information). Steps register Run resources through the
    /// <see cref="IRunResourceRegistry"/> the context carries
    /// (<c>resource_acquired</c>/<c>resource_released</c>); whatever is
    /// still open is released at Run end through the injected
    /// <paramref name="releaseResource"/> seam. The seam receives the Run id of
    /// the Run whose resources it releases and the resource carries the
    /// <c>step</c> of its own <c>resource_acquired</c> record — the two halves
    /// of an acquisition scope — because a (kind, data) pair names the object
    /// but never who opened it, and SPEC §E6 item 4 releases only the Run's own
    /// resource. A failed attempt retries only when the diagnostic is
    /// retryable, attempts remain and the §F5
    /// table allows re-execution — the single
    /// <see cref="ResumePolicy.Decide"/> implementation — waiting
    /// 500 ms ×2 up to 10 s between attempts; the retrying step stays
    /// Running, each attempt's failure is journaled
    /// <c>step_failed</c> (SPEC §E0), the limit fails with OW-RUN-008, an
    /// adopted <see cref="ReconcileResult.Completed"/> is journaled
    /// <c>step_succeeded</c> with <c>reconciled: true</c>, and an
    /// undecidable verdict ends the step <c>step_needs_review</c>.</para>
    /// <para><b>Out of scope.</b> Suspended/NeedsReview resumption is the
    /// §F4 recovery engine (T-W1.8-08); human decisions are
    /// T-W1.8-09.</para>
    /// </remarks>
    internal sealed class RunLoop
    {
        /// <summary>The default SPEC §E3 item 3 tick budget: <c>tickBudgetMs</c> = 50.</summary>
        internal const int DefaultTickBudgetMs = 50;

        /// <summary>The SPEC §F2 <c>step_progress</c> minimum interval: 5 s.</summary>
        internal const long ProgressIntervalMs = 5000;

        /// <summary>The control port every v1 step result returns (SPEC §E3 item 2).</summary>
        internal const string NextPort = "next";

        private readonly RunCoordinator _coordinator;
        private readonly IClock _clock;
        private readonly IEditorState _editorState;
        private readonly ITypeRegistry _types;
        private readonly IOperationSource _operations;
        private readonly Func<PlanStep, Result<IOperation>> _resolveOperation;
        private readonly IRequirementEvaluator _requirements;
        private readonly Func<string, IDisposable> _openUndoScope;
        private readonly Func<bool> _opWeaveInitiatedBusy;
        private readonly int _tickBudgetMs;
        private readonly Func<IMonotonicTimer> _monotonicTimerFactory;
        private readonly Func<string, RunResource, Result<Unit>>? _releaseResource;
        private readonly Func<StepContextRequest, StepContext> _stepContextFactory;

        private readonly Dictionary<string, RunExecution> _executions =
            new Dictionary<string, RunExecution>(StringComparer.Ordinal);

        /// <summary>Creates the loop over the injected Unity-free seams (see the class remarks).</summary>
        /// <exception cref="ArgumentNullException">Any delegate or seam is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="tickBudgetMs"/> is not positive.
        /// </exception>
        internal RunLoop(
            RunCoordinator coordinator,
            IClock clock,
            IEditorState editorState,
            ITypeRegistry types,
            IOperationSource operations,
            Func<PlanStep, Result<IOperation>> resolveOperation,
            IRequirementEvaluator requirements,
            Func<string, IDisposable> openUndoScope,
            Func<bool> opWeaveInitiatedBusy,
            int tickBudgetMs = DefaultTickBudgetMs,
            Func<IMonotonicTimer>? monotonicTimerFactory = null,
            Func<string, RunResource, Result<Unit>>? releaseResource = null,
            Func<StepContextRequest, StepContext>? stepContextFactory = null)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _editorState = editorState ?? throw new ArgumentNullException(nameof(editorState));
            _types = types ?? throw new ArgumentNullException(nameof(types));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _resolveOperation =
                resolveOperation ?? throw new ArgumentNullException(nameof(resolveOperation));
            _requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
            _openUndoScope =
                openUndoScope ?? throw new ArgumentNullException(nameof(openUndoScope));
            _opWeaveInitiatedBusy =
                opWeaveInitiatedBusy ?? throw new ArgumentNullException(nameof(opWeaveInitiatedBusy));
            if (tickBudgetMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tickBudgetMs), "tickBudgetMs must be positive.");
            }
            _tickBudgetMs = tickBudgetMs;
            _monotonicTimerFactory =
                monotonicTimerFactory ?? (() => new StopwatchMonotonicTimer());
            _releaseResource = releaseResource;
            // The context the Operations actually run against: Core's own
            // JSON-level implementation by default; the UnityAdapter host
            // injects its CLR/Ref-resolving one (SPEC §0.6 SG-06, §C2 item 5).
            _stepContextFactory = stepContextFactory ?? CreateJsonContext;
            // The coordinator's human review abort ends a NeedsReview Run
            // outside the Tick — the §E6 item 4 Run-end release still runs
            // through this loop's own cleanup, bound here so the path is
            // shared, never duplicated.
            _coordinator.BindRunEndResourceCleanup(ReleaseRunResourcesAtEnd);
        }

        private static StepContext CreateJsonContext(StepContextRequest request) =>
            new JsonStepContext(
                request.Inputs, request.Changes, request.Log, request.ResumeToken,
                resources: request.Resources);

        // One step attempt's context request, shared by the Execute, Poll
        // and Cancel paths: identical except for the resolved descriptor —
        // including the SG-06 input-type table (see StaticInputTypes) that
        // selects each port's conversion.
        private StepContext ContextFor(
            RunExecution exec, PlanStep step, OperationDescriptor? descriptor,
            StepRuntime rt) =>
            _stepContextFactory(new StepContextRequest(
                step, descriptor, rt.Inputs!,
                StaticInputTypes(exec, step, descriptor),
                rt.Changes, rt.Log, rt.ResumeToken, rt.Resources, rt.Attempt));

        // SPEC §0.6 SG-06 at run time: the static type of each input port —
        // a FromStep port takes the producing step's resolved output type
        // exactly as the Plan recorded it (the §D4 item 6 product, never
        // re-resolved here), a FromInput port the bound workflow input's
        // declared type (the first declaration, matching the D4 duplicate
        // rule), a Constant or Default port the Operation's declared port
        // type, and a Missing port none. A port without a statically known
        // type simply has no entry, exactly like the compile-phase
        // StaticContext.StaticTypeOf answer; the context then leaves it
        // readable only as JsonValue.
        private IReadOnlyDictionary<string, string> StaticInputTypes(
            RunExecution exec, PlanStep step, OperationDescriptor? descriptor)
        {
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            var document = _coordinator.DocumentOf(exec.RunId);
            foreach (var pair in step.Inputs)
            {
                switch (pair.Value)
                {
                    case FromStep fromStep:
                        if (fromStep.StepIndex >= 0 &&
                            fromStep.StepIndex < exec.Plan.Steps.Count &&
                            exec.Plan.Steps[fromStep.StepIndex].OutputTypes
                                .TryGetValue(fromStep.Port, out var outputType))
                        {
                            types[pair.Key] = outputType;
                        }
                        break;
                    case FromInput fromInput:
                        if (document is not null)
                        {
                            for (var i = 0; i < document.Inputs.Count; i++)
                            {
                                if (string.Equals(
                                        document.Inputs[i].Id, fromInput.InputId,
                                        StringComparison.Ordinal))
                                {
                                    types[pair.Key] = document.Inputs[i].Type;
                                    break;
                                }
                            }
                        }
                        break;
                    case Constant _:
                    case Default _:
                        var declared = DeclaredPortType(descriptor, pair.Key);
                        if (declared is not null)
                        {
                            types[pair.Key] = declared;
                        }
                        break;
                }
            }
            return types;
        }

        private static string? DeclaredPortType(
            OperationDescriptor? descriptor, string port)
        {
            if (descriptor is null)
            {
                return null;
            }
            for (var i = 0; i < descriptor.Inputs.Count; i++)
            {
                if (string.Equals(
                        descriptor.Inputs[i].Name, port, StringComparison.Ordinal))
                {
                    return descriptor.Inputs[i].Type;
                }
            }
            return null;
        }

        // SPEC §F4 item 4's plan-side half of the session-key discard: the
        // resolved inputs that come straight from the Plan — an execution
        // input, a constant or a default — were committed in the journal's
        // creation session, so a stale one loses its Ref 'handle' keys at
        // the declared Ref ports only. FromStep ports read the rebuilt §E5
        // store, already stripped per the producing record's own session,
        // and a port whose declared type is core.Json or unresolvable is
        // opaque data the discard never touches.
        private IReadOnlyDictionary<string, InputValue> StripPlanInputs(
            RunExecution exec, PlanStep step, OperationDescriptor? descriptor,
            IReadOnlyDictionary<string, InputValue> inputs)
        {
            var declared = StaticInputTypes(exec, step, descriptor);
            var planSide = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in step.Inputs)
            {
                if (pair.Value is not FromStep &&
                    declared.TryGetValue(pair.Key, out var typeId))
                {
                    planSide[pair.Key] = typeId;
                }
            }
            return SessionHandleDiscard.StripInputs(inputs, planSide, _types) ?? inputs;
        }

        /// <summary>
        /// One Editor-update slice of the run (SPEC §E3 item 1): admits the
        /// oldest Queued Run under the §E2 item 4 start gate, applies the
        /// §E3 item 4 Editor-state gate to the active Run, then performs as
        /// much step work as the tick budget admits. Diagnostics gathered
        /// during the tick ride on the result; a <see cref="Result{T}"/>
        /// failure is reserved for the persistence boundary itself.
        /// </summary>
        /// <remarks>
        /// A Suspended or NeedsReview Run is left to the §F4 recovery engine —
        /// resumption is never automatic (T-W1.8-08), so the loop does nothing
        /// for it here.
        /// </remarks>
        internal Result<Unit> Tick()
        {
            var diagnostics = new List<Diagnostic>();

            // SPEC §F1: a Run the coordinator marked for the store-failure
            // end — a required append the store refused while the Run may
            // hold §E6 item 4 open resources — is finished first, through
            // the same Run-end cleanup as every other terminal transition:
            // still-open resources released before the single terminal
            // record, ahead of the start gate, the Editor-state gate and
            // any further Operation call.
            FinishPendingStoreFailures(diagnostics);

            // A Run whose terminal record is journaled but whose
            // coordinator.lock release failed is terminal-pending-release:
            // the release-only half of EndRun retries each Tick — the
            // journaled state is never rewritten.
            diagnostics.AddRange(_coordinator.ReleaseEndedRunLocks().Diagnostics);

            // SPEC §E6 item 2: Queued/AwaitingApproval Runs with a journaled
            // cancel_requested are ended Cancelled here — before the start
            // gate can pick them up.
            var inactiveCancels = _coordinator.ProcessInactiveCancels();
            if (!inactiveCancels.Ok)
            {
                return Result<Unit>.Failure(inactiveCancels.Diagnostics);
            }
            diagnostics.AddRange(inactiveCancels.Diagnostics);

            // SPEC §E2 item 5: an approval-pending Run whose used Operations
            // changed in the Registry is cancelled before anything else —
            // a human never approves different code than was validated.
            var invalidated = _coordinator.InvalidateStaleApprovals();
            if (!invalidated.Ok)
            {
                return Result<Unit>.Failure(invalidated.Diagnostics);
            }
            diagnostics.AddRange(invalidated.Diagnostics);

            // SPEC §E2 item 4: the start gate runs every Tick (Queued →
            // Running only when the Editor state and the lock admit it).
            var started = _coordinator.TryStartNext();
            if (!started.Ok)
            {
                return Result<Unit>.Failure(started.Diagnostics);
            }
            diagnostics.AddRange(started.Diagnostics);

            var active = _coordinator.ActiveHandle();
            if (active is null || active.State != RunState.Running)
            {
                return Result<Unit>.Success(Unit.Value, diagnostics);
            }
            var exec = ExecutionFor(active.RunId);
            if (exec is null)
            {
                // The coordinator tracks a Running Run whose Plan it can no
                // longer supply — an internal inconsistency, not a runtime
                // outcome.
                return Result<Unit>.Failure(InternalError(
                    "The active Run has no tracked Plan."));
            }

            // SPEC §E3 item 4: the Editor state is read at the head of the
            // Tick. Play Mode — or a compile/import OpWeave did not start —
            // suspends the Run and ends the Tick (SPEC §1.4). A busy period
            // OpWeave itself triggered keeps the Run going: the affected
            // steps wait instead (Waiting, not Suspended).
            if (!_editorState.IsEditMode)
            {
                Suspend(exec, "play_mode", diagnostics);
                return Result<Unit>.Success(Unit.Value, diagnostics);
            }
            if ((_editorState.IsCompiling || _editorState.IsUpdating) &&
                !_opWeaveInitiatedBusy())
            {
                Suspend(exec, "external_compile_or_import", diagnostics);
                return Result<Unit>.Success(Unit.Value, diagnostics);
            }

            // SPEC §E6 item 2: a journaled cancel_requested is processed at
            // the head of the next Tick — before any further step work.
            if (_coordinator.HasPendingCancel(exec.RunId))
            {
                ProcessCancel(exec, diagnostics);
                return Result<Unit>.Success(Unit.Value, diagnostics);
            }

            // SPEC §E1/§E0: a journaled review_resolved{retry_step} moved the
            // Run back to Running — the finished projection is re-armed so
            // the reviewed step re-enters E4 step 1 as a fresh attempt.
            if (exec.Finished && _coordinator.HasPendingReviewRetry(exec.RunId))
            {
                ResumeReviewedStep(exec);
            }

            var tickTimer = _monotonicTimerFactory();
            while (_coordinator.StateOf(exec.RunId) == RunState.Running && !exec.Finished)
            {
                if (exec.Current < 0)
                {
                    FinishRun(exec, RunState.Succeeded, diagnostics, error: null);
                    break;
                }
                if (exec.Current >= exec.Plan.Steps.Count)
                {
                    throw new InvalidOperationException(
                        $"The Transitions table led to step index {exec.Current}, " +
                        "outside the Plan — a Plan defect.");
                }

                var step = exec.Plan.Steps[exec.Current];
                var rt = exec.Step(step);
                if (rt.State == StepState.Waiting)
                {
                    // SPEC §E6 item 1: an expired Waiting is cancelled once
                    // and fails through the §E7 gate — ahead of the poll
                    // gate, so an overdue step never waits for nextPollAt.
                    if (rt.DeadlineAt is { } deadlineAt && _clock.UtcNow >= deadlineAt)
                    {
                        ExpireWaitingStep(exec, step, rt, diagnostics);
                        break;
                    }
                    // SPEC §E3 item 3: a Waiting step is polled only once its
                    // nextPollAt has passed, and only within the tick budget.
                    if (_clock.UtcNow < rt.NextPollAt || BudgetExceeded(tickTimer))
                    {
                        break;
                    }
                    // A still-Waiting step blocks the Run — the Tick ends.
                    if (!PollStep(exec, step, rt, diagnostics))
                    {
                        break;
                    }
                }
                else
                {
                    // SPEC §E7: a scheduled retry re-enters E4 step 1 once
                    // its backoff has elapsed — never inside the same Tick
                    // it was scheduled.
                    if (rt.NextRetryAt is { } retryAt)
                    {
                        if (_clock.UtcNow < retryAt || BudgetExceeded(tickTimer))
                        {
                            break;
                        }
                        rt.NextRetryAt = null;
                    }
                    // Pending — or deferred by a not_compiling wait — steps
                    // (re-)enter E4 step 1 under the remaining budget.
                    if (BudgetExceeded(tickTimer))
                    {
                        break;
                    }
                    // A not_compiling Wait ends the step's Tick work without
                    // starting it (2026-11-06 owner decision).
                    if (!ExecuteStep(exec, step, rt, diagnostics))
                    {
                        break;
                    }
                }
            }

            return Result<Unit>.Success(Unit.Value, diagnostics);
        }

        // ── SPEC §E4 steps 1–4 (step start) ──────────────────────────────────

        // E4 steps 1–3 plus the write-ahead record; the actual attempt runs
        // through RunAttempt so the Poll path shares steps 5–8 unchanged.
        // Returns false exactly when a not_compiling Wait deferred the step —
        // the Tick ends; every other outcome lets the loop decide from the
        // journaled state.
        private bool ExecuteStep(
            RunExecution exec, PlanStep step, StepRuntime rt, List<Diagnostic> diagnostics)
        {
            // E4 step 1: resolve every input port from the Plan's InputSource
            // records (SPEC §E5); a missing produced value is OW-RUN-007.
            var resolved = exec.Values.ResolveInputs(step, exec.Plan.Inputs);
            if (!resolved.Ok)
            {
                FailStep(exec, step, rt, resolved.Diagnostics, diagnostics);
                return true;
            }
            rt.Inputs = resolved.Value!;

            // The verified descriptor the §1.4 fingerprint check bound to the
            // Plan's recorded (id, version) — the source of the declared
            // Requirements.
            var descriptor = _operations.Find(step.OperationId, step.Version);
            if (descriptor is null)
            {
                FailStep(exec, step, rt, new[]
                {
                    ErrorCodes.Create(
                        ErrorCodes.OW_RUN_011,
                        "The step's Operation descriptor cannot be resolved.",
                        nodeId: step.NodeId),
                }, diagnostics);
                return true;
            }
            // SPEC §F4 item 4's plan-side half: the inputs that come
            // straight from the Plan were committed in the journal's
            // creation session — a stale one loses its Ref 'handle' keys
            // at the declared Ref ports only.
            if (exec.PlanSessionStale)
            {
                rt.Inputs = StripPlanInputs(exec, step, descriptor, rt.Inputs!);
            }
            var ctx = ContextFor(exec, step, descriptor, rt);

            // E4 step 2: runtime prerequisites — the injected §G1 evaluators,
            // including the dynamic-path permission check. A Wait outcome
            // (v1: not_compiling only, 2026-11-06 owner decision) ends the
            // step's Tick work with no StepStarted and no diagnostic; the
            // step is re-evaluated from step 1 on the next Tick.
            var requirements = descriptor.Requirements;
            for (var i = 0; i < requirements.Count; i++)
            {
                RequirementEvaluation outcome;
                try
                {
                    outcome = _requirements.Evaluate(requirements[i], ctx);
                }
                catch (Exception e)
                {
                    FailRun(exec, new[] { InternalError(e.GetType().FullName) }, diagnostics);
                    return true;
                }
                if (outcome.Status == RequirementStatus.Wait)
                {
                    return false;
                }
                if (outcome.Status == RequirementStatus.Violated)
                {
                    FailStep(exec, step, rt, new[] { outcome.Error! }, diagnostics);
                    return true;
                }
            }

            var op = _resolveOperation(step);
            if (!op.Ok || op.Value is null)
            {
                FailStep(
                    exec, step, rt,
                    op.Diagnostics.Count == 0
                        ? new[]
                        {
                            ErrorCodes.Create(
                                ErrorCodes.OW_RUN_011,
                                "The step's Operation cannot be resolved.",
                                nodeId: step.NodeId),
                        }
                        : op.Diagnostics,
                    diagnostics);
                return true;
            }

            // E4 step 3: Preflight — no side effects; an error diagnostic or a
            // thrown/absent result fails the step before anything is recorded.
            PreflightResult? preflight = null;
            if (InvokeOperationMember(
                    exec, rt, () => preflight = op.Value.Preflight(ctx),
                    diagnostics, out var preflightThrew))
            {
                return true;
            }
            if (preflightThrew is StepContextException preflightContextError)
            {
                // A context conversion failure carries its own diagnostic
                // (SPEC §0.6 SG-06) — e.g. an unresolvable Ref input's
                // OW-UNITY-001 (SPEC §E5).
                FailStep(exec, step, rt, preflightContextError.Diagnostics, diagnostics);
                return true;
            }
            if (preflightThrew is not null)
            {
                FailStep(exec, step, rt, new[] { OperationThrew(preflightThrew) }, diagnostics);
                return true;
            }
            if (preflight is null)
            {
                FailStep(exec, step, rt, new[]
                {
                    ErrorCodes.Create(
                        ErrorCodes.OW_RUN_006,
                        "The Operation returned no Preflight result.",
                        nodeId: step.NodeId),
                }, diagnostics);
                return true;
            }
            var preflightErrors = ErrorsOf(preflight.Diagnostics);
            if (preflightErrors.Count != 0)
            {
                FailStep(exec, step, rt, preflightErrors, diagnostics);
                return true;
            }

            // E4 step 4: the write-ahead record — StepStarted{attempt,
            // snapshot, inputsDigest} reaches disk before any side effect.
            var startedAt = _clock.UtcNow;
            rt.DeadlineAt = startedAt + TimeSpan.FromSeconds(step.TimeoutSeconds);
            rt.Deadline = UtcTimestamp.Format(rt.DeadlineAt.Value);
            rt.Snapshot = preflight.Snapshot;
            var started = Append(exec, JournalRecordType.StepStarted, step.Index, rt.Attempt,
                new JObject
                {
                    ["snapshot"] = preflight.Snapshot?.ToToken() ?? new JObject(),
                    ["inputsDigest"] = CanonicalJson.Sha256(PresentInputs(resolved.Value!)),
                });
            if (!started.Ok)
            {
                FailRun(exec, started.Diagnostics, diagnostics, recordSkips: false);
                return true;
            }
            rt.State = StepState.Running;

            RunAttempt(exec, step, rt, ctx, op.Value, poll: false, diagnostics);
            // SPEC §E6 item 1: an Immediate step cannot be interrupted — an
            // attempt that overran its deadline is a warning log, never a
            // failure. A step that went Waiting is governed by the deadline
            // branch instead.
            if (descriptor.ExecutionMode == ExecutionMode.Immediate &&
                rt.State != StepState.Waiting &&
                rt.DeadlineAt is { } deadlineAt && _clock.UtcNow > deadlineAt)
            {
                rt.Log.Write(
                    Severity.Warning, "The step exceeded its timeout.",
                    code: ErrorCodes.OW_RUN_004);
            }
            return true;
        }

        // SPEC §F1: the single boundary every IOperation member invoked with
        // the step's resource registry goes through — Preflight, Execute,
        // Poll, Cancel and the §F5 Reconcile inside ResumePolicy.Decide (the
        // only call in this class that reaches Reconcile). A required
        // resource_acquired/resource_released record the store refused stays
        // recorded on the registry and wins over the member's own outcome:
        // checked after the member returned OR threw, the refused append
        // ends the Run Failed on the store-failure path — never the member's
        // result or exception. Returns true when the Run already ended
        // Failed and the caller must stop all processing for the step;
        // otherwise <paramref name="thrown"/> carries the member's exception
        // for the caller's own outcome handling.
        private bool InvokeOperationMember(
            RunExecution exec, StepRuntime rt, Action member,
            List<Diagnostic> diagnostics, out Exception? thrown)
        {
            thrown = null;
            try
            {
                member();
            }
            catch (Exception e)
            {
                thrown = e;
            }
            if (rt.Resources.StoreFailure is { } refused)
            {
                FailRun(exec, refused, diagnostics, recordSkips: false);
                return true;
            }
            return false;
        }

        // SPEC §E4 steps 5–8, shared by the initial Execute and every later
        // Poll: open the named G3 undo group, call the operation under
        // try/catch, journal the outcome, close the group. The undo scope is
        // a wiring-side concern; its own failures are internal errors, never
        // silently ignored.
        private void RunAttempt(
            RunExecution exec, PlanStep step, StepRuntime rt, StepContext ctx,
            IOperation op, bool poll, List<Diagnostic> diagnostics)
        {
            IDisposable scope;
            try
            {
                scope = _openUndoScope($"OpWeave: {exec.Plan.WorkflowId}/{step.NodeId}");
            }
            catch (Exception e)
            {
                FailRun(exec, new[] { InternalError(e.GetType().FullName) }, diagnostics);
                return;
            }
            if (scope is null)
            {
                FailRun(exec, new[] { InternalError("The undo scope factory returned null.") },
                    diagnostics);
                return;
            }

            try
            {
                StepResult result = default;
                if (InvokeOperationMember(
                        exec, rt,
                        () => result = poll ? op.Poll(ctx) : op.Execute(ctx),
                        diagnostics, out var threw))
                {
                    return;
                }
                if (threw is StepContextException contextError)
                {
                    // A context conversion failure carries its own diagnostic
                    // (SPEC §0.6 SG-06) — e.g. an unresolvable Ref input's
                    // OW-UNITY-001 (SPEC §E5).
                    FailStep(exec, step, rt, contextError.Diagnostics, diagnostics);
                    return;
                }
                if (threw is not null)
                {
                    // E4 step 6: an Operation exception is OW-RUN-006.
                    FailAttempt(
                        exec, step, rt, ctx, op, OperationThrew(threw), diagnostics);
                    return;
                }

                foreach (var pair in ctx.Outputs)
                {
                    rt.Outputs[pair.Key] = pair.Value;
                }
                HandleResult(exec, step, rt, ctx, op, result, diagnostics);
            }
            finally
            {
                try
                {
                    scope.Dispose();
                }
                catch (Exception e)
                {
                    diagnostics.Add(InternalError(e.GetType().FullName));
                    if (_coordinator.StateOf(exec.RunId) == RunState.Running)
                    {
                        FailRun(exec, Array.Empty<Diagnostic>(), diagnostics);
                    }
                }
            }
        }

        // E4 step 7: the three StepResult outcomes journaled under the same
        // write-ahead rule.
        private void HandleResult(
            RunExecution exec, PlanStep step, StepRuntime rt, StepContext ctx,
            IOperation op, StepResult result, List<Diagnostic> diagnostics)
        {
            switch (result.Kind)
            {
                case StepResultKind.Succeeded:
                    // B7 output validation: every declared output present,
                    // typed, normalized and within the §E5 bound — all
                    // failures are OW-RUN-007.
                    var collected = StepOutputs.Collect(step, _types, _operations, rt.Outputs);
                    if (!collected.Ok)
                    {
                        FailStep(exec, step, rt, collected.Diagnostics, diagnostics);
                        return;
                    }
                    SucceedStep(exec, step, rt, collected.Value!, reconciled: false, diagnostics);
                    return;

                case StepResultKind.Waiting:
                    var now = _clock.UtcNow;
                    // SPEC §F2 attempt-durability (2026-11-08 owner decision):
                    // every Waiting return journals a StepWaiting — the one a
                    // Poll that recorded new changes and returns Waiting again
                    // re-records — carrying the attempt's cumulative §F7 list
                    // whenever it is non-empty, the latest resumeToken and
                    // waitingFor, and the ORIGINAL deadline (never extended).
                    var waitingData = new JObject
                    {
                        ["resumeToken"] = result.ResumeToken!.ToToken(),
                        ["waitingFor"] = CredentialMask.MaskText(result.WaitingFor!),
                        ["deadline"] = rt.Deadline,
                    };
                    if (rt.Changes.Records.Count != 0)
                    {
                        waitingData["changes"] = ChangeRecordJson.Write(rt.Changes.Records);
                    }
                    var waiting = Append(exec, JournalRecordType.StepWaiting, step.Index,
                        rt.Attempt,
                        waitingData);
                    if (!waiting.Ok)
                    {
                        FailRun(exec, waiting.Diagnostics, diagnostics, recordSkips: false);
                        return;
                    }
                    rt.State = StepState.Waiting;
                    rt.ResumeToken = result.ResumeToken;
                    rt.NextPollAt = now + TimeSpan.FromMilliseconds(result.PollIntervalMs);
                    // SPEC §F2: step_progress (required:false, Buffered) when
                    // the reported progress changes, at most once per 5 s.
                    // The first Waiting report is the StepWaiting record
                    // itself — the change baseline, not a progress event.
                    if (rt.LastReportedWaitingFor is null)
                    {
                        rt.LastReportedWaitingFor = result.WaitingFor;
                        rt.LastProgressAt = now;
                    }
                    else if (!string.Equals(
                                 result.WaitingFor, rt.LastReportedWaitingFor,
                                 StringComparison.Ordinal) &&
                             now - rt.LastProgressAt >=
                                 TimeSpan.FromMilliseconds(ProgressIntervalMs))
                    {
                        var progress = Append(exec, JournalRecordType.StepProgress, step.Index,
                            rt.Attempt,
                            new JObject
                            {
                                ["message"] = CredentialMask.MaskText(result.WaitingFor!),
                            });
                        if (progress.Ok)
                        {
                            rt.LastProgressAt = now;
                            rt.LastReportedWaitingFor = result.WaitingFor;
                        }
                        else
                        {
                            FailRun(exec, progress.Diagnostics, diagnostics);
                        }
                    }
                    return;

                default:
                    // StepResultKind.Failed — and any future kind — goes to
                    // the §E7 gate (T-W1.8-06): retry, adopt, review or fail.
                    FailAttempt(
                        exec, step, rt, ctx, op,
                        result.Error ?? ErrorCodes.Create(
                            ErrorCodes.OW_RUN_006,
                            "The Operation returned an uninterpretable result.",
                            nodeId: step.NodeId),
                        diagnostics);
                    return;
            }
        }

        // E4 step 7's success tail, shared by a normal Succeeded result and a
        // §F5 adoption (reconciled: the outputs came from Reconcile's proof,
        // not from the attempt's own SetOutput calls).
        private void SucceedStep(
            RunExecution exec, PlanStep step, StepRuntime rt,
            JsonValue outputs, bool reconciled, List<Diagnostic> diagnostics)
        {
            var succeeded = Append(exec, JournalRecordType.StepSucceeded, step.Index,
                rt.Attempt,
                new JObject
                {
                    ["outputs"] = outputs.ToToken(),
                    ["changes"] = ChangeRecordJson.Write(rt.Changes.Records),
                    ["port"] = NextPort,
                    ["reconciled"] = reconciled,
                });
            if (!succeeded.Ok)
            {
                FailRun(exec, succeeded.Diagnostics, diagnostics, recordSkips: false);
                return;
            }
            var stored = exec.Values.SetOutputs(step.Index, outputs);
            if (!stored.Ok)
            {
                FailStep(exec, step, rt, stored.Diagnostics, diagnostics);
                return;
            }
            rt.State = StepState.Succeeded;
            // SPEC §E3 item 2: the returned control port selects the next
            // step from Transitions — never index + 1.
            if (!step.Transitions.TryGetValue(NextPort, out var next))
            {
                throw new InvalidOperationException(
                    $"Plan step '{step.NodeId}' has no '{NextPort}' transition — a Plan defect.");
            }
            exec.Current = next;
        }

        // ── SPEC §E6/E7 ──────────────────────────────────────────────────────

        // SPEC §E6 item 1: an expired Waiting step is cancelled through the
        // Operation once, then failed through the §E7 gate with the
        // retryable-conditional OW-RUN-004 (SPEC §J).
        private void ExpireWaitingStep(
            RunExecution exec, PlanStep step, StepRuntime rt, List<Diagnostic> diagnostics)
        {
            var op = _resolveOperation(step);
            StepContext? ctx = null;
            if (op.Ok && op.Value is not null)
            {
                ctx = ContextFor(
                    exec, step, _operations.Find(step.OperationId, step.Version), rt);
                if (!rt.CancelInvoked)
                {
                    rt.CancelInvoked = true;
                    if (InvokeOperationMember(
                            exec, rt, () => op.Value.Cancel(ctx),
                            diagnostics, out var cancelThrew))
                    {
                        return;
                    }
                    if (cancelThrew is not null)
                    {
                        // Cancel is best-effort (SPEC §C2): a throwing Cancel
                        // surfaces as OW-RUN-006 but never blocks the timeout.
                        diagnostics.Add(OperationThrew(cancelThrew));
                    }
                }
            }
            else
            {
                diagnostics.AddRange(
                    op.Diagnostics.Count == 0
                        ? new[]
                        {
                            ErrorCodes.Create(
                                ErrorCodes.OW_RUN_011,
                                "The step's Operation cannot be resolved.",
                                nodeId: step.NodeId),
                        }
                        : op.Diagnostics);
            }
            FailAttempt(
                exec, step, rt, ctx, op.Ok ? op.Value : null,
                ErrorCodes.Create(
                    ErrorCodes.OW_RUN_004,
                    "The step timed out while waiting.",
                    nodeId: step.NodeId,
                    retryable: true),
                diagnostics);
        }

        // SPEC §E7: a failed attempt is sorted by the single §F5 table —
        // retry only when the diagnostic is retryable, attempts remain and
        // re-execution is allowed; an Unsafe step's failure is final. EVERY
        // re-execution decision of a retryable failure goes through
        // ResumePolicy.Decide — Reexecute retries (attempts permitting),
        // AdoptOutputs succeeds from Reconcile's proof, NeedsReview waits
        // for a human, and an Idempotency value outside the declared enum
        // lands in NeedsReview inside Decide, never in a retry; the
        // exhausted retry chain fails OW-RUN-008.
        private void FailAttempt(
            RunExecution exec, PlanStep step, StepRuntime rt,
            StepContext? ctx, IOperation? op, Diagnostic error,
            List<Diagnostic> diagnostics)
        {
            if (!error.Retryable || step.Idempotency == Idempotency.Unsafe)
            {
                FailStep(exec, step, rt, new[] { error }, diagnostics);
                return;
            }
            if (op is null || ctx is null)
            {
                // The §F5 consult cannot run — uncertain is never an
                // automatic outcome.
                StepToNeedsReview(exec, step, rt, error, diagnostics);
                return;
            }
            var decision = ResumeDecision.NeedsReview;
            IReadOnlyDictionary<string, JsonValue>? adopted = null;
            if (InvokeOperationMember(
                    exec, rt,
                    () => decision = ResumePolicy.Decide(
                        step, op, ctx, rt.Snapshot, out adopted),
                    diagnostics, out var decideThrew))
            {
                return;
            }
            if (decideThrew is not null)
            {
                // Decide rejects only a null argument — a runner bug,
                // surfaced like every other broken injected seam.
                FailRun(
                    exec, new[] { InternalError(decideThrew.GetType().FullName) },
                    diagnostics);
                return;
            }
            switch (decision)
            {
                case ResumeDecision.AdoptOutputs:
                    AdoptStepOutputs(exec, step, rt, adopted!, diagnostics);
                    return;
                case ResumeDecision.NeedsReview:
                    StepToNeedsReview(exec, step, rt, error, diagnostics);
                    return;
                // Reexecute falls through to the attempt-count check.
            }
            if (rt.Attempt < step.Retry.MaxAttempts)
            {
                ScheduleRetry(exec, step, rt, error, diagnostics);
                return;
            }
            FailStep(exec, step, rt, new[] { FinalAttemptError(step, rt, error) }, diagnostics);
        }

        // SPEC §E7: the retrying step stays Running — its failed attempt is
        // journaled step_failed (SPEC §E0) and the next attempt re-enters E4
        // step 1 once the 500 ms ×2 (max 10 s) backoff has elapsed. The
        // additive "retry" member ({nextAttempt, notBefore}) records that a
        // re-execution was decided, so the §F2 fold maps the record to
        // Running-with-pending-attempt instead of terminal Failed
        // (compatible: journals without the member keep their meaning).
        private void ScheduleRetry(
            RunExecution exec, PlanStep step, StepRuntime rt,
            Diagnostic error, List<Diagnostic> diagnostics)
        {
            var nextRetryAt = _clock.UtcNow + Backoff(step.Retry, rt.Attempt);
            var failedData = new JObject
            {
                ["error"] = error.Code,
                ["message"] = CredentialMask.MaskText(error.Message),
                ["retry"] = new JObject
                {
                    ["nextAttempt"] = rt.Attempt + 1,
                    ["notBefore"] = UtcTimestamp.Format(nextRetryAt),
                },
            };
            if (rt.Changes.Records.Count != 0)
            {
                failedData["changes"] = ChangeRecordJson.Write(rt.Changes.Records);
            }
            var appended = Append(exec, JournalRecordType.StepFailed, step.Index,
                rt.Attempt,
                failedData);
            if (!appended.Ok)
            {
                FailRun(exec, appended.Diagnostics, diagnostics, recordSkips: false);
                return;
            }
            diagnostics.Add(error);
            rt.Outputs.Clear();
            rt.ResumeToken = null;
            rt.LastReportedWaitingFor = null;
            rt.CancelInvoked = false;
            rt.NextRetryAt = nextRetryAt;
            rt.Attempt++;
            // The failed attempt's cumulative list stays on the StepFailed
            // record just written; the retried attempt's changes are its own
            // elements (SPEC §F2 attempt-durability).
            rt.Changes.BeginAttempt(rt.Attempt);
            rt.State = StepState.Running;
        }

        // SPEC §F5: a Completed verdict's proven outputs are journaled as the
        // step's success with reconciled: true — re-execution never happens.
        private void AdoptStepOutputs(
            RunExecution exec, PlanStep step, StepRuntime rt,
            IReadOnlyDictionary<string, JsonValue> adopted, List<Diagnostic> diagnostics)
        {
            var collected = StepOutputs.Collect(step, _types, _operations, adopted);
            if (!collected.Ok)
            {
                FailStep(exec, step, rt, collected.Diagnostics, diagnostics);
                return;
            }
            SucceedStep(exec, step, rt, collected.Value!, reconciled: true, diagnostics);
        }

        // An undecidable §F5 verdict: the step is journaled needs_review and
        // the Run waits for a human decision — never an automatic success or
        // re-execution (SPEC §F5, §E7).
        private void StepToNeedsReview(
            RunExecution exec, PlanStep step, StepRuntime rt,
            Diagnostic error, List<Diagnostic> diagnostics)
        {
            diagnostics.Add(error);
            var appended = Append(exec, JournalRecordType.StepNeedsReview, step.Index,
                rt.Attempt,
                new JObject
                {
                    ["error"] = error.Code,
                    ["message"] = CredentialMask.MaskText(error.Message),
                });
            if (!appended.Ok)
            {
                FailRun(exec, appended.Diagnostics, diagnostics, recordSkips: false);
                return;
            }
            rt.State = StepState.NeedsReview;
            _coordinator.Transition(exec.RunId, RunState.NeedsReview);
            exec.Finished = true;
        }

        // SPEC §E1/§E0 retry_step: the journaled review_resolved already
        // moved the Run back to Running (the coordinator's mark reaches here
        // through the loop's Tick); the reviewed step is re-armed exactly
        // like a §E7 scheduled retry — back through E4 step 1 as a fresh
        // attempt, attempt + 1, every per-attempt field cleared — so the
        // new step_started{attempt + 1} is the durable record of the
        // human-decided re-execution. The §E0 pair is asserted through the
        // same transition table every other step move uses.
        private void ResumeReviewedStep(RunExecution exec)
        {
            if (exec.Current < 0 || exec.Current >= exec.Plan.Steps.Count)
            {
                // Unreachable while the coordinator marks only a journaled
                // NeedsReview resolution — the mark is left set so the
                // inconsistency stays diagnosable instead of silently
                // spinning.
                return;
            }
            _coordinator.ClearReviewRetry(exec.RunId);
            var rt = exec.Step(exec.Plan.Steps[exec.Current]);
            rt.State = StateRules.RequireTransition(StepState.NeedsReview, StepState.Running);
            rt.Attempt++;
            rt.Changes.BeginAttempt(rt.Attempt);
            rt.Inputs = null;
            rt.Outputs.Clear();
            rt.ResumeToken = null;
            rt.Snapshot = null;
            rt.Deadline = null;
            rt.DeadlineAt = null;
            rt.NextRetryAt = null;
            rt.CancelInvoked = false;
            rt.NextPollAt = default;
            rt.LastReportedWaitingFor = null;
            exec.Finished = false;
        }

        // SPEC §E6 item 2: a Running Run's pending cancel — a Waiting step is
        // cancelled through IOperation.Cancel and journaled step_cancelled, a
        // Pending step ends with the Run; completed changes are not reverted.
        private void ProcessCancel(RunExecution exec, List<Diagnostic> diagnostics)
        {
            var reason = _coordinator.CancelReasonOf(exec.RunId);
            var masked = reason is null ? "cancelled" : CredentialMask.MaskText(reason);
            PlanStep? step = null;
            StepRuntime? rt = null;
            if (exec.Current >= 0 && exec.Current < exec.Plan.Steps.Count)
            {
                step = exec.Plan.Steps[exec.Current];
                rt = exec.Step(step);
            }
            if (step is not null && rt!.State == StepState.Waiting)
            {
                var op = _resolveOperation(step);
                if (op.Ok && op.Value is not null)
                {
                    var ctx = ContextFor(
                        exec, step, _operations.Find(step.OperationId, step.Version), rt);
                    if (InvokeOperationMember(
                            exec, rt, () => op.Value.Cancel(ctx),
                            diagnostics, out var cancelThrew))
                    {
                        // SPEC §F1: a required resource record the store
                        // refused inside Cancel ends the Run Failed — no
                        // step_cancelled or cancelled terminal record
                        // follows a refused append.
                        return;
                    }
                    if (cancelThrew is not null)
                    {
                        diagnostics.Add(OperationThrew(cancelThrew));
                    }
                    rt.CancelInvoked = true;
                }
                else
                {
                    diagnostics.AddRange(
                        op.Diagnostics.Count == 0
                            ? new[]
                            {
                                ErrorCodes.Create(
                                    ErrorCodes.OW_RUN_011,
                                    "The step's Operation cannot be resolved.",
                                    nodeId: step.NodeId),
                            }
                            : op.Diagnostics);
                }
                if (!JournalStepCancelled(exec, step, rt, masked, diagnostics))
                {
                    return;
                }
            }
            else if (step is not null && rt!.State == StepState.Running)
            {
                // A retry pending between attempts: the step ends Cancelled
                // like a Waiting one — its last journaled attempt outcome
                // stands, the cancellation is the step's end.
                if (!JournalStepCancelled(exec, step, rt, masked, diagnostics))
                {
                    return;
                }
            }
            FinishRun(
                exec, RunState.Cancelled, diagnostics,
                ErrorCodes.Create(ErrorCodes.OW_RUN_005, "The Run was cancelled."));
        }

        // The step-level half of a cancel: the step_cancelled record mirrors
        // the request's reason (masked, SPEC §F6) and the in-memory state
        // follows the journal. Returns false on the SPEC §F1 store-failure
        // path — the Run already ended Failed with the store diagnostic and
        // the caller must not write a second, cancelled terminal record.
        private bool JournalStepCancelled(
            RunExecution exec, PlanStep step, StepRuntime rt,
            string reason, List<Diagnostic> diagnostics)
        {
            var cancelledData = new JObject { ["reason"] = reason };
            if (rt.Changes.Records.Count != 0)
            {
                cancelledData["changes"] = ChangeRecordJson.Write(rt.Changes.Records);
            }
            var appended = Append(exec, JournalRecordType.StepCancelled, step.Index,
                rt.Attempt,
                cancelledData);
            if (!appended.Ok)
            {
                FailRun(exec, appended.Diagnostics, diagnostics, recordSkips: false);
                return false;
            }
            rt.State = StepState.Cancelled;
            return true;
        }

        // SPEC §E7: 500 ms doubling up to 10 s — the delay before attempt
        // failedAttempt + 1.
        private static TimeSpan Backoff(RetryPolicy retry, int failedAttempt)
        {
            var delay = retry.InitialBackoffMs * Math.Pow(retry.Multiplier, failedAttempt - 1);
            return TimeSpan.FromMilliseconds(Math.Min(delay, retry.MaxBackoffMs));
        }

        // The exhausted retry chain's terminal error: OW-RUN-008 when a
        // configured retry budget was consumed, otherwise the attempt's own
        // diagnostic (a single-attempt step fails on its own code).
        private static Diagnostic FinalAttemptError(
            PlanStep step, StepRuntime rt, Diagnostic error) =>
            rt.Attempt > 1
                ? ErrorCodes.Create(
                    ErrorCodes.OW_RUN_008,
                    "The step reached its retry limit.",
                    nodeId: step.NodeId)
                : error;

        // E4 step 8's Poll half: the wait continues through E4 steps 5–8 only —
        // inputs and resumeToken come from the journaled waiting state, and a
        // fresh operation instance is resolved (SPEC §C2 items 3–4). Returns
        // false while the step stays Waiting — the Tick ends.
        private bool PollStep(
            RunExecution exec, PlanStep step, StepRuntime rt, List<Diagnostic> diagnostics)
        {
            var op = _resolveOperation(step);
            if (!op.Ok || op.Value is null)
            {
                FailStep(
                    exec, step, rt,
                    op.Diagnostics.Count == 0
                        ? new[]
                        {
                            ErrorCodes.Create(
                                ErrorCodes.OW_RUN_011,
                                "The step's Operation cannot be resolved.",
                                nodeId: step.NodeId),
                        }
                        : op.Diagnostics,
                    diagnostics);
                return true;
            }
            var ctx = ContextFor(
                exec, step, _operations.Find(step.OperationId, step.Version), rt);
            RunAttempt(exec, step, rt, ctx, op.Value, poll: true, diagnostics);
            return rt.State != StepState.Waiting;
        }

        // ── Outcomes ─────────────────────────────────────────────────────────

        // Journals the StepFailed record and ends the Run Failed — the v1
        // E7 outcome (remaining steps become Skipped through FinishRun).
        private void FailStep(
            RunExecution exec, PlanStep step, StepRuntime rt,
            IReadOnlyList<Diagnostic> errors, List<Diagnostic> diagnostics)
        {
            diagnostics.AddRange(errors);
            var failedData = new JObject
            {
                ["error"] = errors[0].Code,
                ["message"] = CredentialMask.MaskText(errors[0].Message),
            };
            if (rt.Changes.Records.Count != 0)
            {
                failedData["changes"] = ChangeRecordJson.Write(rt.Changes.Records);
            }
            var appended = Append(exec, JournalRecordType.StepFailed, step.Index, rt.Attempt,
                failedData);
            if (!appended.Ok)
            {
                // SPEC §F1: the required StepFailed never reached the store —
                // the step is not durably Failed, rt.State keeps mirroring
                // the journal, and the Run takes the store-failure path: no
                // further step records, Failed with the store diagnostic,
                // never the step's own outcome.
                FailRun(exec, appended.Diagnostics, diagnostics, recordSkips: false);
                return;
            }
            // The step's recorded state is Failed (SPEC §E4). rt.State mirrors
            // the journal exactly like the §F2 fold does — including a
            // failure recorded before any StepStarted, which the §E0 table
            // never reaches as a runner-driven transition.
            rt.State = StepState.Failed;
            FinishRun(exec, RunState.Failed, diagnostics, errors[0]);
        }

        // A failure outside any step's records (journal write, injected seam):
        // the Run can no longer be continued safely — Failed (SPEC §F1
        // OW-STORE-001 semantics). recordSkips: false is the store-failure
        // form of SPEC §F1 — a required record refused by the store stops
        // normal processing, so no further step records are attempted.
        private void FailRun(
            RunExecution exec, IReadOnlyList<Diagnostic> errors, List<Diagnostic> diagnostics,
            bool recordSkips = true)
        {
            FinishRun(exec, RunState.Failed, diagnostics,
                errors.Count == 0 ? null : errors[0], recordSkips);
            diagnostics.AddRange(errors);
        }

        // SPEC §E1: every step still Pending when the Run ends becomes
        // Skipped — journaled before the terminal RunFinished record — and
        // the Run's §A6 outputs are collected: only values of steps that ran
        // (and only on a Succeeded Run). recoveryFinish marks the finish the
        // §F4 resume driver completes for a Run whose journal already decided
        // the end — a Run this session never ran, so an open resource whose
        // release cannot be proven ends the Run NeedsReview there instead of
        // under the decided terminal state (see ReleaseOpenResources).
        private void FinishRun(
            RunExecution exec, RunState terminal, List<Diagnostic> diagnostics, Diagnostic? error,
            bool recordSkips = true, bool recoveryFinish = false)
        {
            // The SPEC §E1 table admits no Suspended -> terminal pair
            // but Cancelled — a Suspended Run can only resume, review
            // or cancel, and the resume driver journals run_resumed and
            // moves it to Running before any finish applies (SPEC §F4
            // item 6 write-ahead). Only a required record the store
            // refused — recordSkips: false, the §F1 store-failure form —
            // still forces the assigned Failed from Suspended; every
            // other cause reaching here is an undecidable end, which
            // uncertain recovery resolves to NeedsReview (SPEC §F0) —
            // never an assigned Failed outside the transition table.
            if (_coordinator.StateOf(exec.RunId) == RunState.Suspended &&
                !StateRules.CanTransition(RunState.Suspended, terminal))
            {
                if (!recordSkips)
                {
                    _coordinator.FailRunOnStoreRefusal(
                        exec.RunId,
                        error is null ? Array.Empty<Diagnostic>() : new[] { error },
                        diagnostics);
                    exec.Finished = true;
                    return;
                }
                var reviewStep = exec.Plan.Steps[
                    exec.Current >= 0 && exec.Current < exec.Plan.Steps.Count
                        ? exec.Current
                        : 0];
                StepToNeedsReview(
                    exec, reviewStep, exec.Step(reviewStep),
                    error ?? ErrorCodes.Create(
                        ErrorCodes.OW_STORE_002,
                        "The suspended Run's end cannot be proven."),
                    diagnostics);
                return;
            }
            var steps = exec.Plan.Steps;
            for (var i = 0; recordSkips && i < steps.Count; i++)
            {
                var rt = exec.Step(steps[i]);
                if (rt.State != StepState.Pending)
                {
                    continue;
                }
                var skipped = Append(exec, JournalRecordType.StepSkipped, i, null,
                    new JObject { ["reason"] = "unreached" });
                if (!skipped.Ok)
                {
                    // SPEC §F1: the required StepSkipped never reached the
                    // store — the step stays Pending exactly like the journal
                    // shows, normal processing stops (no further step
                    // records) and the Run can only end Failed with the
                    // store diagnostic, never the interrupted outcome.
                    diagnostics.AddRange(skipped.Diagnostics);
                    terminal = RunState.Failed;
                    error = skipped.Diagnostics.Count == 0 ? error : skipped.Diagnostics[0];
                    break;
                }
                rt.State = StepState.Skipped;
            }

            // SPEC §E6 item 4: whatever Run resources are still open is
            // released before the terminal record — the release phase
            // below is shared with the marked store-failure finish.
            var releaseDiagnostics = ReleaseOpenResources(
                exec, diagnostics, out var refusal, out var unreleasable);
            if (refusal is not null)
            {
                // SPEC §F1: a required resource_released the store refused
                // stops normal processing — the Run can only end Failed
                // with the store diagnostic, never the interrupted outcome.
                terminal = RunState.Failed;
                error = refusal.Count == 0 ? error : refusal[0];
            }
            else if (recoveryFinish && unreleasable is not null)
            {
                // SPEC §F0: the §F4 driver is finishing a Run whose resources
                // another session acquired, and one of them came back neither
                // released nor provably released. The durable pairing can no
                // longer be closed by this session, so the end is undecidable:
                // the Run ends NeedsReview with its step_needs_review record —
                // never a terminal record asserting an outcome over an open
                // resource nobody can account for, and never a release
                // guessed to make the record writable. A store refusal above
                // keeps the §F1 precedence: the required record that never
                // became durable decides the end before any review.
                var reviewStep = exec.Plan.Steps[
                    exec.Current >= 0 && exec.Current < exec.Plan.Steps.Count
                        ? exec.Current
                        : 0];
                StepToNeedsReview(
                    exec, reviewStep, exec.Step(reviewStep), unreleasable, diagnostics);
                return;
            }

            var outputs = new JObject();
            if (terminal == RunState.Succeeded)
            {
                var declared = exec.Plan.Outputs;
                for (var i = 0; i < declared.Count; i++)
                {
                    var output = declared[i];
                    var value = exec.Values.Get(new ValueKey(0, 0, output.StepIndex, output.Port));
                    if (value.IsPresent)
                    {
                        outputs[output.Id] = value.Value!.ToToken();
                    }
                }
            }

            var ended = _coordinator.EndRun(
                exec.RunId, terminal, JsonValue.From(outputs),
                JsonValue.From(TerminalSummary(error, releaseDiagnostics)));
            if (!ended.Ok)
            {
                diagnostics.AddRange(ended.Diagnostics);
                if (_coordinator.HasJournaledEnd(exec.RunId))
                {
                    // The terminal record IS durable — only the lock
                    // release failed. The in-memory state follows the
                    // journaled terminal state (never a fabricated
                    // Failed); the still-held exclusion is retried by
                    // every later Tick's ReleaseEndedRunLocks — a second
                    // RunFinished is never written.
                    _coordinator.ConvergeToJournaledEnd(exec.RunId);
                }
                else
                {
                    // The terminal record could not be written: the
                    // refusal takes the centralized §F1 store-failure
                    // path like every other refused required append —
                    // the Run is marked Failed and the next Tick's
                    // Run-end cleanup journals the single
                    // run_finished{failed}, then the still-held
                    // exclusion is released through
                    // ReleaseEndedRunLocks. A refusal of that record
                    // is still reported and left for the §F4 recovery
                    // engine — it is never rewritten.
                    _coordinator.FailRunOnStoreRefusal(
                        exec.RunId, ended.Diagnostics, diagnostics);
                }
            }
            exec.Finished = true;
        }

        // SPEC §F1: every Run the coordinator marked for the store-failure
        // end is finished through the same Run-end cleanup as every other
        // terminal transition — the mark is the authority: a marked Run's
        // in-memory state is already Failed, so it never reaches the
        // active-Run driver below, and a NeedsReview Run's finished
        // execution may still hold the open resources its marked end owes.
        private void FinishPendingStoreFailures(List<Diagnostic> diagnostics)
        {
            foreach (var pair in _executions)
            {
                var exec = pair.Value;
                var refused = _coordinator.PendingStoreFailureOf(exec.RunId);
                if (refused is null)
                {
                    continue;
                }
                FinishStoreFailedRun(exec, refused, diagnostics);
            }
        }

        // The loop-driven half of a marked Run's SPEC §F1 store-failure
        // end: the §E6 item 4 release runs exactly like FinishRun's —
        // released where the store accepts the resource_released record,
        // kept durable for the §F4 recovery engine where it does not —
        // then the coordinator journals the single run_finished{failed};
        // no step records are attempted (the store-failure policy, never
        // recordSkips).
        private void FinishStoreFailedRun(
            RunExecution exec, IReadOnlyList<Diagnostic> refused, List<Diagnostic> diagnostics)
        {
            // The unreleasable signal is not routed here: SPEC §F1 assigns this
            // end — a required record never became durable — and the store
            // failure's own diagnostic is the Run's outcome, decided
            // independently of what the release phase could prove.
            var releaseDiagnostics = ReleaseOpenResources(
                exec, diagnostics, out var refusal, out _);
            var error = refused.Count == 0 ? null : refused[0];
            if (refusal is not null && refusal.Count != 0)
            {
                error = refusal[0];
            }
            _coordinator.EndStoreFailedRun(
                exec.RunId,
                JsonValue.From(TerminalSummary(error, releaseDiagnostics)),
                diagnostics);
            exec.Finished = true;
        }

        // The coordinator-bound half of a human review abort (T-W1.8-09):
        // a NeedsReview Run's SPEC §E6 item 4 release outside the Tick —
        // the same <see cref="ReleaseOpenResources"/> FinishRun runs, on
        // the Run's execution (created and seeded from the journal fold
        // when this loop never drove it). The phase's own diagnostics land
        // in <paramref name="releaseDiagnostics"/> exactly like the Tick's
        // list; the return is the first refused <c>resource_released</c>
        // append's diagnostics — the §F1 store-failure signal — or null.
        private IReadOnlyList<Diagnostic>? ReleaseRunResourcesAtEnd(
            string runId, List<Diagnostic> releaseDiagnostics)
        {
            var exec = ExecutionFor(runId);
            if (exec is null)
            {
                // The coordinator tracks a Run whose Plan it can no longer
                // supply — an internal inconsistency, never a runtime
                // outcome; the Run fails closed on the store-failure path.
                var error = InternalError("The Run has no tracked Plan.");
                releaseDiagnostics.Add(error);
                return new[] { error };
            }
            // The unreleasable signal is not routed here either: a human
            // decided this Run's end through the review resolution, so a
            // release this session cannot prove is reported and left open in
            // the journal for the next review — never rewritten back into
            // NeedsReview behind the decision that produced this cleanup.
            ReleaseOpenResources(exec, releaseDiagnostics, out var refusal, out _);
            // The terminal outcome was applied — the Run is done.
            exec.Finished = true;
            return refusal;
        }

        // SPEC §E6 item 4: the Run-end resource cleanup under the
        // two-fact release model (TrackedRunResource.PhysicallyHeld +
        // RunResourceJournalState). ONE pass walks the acquisitions in the order
        // the durable pairing applies its records (see below) and completes each
        // acquisition before the next one starts: the seam releases THAT
        // acquisition's object, named by
        // the two halves of the acquisition scope it needs (the Run id of the
        // Run being released and the
        // resource's own AcquiringStep — a (kind, data) pair alone would let a
        // release unload an identically named session somebody else opened),
        // and its resource_released record is appended IMMEDIATELY after that
        // one release succeeded, never after every unload of the Run. Unloading
        // everything in a first pass and journalising in a second left an
        // interruption holding every object it had already unloaded while the
        // journal still showed every acquisition open; interleaved, the window
        // an interruption reaches is one acquisition's own release, and every
        // other acquisition of the Run is either fully closed or untouched.
        // A seam failure collects its diagnostic and the entry stays
        // PhysicallyHeld (the object was never released — its acquisition
        // stays outstanding in the fold for the §F4 recovery engine) and the
        // pass CONTINUES with the next pair. An entry the Operation released
        // inside its own step is already not PhysicallyHeld — including one
        // whose first append the store refused — so its record is written here
        // too, after its object really went: the release runs first and the
        // resource_released record follows it: a crash in between leaves
        // an open acquisition recovery can release again (the journaled
        // pairing stays the durable truth). A refused append keeps that
        // entry Acquired with its diagnostic and the pass CONTINUES with
        // the next entry — the first refusal reports through
        // <paramref name="refusal"/> and the store-failure policy decides
        // the outcome, never the interrupted one. No resource_released is
        // ever journaled for an Unjournaled entry (without the
        // acquisition record it would be an unpaired record the §F2 fold
        // refuses) or for one the seam could not release (still
        // PhysicallyHeld). Open resources with no release seam are a
        // wiring defect — reported, never a silent skip. The returned
        // list holds the phase's own diagnostics, ridden on the Tick AND
        // the terminal record's summary — the Tick's list is transient,
        // so the durable evidence the §F4 recovery engine sees must be
        // journaled.
        //
        // WHY AN UNPROVABLE ACQUISITION IS REPORTED SEPARATELY. An entry the
        // §F2 fold rebuilt (TrackedRunResource.FromRecoveredProjection) names an
        // object this session never loaded, so only the resource handler's own
        // proof can settle it: the session-side binding of its acquisition, or
        // that handler's durable evidence that its own release already unloaded
        // it. A seam refusal on such an entry therefore reports "its physical
        // state cannot be proven", not "an object this session holds could not
        // be released", and <paramref name="unreleasable"/> carries that
        // diagnostic: the §F4 recovery finish that asked for the release ends
        // the Run NeedsReview instead of journaling a terminal record over a
        // durable pairing it can no longer close (SPEC §F0 — uncertain recovery
        // is never an automatic outcome and never a guessed release). An entry
        // this session acquired itself keeps the established outcome: the
        // refusal is reported, the acquisition stays open in the fold, and the
        // Run's decided end stands.
        //
        // THE RELEASE ORDER IS THE FOLD'S PAIRING ORDER. A
        // resource_released record names its acquisition only by
        // (kind, data) — the §F2 fold closes the OLDEST still-open
        // acquisition carrying that pair — so with two acquisitions of one
        // (kind, data) the physical unload and the durable pairing agree only
        // if the seam unloads the OLDEST of them first. The single pass
        // walks one PairingOrder: distinct (kind, data) pairs keep the
        // reverse-acquisition order this cleanup always had, while the
        // members of a single pair go oldest first. On a seam failure the
        // YOUNGER members of THAT pair are left held (every other pair is
        // still released, and never stopping early across pairs): the pair's
        // journaled releases then count exactly the unloads it performed, so
        // the fold closes precisely the acquisitions whose object is gone and
        // retains precisely the ones still open — which is what lets recovery
        // or a later Run end release a surviving object through the very
        // acquisition that still owns it. Releasing past a failure inside one
        // pair would hand the fold's first record to the failed (still-open)
        // acquisition while retaining the released one, leaving the surviving
        // object owned by no acquisition the §F4 engine can name, and its
        // release would then be refused forever.
        private List<Diagnostic> ReleaseOpenResources(
            RunExecution exec, List<Diagnostic> diagnostics,
            out IReadOnlyList<Diagnostic>? refusal, out Diagnostic? unreleasable)
        {
            refusal = null;
            unreleasable = null;
            var releaseDiagnostics = new List<Diagnostic>();
            var resources = exec.Resources;
            var held = 0;
            for (var i = 0; i < resources.Count; i++)
            {
                if (resources[i].PhysicallyHeld)
                {
                    held++;
                }
            }
            if (held != 0 && _releaseResource is null)
            {
                releaseDiagnostics.Add(InternalError(
                    "Open Run resources remain but no release seam is wired."));
            }
            var order = PairingOrder(resources);
            // One entry per (kind, data) pair whose release the seam refused:
            // the pair's remaining members stay held.
            var refusedPairs = new List<TrackedRunResource>();
            for (var o = 0; o < order.Count; o++)
            {
                var entry = resources[order[o]];
                if (entry.PhysicallyHeld)
                {
                    if (_releaseResource is null || IsInRefusedPair(refusedPairs, entry))
                    {
                        continue;
                    }
                    var released = _releaseResource(
                        exec.RunId,
                        new RunResource(entry.Kind, entry.Data)
                        {
                            AcquiringStep = entry.Step,
                        });
                    if (!released.Ok)
                    {
                        releaseDiagnostics.AddRange(released.Diagnostics);
                        refusedPairs.Add(entry);
                        if (entry.FromRecoveredProjection && unreleasable is null)
                        {
                            unreleasable = released.Diagnostics.Count == 0
                                ? InternalError(
                                    "A recovered Run resource's release reported no diagnostic.")
                                : released.Diagnostics[0];
                        }
                        continue;
                    }
                    // This acquisition's object is gone, so its pairing record
                    // follows at once: an interruption from here on reaches at
                    // most this one acquisition's own release, and never the
                    // whole cleanup with every unload already done.
                    entry.PhysicallyHeld = false;
                }
                if (entry.Journal != RunResourceJournalState.Acquired)
                {
                    continue;
                }
                var releasedRecord = Append(exec, JournalRecordType.ResourceReleased,
                    null, null,
                    new JObject
                    {
                        ["kind"] = entry.Kind,
                        ["data"] = entry.Data?.ToToken() ?? JValue.CreateNull(),
                    });
                if (!releasedRecord.Ok)
                {
                    releaseDiagnostics.AddRange(releasedRecord.Diagnostics);
                    refusal ??= releasedRecord.Diagnostics;
                    continue;
                }
                entry.Journal = RunResourceJournalState.ReleaseRecorded;
            }
            diagnostics.AddRange(releaseDiagnostics);
            return releaseDiagnostics;
        }

        // The index order ReleaseOpenResources walks, for the rule stated
        // there. Every tracked entry is placed exactly once: a (kind, data)
        // pair enters at the position its NEWEST member holds in the
        // reverse-acquisition sweep — the order distinct pairs have always
        // been released in — and contributes its members oldest-first, the
        // order the §F2 fold applies their resource_released records.
        private static List<int> PairingOrder(List<TrackedRunResource> resources)
        {
            var order = new List<int>(resources.Count);
            var placed = new bool[resources.Count];
            for (var newest = resources.Count - 1; newest >= 0; newest--)
            {
                if (placed[newest])
                {
                    continue;
                }
                for (var older = 0; older <= newest; older++)
                {
                    if (placed[older] || !SameResourcePair(resources[newest], resources[older]))
                    {
                        continue;
                    }
                    placed[older] = true;
                    order.Add(older);
                }
            }
            return order;
        }

        // The §F2 pairing identity of a Run resource: one kind and equal
        // data. The data equality is the registry's, which is the fold's own,
        // so the cleanup can never group a pair the fold would pair
        // differently.
        private static bool SameResourcePair(
            TrackedRunResource first, TrackedRunResource second) =>
            string.Equals(first.Kind, second.Kind, StringComparison.Ordinal) &&
            StepResourceRegistry.SameData(first.Data, second.Data);

        // True when this entry's (kind, data) pair already had a release
        // refused, so its remaining members stay held for recovery.
        private static bool IsInRefusedPair(
            List<TrackedRunResource> refusedPairs, TrackedRunResource entry)
        {
            for (var i = 0; i < refusedPairs.Count; i++)
            {
                if (SameResourcePair(refusedPairs[i], entry))
                {
                    return true;
                }
            }
            return false;
        }

        // The terminal record's summary: {code, message} stays the
        // decided error; the release phase's diagnostics ride on the
        // additive "diagnostics" member so they are durable with the
        // record (compatible: records without the member keep their
        // meaning). The coordinator's review-abort end shares the shape
        // through this internal seam — never a duplicated format.
        internal static JObject TerminalSummary(
            Diagnostic? error, List<Diagnostic> releaseDiagnostics)
        {
            var summary = error is null
                ? new JObject()
                : new JObject
                {
                    ["code"] = error.Code,
                    ["message"] = CredentialMask.MaskText(error.Message),
                };
            if (releaseDiagnostics.Count != 0)
            {
                var recorded = new JArray();
                for (var i = 0; i < releaseDiagnostics.Count; i++)
                {
                    recorded.Add(new JObject
                    {
                        ["code"] = releaseDiagnostics[i].Code,
                        ["message"] =
                            CredentialMask.MaskText(releaseDiagnostics[i].Message),
                    });
                }
                summary["diagnostics"] = recorded;
            }
            return summary;
        }

        /// <summary>
        /// The event-time suspend of SPEC §1.4 / §E3 item 4 used by the Unity
        /// wiring's <c>playModeStateChanged</c> hook (T-W2.1-04): entering
        /// Play Mode must journal <c>run_suspended</c> at the event itself —
        /// a domain reload can follow before the next Tick — so the active
        /// Running Run is suspended outside the tick loop under the same
        /// write-ahead order (record Flushed, then the in-memory
        /// transition). A Run that is not Running is left exactly as
        /// journaled.
        /// </summary>
        internal Result<Unit> SuspendActive(string reason)
        {
            var diagnostics = new List<Diagnostic>();
            var active = _coordinator.ActiveHandle();
            if (active is null || active.State != RunState.Running)
            {
                return Result<Unit>.Success(Unit.Value, diagnostics);
            }
            var exec = ExecutionFor(active.RunId);
            if (exec is null)
            {
                return Result<Unit>.Failure(InternalError(
                    "The active Run has no tracked Plan."));
            }
            Suspend(exec, reason, diagnostics);
            return ErrorsOf(diagnostics).Count == 0
                ? Result<Unit>.Success(Unit.Value, diagnostics)
                : Result<Unit>.Failure(diagnostics);
        }

        // SPEC §E3 item 4: the Run is suspended and the Tick does nothing
        // further; a RunSuspended record (Flushed) precedes the transition.
        private void Suspend(RunExecution exec, string reason, List<Diagnostic> diagnostics)
        {
            var appended = Append(exec, JournalRecordType.RunSuspended, null, null,
                new JObject { ["reason"] = reason });
            if (!appended.Ok)
            {
                FailRun(exec, appended.Diagnostics, diagnostics, recordSkips: false);
                return;
            }
            _coordinator.Transition(exec.RunId, RunState.Suspended);
        }

        // ── SPEC §F4 resume driver (journal state -> continued Run) ──────────

        /// <summary>
        /// The Core resume driver of SPEC §F4 items 2 and 5–6: rebuilds the
        /// Runner state and the SPEC §E5 ValueStore purely from the journal
        /// reconstruction the Run Store produces, then continues the Run —
        /// the resumed state is journaled <c>run_resumed</c> (write-ahead)
        /// and the §E2 exclusion is held through the coordinator's adoption
        /// before anything is driven.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The last state-changing step record decides (SPEC §F4 item 5):
        /// <c>StepSucceeded</c> continues at the Plan's Transitions target,
        /// <c>StepWaiting</c> resumes <see cref="IOperation.Poll"/> on a new
        /// Operation instance from the journaled resumeToken alone — with
        /// the original journaled deadline, never a reset (SPEC §E6 item 1),
        /// a <c>StepFailed</c> carrying a decided retry resumes Running at
        /// the recorded attempt after its not-before instant without
        /// exceeding MaxAttempts (SPEC §E7), and a bare
        /// <c>StepStarted</c> — including one left by a dropped trailing
        /// line — is decided by the single §F5 table through
        /// <see cref="ResumePolicy.Decide"/>: Reexecute re-enters E4 step 1
        /// as the next attempt, AdoptOutputs is journaled
        /// <c>step_succeeded{reconciled: true}</c>, and everything
        /// undecidable — an Unsafe declaration, a Reconcile
        /// <see cref="ReconcileResult.Unknown"/>, an Operation that cannot
        /// be resolved, journaled state the rebuild cannot interpret —
        /// ends in NeedsReview, never in automatic success or re-execution.
        /// </para>
        /// <para>
        /// A run-level NeedsReview snapshot — an unknown required record,
        /// middle corruption, an unknown or mismatched version or a plan
        /// digest failure, all surfaced by the store as NeedsReview — is
        /// adopted for the human decision path and never driven (SPEC
        /// §F0/§F2). A decided-but-unrecorded end (a terminal step record
        /// or a journaled abort with no <c>run_finished</c>) completes its
        /// finish: the §E6 item 4 release runs once and the terminal record
        /// is journaled — a Suspended Run whose decided end is Failed
        /// resumes to Running first, the §E1 table's only way to it.
        /// A Queued or AwaitingApproval Run is adopted only —
        /// its own gate applies. Resume is idempotent for an already
        /// tracked Run; a Suspended one is re-evaluated against the §E3
        /// item 4 Editor-state gate and resumed only when it clears.
        /// </para>
        /// </remarks>
        /// <returns>
        /// Success with the Run's state after the driver ran — Running,
        /// Suspended, NeedsReview or a terminal state — or failure when the
        /// store or the §E2 exclusion refuse the resume.
        /// </returns>
        /// <param name="offeredLock">
        /// The <c>coordinator.lock</c> handle the SPEC §F4 recovery engine of
        /// T-W4.3-01 already holds for its whole pass, handed over so the §F4
        /// item 6 hand-over happens inside that same critical section
        /// (<see cref="RunCoordinator.AdoptRecoveredRun(RunSnapshot, CoordinatorRunLock?)"/>);
        /// null — the live suspend/resume and test callers — makes the adoption
        /// acquire the exclusion itself.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="runId"/> is null.</exception>
        internal Result<RunHandle> Resume(
            string runId, CoordinatorRunLock? offeredLock = null)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            var diagnostics = new List<Diagnostic>();

            // Idempotent re-resume: the Run is already tracked this session.
            // Only a Suspended one still needs the §E3 item 4 gate
            // re-evaluation — every other tracked state answers as recorded.
            if (_coordinator.StateOf(runId) is { } tracked)
            {
                if (tracked != RunState.Suspended)
                {
                    return Result<RunHandle>.Success(new RunHandle(runId, tracked));
                }
                if (ResumeGateBlocked())
                {
                    return Result<RunHandle>.Success(new RunHandle(runId, RunState.Suspended));
                }
                if (_executions.ContainsKey(runId))
                {
                    // A live suspend/resume cycle (SPEC §1.4): the in-memory
                    // projection already mirrors the journal — resuming is
                    // just the write-ahead record and the transition.
                    var resumed = _coordinator.AppendRecord(
                        runId, JournalRecordType.RunResumed, null, null,
                        JsonValue.From(new JObject { ["reason"] = "journal_recovery" }));
                    if (!resumed.Ok)
                    {
                        _coordinator.FailRunOnStoreRefusal(runId, resumed.Diagnostics, diagnostics);
                        return Result<RunHandle>.Failure(diagnostics);
                    }
                    _coordinator.Transition(runId, RunState.Running);
                    return Result<RunHandle>.Success(new RunHandle(runId, RunState.Running));
                }
                // The Run was adopted under the still-blocked gate and
                // returned before its journal was rebuilt — no in-memory
                // projection exists, so resuming directly would drive an
                // EMPTY one (every journaled step state and the §E5 store
                // lost, a started-only step re-executed without the §F5
                // consult). Fall through to the recovery path: the rebuild
                // and its §F4 item 5 / §F5 evaluation decide before the
                // Run may transition to Running.
            }

            var loaded = _coordinator.LoadRun(runId);
            if (!loaded.Ok || loaded.Value is null)
            {
                return Result<RunHandle>.Failure(
                    loaded.Diagnostics.Count == 0
                        ? new[]
                        {
                            ErrorCodes.Create(ErrorCodes.OW_RUN_010, "The Run is unknown."),
                        }
                        : loaded.Diagnostics);
            }
            var snapshot = loaded.Value;
            diagnostics.AddRange(loaded.Diagnostics);

            var adopted = _coordinator.AdoptRecoveredRun(snapshot, offeredLock);
            if (!adopted.Ok)
            {
                return Result<RunHandle>.Failure(adopted.Diagnostics);
            }
            diagnostics.AddRange(adopted.Diagnostics);
            var handle = adopted.Value!;

            // Nothing to drive: an undecodable Plan (NeedsReview by
            // construction), an already journaled end, or a Run that was
            // never started — its own gates (start, approval, review) apply.
            if (snapshot.Plan is null || snapshot.Ended ||
                snapshot.State == RunState.Queued ||
                snapshot.State == RunState.AwaitingApproval)
            {
                return Result<RunHandle>.Success(handle, diagnostics);
            }

            if (snapshot.State == RunState.Suspended && ResumeGateBlocked())
            {
                // The §E3 item 4 gate still holds the Run Suspended — adopted
                // under its lock, resumable by a later call (SPEC §1.4).
                return Result<RunHandle>.Success(handle, diagnostics);
            }

            var exec = ExecutionFor(runId);
            if (exec is null)
            {
                // The coordinator adopted the Run but cannot supply its Plan
                // — an internal inconsistency, not a runtime outcome.
                return Result<RunHandle>.Failure(InternalError(
                    "The resumed Run has no tracked Plan."));
            }
            if (!RebuildExecution(exec, snapshot, diagnostics))
            {
                // The rebuild itself decided the state (NeedsReview was
                // journaled) or the Run took the §F1 store-failure path.
                return Result<RunHandle>.Success(
                    new RunHandle(runId, _coordinator.StateOf(runId) ?? RunState.NeedsReview),
                    diagnostics);
            }

            switch (snapshot.State)
            {
                case RunState.NeedsReview:
                    // Adopted for the human decision path only (SPEC §E1);
                    // a journaled review_resolved never arrives here.
                    exec.Finished = true;
                    return Result<RunHandle>.Success(handle, diagnostics);
                case RunState.Cancelled:
                    // A decided-but-unrecorded end: the journaled decision
                    // completes — skips, the §E6 item 4 release, then the
                    // single run_finished record (never a run_resumed).
                    FinishRun(
                        exec, RunState.Cancelled, diagnostics,
                        ErrorCodes.Create(
                            ErrorCodes.OW_RUN_005, "The Run was cancelled."),
                        recoveryFinish: true);
                    return Result<RunHandle>.Success(handle, diagnostics);
            }

            // SPEC §F4 item 3 (R4): every remaining step's recorded
            // fingerprints are compared with the current Registry — a
            // removed, changed or failed registration ends the resume
            // NeedsReview, standard and custom alike.
            var mismatched = _coordinator.FirstRemainingFingerprintMismatch(snapshot);
            if (mismatched is not null)
            {
                StepToNeedsReview(
                    exec, mismatched, exec.Step(mismatched),
                    ErrorCodes.Create(
                        ErrorCodes.OW_RUN_011,
                        "An Operation used by the Plan was deleted or changed since the Plan was validated.",
                        nodeId: mismatched.NodeId),
                    diagnostics);
                return Result<RunHandle>.Success(
                    new RunHandle(
                        runId, _coordinator.StateOf(runId) ?? RunState.NeedsReview),
                    diagnostics);
            }

            // SPEC §F4 item 4: a journal session that differs from this
            // Editor's proves an Editor restart — the rebuilt projection
            // already dropped every dead session key (handle) per the
            // producing record's own session inside RebuildExecution, and
            // a scene.modify that completed after the last scene.save may
            // have lost unsaved changes: OW-RUN-012 NeedsReview.
            if (snapshot.Session is { } journaledSession &&
                !string.Equals(
                    journaledSession, _coordinator.EditorSessionId,
                    StringComparison.Ordinal))
            {
                var unsaved = FirstUnsavedSceneModify(exec.Plan, snapshot);
                if (unsaved is not null)
                {
                    StepToNeedsReview(
                        exec, unsaved, exec.Step(unsaved),
                        ErrorCodes.Create(
                            ErrorCodes.OW_RUN_012,
                            "The Editor restarted with unsaved scene changes.",
                            nodeId: unsaved.NodeId),
                        diagnostics);
                    return Result<RunHandle>.Success(
                        new RunHandle(
                            runId, _coordinator.StateOf(runId) ?? RunState.NeedsReview),
                        diagnostics);
                }
            }

            // Running — or a Suspended Run whose gate cleared. The last
            // state-changing step record decides the continuation (§F4 5).
            var last = LastStepOf(snapshot);
            if (last is not null &&
                (last.State == StepState.Failed || last.State == StepState.Cancelled ||
                 last.State == StepState.Skipped))
            {
                // The Run was interrupted inside its own finish: complete
                // the decided end. A Suspended Run whose decided end is
                // Failed resumes to Running first — the §E1 table admits
                // no Suspended -> Failed pair; every other decided end
                // writes no run_resumed.
                CompleteInterruptedEnd(exec, snapshot, last, diagnostics);
                return Result<RunHandle>.Success(
                    new RunHandle(runId, _coordinator.StateOf(runId) ?? handle.State),
                    diagnostics);
            }

            // A bare StepStarted decides through the §F5 table BEFORE the
            // resume is journaled — a NeedsReview verdict means the Run
            // never resumed at all.
            PlanStep? decidedStep = null;
            StepRuntime? decidedRt = null;
            var decision = ResumeDecision.NeedsReview;
            IReadOnlyDictionary<string, JsonValue>? adoptedOutputs = null;
            if (last is { State: StepState.Running, RetryNotBefore: null })
            {
                decidedStep = exec.Plan.Steps[last.Index];
                decidedRt = exec.Step(decidedStep);
                if (!DecideStartedOnly(
                        exec, decidedStep, decidedRt, diagnostics,
                        out decision, out adoptedOutputs))
                {
                    return Result<RunHandle>.Success(
                        new RunHandle(runId, _coordinator.StateOf(runId) ?? RunState.NeedsReview),
                        diagnostics);
                }
            }

            // SPEC §F4 item 6 write-ahead — and the §E1 table's only way
            // a Suspended Run may reach a finish: run_resumed and the
            // Suspended -> Running transition precede the recovery
            // decision's own records, so an adoption whose outputs fail
            // validation ends through the normal Running -> Failed
            // finish with its step_skipped records, never a Suspended
            // -> Failed pair outside the table.
            if (!JournalRunResumed(exec, diagnostics))
            {
                return Result<RunHandle>.Failure(diagnostics);
            }
            if (_coordinator.StateOf(runId) == RunState.Suspended)
            {
                _coordinator.Transition(runId, RunState.Running);
            }
            if (decidedStep is not null)
            {
                ApplyStartedOnlyDecision(
                    exec, decidedStep, decidedRt!, decision, adoptedOutputs, diagnostics);
            }
            handle = new RunHandle(runId, _coordinator.StateOf(runId) ?? RunState.Running);
            return Result<RunHandle>.Success(handle, diagnostics);
        }

        // SPEC §F4 item 4's restart check: the earliest scene.modify step
        // the journal shows completed AFTER the last completed scene.save —
        // its changes may be unsaved across the restart. The order compared
        // is the journal's own record order (SucceededSeq), never the step
        // index: a reordered Transitions table completes steps out of index
        // order. A scene.modify whose completion position the journal
        // cannot show is never proven saved — it counts as modified after
        // every save; a scene.save with no position protects nothing.
        private static PlanStep? FirstUnsavedSceneModify(
            CompiledPlan plan, RunSnapshot snapshot)
        {
            var lastSaveSeq = -1L;
            var steps = snapshot.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                var snap = steps[i];
                if (snap.State == StepState.Succeeded &&
                    snap.Index >= 0 && snap.Index < plan.Steps.Count &&
                    HasEffect(plan.Steps[snap.Index], EffectNames.SceneSave))
                {
                    lastSaveSeq = Math.Max(lastSaveSeq, snap.SucceededSeq ?? -1L);
                }
            }
            PlanStep? unsaved = null;
            var unsavedSeq = long.MaxValue;
            for (var i = 0; i < steps.Count; i++)
            {
                var snap = steps[i];
                if (snap.State != StepState.Succeeded ||
                    snap.Index < 0 || snap.Index >= plan.Steps.Count ||
                    !HasEffect(plan.Steps[snap.Index], EffectNames.SceneModify))
                {
                    continue;
                }
                var seq = snap.SucceededSeq ?? long.MaxValue;
                if (seq > lastSaveSeq && seq < unsavedSeq)
                {
                    unsavedSeq = seq;
                    unsaved = plan.Steps[snap.Index];
                }
            }
            return unsaved;
        }

        private static bool HasEffect(PlanStep step, string effect)
        {
            var effects = step.Effects;
            for (var i = 0; i < effects.Count; i++)
            {
                if (string.Equals(effects[i], effect, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // SPEC §E3 item 4 for the resume driver: the same gate the Tick's
        // head applies — Play Mode or an Editor-busy period OpWeave did not
        // start keeps a Suspended Run suspended.
        private bool ResumeGateBlocked() =>
            !_editorState.IsEditMode ||
            ((_editorState.IsCompiling || _editorState.IsUpdating) &&
             !_opWeaveInitiatedBusy());

        // The journaled last state-changing step record's snapshot, or null.
        private static StepSnapshot? LastStepOf(RunSnapshot snapshot)
        {
            if (snapshot.LastStepIndex is not { } index)
            {
                return null;
            }
            var steps = snapshot.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Index == index)
                {
                    return steps[i];
                }
            }
            return null;
        }

        // SPEC §F4 item 6: the resume is journaled before the in-memory
        // continuation (write-ahead). A refused run_resumed sends the Run
        // down the §F1 store-failure path — the resume never durably
        // happened, so nothing is driven.
        private bool JournalRunResumed(RunExecution exec, List<Diagnostic> diagnostics)
        {
            var resumed = Append(exec, JournalRecordType.RunResumed, null, null,
                new JObject { ["reason"] = "journal_recovery" });
            if (!resumed.Ok)
            {
                _coordinator.FailRunOnStoreRefusal(
                    exec.RunId, resumed.Diagnostics, diagnostics);
                return false;
            }
            return true;
        }

        // The §F5 consult for a step left with only StepStarted — the
        // decided retry's not-before member already routes the other
        // Running case. The decision is split from its application: the
        // caller journals run_resumed and moves a Suspended Run to
        // Running BEFORE the decided outcome's records are applied
        // (SPEC §F4 item 6 write-ahead), so a failed adoption ends
        // through the normal Running -> Failed finish — the §E1 table
        // admits no Suspended -> Failed pair. Returns false when the
        // decided outcome already diverted the Run — NeedsReview was
        // journaled (Unsafe, Reconcile Unknown, an unresolvable
        // Operation or an interpreter failure) or the §F1 store-failure
        // end took it — and true with the decision for the caller to
        // apply after the journaled resume.
        private bool DecideStartedOnly(
            RunExecution exec, PlanStep step, StepRuntime rt, List<Diagnostic> diagnostics,
            out ResumeDecision decision,
            out IReadOnlyDictionary<string, JsonValue>? adopted)
        {
            decision = ResumeDecision.NeedsReview;
            adopted = null;
            var op = _resolveOperation(step);
            if (!op.Ok || op.Value is null)
            {
                // The §F5 consult cannot run — uncertain is never an
                // automatic outcome (same shape as FailAttempt's).
                StepToNeedsReview(
                    exec, step, rt,
                    op.Diagnostics.Count == 0
                        ? ErrorCodes.Create(
                            ErrorCodes.OW_RUN_011,
                            "The step's Operation cannot be resolved.",
                            nodeId: step.NodeId)
                        : op.Diagnostics[0],
                    diagnostics);
                return false;
            }
            var ctx = ContextFor(
                exec, step, _operations.Find(step.OperationId, step.Version), rt);
            var decided = ResumeDecision.NeedsReview;
            IReadOnlyDictionary<string, JsonValue>? restored = null;
            if (InvokeOperationMember(
                    exec, rt,
                    () => decided = ResumePolicy.Decide(
                        step, op.Value, ctx, rt.Snapshot, out restored),
                    diagnostics, out var threw))
            {
                return false;
            }
            if (threw is not null)
            {
                // Decide rejects only a null argument — a runner bug,
                // surfaced like every other broken injected seam.
                FailRun(exec, new[] { InternalError(threw.GetType().FullName) }, diagnostics);
                return false;
            }
            if (decided == ResumeDecision.NeedsReview)
            {
                StepToNeedsReview(
                    exec, step, rt,
                    ErrorCodes.Create(
                        ErrorCodes.OW_RUN_011,
                        "The interrupted step cannot be resumed safely.",
                        nodeId: step.NodeId),
                    diagnostics);
                return false;
            }
            decision = decided;
            adopted = restored;
            return true;
        }

        // The deferred half of a bare-StepStarted §F5 decision, applied
        // only after run_resumed is durable and the Run stands Running:
        // AdoptOutputs journals the adopted success itself — a refused
        // record or a failed output validation then takes the step's
        // ordinary Running outcome (SPEC §F1, §E1) — and Reexecute
        // re-arms the step as the next attempt so the next Tick
        // re-enters E4 step 1.
        private void ApplyStartedOnlyDecision(
            RunExecution exec, PlanStep step, StepRuntime rt,
            ResumeDecision decision, IReadOnlyDictionary<string, JsonValue>? adopted,
            List<Diagnostic> diagnostics)
        {
            switch (decision)
            {
                case ResumeDecision.AdoptOutputs:
                    AdoptStepOutputs(exec, step, rt, adopted!, diagnostics);
                    return;
                case ResumeDecision.Reexecute:
                    // SPEC §F5: attempt+1 — the crashed attempt never
                    // completed; the next Tick re-enters E4 step 1 and
                    // journals step_started{attempt+1} before any side
                    // effect, exactly like a scheduled retry.
                    rt.Attempt++;
                    rt.Changes.BeginAttempt(rt.Attempt);
                    rt.Inputs = null;
                    rt.Outputs.Clear();
                    rt.ResumeToken = null;
                    rt.Snapshot = null;
                    rt.Deadline = null;
                    rt.DeadlineAt = null;
                    rt.NextRetryAt = null;
                    rt.CancelInvoked = false;
                    rt.NextPollAt = default;
                    rt.LastReportedWaitingFor = null;
                    return;
                default:
                    // Unreachable while DecideStartedOnly gates the
                    // outcomes — an undecidable residue is NeedsReview,
                    // never an automatic outcome.
                    StepToNeedsReview(
                        exec, step, rt,
                        ErrorCodes.Create(
                            ErrorCodes.OW_RUN_011,
                            "The interrupted step cannot be resumed safely.",
                            nodeId: step.NodeId),
                        diagnostics);
                    return;
            }
        }

        // The decided-but-unrecorded end behind a terminal step record: the
        // journaled cause determines the terminal state — a terminal
        // StepFailed ends Failed, a StepCancelled or the pending cancel
        // ends Cancelled, and skips whose cause the journal cannot show are
        // undecidable → NeedsReview, never a guessed end (SPEC §F0).
        private void CompleteInterruptedEnd(
            RunExecution exec, RunSnapshot snapshot, StepSnapshot last,
            List<Diagnostic> diagnostics)
        {
            switch (last.State)
            {
                case StepState.Failed:
                    FinishInterruptedFailure(
                        exec, diagnostics, RecoveredError(snapshot, last));
                    return;
                case StepState.Cancelled:
                    FinishRun(
                        exec, RunState.Cancelled, diagnostics,
                        ErrorCodes.Create(
                            ErrorCodes.OW_RUN_005, "The Run was cancelled."),
                        recoveryFinish: true);
                    return;
                default:
                    // StepSkipped last: skips are only journaled inside a
                    // finish. The recorded cause decides — a terminal
                    // StepFailed ends Failed, a pending cancel Cancelled;
                    // anything else cannot be proven.
                    for (var i = 0; i < snapshot.Steps.Count; i++)
                    {
                        if (snapshot.Steps[i].State == StepState.Failed)
                        {
                            FinishInterruptedFailure(
                                exec, diagnostics,
                                RecoveredError(snapshot, snapshot.Steps[i]));
                            return;
                        }
                    }
                    if (snapshot.CancelPending)
                    {
                        FinishRun(
                            exec, RunState.Cancelled, diagnostics,
                            ErrorCodes.Create(
                                ErrorCodes.OW_RUN_005, "The Run was cancelled."),
                            recoveryFinish: true);
                        return;
                    }
                    NeedsReviewRebuild(
                        exec, snapshot,
                        ErrorCodes.Create(
                            ErrorCodes.OW_STORE_002,
                            "The journal ends inside a Run finish whose cause cannot be proven."),
                        diagnostics);
                    return;
            }
        }

        // The decided-but-unrecorded Failed end. A Run still standing
        // Suspended resumes to Running first — the §E1 table admits no
        // Suspended -> Failed pair, so the write-ahead run_resumed and
        // the transition precede the finish, which then completes
        // through the normal Running -> Failed path with its
        // step_skipped records; a refused run_resumed takes the §F1
        // store-failure path and nothing further is driven.
        private void FinishInterruptedFailure(
            RunExecution exec, List<Diagnostic> diagnostics, Diagnostic? error)
        {
            if (_coordinator.StateOf(exec.RunId) == RunState.Suspended)
            {
                if (!JournalRunResumed(exec, diagnostics))
                {
                    return;
                }
                _coordinator.Transition(exec.RunId, RunState.Running);
            }
            FinishRun(exec, RunState.Failed, diagnostics, error, recoveryFinish: true);
        }

        // The terminal StepFailed's recorded diagnostic, when the journal
        // carried a published code — audit data for the run_finished
        // summary; an unpublished/absent one leaves a null error.
        private static Diagnostic? RecoveredError(RunSnapshot snapshot, StepSnapshot step) =>
            step.FailureCode is { } code && ErrorCodes.IsPublished(code)
                ? ErrorCodes.Create(
                    code,
                    step.FailureMessage ?? "The step failed.",
                    nodeId: snapshot.Plan is { } plan &&
                        step.Index >= 0 && step.Index < plan.Steps.Count
                            ? plan.Steps[step.Index].NodeId
                            : null)
                : null;

        // A resume-time NeedsReview for journaled state the rebuild cannot
        // interpret: the step_needs_review record makes the fold itself
        // reproduce the outcome — attached to the last recorded step (the
        // resume's point of uncertainty), or step 0 when no step exists.
        private void NeedsReviewRebuild(
            RunExecution exec, RunSnapshot snapshot, Diagnostic error,
            List<Diagnostic> diagnostics)
        {
            var index = snapshot.LastStepIndex ?? 0;
            var step = index >= 0 && index < exec.Plan.Steps.Count
                ? exec.Plan.Steps[index]
                : exec.Plan.Steps[0];
            StepToNeedsReview(exec, step, exec.Step(step), error, diagnostics);
        }

        // The journal-faithful rebuild of the in-flight projection: the
        // §E5 ValueStore from StepSucceeded outputs, every journaled step
        // state, the §F4 item 5 resumption point and the §E6/§E7 journaled
        // deadline and retry not-before instants. Returns false when the
        // recorded state cannot be interpreted — the Run then ends
        // NeedsReview (uncertain is never an automatic outcome).
        private bool RebuildExecution(
            RunExecution exec, RunSnapshot snapshot, List<Diagnostic> diagnostics)
        {
            var plan = snapshot.Plan!;
            var restored = ValueStore.Restore(
                snapshot, _types, _coordinator.EditorSessionId);
            if (!restored.Ok)
            {
                NeedsReviewRebuild(
                    exec, snapshot,
                    restored.Diagnostics.Count == 0
                        ? ErrorCodes.Create(
                            ErrorCodes.OW_STORE_002,
                            "The recorded step outputs cannot be restored.")
                        : restored.Diagnostics[0],
                    diagnostics);
                return false;
            }
            exec.Values = restored.Value!;
            // The session the Plan-side values were committed in — the
            // journal's first record — decides whether the resolved
            // execution inputs, constants and defaults still hold live
            // handles; the step outputs' own provenance was applied
            // inside Restore.
            exec.PlanSessionStale =
                snapshot.CreationSession is { } creationSession &&
                !string.Equals(
                    creationSession, _coordinator.EditorSessionId,
                    StringComparison.Ordinal);

            var steps = snapshot.Steps;
            var inFlight = 0;
            for (var i = 0; i < steps.Count; i++)
            {
                var snap = steps[i];
                if (snap.Index < 0 || snap.Index >= plan.Steps.Count)
                {
                    NeedsReviewRebuild(
                        exec, snapshot,
                        ErrorCodes.Create(
                            ErrorCodes.OW_STORE_002,
                            "The journal records a step outside the Plan."),
                        diagnostics);
                    return false;
                }
                var rt = exec.Step(plan.Steps[snap.Index]);
                rt.State = snap.State;
                rt.Attempt = snap.Attempt;
                // SPEC §F2 attempt-durability restore (§F4 item 5): a step
                // left Waiting continues the SAME attempt, so its change
                // recorder is seeded with the cumulative list the last
                // StepWaiting journaled — the StepSucceeded, StepFailed or
                // StepCancelled that ends the attempt then carries the whole
                // attempt. Any other recorded state opens its attempt's own
                // list: a retry-pending step's next attempt starts empty.
                if (snap.State == StepState.Waiting)
                {
                    if (!TryReadSnapshotChanges(
                            snap, out var journaledChanges, out var changesFailure))
                    {
                        NeedsReviewRebuild(
                            exec, snapshot,
                            ErrorCodes.Create(
                                ErrorCodes.OW_STORE_002,
                                "The recorded step changes cannot be interpreted.",
                                nodeId: plan.Steps[snap.Index].NodeId,
                                hint: changesFailure),
                            diagnostics);
                        return false;
                    }
                    rt.Changes.RestoreAttempt(snap.Attempt, journaledChanges!);
                }
                else
                {
                    rt.Changes.BeginAttempt(snap.Attempt);
                }
                rt.ResumeToken = snap.ResumeToken;
                rt.Snapshot = snap.PreflightSnapshot;
                rt.Deadline = snap.Deadline;
                rt.LastReportedWaitingFor = snap.WaitingFor;
                if (snap.Deadline is { } deadline)
                {
                    if (!TryParseInstant(deadline, out var deadlineAt))
                    {
                        NeedsReviewRebuild(
                            exec, snapshot,
                            ErrorCodes.Create(
                                ErrorCodes.OW_STORE_002,
                                "The recorded step deadline cannot be interpreted.",
                                nodeId: plan.Steps[snap.Index].NodeId),
                            diagnostics);
                        return false;
                    }
                    rt.DeadlineAt = deadlineAt;
                }
                if (snap.RetryNotBefore is { } notBefore)
                {
                    if (!TryParseInstant(notBefore, out var retryAt))
                    {
                        NeedsReviewRebuild(
                            exec, snapshot,
                            ErrorCodes.Create(
                                ErrorCodes.OW_STORE_002,
                                "The recorded retry time cannot be interpreted.",
                                nodeId: plan.Steps[snap.Index].NodeId),
                            diagnostics);
                        return false;
                    }
                    rt.NextRetryAt = retryAt;
                }
                if (snap.State == StepState.Running || snap.State == StepState.Waiting)
                {
                    inFlight++;
                }
            }
            // A sequential runner can never leave two steps in flight —
            // recorded state contradicting that cannot be interpreted.
            if (inFlight > 1)
            {
                NeedsReviewRebuild(
                    exec, snapshot,
                    ErrorCodes.Create(
                        ErrorCodes.OW_STORE_002,
                        "The journal leaves more than one step unfinished."),
                    diagnostics);
                return false;
            }

            // SPEC §F4 item 5's resumption point: the last state-changing
            // step record; its Transitions target after a Succeeded (never
            // index + 1), otherwise the step itself. A record outside the
            // Plan cannot be interpreted.
            var lastStep = LastStepOf(snapshot);
            if (snapshot.LastStepIndex is { } current &&
                (current < 0 || current >= plan.Steps.Count))
            {
                NeedsReviewRebuild(
                    exec, snapshot,
                    ErrorCodes.Create(
                        ErrorCodes.OW_STORE_002,
                        "The journal records a step outside the Plan."),
                    diagnostics);
                return false;
            }
            if (lastStep is null)
            {
                exec.Current = 0;
            }
            else if (lastStep.State == StepState.Succeeded)
            {
                var step = plan.Steps[lastStep.Index];
                if (!step.Transitions.TryGetValue(NextPort, out var next))
                {
                    throw new InvalidOperationException(
                        $"Plan step '{step.NodeId}' has no '{NextPort}' transition — a Plan defect.");
                }
                exec.Current = next;
            }
            else
            {
                exec.Current = lastStep.Index;
            }

            // A resumed Waiting — or the §F5 consult for a bare
            // StepStarted — runs its step context against the inputs the
            // journaled values resolve to; an unresolvable one is
            // undecidable, never guessed.
            if (lastStep is { State: StepState.Waiting } ||
                lastStep is { State: StepState.Running, RetryNotBefore: null })
            {
                var step = plan.Steps[lastStep.Index];
                var rt = exec.Step(step);
                var resolved = exec.Values.ResolveInputs(step, plan.Inputs);
                if (!resolved.Ok)
                {
                    NeedsReviewRebuild(
                        exec, snapshot,
                        resolved.Diagnostics.Count == 0
                            ? ErrorCodes.Create(
                                ErrorCodes.OW_RUN_007,
                                "The step's recorded inputs cannot be restored.",
                                nodeId: step.NodeId)
                            : resolved.Diagnostics[0],
                        diagnostics);
                    return false;
                }
                rt.Inputs = exec.PlanSessionStale
                    ? StripPlanInputs(
                        exec, step,
                        _operations.Find(step.OperationId, step.Version),
                        resolved.Value!)
                    : resolved.Value;
            }

            // A journaled review_resolved{retry_step} whose re-arm never
            // journaled — the durable decision outlived the session it was
            // taken in: restore the parked projection (the reviewed step
            // NeedsReview at its journaled attempt, the execution finished)
            // so the next Tick re-arms it through ResumeReviewedStep —
            // attempt + 1 and the pending mark consumed — never a bare
            // ExecuteStep at the journaled attempt (SPEC §E1, §F4 item 5).
            if (RunCoordinator.ReviewRetryPending(snapshot))
            {
                exec.Current = snapshot.ReviewRetryStep!.Value;
                exec.Finished = true;
            }
            return true;
        }

        // The §0.4 millisecond UTC timestamp form every journaled instant
        // uses; an unparseable one is recorded state the resume cannot
        // interpret — the caller fails closed.
        private static bool TryParseInstant(string text, out DateTimeOffset instant) =>
            DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out instant);

        // ── Bookkeeping ──────────────────────────────────────────────────────

        private Result<Unit> Append(
            RunExecution exec, JournalRecordType type, int? step, int? attempt, JObject data) =>
            _coordinator.AppendRecord(exec.RunId, type, step, attempt, JsonValue.From(data));

        // The log-record seam handed to each step's StepLogSink.
        private Result<Unit> AppendLogRecord(
            string runId, JournalRecordType type, int? step, JsonValue data) =>
            _coordinator.AppendRecord(runId, type, step, null, data);

        // SPEC §0.4: the budget is a duration — measured on the monotonic
        // timer, never the wall clock.
        private bool BudgetExceeded(IMonotonicTimer tickTimer) =>
            tickTimer.Elapsed >= TimeSpan.FromMilliseconds(_tickBudgetMs);

        private RunExecution? ExecutionFor(string runId)
        {
            if (_executions.TryGetValue(runId, out var exec))
            {
                return exec;
            }
            var plan = _coordinator.PlanOf(runId);
            if (plan is null)
            {
                return null;
            }
            exec = new RunExecution(runId, plan, this);
            SeedOpenResources(exec);
            _executions.Add(runId, exec);
            return exec;
        }

        // SPEC §E6 item 4's "released after a reload": a RunExecution built
        // for a Run whose journal already records acquisitions — a rebuilt
        // in-memory projection, and later the §F4 resume driver's path —
        // seeds its open-resource list from the journal fold (every
        // ResourceAcquired without a matching ResourceReleased) so the Run
        // end still releases them exactly once. A Run created in this
        // domain folds to none; a store that cannot produce the snapshot
        // leaves the list empty exactly like the in-memory projection —
        // the journal stays the durable truth for the §F4 engine.
        private void SeedOpenResources(RunExecution exec)
        {
            var snapshot = _coordinator.Get(exec.RunId);
            if (snapshot is null)
            {
                return;
            }
            var open = snapshot.OpenResources;
            for (var i = 0; i < open.Count; i++)
            {
                // A rebuilt projection: acquired and still held — the
                // releasing process is gone, so the seam owes it one call.
                // The step of its record travels with it: §F5's Completed
                // evidence belongs to the step that acquired the resource,
                // and an entry whose record carried no step belongs to none.
                // Marked as rebuilt because this session never saw the object:
                // a release the seam refuses for such an entry reports that its
                // physical state cannot be proven, which is what lets the §F4
                // finish end NeedsReview instead of asserting an outcome over
                // an acquisition nobody can account for.
                exec.Resources.Add(new TrackedRunResource(
                    open[i].AcquiringStep, open[i].Kind, open[i].Data,
                    RunResourceJournalState.Acquired)
                {
                    FromRecoveredProjection = true,
                });
            }
        }

        private static List<Diagnostic> ErrorsOf(IReadOnlyList<Diagnostic> diagnostics)
        {
            var errors = new List<Diagnostic>();
            for (var i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i].Severity == Severity.Error)
                {
                    errors.Add(diagnostics[i]);
                }
            }
            return errors;
        }

        private static Diagnostic OperationThrew(Exception e) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_006,
                "The Operation threw an exception.",
                hint: e.GetType().FullName);

        private static Diagnostic InternalError(string? detail) =>
            ErrorCodes.Create(
                ErrorCodes.OW_API_004,
                "An internal runner error occurred.",
                hint: detail);

        // The inputsDigest input: only present inputs take part — a Missing
        // port is never serialized (SPEC §A0.1).
        private static JObject PresentInputs(IReadOnlyDictionary<string, InputValue> inputs)
        {
            var present = new JObject();
            foreach (var pair in inputs)
            {
                if (pair.Value.IsPresent)
                {
                    present[pair.Key] = pair.Value.Value!.ToToken();
                }
            }
            return present;
        }

        // Reads the §F7 element list a journaled StepWaiting persisted for the
        // resumed attempt — absent is the SPEC §F2 backward-compatible "no
        // durable interim changes" (pre-decision journals and empty lists),
        // a malformed element is a failure the caller fails closed on.
        private static bool TryReadSnapshotChanges(
            StepSnapshot snap,
            out List<ChangeRecord>? changes, out string? failure)
        {
            changes = new List<ChangeRecord>();
            failure = null;
            if (snap.Changes is null)
            {
                return true;
            }
            if (snap.Changes.ToToken() is not JArray elements)
            {
                failure = "The step_waiting changes member is not an array.";
                changes = null;
                return false;
            }
            for (var i = 0; i < elements.Count; i++)
            {
                if (!ChangeRecordJson.TryReadElement(
                        elements[i], snap.Attempt,
                        "a step_waiting journal record",
                        out var element, out failure))
                {
                    changes = null;
                    return false;
                }
                changes!.Add(element!);
            }
            return true;
        }

        // The live per-Run bookkeeping: the journal is the durable truth;
        // this is only the in-flight projection of it for the current
        // Editor session (recovery rebuilds from the journal, T-W1.8-08).
        private sealed class RunExecution
        {
            private readonly Dictionary<int, StepRuntime> _steps = new Dictionary<int, StepRuntime>();
            private readonly RunLoop _loop;

            internal RunExecution(string runId, CompiledPlan plan, RunLoop loop)
            {
                RunId = runId;
                Plan = plan;
                _loop = loop;
                Values = ValueStore.Empty();
            }

            internal string RunId { get; }
            internal CompiledPlan Plan { get; }

            // The §E5 store — rebuilt from the journal on a resume.
            internal ValueStore Values { get; set; }

            // The Run-level tracked-resource list of SPEC §E6 item 4 —
            // every resource acquired in this session (journaled or not)
            // plus the fold's OpenResources reconstruction after a reload.
            // Each entry carries the two independent facts of the release
            // model: PhysicallyHeld (until the Operation or the release
            // seam released it in this process) and its journal state.
            internal List<TrackedRunResource> Resources { get; } =
                new List<TrackedRunResource>();

            // The current step index; -1 when a transition reached End.
            internal int Current { get; set; }

            // True once the terminal outcome was applied — the Run is done.
            internal bool Finished { get; set; }

            // True when the journal's first record — the session the
            // Plan-side values (execution inputs, constants, defaults)
            // were committed in — was stamped in another Editor session
            // (SPEC §F4 item 4): their Ref 'handle' keys are dead and every
            // input dictionary resolved from the Plan is stripped at the
            // declared Ref ports. The §E5 store needs no flag: each output
            // was already judged by its own producing record's session on
            // rebuild, and a Waiting step's resumeToken is opaque
            // operation data the discard never rewrites.
            internal bool PlanSessionStale { get; set; }

            internal StepRuntime Step(PlanStep step)
            {
                if (!_steps.TryGetValue(step.Index, out var rt))
                {
                    rt = new StepRuntime(
                        step.Index, step.NodeId, RunId, _loop, Resources);
                    _steps.Add(step.Index, rt);
                }
                return rt;
            }
        }

        // One step's in-flight state: the recorded state the journal shows
        // (mirroring the §F2 fold) plus the attempt's working values.
        private sealed class StepRuntime
        {
            internal StepRuntime(
                int index, string nodeId, string runId, RunLoop loop,
                List<TrackedRunResource> resources)
            {
                Changes = new StepChangeRecorder(index, nodeId);
                Log = new StepLogSink(runId, index, loop.AppendLogRecord);
                Resources = new StepResourceRegistry(
                    runId, index, resources,
                    (type, step, data) => loop.AppendLogRecord(runId, type, step, data));
            }

            internal StepState State = StepState.Pending;
            internal int Attempt = 1;
            internal IReadOnlyDictionary<string, InputValue>? Inputs;
            internal readonly Dictionary<string, JsonValue> Outputs =
                new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            internal JsonValue? ResumeToken;
            internal JsonValue? Snapshot;
            internal string? Deadline;
            internal DateTimeOffset? DeadlineAt;
            internal DateTimeOffset? NextRetryAt;
            internal bool CancelInvoked;
            internal DateTimeOffset NextPollAt;
            internal string? LastReportedWaitingFor;
            internal DateTimeOffset LastProgressAt;
            internal readonly StepChangeRecorder Changes;
            internal readonly StepLogSink Log;
            internal readonly StepResourceRegistry Resources;
        }
    }
}
