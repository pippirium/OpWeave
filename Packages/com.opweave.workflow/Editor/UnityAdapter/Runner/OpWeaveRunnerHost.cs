#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Recovery;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Editor lifecycle wiring of SPEC §1.4/§E3 (T-W2.1-04): owns the Core
    /// <see cref="RunCoordinator"/> and <see cref="RunLoop"/> inside the
    /// Editor, drives them from <see cref="EditorApplication.update"/>, and
    /// honours the Editor lifecycle events — Buffered journal records are
    /// flushed on <see cref="AssemblyReloadEvents.beforeAssemblyReload"/> and
    /// <see cref="EditorApplication.quitting"/> (SPEC §F3 item 1), entering
    /// Play Mode suspends the active Run with a journaled
    /// <c>run_suspended</c> (SPEC §1.4, §E3 item 4), and the first
    /// <see cref="EditorApplication.delayCall"/> runs the ordered startup
    /// hook list (Registry rebuild before recovery, SPEC §F4 item 1).
    /// </summary>
    /// <remarks>
    /// <para><b>Construction.</b> The host builds the production chain —
    /// <see cref="FileRunStore"/> rooted at the injected
    /// <paramref name="runStoreRoot"/> (<c>Library/OpWeave/</c> in
    /// production), the §E2 coordinator over the
    /// <see cref="CoordinatorRunLock"/> exclusion, and the §E3 loop with the
    /// real seams (§G3 undo scope, §G1 requirement evaluators through
    /// <see cref="IRequirementEvaluator"/>, injected
    /// <paramref name="currentRegistry"/> so a later Registry rebuild —
    /// T-W2.2-07 — is picked up on the next read without rewiring). The
    /// <see cref="ChangeSetTracker"/> it hands every step context is the SPEC §F7
    /// <c>ctx.Changes</c> recorder, and the same tracker writes the Run's
    /// <c>changes.json</c> inside the Run's durable completion path through
    /// the run loop's pre-terminal artifact seam (a refused write ends the
    /// Run Failed, SPEC §F1 OW-STORE-001).</para>
    /// <para><b>Acceptance gates.</b> <see cref="AcceptRun"/> applies the
    /// ordered <see cref="IRunAcceptanceGate"/> list before the Core accept —
    /// the SG-18 order settings error → stale Registry → Plan permission —
    /// so a refusal happens before any side effect and reports the gate's own
    /// diagnostic. The settings gate (<see cref="SettingsErrorGate"/>) is
    /// always first, immediately followed by the configured-integrity-level
    /// refusal (<see cref="IntegrityLevelGate"/>, SPEC §H4.1's
    /// OW-OP-011-at-acceptance, judged on the settings snapshot the first
    /// gate just validated); further gates register through
    /// <paramref name="additionalGates"/>. The settings snapshot each
    /// admission validates is recorded per accepted Run — the §G1
    /// <c>path_writable</c> check reads exactly it for the Run it belongs
    /// to (<see cref="ActiveRunAllowedPaths"/>).</para>
    /// <para><b>Tests.</b> Tests never invoke
    /// <see cref="EditorApplication.update"/> itself (re-entering UTF's own
    /// callback corrupts the run): <see cref="Tick"/>,
    /// <see cref="OnPlayModeStateChanged"/>, <see cref="FlushJournal"/> and
    /// <see cref="RunStartupHooks"/> are the internal seams the event
    /// subscriptions call, driven directly by fixtures.</para>
    /// </remarks>
    internal sealed class OpWeaveRunnerHost : IDisposable
    {
        private readonly string _runStoreRoot;
        private readonly FileRunStore _store;
        private readonly Func<Result<OpWeaveSettings>> _loadSettings;
        private readonly RunCoordinator _coordinator;
        private readonly RunLoop _loop;
        private readonly RunRecoveryEngine _recovery;
        private readonly OpWeaveApprovalService _approvals;
        private readonly SettingsErrorGate _settingsGate;
        private readonly IReadOnlyList<IRunAcceptanceGate> _gates;
        private readonly RunResourceReleaseDispatcher _releaseResources;
        private readonly ChangeSetTracker _changeSets;
        private readonly ConsoleLogCapture _logCapture;
        private readonly Func<RegistrySnapshot> _currentRegistry;
        private readonly Func<bool> _debugLogging;
        private readonly StartupHookList _startupHooks = new StartupHookList();
        private bool _installed;
        private bool _startupRan;

        // The G2 resolver the Unity StepContexts share: stateless — every
        // Resolve/ToRef re-evaluates the live Editor state — so one instance
        // serves every step. prefab-contents: scene keys resolve through the
        // Run resources G6 opens, which the §E6 item 4 release registration
        // below already names: the contents of the journaled Prefab guid are
        // re-found in their preview scene (SPEC §G2, §G6).
        private static readonly RefResolver s_refResolver =
            new RefResolver(PrefabContentsResources.FindEditingRoot);

        // The settings snapshot each accepted Run's admission validated —
        // and each recovered Run's re-attachment loaded (see
        // RecordRecoveredRunSettings) — keyed by Run id: the §G1
        // path_writable authority while that Run executes (see
        // ActiveRunAllowedPaths).
        private readonly Dictionary<string, OpWeaveSettings> _acceptedSettings =
            new Dictionary<string, OpWeaveSettings>(StringComparer.Ordinal);

        /// <summary>
        /// Builds the full host over the injected seams (see the class
        /// remarks). <paramref name="editorSessionId"/> is the SessionState
        /// GUID stamped into lock.json and every journal record (SPEC §E2
        /// item 3, §F2); <paramref name="currentRegistry"/> is re-read for
        /// every lookup so a rebuilt snapshot takes over without rewiring;
        /// <paramref name="requirements"/> is the requirement-evaluator
        /// factory, invoked once during construction with the host itself so
        /// the evaluator can read <see cref="ActiveRunAllowedPaths"/> — the
        /// settings snapshot validated at each Run's acceptance — when its
        /// checks run (never during the factory call itself);
        /// <paramref name="opWeaveInitiatedBusy"/> reports whether an
        /// in-progress compile/import was triggered by an OpWeave node — the
        /// §1.4 Waiting-vs-Suspended distinction;
        /// <paramref name="isBatchMode"/> reports whether the Editor is
        /// non-interactive — the <see cref="NonInteractiveApprovalGate"/>
        /// refuses approval-carrying Plans with OW-PERM-004 and the approval
        /// service's decisions fail the same way (SPEC §H4, T-W3.4-02);
        /// <paramref name="releaseResources"/> is the SPEC §E6 item 4
        /// release dispatcher the run loop calls at Run end — a fresh
        /// fail-safe table (every kind unregistered → diagnostic, resource
        /// left journaled-open) when omitted;
        /// <paramref name="debugLogging"/> is the lazily read SPEC §F6
        /// SG-08 policy the host hands to the coordinator's write-path gate
        /// and to the Unity Console half of <see cref="DebugLog"/> — omitted
        /// means the SPEC default <c>debugLogging: false</c>;
        /// <paramref name="monotonicTimerFactory"/> replaces the Tick
        /// budget's <see cref="StopwatchMonotonicTimer"/> source — a test
        /// seam only; omitted keeps the production stopwatch.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="runStoreRoot"/> or <paramref name="editorSessionId"/>
        /// is null or empty, or <paramref name="tickBudgetMs"/> is not positive,
        /// or <paramref name="additionalGates"/> contains a null entry.
        /// </exception>
        /// <exception cref="ArgumentNullException">Any other seam is null.</exception>
        internal OpWeaveRunnerHost(
            string runStoreRoot,
            string editorSessionId,
            IClock clock,
            IEditorState editorState,
            Func<Result<OpWeaveSettings>> loadSettings,
            Func<RegistrySnapshot> currentRegistry,
            Func<OpWeaveRunnerHost, IRequirementEvaluator> requirements,
            Func<string, IDisposable> openUndoScope,
            Func<bool> opWeaveInitiatedBusy,
            Func<bool> isBatchMode,
            int tickBudgetMs,
            IReadOnlyList<IRunAcceptanceGate>? additionalGates = null,
            RunResourceReleaseDispatcher? releaseResources = null,
            Func<bool>? debugLogging = null,
            Func<IMonotonicTimer>? monotonicTimerFactory = null)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.", nameof(runStoreRoot));
            }
            if (string.IsNullOrEmpty(editorSessionId))
            {
                throw new ArgumentException(
                    "The Editor session id must not be null or empty.", nameof(editorSessionId));
            }
            if (clock is null)
            {
                throw new ArgumentNullException(nameof(clock));
            }
            if (editorState is null)
            {
                throw new ArgumentNullException(nameof(editorState));
            }
            if (loadSettings is null)
            {
                throw new ArgumentNullException(nameof(loadSettings));
            }
            if (currentRegistry is null)
            {
                throw new ArgumentNullException(nameof(currentRegistry));
            }
            if (requirements is null)
            {
                throw new ArgumentNullException(nameof(requirements));
            }
            if (openUndoScope is null)
            {
                throw new ArgumentNullException(nameof(openUndoScope));
            }
            if (opWeaveInitiatedBusy is null)
            {
                throw new ArgumentNullException(nameof(opWeaveInitiatedBusy));
            }
            if (isBatchMode is null)
            {
                throw new ArgumentNullException(nameof(isBatchMode));
            }
            if (tickBudgetMs <= 0)
            {
                throw new ArgumentException(
                    "tickBudgetMs must be positive.", nameof(tickBudgetMs));
            }
            var settingsGate = new SettingsErrorGate(loadSettings);
            _settingsGate = settingsGate;
            _loadSettings = loadSettings;
            // The SG-18 settings stage closes with the integrity-level
            // refusal of SPEC §H4.1 (T-W2.9-02): a configured
            // extensions.integrityLevel=full is a valid value, not a
            // settings error, and refuses every new Run with OW-OP-011 —
            // judged on the snapshot the settings gate just validated,
            // never downgraded to weak (SPEC §0.1 F-05).
            var gates = new List<IRunAcceptanceGate>
            {
                settingsGate,
                new IntegrityLevelGate(settingsGate),
            };
            if (additionalGates is not null)
            {
                foreach (var gate in additionalGates)
                {
                    if (gate is null)
                    {
                        throw new ArgumentException(
                            "The gate list must not contain null entries.", nameof(additionalGates));
                    }
                    gates.Add(gate);
                }
            }
            // The Plan-permission position of the SG-18 order (settings
            // error → stale Registry → Plan permission): a non-interactive
            // Editor can never produce the human decision an
            // approvals-carrying Plan waits for (SPEC §H4).
            gates.Add(new NonInteractiveApprovalGate(isBatchMode));
            _gates = gates;
            _releaseResources = releaseResources ?? new RunResourceReleaseDispatcher();
            _changeSets = new ChangeSetTracker(runStoreRoot);
            _logCapture = new ConsoleLogCapture();
            _currentRegistry = currentRegistry;
            // The same SPEC default as the coordinator's own: an omitted
            // predicate means debugLogging: false (SPEC §H4.1).
            _debugLogging = debugLogging ?? (static () => false);

            _runStoreRoot = runStoreRoot;
            _store = new FileRunStore(runStoreRoot, editorSessionId);
            _coordinator = new RunCoordinator(
                _store,
                (runId, plan, document, actor) =>
                    _store.CreateRun(runId, plan, document, actor),
                editorState,
                clock,
                currentRegistry,
                () => AcquireRunLock(),
                editorSessionId,
                debugLogging: debugLogging);
            var types = new ForwardingTypeRegistry(currentRegistry);
            _loop = new RunLoop(
                _coordinator,
                clock,
                editorState,
                types,
                new ForwardingOperationSource(currentRegistry),
                step => ResolveOperation(currentRegistry, step),
                requirements(this),
                openUndoScope,
                opWeaveInitiatedBusy,
                tickBudgetMs,
                monotonicTimerFactory: monotonicTimerFactory,
                releaseResource: _releaseResources.Release,
                stepContextFactory: request => CreateUnityStepContext(request, types));
            _recovery = new RunRecoveryEngine(
                _coordinator,
                _loop,
                _store,
                () => RunLockHandoff.TryObtain(_runStoreRoot),
                RunLockHandoff.PreviousOwnerTerminationProven);
            _approvals = new OpWeaveApprovalService(
                _coordinator, currentRegistry, clock, isBatchMode,
                static () => Environment.UserName);
            // SPEC §F7: the Run's change list is finalized with the Run — the
            // coordinator's single run_finished writer invokes this seam once
            // per Run end, after the §E6 item 4 release and BEFORE the
            // terminal record, on every end path (the Tick's ends and the
            // coordinator's own, like a human review abort). A write the Run
            // Store refuses ends the Run Failed with OW-STORE-001 (SPEC §F1).
            _coordinator.BindRunEndArtifactFinalizer(FinalizeChangeSetForRunEnd);
            // The §E8 subscription stays for the attribution-window
            // bookkeeping alone — the finalize above is the single
            // finalization path, so a terminal state only closes the window.
            _coordinator.Events.RunStateChanged += OnRunStateChangedForChangeSet;
            // SPEC §F6: the Console capture follows the same attribution
            // window — a Running Run owns the entries of its latest attempt.
            _coordinator.Events.RunStateChanged += _logCapture.OnRunStateChanged;
            // SPEC §F8: a Run reaching a terminal state is one of the two
            // retention triggers (the other is the startup hook). The pass
            // only selects candidates and delegates their deletion — the
            // journaled terminal record is already durable, so this
            // subscription can never disturb the Run it follows.
            _coordinator.Events.RunStateChanged += OnRunStateChangedForRetention;
        }

        // The §F7 writer the coordinator's single run_finished writer calls
        // inside the durable completion path — BEFORE the terminal record,
        // so a write the Run Store refuses can end the Run Failed with
        // OW-STORE-001 (SPEC §F1) instead of leaving a Succeeded Run whose
        // change list is missing or stale. The refusal is reported — the §F6
        // log record plus the Console line — and returned so the Run ends
        // with it.
        private IReadOnlyList<Diagnostic>? FinalizeChangeSetForRunEnd(string runId)
        {
            var failure = _changeSets.FinalizeRun(runId);
            if (failure is null)
            {
                return null;
            }
            JournalChangeSetFailure(runId, failure);
            return new[] { failure };
        }

        // The §F7 attribution-window bookkeeping: a Run only owns import
        // reports while it is executing, so the tracker follows the §E8 Run
        // states — the change list itself is finalized once, pre-terminal,
        // through the coordinator's writer, never here.
        private void OnRunStateChangedForChangeSet(RunStateChanged change)
        {
            try
            {
                _changeSets.OnRunStateChanged(change);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        // One §F6 log record on the Run whose change list never reached the Run
        // Store, carrying the store diagnostic's own code and message: the Run
        // Viewer, get_run_logs and any later reader of the journal see the same
        // OW-STORE-001 the Console does. The record is Buffered per the §F2 table,
        // so §F3 item 1's flush is what makes the failure durable — the next
        // reload or quit must not be what decides whether it was ever written.
        // Only the diagnostic's own fixed English text is journaled; the exception
        // detail behind it (a path, a sharing violation) stays in the Console line,
        // where §F6's credential masking does not have to catch up with it.
        private void JournalChangeSetFailure(string runId, Diagnostic failure)
        {
            var payload = new JObject
            {
                ["level"] = JsonNaming.ToSnakeCase(failure.Severity.ToString()),
                ["message"] = failure.Message,
                ["code"] = failure.Code,
            };
            var appended = _coordinator.AppendRecord(
                runId, JournalRecordType.Log, null, null, JsonValue.From(payload));
            var flushed = _store.FlushPending();
            var text = "OpWeave: finalizing the change list of Run " + runId +
                " failed: " + failure.Code + " " + failure.Message;
            if (failure.Hint is { } hint)
            {
                text += " (" + hint + ")";
            }
            if (!appended.Ok)
            {
                text += "; the journal record failed: " +
                    DescribeDiagnostics(appended.Diagnostics);
            }
            if (!flushed.Ok)
            {
                text += "; the journal flush failed: " +
                    DescribeDiagnostics(flushed.Diagnostics);
            }
            Debug.LogError(text);
        }

        /// <summary>
        /// Acquires the <see cref="CoordinatorRunLock"/> exclusion for a new Run
        /// and records the OS handle this domain now holds, so a domain reload —
        /// which per SPEC §E2 item 3 does not release the exclusion while the
        /// Editor process lives — can confirm it instead of assuming it was lost.
        /// </summary>
        private Result<CoordinatorRunLock> AcquireRunLock()
        {
            var acquired = CoordinatorRunLock.TryAcquire(_runStoreRoot);
            if (acquired.Ok && acquired.Value is { } held)
            {
                RunLockHandoff.RecordHandle(_runStoreRoot, held.LockHandleValue);
            }
            return acquired;
        }

        // The §E3 loop's StepContext factory (SPEC §0.6 SG-06): builds the
        // Unity implementation with the port→Type ID maps each conversion
        // needs — the loop's SG-06 static input types (an edge-fed port's
        // source output type, a bound workflow input's declared type, a
        // constant/default port's declared type) and the Plan's resolved
        // output types — over the Run's own Registry snapshot.
        //
        // ctx.Changes is the SPEC §F7 recorder of this step attempt, not the
        // loop's bare step recorder: the Operation's explicit records and the
        // asset changes the import pipeline reports for the same step land on one
        // Run change list (T-W2.4-02).
        //
        // ctx.Log is the SPEC §F6 sink this step attempt's capture writes
        // through (T-W2.4-03): OpenAttempt makes the Run's step sink the
        // latest Console attribution target, and the context applies the
        // §F6 provenance stamping itself — the operationId/origin members
        // belong to the shared step-context path, not the host's capture.
        private StepContext CreateUnityStepContext(
            StepContextRequest request, ITypeRegistry types)
        {
            var origin = _currentRegistry()
                .Find(request.Step.OperationId, request.Step.Version)?.Origin;
            var runId = ActiveRunId();
            var stepJournal =
                runId is null ? null : JournaledChanges.PathOf(_runStoreRoot, runId);
            // The §G4 wait's restart-durable error channel: an import error
            // journaled after the last persisted resume token is read back
            // from the step's own §F6 RunLog — the channel an Editor
            // restart cannot lose. It names the same (Run, step) the §F6
            // Console capture binds below; with no active Run there is
            // nothing to read. The third argument hands the waiting capture
            // the §F3 item 1 flush itself: an error-level record journaled
            // while the §G4 wait owns the step is made durable at append
            // time, not at the next beforeAssemblyReload/quitting hook an
            // abrupt termination never runs.
            ImportErrorCapture.BindStepLog(
                stepJournal, request.Step.Index, FlushJournalSafely);
            var context = new UnityStepContext(
                request.Inputs,
                request.InputTypes,
                request.Step.OutputTypes,
                types,
                s_refResolver,
                _changeSets.OpenAttempt(
                    runId,
                    request.Changes,
                    request.Step.Index,
                    request.Step.NodeId,
                    request.Attempt),
                _logCapture.OpenAttempt(
                    runId,
                    request.Log,
                    request.Step.Index,
                    request.Step.OperationId,
                    origin),
                request.ResumeToken,
                operationId: request.Step.OperationId,
                operationOrigin: origin)
            {
                Resources = request.Resources,
            };
            // The compile wait's twin channel (SPEC §G4): the pipeline's
            // per-assembly callback journals each captured error through
            // the in-flight step's §F6 sink — carrying the step index the
            // record is attributed to — and the same §F3 item 1 flush makes
            // it durable at append time.
            CompileErrorCapture.BindStepLog(
                stepJournal, request.Step.Index, context.Log, FlushJournalSafely);
            return context;
        }

        // The Run the step being built belongs to. SPEC §E2 admits one executing
        // Run per Editor and the loop builds a step context for no other, so the
        // active handle is its owner; with none the attempt records on the loop's
        // recorder alone, because a change list may not invent an owner.
        private string? ActiveRunId() => _coordinator.ActiveHandle()?.RunId;

        /// <summary>The ordered startup hook list of SPEC §1.4 / §F4 item 1.</summary>
        internal StartupHookList StartupHooks => _startupHooks;

        /// <summary>
        /// The SPEC §E6 item 4 release dispatcher the host's run loop calls at
        /// Run end — the registration seam each resource-acquiring Operation
        /// wiring populates for its resource kind.
        /// </summary>
        internal RunResourceReleaseDispatcher ResourceReleases => _releaseResources;

        /// <summary>The Core coordinator — the accept/state/query surface.</summary>
        internal RunCoordinator Coordinator => _coordinator;

        /// <summary>
        /// The SPEC §E8 dispatcher the coordinator publishes to after each
        /// journal write — the seam GUI, log and API consumers subscribe
        /// through (subscriptions die with the domain; subscribers
        /// re-register through <c>[InitializeOnLoad]</c>, SPEC §E8).
        /// </summary>
        internal RunEvents Events => _coordinator.Events;

        /// <summary>
        /// The Unity-side §F6 debug channel of SPEC §F6 SG-08
        /// (T-W2.4-04, <see cref="RunDebugLog"/>): internal diagnostics write
        /// one debug-level Run log entry bound to the step
        /// <paramref name="sink"/> serves — journaled through it and mirrored
        /// to the Unity Console — under the same lazily read
        /// <c>debugLogging</c> policy the coordinator gates the journal with.
        /// Missing or false drops the write before either destination.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="sink"/> or <paramref name="message"/> is null.
        /// </exception>
        internal void DebugLog(
            IRunLogSink sink, string message, string? code = null, JsonValue? data = null) =>
            RunDebugLog.Write(sink, _debugLogging, message, code, data);

        /// <summary>
        /// The human approval surface of SPEC §H4 (T-W3.4-02): the Run
        /// Viewer's approval panel calls it; it owns the planHash binding,
        /// the OS-user approver and the <c>approvalTimeoutMinutes</c>
        /// expiry — never a second journal writer.
        /// </summary>
        internal OpWeaveApprovalService Approvals => _approvals;

        /// <summary>The §F1 Run Store — the journal lives under its root.</summary>
        internal FileRunStore Store => _store;

        /// <summary>
        /// The SPEC §F1 Run Store root this host was built over — the
        /// <c>Runs/&lt;runId&gt;/journal.ndjson</c> layout the §H5 Run
        /// Viewer reads the detail pane's per-Run evidence from.
        /// </summary>
        internal string RunStoreRoot => _runStoreRoot;

        /// <summary>
        /// The live Registry seam this host was built over — re-read per
        /// call so a rebuilt snapshot takes over without rewiring (SPEC §C3
        /// rule 8), the same source the host's own Operation lookups and
        /// acceptance gates resolve through. The §H1 workflow commands bind
        /// it so a validate/migrate never reads a different Registry than
        /// the Runner would.
        /// </summary>
        internal Func<RegistrySnapshot> CurrentRegistry => _currentRegistry;

        /// <summary>
        /// The SPEC §F7 change-list attribution of this host — the tracker every
        /// step context records through. Exposed as the seam the §F7 tests finalize
        /// a Run's list through and inject a store write failure with; production
        /// code never needs it, the host's own pre-terminal seam does that.
        /// </summary>
        internal ChangeSetTracker ChangeSets => _changeSets;

        /// <summary>
        /// The gated accept of SPEC §E2 behind the SG-18 gate chain (see the
        /// class remarks): every gate must admit before the Core accept runs,
        /// so a refusal creates no Run directory, writes no journal and probes
        /// no lock — the gate's diagnostics are returned unchanged (settings
        /// OW-CFG-001/002, stale Registry OW-RUN-009; no new RUN code). The
        /// diagnostics an admitting gate emits — the settings load's
        /// OW-CFG-003/004/005 warnings of SPEC §H4.1 — accumulate across the
        /// successful chain and ride on the returned result alongside the
        /// coordinator's own, so a warning is never dropped by a successful
        /// accept. On a successful accept the settings the settings gate just
        /// validated are recorded for the new Run — its runtime
        /// <c>path_writable</c> checks are judged by exactly that snapshot
        /// (<see cref="ActiveRunAllowedPaths"/>), never the startup load or
        /// a later acceptance's.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="plan"/>, <paramref name="document"/> or
        /// <paramref name="options"/> is null.
        /// </exception>
        internal Result<RunHandle> AcceptRun(
            CompiledPlan plan, WorkflowDocument document, ActorKind actor, AcceptOptions options)
        {
            if (plan is null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            var request = new RunAcceptanceRequest(plan, document, actor, options);
            var gateDiagnostics = new List<Diagnostic>();
            for (var i = 0; i < _gates.Count; i++)
            {
                var admitted = _gates[i].Admit(request);
                if (!admitted.Ok)
                {
                    return Result<RunHandle>.Failure(admitted.Diagnostics);
                }
                gateDiagnostics.AddRange(admitted.Diagnostics);
            }
            var accepted = _coordinator.Accept(plan, document, actor, options);
            if (accepted.Ok && accepted.Value is { } handle)
            {
                var validated = _settingsGate.LastValidated;
                if (validated is not null)
                {
                    _acceptedSettings[handle.RunId] = validated;
                }
                if (handle.State == RunState.AwaitingApproval)
                {
                    // SPEC §H4 item 3: the ask landed — track the approval's
                    // expiry material and raise the Run Viewer notification.
                    _approvals.NotifyAwaiting(handle.RunId, validated ?? OpWeaveSettings.Default);
                }
            }
            if (gateDiagnostics.Count == 0)
            {
                return accepted;
            }
            gateDiagnostics.AddRange(accepted.Diagnostics);
            return accepted.Ok
                ? Result<RunHandle>.Success(accepted.Value!, gateDiagnostics)
                : Result<RunHandle>.Failure(gateDiagnostics);
        }

        /// <summary>
        /// The <c>allowedPaths</c> of the settings snapshot validated at the
        /// active Run's acceptance — the §G1 <c>path_writable</c> authority
        /// the requirement evaluator reads on every check (SPEC §H4.1,
        /// decision D-R6): each Run is judged by the settings loaded for its
        /// own admission, so a settings file tightened or repaired after
        /// startup binds the next accepted Run and an invalid file — whose
        /// Runs the settings gate refused — can never degrade to a default
        /// here. Null while no active Run's acceptance or recovery
        /// re-attachment recorded settings — callers treat that as fail
        /// closed.
        /// </summary>
        internal IReadOnlyList<string>? ActiveRunAllowedPaths =>
            _coordinator.ActiveHandle() is { } active &&
            _acceptedSettings.TryGetValue(active.RunId, out var settings)
                ? settings.AllowedPaths
                : null;

        /// <summary>
        /// The SPEC §D0.3 protected set this session tracks (T-W4.2-06):
        /// every non-terminal Run, appended to <paramref name="into"/> with
        /// the Plan whose used definitions the definition-mutation guard
        /// (<see cref="RunDefinitionGuard"/>) compares mutation targets
        /// against. Read on the Unity main thread — the same thread
        /// <see cref="AcceptRun"/> and <see cref="Tick"/> serialize on — so
        /// an Operation's pre-side-effect recheck always sees a Run
        /// accepted since its Preflight.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="into"/> is null.</exception>
        internal void CollectProtectedRuns(List<ProtectedRun> into)
        {
            if (into is null)
            {
                throw new ArgumentNullException(nameof(into));
            }
            _coordinator.CollectProtectedRuns(into);
        }

        /// <summary>
        /// The current Registry snapshot the guard resolves used Operations
        /// against, or null when the snapshot cannot be obtained — callers
        /// treat that as an unprovable protected set (SPEC §D0.3 item 3
        /// fail closed). Where <see cref="CurrentRegistry"/> hands out the
        /// provider so its consumer re-reads and handles the failure itself,
        /// this reads it once here and answers with the snapshot.
        /// </summary>
        internal RegistrySnapshot? CurrentRegistrySnapshot()
        {
            try
            {
                return _currentRegistry();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The settings-derived <see cref="RunPolicyEnvironment"/> a
        /// validate/execute command binds (SPEC §H4.1, decision D-R6): the
        /// same re-read the acceptance gate chain performs, so a tightened
        /// or repaired settings file binds the next validation just as it
        /// binds the next Run acceptance, and a failed or invalid load fails
        /// closed as a command failure carrying the loader's own OW-CFG
        /// diagnostics — never a compile under defaults that were never
        /// approved.
        /// </summary>
        internal Result<RunPolicyEnvironment> CurrentRunPolicyEnvironment()
        {
            var loaded = _loadSettings();
            if (!loaded.Ok)
            {
                return Result<RunPolicyEnvironment>.Failure(loaded.Diagnostics);
            }
            return Result<RunPolicyEnvironment>.Success(
                new RunPolicyEnvironment(loaded.Value!), loaded.Diagnostics);
        }

        /// <summary>
        /// One Editor-update slice of the run — the method subscribed to
        /// <see cref="EditorApplication.update"/> (SPEC §E3 item 1): the loop
        /// advances the Run, then the approval sweep ends any AwaitingApproval
        /// Run past its admission-time <c>approvalTimeoutMinutes</c>
        /// (SPEC §H4 item 3 — Cancelled, OW-PERM-003).
        /// </summary>
        internal Result<Unit> Tick()
        {
            var tick = _loop.Tick();
            var diagnostics = new List<Diagnostic>(tick.Diagnostics);
            if (!tick.Ok)
            {
                return Result<Unit>.Failure(diagnostics);
            }
            var sweep = _approvals.SweepExpired();
            diagnostics.AddRange(sweep.Diagnostics);
            return sweep.Ok
                ? Result<Unit>.Success(Unit.Value, diagnostics)
                : Result<Unit>.Failure(diagnostics);
        }

        /// <summary>
        /// The SPEC §F3 item 1 flush hook: writes every Buffered journal line
        /// still pending to disk — subscribed to
        /// <see cref="AssemblyReloadEvents.beforeAssemblyReload"/> and
        /// <see cref="EditorApplication.quitting"/>.
        /// </summary>
        internal Result<Unit> FlushJournal() => _store.FlushPending();

        /// <summary>
        /// The SPEC §F4 items 1, 2 and 6 recovery pass of T-W4.3-01: confirms or
        /// takes the <c>coordinator.lock</c> exclusion, reads <c>lock.json</c> and
        /// the journal-derived unfinished set inside it and re-attaches every
        /// unfinished Run, resuming the one the exclusion passes to
        /// (<see cref="RunRecoveryEngine.RecoverAtStartup"/>). The record of the
        /// previous domain is spent once the pass ran inside the exclusion and left
        /// nothing attached; it survives a pass that could not obtain the exclusion,
        /// because that is SPEC §E2 item 3 (a)'s evidence naming "自分の Run".
        /// Never throws: a startup hook must not take the Editor down with it.
        /// </summary>
        internal Result<RunRecoveryReport> RecoverRuns()
        {
            var recovered = _recovery.RecoverAtStartup(
                RunLockHandoff.OwnedRunId(_runStoreRoot));
            if (recovered.Ok && recovered.Value is { } report)
            {
                RecordRecoveredRunSettings(report);
                if (report.ExclusionHeld && !_coordinator.HoldsAnyRunLock())
                {
                    RunLockHandoff.Clear(_runStoreRoot);
                }
            }
            return recovered;
        }

        // A Run the §F4 pass re-attached carries no in-memory settings
        // snapshot — the admission that validated one ran in a dead domain
        // and the §F1 Run record persists none. Its §G1 path_writable
        // checks and the Operations bound to ActiveRunAllowedPaths would
        // fail closed on every re-executed step without one, so the
        // re-attachment records the settings this pass's own load just
        // validated — one load for every Run the pass attached, held like
        // the acceptance snapshot for the rest of its resumed execution:
        // a tightened or repaired file binds the resumed Run exactly as it
        // binds the next accepted one, and a failed load records nothing,
        // so every path check keeps failing closed (SPEC §H4.1, D-R6).
        private void RecordRecoveredRunSettings(RunRecoveryReport report)
        {
            if (report.Runs.Count == 0)
            {
                return;
            }
            var loaded = _loadSettings();
            if (!loaded.Ok || loaded.Value is not { } settings)
            {
                return;
            }
            foreach (var run in report.Runs)
            {
                if (!_acceptedSettings.ContainsKey(run.RunId))
                {
                    _acceptedSettings[run.RunId] = settings;
                }
            }
        }

        /// <summary>
        /// The SPEC §1.4 / §E1 Edit-Mode re-entry reconciliation of the Suspended
        /// Run (<see cref="RunRecoveryEngine.ReconcileSuspendedRun"/>).
        /// </summary>
        internal Result<RunRecoveryReport> ReconcileSuspendedRuns() =>
            _recovery.ReconcileSuspendedRun();

        /// <summary>
        /// One SPEC §F8 retention pass over the Run Store — the unit the
        /// Editor-start hook (<see cref="RunnerStartupOrder.RunRetention"/>)
        /// and the terminal-Run subscription invoke: finishes the SG-16
        /// residue sweep (every tombstoned Run whose directory or index
        /// entry survived an interrupted deletion), then selects the
        /// oldest finished Runs past the settings' <c>retainRuns</c> /
        /// <c>retainMegabytes</c> bounds and delegates their deletion to
        /// <see cref="FileRunStore.Delete"/>, which commits the SG-16
        /// tombstone under the Run Store exclusion before removing the
        /// Run. A sweep that fails closed — a corrupt tombstone file —
        /// stops the pass: deleting while the tombstone set is unproven
        /// would lose the deleted-vs-never-created distinction. The
        /// limits are read per pass, like the §F6
        /// debug gate: a settings load that fails yields the documented
        /// §H4.1 defaults (a settings error already refuses new Runs
        /// through the settings gate; retention maintenance keeps its own
        /// contract either way).
        /// </summary>
        internal Result<RunRetentionOutcome> RunRetentionPass()
        {
            var swept = _store.CompleteInterruptedDeletions();
            if (!swept.Ok)
            {
                return Result<RunRetentionOutcome>.Failure(swept.Diagnostics);
            }
            var settings = _loadSettings() is { Ok: true, Value: { } loaded }
                ? loaded
                : OpWeaveSettings.Default;
            var limits =
                RunRetentionLimits.FromSettings(settings.RetainRuns, settings.RetainMegabytes);
            var retention = new RunRetention(_store, _store.MeasureRunBytes, _store.Delete);
            return retention.Apply(limits);
        }

        // The retention trigger both call sites share: reports, never
        // throws — a startup hook or an §E8 subscriber must not take the
        // Editor or the event down with it. A deletion refused because
        // the Run Store exclusion is held (a live Run owns it for its
        // whole execution) is the expected transient outcome, so a pass
        // that selected candidates but deleted none is a single
        // information line, not one error per candidate.
        private void RunRetentionSafely()
        {
            try
            {
                var applied = RunRetentionPass();
                if (!applied.Ok)
                {
                    Debug.LogError("OpWeave: the Run retention pass failed: " +
                        DescribeDiagnostics(applied.Diagnostics));
                    return;
                }
                var outcome = applied.Value!;
                if (outcome.Deleted.Count > 0)
                {
                    Debug.Log("OpWeave: run retention deleted " + outcome.Deleted.Count +
                        " finished Run(s) past the limits.");
                }
                var deferred = outcome.Candidates.Count - outcome.Deleted.Count;
                if (deferred > 0)
                {
                    Debug.Log("OpWeave: run retention selected " + outcome.Candidates.Count +
                        " finished Run(s) for deletion; " + deferred +
                        " were refused and are retried by later passes.");
                }
                if (applied.Diagnostics.Count > 0)
                {
                    var first = applied.Diagnostics[0];
                    Debug.LogWarning("OpWeave: run retention reported " +
                        applied.Diagnostics.Count + " diagnostic(s); first: " +
                        first.Code + " " + first.Message);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        // The §F8 Run-end trigger: a terminal §E8 transition schedules one
        // retention pass (NeedsReview is not terminal and never triggers).
        private void OnRunStateChangedForRetention(RunStateChanged change)
        {
            if (change.State is not (RunState.Succeeded or RunState.Failed or RunState.Cancelled))
            {
                return;
            }
            RunRetentionSafely();
        }

        // The run-recovery startup hook: reports instead of throwing, and a
        // fail-closed verdict (no exclusion, an unreconciled lock.json) is logged —
        // nothing was resumed, and a silent pass would look like a clean start.
        private void RecoverRunsSafely()
        {
            try
            {
                var recovered = RecoverRuns();
                if (!recovered.Ok)
                {
                    Debug.LogError("OpWeave: the Run recovery pass failed: " +
                        DescribeDiagnostics(recovered.Diagnostics));
                    return;
                }
                if (recovered.Value is { } report && report.Diagnostics.Count > 0)
                {
                    Debug.LogError("OpWeave: Run recovery ended without resuming: " +
                        DescribeDiagnostics(report.Diagnostics));
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        // Keeps SPEC §E2 item 3 (a)'s "自分の Run" record current: the Run this
        // Editor holds the exclusion for is named at each lock-holding state change
        // and forgotten once its journal is terminal, when lock.json is emptied and
        // the handle is closed or released.
        private void OnRunStateChanged(RunStateChanged change)
        {
            if (change.State is RunState.Succeeded or RunState.Failed or RunState.Cancelled)
            {
                // The terminal event alone is not the emptying and release the
                // comment above names: EndRun publishes it only after a
                // successful CoordinatorRunLock.Release, but the SPEC §F1
                // store-failure end (EndStoreFailedRun) and the
                // terminal-pending-release convergence (ConvergeToJournaledEnd)
                // publish it while the Run's HeldLock still holds the OS
                // exclusion — the deferred ReleaseEndedRunLocks retry that
                // later drops it publishes no event — and an inactive Run's
                // terminal record can arrive while another Run still holds.
                // Clearing then would erase the only record of a live handle:
                // the orphaned exclusion §E2 item 3 (a) exists to survive.
                // The record is therefore spent only once no tracked Run
                // holds the exclusion — the same proof the recovery pass's own
                // guard requires. A record a deferred release leaves behind
                // is residue TryAdopt fails closed on; the next RecoverRuns
                // drops it through the same check.
                if (!_coordinator.HoldsAnyRunLock())
                {
                    RunLockHandoff.Clear(_runStoreRoot);
                }
                return;
            }
            if (_coordinator.HoldsRunLock(change.RunId))
            {
                RunLockHandoff.RecordOwnedRun(_runStoreRoot, change.RunId);
            }
        }

        /// <summary>
        /// The SPEC §1.4 play-mode transition hook subscribed to
        /// <see cref="EditorApplication.playModeStateChanged"/>: leaving Edit
        /// Mode suspends the active Running Run — the
        /// <c>run_suspended</c> record is journaled at the event itself
        /// because a domain reload can follow before the next Tick — and
        /// returning to Edit Mode reconciles the Suspended Run (SPEC §1.4,
        /// §E1 <c>Suspended → Running | NeedsReview</c>). A re-entry that also
        /// reloaded the domain has no tracked Run left to reconcile; the
        /// recovery pass of SPEC §F4 item 1 handles that one, which is why this
        /// branch only consults the Run the live Runner still tracks.
        /// </summary>
        internal void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                var suspended = _loop.SuspendActive("play_mode");
                if (!suspended.Ok)
                {
                    Debug.LogError("OpWeave: suspending the active Run failed: " +
                        DescribeDiagnostics(suspended.Diagnostics));
                }
                return;
            }
            if (change != PlayModeStateChange.EnteredEditMode)
            {
                return;
            }
            var reconciled = ReconcileSuspendedRuns();
            if (!reconciled.Ok)
            {
                Debug.LogError("OpWeave: reconciling the suspended Run after returning " +
                    "to Edit Mode failed: " + DescribeDiagnostics(reconciled.Diagnostics));
            }
        }

        /// <summary>
        /// The first-delayCall startup hook of SPEC §1.4 / §F4 item 1: runs
        /// the ordered hook list exactly once per domain.
        /// </summary>
        internal void RunStartupHooks()
        {
            if (_startupRan)
            {
                return;
            }
            _startupRan = true;
            _startupHooks.RunAll();
        }

        /// <summary>
        /// Subscribes the host to the Editor lifecycle events (SPEC §1.4):
        /// <see cref="EditorApplication.update"/> → Tick,
        /// <see cref="AssemblyReloadEvents.beforeAssemblyReload"/> and
        /// <see cref="EditorApplication.quitting"/> → journal flush,
        /// <see cref="EditorApplication.playModeStateChanged"/> → suspend on
        /// Play Mode entry and reconcile on Edit Mode re-entry, and the first
        /// <see cref="EditorApplication.delayCall"/> → the ordered startup
        /// hooks. Idempotent within a domain.
        /// </summary>
        internal void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += FlushJournalSafely;
            EditorApplication.quitting += FlushJournalSafely;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.delayCall += RunStartupHooks;
            _coordinator.Events.RunStateChanged += OnRunStateChanged;
        }

        /// <summary>Removes every subscription <see cref="Install"/> made.</summary>
        internal void Uninstall()
        {
            if (!_installed)
            {
                return;
            }
            _installed = false;
            EditorApplication.update -= OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= FlushJournalSafely;
            EditorApplication.quitting -= FlushJournalSafely;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.delayCall -= RunStartupHooks;
            _coordinator.Events.RunStateChanged -= OnRunStateChanged;
        }

        /// <summary>
        /// Drops the coordinator-held OS exclusions (the abandoned-state
        /// semantics of <see cref="RunCoordinator.Dispose"/>: lock.json
        /// residue is left for the §F4 recovery engine) and unsubscribes.
        /// </summary>
        public void Dispose()
        {
            Uninstall();
            _coordinator.Events.RunStateChanged -= OnRunStateChangedForChangeSet;
            _coordinator.Events.RunStateChanged -= _logCapture.OnRunStateChanged;
            _logCapture.Dispose();
            _coordinator.Events.RunStateChanged -= OnRunStateChangedForRetention;
            _changeSets.Dispose();
            _coordinator.Dispose();
        }

        // The update-callback wrapper: diagnostics that mean the tick itself
        // could not run (the persistence boundary) are logged; an exception
        // escaping the loop would re-fire on every editor update, so it is
        // captured and logged instead.
        private void OnEditorUpdate()
        {
            try
            {
                var tick = Tick();
                if (!tick.Ok)
                {
                    Debug.LogError("OpWeave: the Run tick failed: " +
                        DescribeDiagnostics(tick.Diagnostics));
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        // The lifecycle-event flush wrapper: a flush failure is logged, never
        // thrown into an event the Editor cannot answer.
        private void FlushJournalSafely()
        {
            try
            {
                var flushed = FlushJournal();
                if (!flushed.Ok)
                {
                    Debug.LogError("OpWeave: flushing the journal failed: " +
                        DescribeDiagnostics(flushed.Diagnostics));
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        // The Runner's operation resolver: the §C3 step-6 instantiated
        // implementation on the registered entry — the only object the §C2
        // lifecycle contract allows the Runner to drive.
        private static Result<IOperation> ResolveOperation(
            Func<RegistrySnapshot> currentRegistry, PlanStep step)
        {
            var implementation =
                currentRegistry().Find(step.OperationId, step.Version)?.Implementation;
            return implementation is null
                ? Result<IOperation>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_011,
                    "The step's Operation cannot be resolved.",
                    nodeId: step.NodeId))
                : Result<IOperation>.Success(implementation);
        }

        private static string DescribeDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            var parts = new string[diagnostics.Count];
            for (var i = 0; i < diagnostics.Count; i++)
            {
                parts[i] = diagnostics[i].Code + " " + diagnostics[i].Message;
            }
            return string.Join("; ", parts);
        }

        // The registry-following descriptor/type lookups: the snapshot
        // provider is re-read for every call so a rebuilt Registry
        // (T-W2.2-07's startup hook) takes over without rewiring the loop.
        private sealed class ForwardingOperationSource : IOperationSource
        {
            private readonly Func<RegistrySnapshot> _currentRegistry;

            internal ForwardingOperationSource(Func<RegistrySnapshot> currentRegistry) =>
                _currentRegistry = currentRegistry;

            public OperationDescriptor? Find(string id, int version) =>
                _currentRegistry().Find(id, version)?.Descriptor;
        }

        private sealed class ForwardingTypeRegistry : ITypeRegistry
        {
            private readonly Func<RegistrySnapshot> _currentRegistry;

            internal ForwardingTypeRegistry(Func<RegistrySnapshot> currentRegistry) =>
                _currentRegistry = currentRegistry;

            public Result<TypeDefinition> Resolve(TypeRef type) =>
                _currentRegistry().Types.Resolve(type);

            public AssignResult IsAssignable(TypeRef source, TypeRef target) =>
                _currentRegistry().Types.IsAssignable(source, target);

            public IReadOnlyList<TypeDefinition> All => _currentRegistry().Types.All;
        }

        // ── Production wiring ─────────────────────────────────────────────

        private static OpWeaveRunnerHost? s_current;
        private static RegistrySnapshot? s_emptyRegistry;

        /// <summary>The domain's installed production host, or null before install.</summary>
        internal static OpWeaveRunnerHost? Current => s_current;

        /// <summary>
        /// Creates and installs the production host — invoked once per domain
        /// by <see cref="OpWeaveAdapterBootstrap.InitializeServices"/> inside
        /// the §G1 item 3 worker-process guard.
        /// </summary>
        internal static OpWeaveRunnerHost InstallProduction()
        {
            var host = CreateProduction();
            host.Install();
            s_current = host;
            return host;
        }

        /// <summary>
        /// The production construction: real Editor state probe, wall clock,
        /// SessionState session id, <c>Library/OpWeave/</c> Run Store root
        /// (SPEC §1.5), settings-driven tick budget and the merged §G1
        /// requirement evaluators reading <see cref="ActiveRunAllowedPaths"/>
        /// — the settings snapshot each Run's own acceptance validated, so a
        /// tightened or repaired settings file binds the next admitted Run
        /// and an invalid file keeps refusing Runs (D-R6) instead of falling
        /// back to a default.
        /// </summary>
        /// <remarks>
        /// The Registry provider reads the shared
        /// <see cref="RegistryPublication"/> — the snapshot the
        /// <see cref="RunnerStartupOrder.RegistryRebuild"/> startup hook
        /// publishes atomically on the first delayCall (SPEC §C3 step 8) —
        /// falling back to <see cref="EmptyRegistry"/> until the first build
        /// lands; the second acceptance gate refuses new Runs while the
        /// Registry is stale (SPEC §C3 rule 9, SG-18: settings error → stale
        /// Registry → Plan permission). One seam stays a placeholder until its
        /// owning ticket lands: <c>opWeaveInitiatedBusy</c> reports
        /// <see langword="false"/> until the §G4 wait conditions track
        /// OpWeave-triggered compiles — the conservative §1.4 reading (an
        /// OpWeave compile then suspends instead of waiting) until then. The
        /// §E6 item 4 release dispatcher carries the handler of every resource
        /// kind a standard Operation acquires — currently SPEC §G6's
        /// <c>prefab_edit</c>, registered at the end of this method; an
        /// unregistered kind still fails safe — the acquisition stays
        /// journaled-open and a diagnostic is recorded, never a silent skip.
        /// </remarks>
        internal static OpWeaveRunnerHost CreateProduction()
        {
            var settings = OpWeaveSettingsLoader.Load();
            var loaded = settings.Ok && settings.Value is not null
                ? settings.Value
                : OpWeaveSettings.Default;
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var probe = new EditorStateProbe();
            var host = new OpWeaveRunnerHost(
                Path.Combine(projectRoot, "Library", "OpWeave"),
                EditorSessionId.GetOrCreate(),
                new SystemUtcClock(),
                probe,
                OpWeaveSettingsLoader.Load,
                () => RegistryPublication.Shared.Snapshot ?? EmptyRegistry(),
                host => new RequirementEvaluatorAdapter(
                    new EditorRequirementEvaluator(
                        () => host.ActiveRunAllowedPaths ?? Array.Empty<string>())),
                EditorUndoScope.Open,
                () => false,
                () => probe.IsBatchMode,
                ClampTickBudget(loaded.TickBudgetMs),
                new IRunAcceptanceGate[] { new StaleRegistryGate(RegistryStale) },
                debugLogging: DebugLoggingEnabled);
            // SPEC §1.4 / §C3: the first delayCall runs the ordered startup
            // list; the Registry rebuild slot runs stages 1–8 and publishes
            // the finished snapshot atomically into the shared publication.
            host.StartupHooks.Register(
                RunnerStartupOrder.RegistryRebuild,
                "registry-build",
                () => RegistryBuildHost.RebuildProduction(OpWeaveSettingsLoader.Load));
            // SPEC §F4 item 1: the recovery slot runs after the rebuild, so the
            // resume's Operation lookups see the verified snapshot (§C3 rule 6).
            host.StartupHooks.Register(
                RunnerStartupOrder.RunRecovery,
                "run-recovery",
                () => host.RecoverRunsSafely());
            // SPEC §F8: the retention pass also runs at Editor start, after
            // recovery has re-attached the unfinished Runs it must never
            // select.
            host.StartupHooks.Register(
                RunnerStartupOrder.RunRetention,
                "run-retention",
                () => host.RunRetentionSafely());
            // SPEC §E6 item 4: the prefab-contents editing session of SPEC §G6
            // is the one resource kind a standard Operation acquires in v1, so
            // its release — discard the contents a Run never saved — is
            // registered on the production dispatcher.
            host.ResourceReleases.Register(
                PrefabContentsResources.Kind, PrefabContentsResources.Release);
            return host;
        }

        // SPEC §C3 rule 9: the publication's stale state is the one source
        // of truth — Shared already ORs the stored flag with the live
        // EditorUtility.scriptCompilationFailed signal (a failed compile in
        // a live domain produces no domain reload, so no rebuild can mark
        // it) and starts stale until the first successful build publishes
        // (fail closed).
        private static bool RegistryStale() =>
            RegistryPublication.Shared.Stale;

        // The SPEC §F6 SG-08 write-path gate for the production
        // coordinator, read lazily from the settings file on each
        // debug-level write (rare): a failed or invalid load answers the
        // SPEC default false — a settings error already refuses new Runs
        // through the settings gate and never upgrades the record level.
        private static bool DebugLoggingEnabled() =>
            OpWeaveSettingsLoader.Load() is { Ok: true, Value: { } settings } &&
            settings.DebugLogging;

        // The §E3 item 3 budget from settings (§H4.1 tickBudgetMs): a
        // non-positive or overflowing value clamps to the 50 ms default /
        // int.MaxValue — a malformed value already trips OW-CFG-002 at load.
        private static int ClampTickBudget(long tickBudgetMs) =>
            tickBudgetMs <= 0
                ? RunLoop.DefaultTickBudgetMs
                : tickBudgetMs > int.MaxValue
                    ? int.MaxValue
                    : (int)tickBudgetMs;

        // The pre-Registry snapshot: no Operations until T-W2.2-07's rebuild,
        // but the real §B2 type snapshot so the loop's output validation sees
        // the built-in core.*/unity.* types. The hash is the Canonical JSON
        // digest of the empty operation set — a placeholder, never a §C3
        // rule-6 registryHash.
        private static RegistrySnapshot EmptyRegistry()
        {
            if (s_emptyRegistry is not null)
            {
                return s_emptyRegistry;
            }
            var types = TypeRegistryBuilder.Build(
                new[]
                {
                    new TypeProviderCandidate(
                        CoreTypeProvider.ProviderId, new CoreTypeProvider(),
                        OperationOrigin.Standard),
                    new TypeProviderCandidate(
                        UnityTypeProvider.ProviderId, new UnityTypeProvider(),
                        OperationOrigin.Standard),
                },
                new UnityClassRelationProvider());
            s_emptyRegistry = new RegistrySnapshot(
                "reg_" + Identifiers.NewUlid(),
                CanonicalJson.Sha256(new JArray()),
                Array.Empty<RegisteredOperation>(),
                types.Registry,
                Array.Empty<RegistryChange>(),
                types.Failures);
            return s_emptyRegistry;
        }
    }
}
