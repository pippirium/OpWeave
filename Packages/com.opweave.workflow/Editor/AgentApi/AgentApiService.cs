#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEngine;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The transport-independent Agent service of SPEC §H1: every Agent-facing
    /// operation runs here with the actor fixed to <see cref="ActorKind.Agent"/>
    /// and every answer leaves through the SPEC §H3 envelope. The Pipeline
    /// adapter (SPEC §H2) and any future entry point only translate their wire
    /// arguments into these calls and serialize the returned
    /// <see cref="AgentApiResponse"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failure discipline (SPEC §H2 item 5, §J): business failures are normal
    /// <c>ok:false</c> envelopes; request-shape misuse is OW-API-001, an invalid
    /// cursor OW-API-002, an internal failure OW-API-004, and an unknown runId
    /// OW-RUN-010 through the injected Run Store.
    /// </para>
    /// <para>
    /// Command implementations return through <see cref="Ok"/>,
    /// <see cref="Failure(System.Collections.Generic.IEnumerable{OpWeave.Core.Diagnostic}, System.Collections.Generic.IEnumerable{OpWeave.Core.Diagnostic}?, System.String?, System.String?)"/>
    /// or <see cref="PageResponse"/> inside <see cref="Invoke"/> so the
    /// 64 KiB cap is enforced once and an escaping exception becomes OW-API-004
    /// instead of crashing the transport.
    /// </para>
    /// <para>
    /// Permissions boundary (SPEC §H4 item 5): no member of this service grants
    /// approval, resolves NeedsReview or changes settings; there is no actor
    /// parameter anywhere because <see cref="Actor"/> is fixed.
    /// </para>
    /// </remarks>
    public sealed partial class AgentApiService
    {
        /// <summary>The SPEC §H3 item 3 response cap: 64 KiB of UTF-8.</summary>
        public const int MaxResponseBytes = 64 * 1024;

        // The SPEC §H1 table makes cancel_workflow's reason optional while
        // the §E6 cancel-request journal record always carries one — an
        // omitted or blank reason is recorded as this fixed provenance
        // marker (a local reversible choice; the caller's own reason is
        // journaled verbatim — credential-masked by the Core path).
        private const string DefaultCancelReason = "cancel_workflow";

        // The SPEC §H2 item 3 long-poll ceiling — "waitSeconds?（最大 20）"
        // of the §H1 table; a request outside the range is request-shape
        // misuse (OW-API-001) rather than a silently clamped wait.
        private const int MaxWaitSeconds = 20;

        // The SPEC §H3 item 4 hint of an AwaitingApproval Run — the Agent
        // relays it to its user; the decision itself stays human-only
        // (SPEC §H4 item 5).
        private const string ApprovalHint = "Approval is required in the Unity Run Viewer.";

        // The journaled §F6 levels ranked for the get_run_logs minimum-
        // severity filter — "debug" is the only wire level outside the
        // §0.4 Severity set (SanitizedRunLogEntry keeps it a string); an
        // unrecognized level ranks as info rather than vanishing.
        private const int InfoRank = 1;
        private static readonly IReadOnlyDictionary<string, int> s_logLevelRanks =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["debug"] = 0,
                ["info"] = InfoRank,
                ["warning"] = 2,
                ["error"] = 3,
            };

        // The wire names of the §E1 Run states — the list_runs state
        // filter's accepted values, snake_case per SPEC §0.4.
        private static readonly IReadOnlyDictionary<string, RunState> s_runStates =
            BuildRunStateNames();

        private static readonly IReadOnlyList<Diagnostic> s_emptyDiagnostics =
            Array.AsReadOnly(Array.Empty<Diagnostic>());

        private readonly IRunStore _runStore;
        private readonly AgentApiCursorCodec _cursors;
        private readonly Func<string, string, Result<RunHandle>> _cancelRun;
        private readonly RunStatusTracker? _runStatus;
        private readonly string? _runStoreRoot;
        private readonly Func<bool>? _debugLogging;
        private readonly Func<string, Result<IReadOnlyList<ChangeRecord>>>? _readChanges;
        private readonly Func<string, Result<bool>>? _isDeletedRun;
        private readonly IAgentWorkflowBackend? _workflows;
        private readonly IAgentCatalogBackend? _catalog;

        /// <summary>
        /// The service over its seams: <paramref name="runStore"/> resolves runIds
        /// (its failures — OW-RUN-010 among them — pass through unchanged),
        /// <paramref name="cursors"/> signs/verifies page cursors and
        /// <paramref name="cancelRun"/> is the SPEC §E6 item 2 cancel-request
        /// entry point — the wiring binds the Core coordinator's
        /// <c>IRunCoordinator.Cancel</c>; the human-only decision members of
        /// that coordinator never reach this service (SPEC §H4 item 5).
        /// Internal so the only producers are <see cref="AgentApiHost"/> wiring
        /// and tests.
        /// </summary>
        /// <param name="runStatus">
        /// The SPEC §H2 item 3 thread-safe snapshot — the only Run-state
        /// source the observation commands read, so they never touch mutable
        /// Core state off the main thread.
        /// </param>
        /// <param name="runStoreRoot">
        /// The SPEC §F1 Run Store root the §F6 log reader opens — read-only
        /// file access, safe off the main thread.
        /// </param>
        /// <param name="debugLogging">
        /// The live §F6 SG-08 <c>debugLogging</c> policy reading; absent
        /// means the SPEC default <see langword="false"/>.
        /// </param>
        /// <param name="readChanges">
        /// The read-only journal read that reconstructs the Run's SPEC §F7
        /// change list (<c>JournaledChanges</c> behind the seam): the full
        /// durable union or a failure — a partial list must never be served
        /// as the complete one.
        /// </param>
        /// <param name="isDeletedRun">
        /// The Core-owned SG-16 tombstone lookup — the same
        /// deleted-vs-never-created verdict <see cref="IRunStore.Load"/>
        /// answers, bound to the Run Store's own tombstone file (never a
        /// second copy: SPEC §0.6 SG-16 forbids one outside Core). A
        /// tombstoned runId answers OW-STORE-003 on every read; a
        /// tombstone file that cannot prove its contents fails closed
        /// with OW-STORE-002.
        /// </param>
        /// <param name="workflows">
        /// The Unity-facing backend of the §H1 workflow commands
        /// (<c>validate_workflow</c>, <c>execute_workflow</c>,
        /// <c>migrate_workflow</c>): project file reads, the shared
        /// snapshot-backed <see cref="WorkflowCompiler"/>, the §E2
        /// acceptance entry point and the §A7 migration chain. Absent
        /// means the workflow commands are not installed — their calls
        /// fail closed as OW-API-004 internal errors, never as partial
        /// behaviour.
        /// </param>
        /// <param name="catalog">
        /// The Unity-facing backend of the §H1 discovery and Registry
        /// commands (<c>search_operations</c>, <c>describe_operation</c>,
        /// <c>search_compatible_operations</c>, <c>list_types</c>,
        /// <c>get_workflow_schema</c>, <c>list_workflows</c> and
        /// <c>get_registry_status</c>): the published snapshot, the §C3
        /// rule-7/9 persistence state, the configuration-load diagnostics,
        /// the §A2 schema artifact and the project workflow file listing.
        /// Absent means those commands are not installed — their calls
        /// fail closed as OW-API-004 internal errors.
        /// </param>
        internal AgentApiService(
            IRunStore runStore,
            AgentApiCursorCodec cursors,
            Func<string, string, Result<RunHandle>> cancelRun,
            RunStatusTracker? runStatus = null,
            string? runStoreRoot = null,
            Func<bool>? debugLogging = null,
            Func<string, Result<IReadOnlyList<ChangeRecord>>>? readChanges = null,
            Func<string, Result<bool>>? isDeletedRun = null,
            IAgentWorkflowBackend? workflows = null,
            IAgentCatalogBackend? catalog = null)
        {
            _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
            _cursors = cursors ?? throw new ArgumentNullException(nameof(cursors));
            _cancelRun = cancelRun ?? throw new ArgumentNullException(nameof(cancelRun));
            _runStatus = runStatus;
            _runStoreRoot = runStoreRoot;
            _debugLogging = debugLogging;
            _readChanges = readChanges;
            _isDeletedRun = isDeletedRun;
            _workflows = workflows;
            _catalog = catalog;
        }

        /// <summary>The actor every command runs as — always
        /// <see cref="ActorKind.Agent"/> (SPEC §H1); the value is a property, not
        /// a parameter, so no caller can impersonate the human side.</summary>
        public ActorKind Actor => ActorKind.Agent;

        /// <summary>
        /// A successful envelope around <paramref name="data"/>. A response
        /// whose envelope exceeds the 64 KiB cap (SPEC §H3 item 3) is not an
        /// error: the Canonical JSON of the whole envelope —
        /// <c>{ok, data, errors, warnings}</c> — is delivered in string
        /// segments. Every segment keeps the original <c>ok</c>, carries
        /// <c>data</c> as the next UTF-16-clean substring of the serialized
        /// envelope with <c>errors</c>/<c>warnings</c> empty (the real
        /// diagnostics stay inside the segmented content), plus
        /// <c>truncated:true</c> and a <c>nextCursor</c>; the final segment
        /// carries no cursor (and <c>truncated</c> is omitted with it).
        /// A client concatenates the segment strings and parses the
        /// reassembled text as the original response envelope.
        /// </summary>
        /// <param name="context">Request-binding material for the segment
        /// cursor, typically <see cref="PageContext"/> output. A command that can
        /// emit oversized data passes its context and the incoming
        /// <paramref name="cursor"/> straight through; when null the segments
        /// are bound to the envelope's own digest, so a replay still dies the
        /// moment the response changes.</param>
        /// <param name="cursor">A <c>nextCursor</c> replayed to continue the
        /// segmentation, or null for the first call. A tampered or foreign
        /// cursor — or one replayed after the data changed — is OW-API-002.</param>
        /// <returns>The whole envelope, or the next segment of it; OW-API-002 on
        /// a bad cursor. OW-API-004 is reserved for a genuine internal error —
        /// never for size.</returns>
        public AgentApiResponse Ok(
            JToken? data,
            IReadOnlyList<Diagnostic>? warnings = null,
            string? context = null,
            string? cursor = null)
        {
            var whole = AgentApiResponse.Success(
                data, warnings ?? s_emptyDiagnostics, null);
            if (cursor is null && whole.Utf8ByteCount <= MaxResponseBytes)
            {
                return whole;
            }
            return SegmentEnvelope(whole, context, cursor);
        }

        /// <summary>
        /// A failed envelope (<c>ok:false</c>) carrying <paramref name="errors"/>
        /// — business and API-misuse failures alike; the diagnostics decide
        /// which, per SPEC §H2 item 5. An envelope over the 64 KiB cap is
        /// delivered in string segments exactly like <see cref="Ok"/>: every
        /// segment keeps <c>ok:false</c> while the real diagnostics ride inside
        /// the segmented <c>{ok, data, errors, warnings}</c> content the client
        /// reassembles — a large failure never degrades to OW-API-004.
        /// </summary>
        /// <param name="context">Request-binding material for the segment
        /// cursor, as in <see cref="Ok"/>; commands that can emit oversized
        /// failures pass their request context and the incoming
        /// <paramref name="cursor"/> through so a replay resumes inside the
        /// same response.</param>
        /// <param name="cursor">A <c>nextCursor</c> replayed to continue the
        /// segmentation, or null for the first call; an invalid one is
        /// OW-API-002.</param>
        public AgentApiResponse Failure(
            IEnumerable<Diagnostic> errors,
            IEnumerable<Diagnostic>? warnings = null,
            string? context = null,
            string? cursor = null)
        {
            var whole = AgentApiResponse.Error(
                ReadOnlyCollections.CopyList(errors),
                warnings is null
                    ? s_emptyDiagnostics
                    : ReadOnlyCollections.CopyList(warnings));
            if (cursor is null && whole.Utf8ByteCount <= MaxResponseBytes)
            {
                return whole;
            }
            return SegmentEnvelope(whole, context, cursor);
        }

        /// <summary>
        /// A failed envelope from a failed <see cref="Result{T}"/>: the command
        /// boundary mapping that keeps business failures as <c>ok:false</c>
        /// envelopes without re-wrapping their diagnostics.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="result"/> succeeded.</exception>
        public AgentApiResponse Failure<T>(Result<T> result)
        {
            if (result.Ok)
            {
                throw new ArgumentException(
                    "A succeeded Result cannot build a failure response.", nameof(result));
            }
            return Failure(result.Diagnostics);
        }

        /// <summary>
        /// Builds a capped list page (SPEC §H3 item 3): emits as many
        /// <paramref name="items"/> — from the cursor's offset, capped by
        /// <paramref name="limit"/> — as fit inside the 64 KiB envelope, then
        /// attaches an opaque cursor to the remainder. Replaying the cursor
        /// through the same command resumes the listing.
        /// </summary>
        /// <para>
        /// An item too large for a page of its own is not an error: the whole
        /// single-item page envelope — <c>{ok, data, errors, warnings}</c> plus
        /// the page-continuation <c>nextCursor</c> — is delivered as string
        /// segments of its Canonical JSON (<paramref name="pack"/> supplies the
        /// one-item slice for <c>data</c>). While an item is segmented,
        /// <c>data</c> carries the next UTF-16-clean substring of that
        /// serialized envelope — a client concatenates the segment strings and
        /// parses the original page envelope — and the cursor carries the item
        /// and segment offsets plus the content digest; the item's last segment
        /// continues with the next item's page cursor, exactly where the
        /// reassembled envelope points.
        /// </para>
        /// <param name="context">Opaque request-binding material, typically
        /// <see cref="PageContext"/> output: cursors are replayed only against an
        /// identical context, so a cursor of one query cannot resume inside
        /// another.</param>
        /// <param name="cursor">The previous page's <c>nextCursor</c>, or null
        /// for the first page. Anything that does not decode for this
        /// <paramref name="context"/> is OW-API-002.</param>
        /// <param name="items">The full listing; the page takes the slice
        /// starting at the cursor offset.</param>
        /// <param name="pack">Builds the <c>data</c> payload for a
        /// (startIndex, count) slice of <paramref name="items"/>. Invoked
        /// repeatedly during the fit search; must return the same-shaped payload
        /// whose size does not shrink as <c>count</c> grows.</param>
        /// <param name="limit">A caller item cap on top of the byte cap; values
        /// below 1 are OW-API-001.</param>
        /// <param name="warnings">Warnings riding on the page.</param>
        /// <returns>The page response — or OW-API-002 on a bad cursor,
        /// OW-API-001 on a bad limit. OW-API-004 is reserved for a genuine
        /// internal failure — never for size: an oversized item or oversized
        /// warnings page through Canonical-JSON segments of the whole
        /// envelope.</returns>
        public AgentApiResponse PageResponse(
            string context,
            string? cursor,
            IReadOnlyList<JToken> items,
            Func<int, int, JToken> pack,
            int? limit = null,
            IReadOnlyList<Diagnostic>? warnings = null)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (items is null)
            {
                throw new ArgumentNullException(nameof(items));
            }
            if (pack is null)
            {
                throw new ArgumentNullException(nameof(pack));
            }
            if (limit is < 1)
            {
                return Failure(new[]
                {
                    AgentApiErrors.MalformedRequest(
                        "The 'limit' argument must be a positive integer.", jsonPath: "$.limit"),
                });
            }

            var pageWarnings = warnings ?? s_emptyDiagnostics;
            var offset = 0;
            if (cursor is not null)
            {
                var decoded = _cursors.Decode(cursor, context);
                if (!decoded.Ok)
                {
                    return Failure(decoded.Diagnostics);
                }
                var position = decoded.Value;
                if (position.IsSegment)
                {
                    return SegmentPageItem(
                        context, position, items, pack, pageWarnings);
                }
                offset = position.ItemOffset;
                if (offset > items.Count)
                {
                    return Failure(new[] { AgentApiErrors.InvalidCursor() });
                }
            }

            // Whether a (offset, count) slice produces a response inside the cap
            // — measured on the exact envelope, cursor field included, so the
            // emitted bytes never exceed 64 KiB.
            bool Fits(int count)
            {
                var end = offset + count;
                var next = end < items.Count ? _cursors.Issue(context, end) : null;
                var candidate = AgentApiResponse.Success(pack(offset, count), pageWarnings, next);
                return candidate.Utf8ByteCount <= MaxResponseBytes;
            }

            var remaining = items.Count - offset;
            var upper = limit.HasValue ? Math.Min(limit.Value, remaining) : remaining;
            int count;
            if (Fits(upper))
            {
                count = upper;
            }
            else
            {
                // Binary search the largest fitting prefix in [0, upper):
                // Fits is monotonic in count by the pack contract.
                var low = 0;
                var high = upper - 1;
                count = -1;
                while (low <= high)
                {
                    var mid = low + (high - low) / 2;
                    if (Fits(mid))
                    {
                        count = mid;
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }
                if (count <= 0)
                {
                    // The first remaining item alone — or the empty tail page
                    // whose warnings alone exceed the cap — does not fit: page
                    // through its whole page envelope as Canonical-JSON string
                    // segments (SPEC §H3 item 3) — never emit a zero-progress
                    // cursor (an Agent would loop).
                    return SegmentPageItem(
                        context, new AgentApiCursor(offset, 0, null), items,
                        pack, pageWarnings);
                }
            }

            var nextOffset = offset + count;
            var nextCursor = nextOffset < items.Count
                ? _cursors.Issue(context, nextOffset)
                : null;
            return EnforceCap(
                AgentApiResponse.Success(pack(offset, count), pageWarnings, nextCursor),
                context, cursor: null);
        }

        /// <summary>
        /// Resolves a runId to its recorded <see cref="RunSnapshot"/> through the
        /// injected Run Store — the shared lookup the run-scoped commands use.
        /// A runId that is missing, blank or outside the SPEC §0.4
        /// <c>"run_" + ULID</c> grammar is request-shape misuse (OW-API-001); a
        /// well-formed but unknown runId surfaces the store's own OW-RUN-010 —
        /// and OW-STORE-002 on integrity failures — unchanged so the caller can
        /// place it straight into a failure envelope.
        /// </summary>
        public Result<RunSnapshot> RequireRun(string? runId)
        {
            if (string.IsNullOrWhiteSpace(runId) || !IsRunId(runId))
            {
                return Result<RunSnapshot>.Failure(AgentApiErrors.MalformedRequest(
                    "The 'runId' argument must be a run_<ULID> identifier.",
                    jsonPath: "$.runId"));
            }
            return _runStore.Load(runId!);
        }

        // The SPEC §0.4 identifier grammar "run_" + 26 Crockford-Base32
        // characters — the same rule FileRunStore enforces internally; checking
        // it here keeps malformed input on the OW-API-001 path instead of
        // reaching the store's contract exception.
        private static bool IsRunId(string runId)
        {
            const string ulidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            const int ulidLength = 26;
            if (runId.Length != Identifiers.RunIdPrefix.Length + ulidLength ||
                !runId.StartsWith(Identifiers.RunIdPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            for (var i = Identifiers.RunIdPrefix.Length; i < runId.Length; i++)
            {
                if (ulidAlphabet.IndexOf(runId[i]) < 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Runs <paramref name="command"/> and guarantees its answer is a
        /// capped envelope: an oversized one leaves as Canonical-JSON string
        /// segments like every other producer, and an escaping exception is
        /// logged to the Editor and reported as OW-API-004 — the transport
        /// never sees a crash or an over-limit body (SPEC §H2 item 5, §H3
        /// item 3).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
        public AgentApiResponse Invoke(Func<AgentApiResponse> command)
        {
            if (command is null)
            {
                throw new ArgumentNullException(nameof(command));
            }
            try
            {
                var response = command();
                if (response is null)
                {
                    throw new InvalidOperationException("The command returned no response.");
                }
                return EnforceCap(response);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return Failure(new[]
                {
                    AgentApiErrors.InternalError(
                        "The command failed with an internal error."),
                });
            }
        }

        /// <summary>
        /// The request-binding material for <see cref="PageResponse"/>: the
        /// command name plus the Canonical JSON digest of its arguments, so a
        /// cursor resumes only inside the exact query that issued it.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
        public string PageContext(string command, JToken? requestArguments) =>
            AgentApiCursorCodec.ContextDigest(
                (command ?? throw new ArgumentNullException(nameof(command))) +
                ":" + CanonicalJson.Sha256(requestArguments ?? JValue.CreateNull()));

        /// <summary>
        /// The <c>cancel_workflow</c> command of SPEC §H1, bound to §E6:
        /// records a <c>cancel_requested</c> journal record the run loop
        /// processes on the next Tick — the Run's state itself does not
        /// change at request time.
        /// </summary>
        /// <remarks>
        /// SPEC §0.6 SG-14: the Agent command accepts only the ordinary §E6
        /// states — Queued, Running and Suspended (a "Waiting" Run is
        /// Running with its current step waiting, §E4). A NeedsReview or
        /// AwaitingApproval Run waits for a human-only decision (§H4
        /// item 5) and a terminal Run has nothing left to cancel: every
        /// refusal is OW-RUN-014 (Error, not retryable) with the Run's
        /// state, approval and journal untouched. The journaled state the
        /// Run Store reconstructs decides — all §F2 state records are
        /// Flushed, so it never lags an in-memory transition; the §E6 seam
        /// re-checks before it journals. A well-formed but unknown runId is
        /// the store's OW-RUN-010; a second cancel while the first is still
        /// pending answers the same accepted result without journaling a
        /// duplicate — the journal keeps exactly one
        /// <c>cancel_requested</c>.
        /// </remarks>
        /// <param name="request">The command's arguments (runId, optional reason).</param>
        /// <returns>
        /// The acceptance result — <c>{runId, state, cancelRequested}</c>
        /// with the Run's state at request time — or an <c>ok:false</c>
        /// envelope carrying OW-API-001 (malformed runId), OW-RUN-010
        /// (unknown), OW-RUN-014 (forbidden state) or the store/seam's own
        /// diagnostics.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public AgentApiResponse CancelWorkflow(CancelWorkflowRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var run = RequireRun(request.RunId);
                if (!run.Ok)
                {
                    return Failure(run.Diagnostics);
                }
                var snapshot = run.Value!;
                if (snapshot.State != RunState.Queued &&
                    snapshot.State != RunState.Running &&
                    snapshot.State != RunState.Suspended)
                {
                    return Failure(new[]
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_RUN_014,
                            "The Run is in a state that cannot be cancelled by an Agent."),
                    });
                }
                if (!snapshot.CancelPending)
                {
                    var cancelled = _cancelRun(
                        snapshot.RunId,
                        string.IsNullOrWhiteSpace(request.Reason)
                            ? DefaultCancelReason
                            : request.Reason!);
                    if (!cancelled.Ok)
                    {
                        return Failure(cancelled.Diagnostics);
                    }
                }
                // The cancel request publishes no §E8 event, so the thread-safe
                // snapshot learns the journaled record from this main-thread
                // refresh instead of waiting for the next transition.
                _runStatus?.RefreshNow(snapshot.RunId);
                return Ok(new JObject
                {
                    ["runId"] = snapshot.RunId,
                    ["state"] = JsonNaming.ToSnakeCase(snapshot.State.ToString()),
                    ["cancelRequested"] = true,
                });
            });
        }

        /// <summary>
        /// The <c>validate_workflow</c> command of SPEC §H1: resolves the
        /// <c>workflow</c>/<c>path</c> source, reads the document through the
        /// shared §A3 serializer and compiles it through the snapshot-backed
        /// <see cref="WorkflowCompiler"/> as <see cref="ActorKind.Agent"/>.
        /// The answer carries the §D1 diagnostics on the envelope and the
        /// compact summary in <c>data</c> — step count, declared effects,
        /// approval requirements and the §D0.1 <c>planHash</c>
        /// <paramref name="request"/>'s caller later binds an execution to;
        /// a §D8 <c>dryRun</c> adds the predicted
        /// <c>plannedChanges</c> and the <c>guarantee:"none"</c> marker, and
        /// <c>includePlan</c> adds the serialized Plan (§H3 item 2).
        /// </summary>
        /// <returns>
        /// The summary — <c>{steps, effects, approvals, planHash}</c> plus
        /// <c>{plannedChanges, guarantee}</c> on a Dry Run and <c>{plan}</c>
        /// on request — or an <c>ok:false</c> envelope carrying OW-API-001
        /// (malformed source or unreadable file), the serializer's
        /// §A3/§J codes or the compiler's validation errors.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public AgentApiResponse ValidateWorkflow(ValidateWorkflowRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var loaded = LoadWorkflowDocument(request.Workflow, request.Path);
                if (!loaded.Ok)
                {
                    return Failure(loaded.Diagnostics);
                }
                var warnings = new List<Diagnostic>(loaded.Diagnostics);
                var compiled = Workflows().Validate(
                    loaded.Value!,
                    InputsOf(request.Inputs),
                    request.DryRun ? ValidationMode.DryRun : ValidationMode.Static,
                    Actor);
                if (!compiled.Ok)
                {
                    return Failure(compiled.Diagnostics, warnings);
                }
                warnings.AddRange(compiled.Diagnostics);
                var report = compiled.Value!;
                warnings.AddRange(report.Warnings);
                if (!report.Ok || report.Plan is null)
                {
                    return Failure(report.Errors, warnings);
                }
                var data = PlanSummary(report);
                if (report.Truncated)
                {
                    data["diagnosticsTruncated"] = true;
                }
                if (request.IncludePlan)
                {
                    data["plan"] = JObject.Parse(PlanJsonCodec.Write(report.Plan));
                }
                return Ok(data, warnings);
            });
        }

        /// <summary>
        /// The <c>execute_workflow</c> command of SPEC §H1: resolves and
        /// statically compiles the document exactly as
        /// <see cref="ValidateWorkflow"/> — an invalid workflow never
        /// produces a Run — then admits the Plan through the §E2
        /// acceptance chain as <see cref="ActorKind.Agent"/> with the
        /// request's <see cref="AcceptOptions"/>: the coordinator's
        /// <c>expectedPlanHash</c> comparison rejects a changed plan with
        /// OW-RUN-003, the settings/integrity gates re-check permissions
        /// and dynamic paths immediately before admission, and
        /// <c>queue</c> selects the waiting-queue admission.
        /// </summary>
        /// <returns>
        /// The acceptance result — <c>{runId, state}</c> with the Run's
        /// §E1 state at admission (Queued, Running or AwaitingApproval) —
        /// or an <c>ok:false</c> envelope carrying the same failures as
        /// <see cref="ValidateWorkflow"/> plus the §E2 refusal codes
        /// (OW-RUN-003 hash mismatch, OW-OP-011 full integrity, the
        /// permission/precondition diagnostics).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public AgentApiResponse ExecuteWorkflow(ExecuteWorkflowRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var loaded = LoadWorkflowDocument(request.Workflow, request.Path);
                if (!loaded.Ok)
                {
                    return Failure(loaded.Diagnostics);
                }
                var document = loaded.Value!;
                var warnings = new List<Diagnostic>(loaded.Diagnostics);
                var compiled = Workflows().Validate(
                    document, InputsOf(request.Inputs), ValidationMode.Static, Actor);
                if (!compiled.Ok)
                {
                    return Failure(compiled.Diagnostics, warnings);
                }
                warnings.AddRange(compiled.Diagnostics);
                var report = compiled.Value!;
                warnings.AddRange(report.Warnings);
                if (!report.Ok || report.Plan is null)
                {
                    return Failure(report.Errors, warnings);
                }
                var accepted = Workflows().AcceptRun(
                    report.Plan, document, Actor,
                    new AcceptOptions(request.ExpectedPlanHash, request.Queue));
                if (!accepted.Ok)
                {
                    return Failure(accepted.Diagnostics, warnings);
                }
                warnings.AddRange(accepted.Diagnostics);
                var handle = accepted.Value!;
                return Ok(new JObject
                {
                    ["runId"] = handle.RunId,
                    ["state"] = JsonNaming.ToSnakeCase(handle.State.ToString()),
                }, warnings);
            });
        }

        /// <summary>
        /// The <c>migrate_workflow</c> command of SPEC §H1, bound to §A7:
        /// resolves the <c>workflow</c>/<c>path</c> source into the raw
        /// document JSON — a pre-migration document is not a validated
        /// <see cref="WorkflowDocument"/>, so the §A3 serializer never
        /// runs — and chains the schema-version and Operation-version
        /// migrations against the live Registry. The returned
        /// <c>workflow</c> is the migrated document JSON; <c>changes</c>
        /// lists every applied step in order. Nothing is written back —
        /// saving stays with the caller (SPEC §A7 item 3).
        /// </summary>
        /// <returns>
        /// <c>{workflow, changes}</c> — each change
        /// <c>{kind, nodeId?, operationId?, fromVersion, toVersion}</c> —
        /// or an <c>ok:false</c> envelope carrying OW-API-001 (malformed
        /// source or unreadable file), OW-WF-001 (unreadable JSON) or the
        /// §A7 migration codes (OW-WF-002, OW-WF-007, OW-OP-001,
        /// OW-OP-002).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public AgentApiResponse MigrateWorkflow(MigrateWorkflowRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var loaded = LoadWorkflowJson(request.Workflow, request.Path);
                if (!loaded.Ok)
                {
                    return Failure(loaded.Diagnostics);
                }
                var migrated = Workflows().Migrate(loaded.Value!);
                if (!migrated.Ok)
                {
                    return Failure(migrated.Diagnostics);
                }
                var result = migrated.Value!;
                var changes = new JArray();
                foreach (var change in result.Changes)
                {
                    var entry = new JObject
                    {
                        ["kind"] = JsonNaming.ToSnakeCase(change.Kind.ToString()),
                        ["fromVersion"] = change.FromVersion,
                        ["toVersion"] = change.ToVersion,
                    };
                    if (change.NodeId is not null)
                    {
                        entry["nodeId"] = change.NodeId;
                    }
                    if (change.OperationId is not null)
                    {
                        entry["operationId"] = change.OperationId;
                    }
                    changes.Add(entry);
                }
                return Ok(new JObject
                {
                    ["workflow"] = result.Document,
                    ["changes"] = changes,
                }, migrated.Diagnostics);
            });
        }

        // The workflow backend every §H1 workflow command shares; absent
        // means the command layer was installed without its environment —
        // an internal error (OW-API-004 through Invoke), never a partial
        // behaviour.
        private IAgentWorkflowBackend Workflows() =>
            _workflows ?? throw new InvalidOperationException(
                "The workflow backend is not installed on this service.");

        // The execution-input conversion of §D0.1: the optional 'inputs'
        // object crosses the boundary as a JsonValue; the compiler's own
        // Codec conversion decides the Plan-hash material.
        private static JsonValue? InputsOf(JObject? inputs) =>
            inputs is null ? (JsonValue?)null : JsonValue.From(inputs);

        // Resolves the workflow/path source to a validated
        // WorkflowDocument: the request-shape checks of WorkflowSource,
        // the backend's project-contained file read and the shared §A3
        // serializer's strict parse — diagnostics, never exceptions.
        private Result<WorkflowDocument> LoadWorkflowDocument(
            JToken? workflow, string? path)
        {
            var source = WorkflowSource.From(workflow, path);
            if (!source.Ok)
            {
                return Result<WorkflowDocument>.Failure(source.Diagnostics);
            }
            string text;
            var resolved = source.Value!;
            if (resolved.IsBody)
            {
                text = resolved.Workflow!.ToString(Formatting.None);
            }
            else
            {
                var read = Workflows().ReadWorkflowText(resolved.Path!);
                if (!read.Ok)
                {
                    return Result<WorkflowDocument>.Failure(read.Diagnostics);
                }
                text = read.Value!;
            }
            return WorkflowSerializer.Read(text);
        }

        // Resolves the workflow/path source to the raw document JSON the
        // §A7 migrator consumes — the same source/file checks as
        // LoadWorkflowDocument but only the strict SG-02 JSON parse, since
        // a pre-migration document may not satisfy the current §A3 shape.
        private Result<JObject> LoadWorkflowJson(JToken? workflow, string? path)
        {
            var source = WorkflowSource.From(workflow, path);
            if (!source.Ok)
            {
                return Result<JObject>.Failure(source.Diagnostics);
            }
            var resolved = source.Value!;
            if (resolved.IsBody)
            {
                return Result<JObject>.Success(resolved.Workflow!);
            }
            var read = Workflows().ReadWorkflowText(resolved.Path!);
            if (!read.Ok)
            {
                return Result<JObject>.Failure(read.Diagnostics);
            }
            var parsed = JsonValue.Parse(read.Value!);
            if (!parsed.Ok)
            {
                // SG-02's caller-maps-once rule: a workflow document read
                // reports strict-JSON failures as OW-WF-001.
                var diagnostics = new List<Diagnostic>(parsed.Diagnostics.Count);
                foreach (var diagnostic in parsed.Diagnostics)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_WF_001, diagnostic.Message,
                        jsonPath: diagnostic.JsonPath));
                }
                return Result<JObject>.Failure(diagnostics);
            }
            return parsed.Value!.ToToken() is JObject document
                ? Result<JObject>.Success(document)
                : Result<JObject>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_WF_001,
                    "The workflow document must be a JSON object.",
                    jsonPath: "$"));
        }

        // The compact Plan summary of validate_workflow (§H3 item 2):
        // step count, the §C4 effects the Plan declares and its approval
        // requirements, the §D0.1 planHash — plus the Dry-Run-only
        // predicted changes and the §D8 "none" guarantee marker.
        private static JObject PlanSummary(ValidationReport report)
        {
            var plan = report.Plan!;
            var data = new JObject
            {
                ["steps"] = plan.Steps.Count,
                ["effects"] = new JArray(plan.Effects),
                ["approvals"] = ApprovalsJson(plan.Approvals),
                ["planHash"] = plan.PlanHash,
            };
            if (report.PlannedChanges is { } changes)
            {
                var items = new JArray();
                foreach (var change in changes)
                {
                    items.Add(new JObject
                    {
                        ["nodeId"] = change.NodeId,
                        ["kind"] = change.Kind,
                        ["target"] = change.Target,
                        ["effect"] = change.Effect,
                    });
                }
                data["plannedChanges"] = items;
                data["guarantee"] = report.Guarantee;
            }
            return data;
        }

        // The approval-requirement serialization shared by the Plan
        // summary: {nodeId, effect, detail} per entry.
        private static JArray ApprovalsJson(IReadOnlyList<ApprovalRequirement> approvals)
        {
            var items = new JArray();
            foreach (var approval in approvals)
            {
                items.Add(new JObject
                {
                    ["nodeId"] = approval.NodeId,
                    ["effect"] = approval.Effect,
                    ["detail"] = approval.Detail,
                });
            }
            return items;
        }

        /// <summary>
        /// The <c>get_run_status</c> command of SPEC §H1: one Run's journaled
        /// status — runId, workflowId, §E1 state, the per-step entries and the
        /// terminal outputs — read from the SPEC §H2 item 3 thread-safe
        /// snapshot, so it answers on a Pipeline worker thread while the
        /// Editor's main thread is busy and never touches mutable Core state
        /// off it (SPEC §0.4).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>verbosity: "compact"</c> reduces every step to the SPEC §H3
        /// item 2 <c>{node, state, attempt}</c>; omitted or <c>"full"</c>
        /// keeps the step detail — the wait label of a §E0
        /// <c>step_waiting</c>, the persisted step outputs and the recorded
        /// failure. <c>waitSeconds</c> (0–20, SPEC §H2 item 3) turns the call
        /// into a long-poll on the calling thread: it returns as soon as the
        /// snapshot is replaced — every relevant §E8 event replaces it — or
        /// the duration ends, never earlier than a real change and never on
        /// the main thread. A Run whose terminal record is already journaled
        /// cannot change, so its wait returns immediately rather than
        /// sleeping out the request (a local reversible choice — the §H2
        /// contract only fixes the bound and the wake-up).
        /// </para>
        /// <para>
        /// While the Run is AwaitingApproval the answer carries the Plan's
        /// required-approval list (§D6 item 1: the effect-gated approvals the
        /// human must decide) and the SPEC §H3 item 4 hint — the Agent can
        /// relay "approve in the Unity Run Viewer" but the decision itself is
        /// human-only (SPEC §H4 item 5); no member here ever performs it.
        /// </para>
        /// </remarks>
        /// <param name="request">The command's arguments (runId, optional
        /// waitSeconds, optional verbosity).</param>
        /// <returns>
        /// The status payload, or an <c>ok:false</c> envelope carrying
        /// OW-API-001 (malformed runId, waitSeconds outside 0–20 or an unknown
        /// verbosity) or OW-RUN-010 (well-formed but unknown runId) — SPEC §J.
        /// While the tracker's seed stands incomplete the answer is the
        /// Run Store's own retained failure instead: an unproven runId is
        /// never reported as unknown and an unserved Run never as absent
        /// (T-W3.2-08).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public AgentApiResponse GetRunStatus(GetRunStatusRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var malformed = MalformedRunId(request.RunId);
                if (malformed is not null)
                {
                    return Failure(new[] { malformed });
                }
                if (request.WaitSeconds < 0 || request.WaitSeconds > MaxWaitSeconds)
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'waitSeconds' argument must be between 0 and 20 seconds.",
                            jsonPath: "$.waitSeconds"),
                    });
                }
                var compact = false;
                if (request.Verbosity is not null)
                {
                    if (request.Verbosity == "compact")
                    {
                        compact = true;
                    }
                    else if (request.Verbosity != "full")
                    {
                        return Failure(new[]
                        {
                            AgentApiErrors.MalformedRequest(
                                "The 'verbosity' argument must be \"compact\" or \"full\".",
                                jsonPath: "$.verbosity"),
                        });
                    }
                }
                var tracker = _runStatus ?? throw new InvalidOperationException(
                    "The run status snapshot is not installed on this service.");
                var deleted = DeletedRunFailure(request.RunId!);
                if (deleted is not null)
                {
                    return deleted;
                }
                var observed = tracker.SnapshotOf(request.RunId!);
                if (observed.View is { } view &&
                    request.WaitSeconds > 0 && !view.Snapshot.Ended)
                {
                    var waited = tracker.AwaitChange(
                        request.RunId!, view.Version,
                        TimeSpan.FromSeconds(request.WaitSeconds));
                    // A null wait means the tracked entry was dropped
                    // mid-wait — re-read the observation so a retained
                    // store failure still reports instead of OW-RUN-010.
                    observed = waited is { } newer
                        ? new RunObservation(newer, null)
                        : tracker.SnapshotOf(request.RunId!);
                }
                if (observed.View is { } final)
                {
                    return Ok(StatusData(final.Snapshot, compact));
                }
                // While the seed is incomplete the tracker keeps the
                // store's own failure (T-W3.2-08): an unproven or
                // unserved Run reports it, never OW-RUN-010.
                return observed.Failure is { } failure
                    ? Failure(failure)
                    : Failure(new[] { RunMissing() });
            });
        }

        /// <summary>
        /// The <c>get_run_logs</c> command of SPEC §H1: the Run's §F6 log
        /// entries and its §F7 change list, read straight from the journal
        /// file — read-only access that touches no mutable Core state, so it
        /// answers off the main thread exactly like the status snapshot
        /// (SPEC §0.4 / §H2 item 3).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>level</c> is the minimum severity served — the reading SPEC
        /// §F6 already fixes for its default: omitted means info and above,
        /// <c>"debug"</c> additionally admits the debug level, which the
        /// §F6 SG-08 filter still drops unless the live
        /// <c>debugLogging</c> policy permits it at read time. <c>step</c>
        /// keeps the entries of one Plan step. Entries come back in journal
        /// order — <see cref="SanitizedRunLogEntry.Seq"/> is inside each —
        /// paged by the SPEC §H3 item 3 cursor over the filtered list, while
        /// <c>changes</c> carries the whole durable union every page (a page
        /// is self-contained).
        /// </para>
        /// <para>
        /// The journal read is fail-closed (SPEC §F1): a mid-journal record
        /// that cannot be interpreted surfaces its OW-STORE-002 diagnostic as
        /// an <c>ok:false</c> envelope rather than a partial list presented as
        /// complete; only the SPEC §F3 item 3 interrupted-write tail rides as
        /// a warning on the decoded prefix. Every served value already passed
        /// the §F6 credential mask — writer-side, then again inside
        /// <see cref="RunLogReader"/> — and no Plan or code text is ever
        /// emitted by this command.
        /// </para>
        /// </remarks>
        /// <param name="request">The command's arguments (runId, optional
        /// level, optional step, optional cursor).</param>
        /// <returns>
        /// <c>{runId, logs, changes}</c> — the entry page and the §F7 union —
        /// or an <c>ok:false</c> envelope carrying OW-API-001 (malformed
        /// runId, unknown level or a negative step), OW-API-002 (a cursor
        /// that does not decode for this query), OW-RUN-010 (unknown runId)
        /// or the journal read's own OW-STORE-002 (SPEC §J).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public AgentApiResponse GetRunLogs(GetRunLogsRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                var malformed = MalformedRunId(request.RunId);
                if (malformed is not null)
                {
                    return Failure(new[] { malformed });
                }
                var minimumLevel = InfoRank;
                if (request.Level is not null &&
                    !s_logLevelRanks.TryGetValue(request.Level, out minimumLevel))
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'level' argument must be \"debug\", \"info\", " +
                                "\"warning\" or \"error\".",
                            jsonPath: "$.level"),
                    });
                }
                if (request.Step is < 0)
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'step' argument must be a Plan step index (0 or greater).",
                            jsonPath: "$.step"),
                    });
                }
                var root = _runStoreRoot ?? throw new InvalidOperationException(
                    "The Run Store root is not installed on this service.");
                var readChanges = _readChanges ?? throw new InvalidOperationException(
                    "The journaled change read is not installed on this service.");
                var deleted = DeletedRunFailure(request.RunId!);
                if (deleted is not null)
                {
                    return deleted;
                }

                // The §F6 SG-08 policy is read once, at request time: a toggled
                // setting never changes an answer already in flight.
                var read = RunLogReader.Read(
                    root, request.RunId!, _debugLogging?.Invoke() ?? false);
                if (!read.Ok)
                {
                    return Failure(read.Diagnostics);
                }
                // The reader returns its decoded prefix together with the
                // diagnostic that ended the read; severity decides the
                // envelope — an Error-severity diagnostic means the listing
                // is partial and fails closed (SPEC §F1), the §F3 item 3
                // tail-drop Warning rides on the served prefix.
                var errors = new List<Diagnostic>();
                var warnings = new List<Diagnostic>();
                for (var i = 0; i < read.Diagnostics.Count; i++)
                {
                    (read.Diagnostics[i].Severity == Severity.Error ? errors : warnings)
                        .Add(read.Diagnostics[i]);
                }
                if (errors.Count > 0)
                {
                    return Failure(errors, warnings);
                }
                var journaled = readChanges(request.RunId!);
                if (!journaled.Ok)
                {
                    return Failure(journaled.Diagnostics, warnings);
                }
                var entries = read.Value!;
                var items = new List<JToken>(entries.Count);
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (LevelRankOf(entry.Level) < minimumLevel ||
                        (request.Step is { } step && entry.Step != step))
                    {
                        continue;
                    }
                    items.Add(LogEntryJson(entry));
                }
                var changesJson = ChangesJson(journaled.Value!);
                // The cursor's request binding carries only the arguments
                // the request actually supplied: an omitted optional filter
                // is Missing (the key is left out), never a null-valued
                // string token — Canonical JSON cannot serialize the latter
                // and SPEC §A0.1 never emits one.
                var context = PageContext("get_run_logs", GetRunLogsArguments(request));
                return PageResponse(
                    context, request.Cursor, items,
                    (start, count) => new JObject
                    {
                        ["runId"] = request.RunId,
                        ["logs"] = new JArray(Slice(items, start, count)),
                        ["changes"] = changesJson,
                    },
                    warnings: warnings.Count == 0 ? null : warnings);
            });
        }

        /// <summary>
        /// The <c>list_runs</c> command of SPEC §H1: the stored Runs — runId,
        /// workflowId, §E1 state and the journaled timestamps — filtered by
        /// <paramref name="request"/>'s state and workflowId and paged under
        /// its <paramref name="ListRunsRequest.Limit"/>, read from the same
        /// thread-safe snapshots <see cref="GetRunStatus"/> uses, so the
        /// listing is also safe off the main thread (SPEC §H2 item 3).
        /// </summary>
        /// <param name="request">The command's arguments (optional state and
        /// workflowId filters, the required limit, optional cursor).</param>
        /// <returns>
        /// <c>{runs}</c> — one object per matching Run — or an
        /// <c>ok:false</c> envelope carrying OW-API-001 (an unknown state,
        /// a missing or non-positive limit) or OW-API-002 (a cursor that does
        /// not decode for this query) — SPEC §J. While the tracker's seed
        /// stands incomplete the answer is the Run Store's own retained
        /// failure, never an empty or silently partial list (T-W3.2-08).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public AgentApiResponse ListRuns(ListRunsRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return Invoke(() =>
            {
                RunState? state = null;
                if (request.State is not null)
                {
                    if (s_runStates.TryGetValue(request.State, out var parsed))
                    {
                        state = parsed;
                    }
                    else
                    {
                        return Failure(new[]
                        {
                            AgentApiErrors.MalformedRequest(
                                "The 'state' argument must be a Run state name.",
                                jsonPath: "$.state"),
                        });
                    }
                }
                if (request.WorkflowId is not null &&
                    request.WorkflowId.Length == 0)
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'workflowId' argument must be a workflow identifier.",
                            jsonPath: "$.workflowId"),
                    });
                }
                if (request.Limit is null)
                {
                    return Failure(new[]
                    {
                        AgentApiErrors.MalformedRequest(
                            "The 'limit' argument is required.",
                            jsonPath: "$.limit"),
                    });
                }
                var tracker = _runStatus ?? throw new InvalidOperationException(
                    "The run status snapshot is not installed on this service.");
                var listed = tracker.ListRuns();
                if (!listed.Ok)
                {
                    // An incomplete seed is never served as an empty or
                    // silently partial listing — the retained store
                    // failure is the answer (T-W3.2-08).
                    return Failure(listed.Diagnostics);
                }
                var items = new List<JToken>();
                foreach (var view in listed.Value!)
                {
                    var snapshot = view.Snapshot;
                    if (_isDeletedRun is { } isDeleted)
                    {
                        // A tombstoned Run is deleted — a snapshot the
                        // tracker still holds for it must never be
                        // listed; a tombstone set that cannot be proven
                        // fails the whole listing closed instead of
                        // risking a deleted Run presented as live.
                        var deleted = isDeleted(snapshot.RunId);
                        if (!deleted.Ok)
                        {
                            return Failure(deleted.Diagnostics);
                        }
                        if (deleted.Value)
                        {
                            continue;
                        }
                    }
                    var workflowId = snapshot.Plan?.WorkflowId;
                    if ((state is null || snapshot.State == state.Value) &&
                        (request.WorkflowId is null ||
                         string.Equals(
                             workflowId, request.WorkflowId, StringComparison.Ordinal)))
                    {
                        items.Add(RunEntryJson(snapshot, workflowId));
                    }
                }
                // Omitted optional filters are Missing, exactly as in the
                // get_run_logs context — never a null-valued string token.
                var context = PageContext("list_runs", ListRunsArguments(request));
                return PageResponse(
                    context, request.Cursor, items,
                    (start, count) => new JObject
                    {
                        ["runs"] = new JArray(Slice(items, start, count)),
                    },
                    limit: request.Limit);
            });
        }

        // The request-argument object get_run_logs binds its page cursor to:
        // only the arguments the request actually supplied — an omitted
        // optional argument leaves no member behind (Missing is never a
        // null-valued token, SPEC §A0.1), so a cursor issued for an
        // unfiltered query replays against the identical context.
        private static JObject GetRunLogsArguments(GetRunLogsRequest request)
        {
            var arguments = new JObject { ["runId"] = request.RunId };
            if (request.Level is not null)
            {
                arguments["level"] = request.Level;
            }
            if (request.Step is { } step)
            {
                arguments["step"] = step;
            }
            return arguments;
        }

        // The list_runs counterpart: the required limit plus the supplied
        // optional filters — omitted filters stay Missing.
        private static JObject ListRunsArguments(ListRunsRequest request)
        {
            var arguments = new JObject();
            if (request.State is not null)
            {
                arguments["state"] = request.State;
            }
            if (request.WorkflowId is not null)
            {
                arguments["workflowId"] = request.WorkflowId;
            }
            arguments["limit"] = request.Limit;
            return arguments;
        }

        // The SPEC §H3 test 1 / H3 item 2 status payload: the journaled
        // identity, the §E1 state and the step list — compact drops each
        // step to {node, state, attempt} (a 50-step Run's compact status
        // must fit 8 KiB). Plan steps with no journal record report their
        // recorded state — pending, attempt 0 (the fold's own rule: no
        // record, no snapshot). A Run whose plan.json could not be decoded
        // is NeedsReview; its step list then reports the journaled step
        // records, keyed by index since no Plan node names exist to claim.
        private static JObject StatusData(RunSnapshot snapshot, bool compact)
        {
            var recorded = new Dictionary<int, StepSnapshot>();
            for (var i = 0; i < snapshot.Steps.Count; i++)
            {
                recorded[snapshot.Steps[i].Index] = snapshot.Steps[i];
            }
            var data = new JObject
            {
                ["runId"] = snapshot.RunId,
                ["state"] = JsonNaming.ToSnakeCase(snapshot.State.ToString()),
            };
            // A Run whose plan.json could not be decoded has no workflowId to
            // report — the member is omitted, never written null (§A0.1).
            if (snapshot.Plan?.WorkflowId is { } workflowId)
            {
                data["workflowId"] = workflowId;
            }
            if (snapshot.CreatedAt is { } createdAt)
            {
                data["createdAt"] = createdAt;
            }
            if (snapshot.UpdatedAt is { } updatedAt)
            {
                data["updatedAt"] = updatedAt;
            }
            if (snapshot.CancelPending)
            {
                data["cancelRequested"] = true;
            }
            if (CurrentStep(snapshot.Plan, recorded) is { } current)
            {
                data["currentStep"] = current;
            }
            data["steps"] = StepsData(snapshot, recorded, compact);
            if (snapshot.Outputs is { } outputs)
            {
                data["outputs"] = outputs.ToToken();
            }
            if (snapshot.State == RunState.AwaitingApproval)
            {
                data["approvals"] = RequiredApprovals(snapshot.Plan);
                data["hint"] = ApprovalHint;
            }
            return data;
        }

        // The node id of the first Plan step whose last record left it
        // Running or Waiting — the "current" step the §H2 status view names
        // — or null (a Plan that could not be decoded names none).
        private static string? CurrentStep(
            CompiledPlan? plan, IReadOnlyDictionary<int, StepSnapshot> recorded)
        {
            if (plan is null)
            {
                return null;
            }
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var planStep = plan.Steps[i];
                if (recorded.TryGetValue(planStep.Index, out var step) &&
                    (step.State == StepState.Running || step.State == StepState.Waiting))
                {
                    return planStep.NodeId;
                }
            }
            return null;
        }

        // The step entries in Plan order when a Plan exists — the journaled
        // StepSnapshots looked up by index — else the journaled records
        // alone, keyed by their index.
        private static JArray StepsData(
            RunSnapshot snapshot,
            IReadOnlyDictionary<int, StepSnapshot> recorded,
            bool compact)
        {
            var steps = new JArray();
            if (snapshot.Plan is { } plan)
            {
                for (var i = 0; i < plan.Steps.Count; i++)
                {
                    var planStep = plan.Steps[i];
                    recorded.TryGetValue(planStep.Index, out var step);
                    steps.Add(StepData(planStep.Index, planStep.NodeId, step, compact));
                }
            }
            else
            {
                for (var i = 0; i < snapshot.Steps.Count; i++)
                {
                    var step = snapshot.Steps[i];
                    steps.Add(StepData(step.Index, null, step, compact));
                }
            }
            return steps;
        }

        // One step entry: compact is exactly {node, state, attempt} (with
        // `index` standing in only when no Plan exists to name the node);
        // full adds what the journal recorded — the §E0 wait label, the
        // persisted step outputs (SPEC §E5) and the recorded failure.
        private static JObject StepData(
            int index, string? nodeId, StepSnapshot? step, bool compact)
        {
            var entry = new JObject();
            if (nodeId is not null)
            {
                entry["node"] = nodeId;
            }
            else
            {
                entry["index"] = index;
            }
            entry["state"] = JsonNaming.ToSnakeCase(
                (step?.State ?? StepState.Pending).ToString());
            entry["attempt"] = step?.Attempt ?? 0;
            if (!compact && step is { } recorded)
            {
                if (recorded.WaitingFor is { } waitingFor)
                {
                    entry["waitingFor"] = waitingFor;
                }
                if (recorded.Outputs is { } outputs)
                {
                    entry["outputs"] = outputs.ToToken();
                }
                if (recorded.FailureCode is not null ||
                    recorded.FailureMessage is not null)
                {
                    var error = new JObject();
                    if (recorded.FailureCode is { } code)
                    {
                        error["code"] = code;
                    }
                    if (recorded.FailureMessage is { } message)
                    {
                        error["message"] = message;
                    }
                    entry["error"] = error;
                }
            }
            return entry;
        }

        // The required-approval list of SPEC §D6 item 1 the H3 item 4 status
        // carries while the Run waits on the human — each element the Plan's
        // {nodeId, effect, detail}; the decision stays human-only (§H4
        // item 5).
        private static JArray RequiredApprovals(CompiledPlan? plan)
        {
            var approvals = new JArray();
            if (plan is null)
            {
                return approvals;
            }
            for (var i = 0; i < plan.Approvals.Count; i++)
            {
                var required = plan.Approvals[i];
                approvals.Add(new JObject
                {
                    ["nodeId"] = required.NodeId,
                    ["effect"] = required.Effect,
                    ["detail"] = required.Detail,
                });
            }
            return approvals;
        }

        // The list_runs element: what §H1 names (runId, workflowId, state)
        // plus the journaled timestamps an Agent pages over.
        private static JObject RunEntryJson(RunSnapshot snapshot, string? workflowId)
        {
            var entry = new JObject
            {
                ["runId"] = snapshot.RunId,
                ["state"] = JsonNaming.ToSnakeCase(snapshot.State.ToString()),
            };
            if (workflowId is not null)
            {
                entry["workflowId"] = workflowId;
            }
            if (snapshot.CreatedAt is { } createdAt)
            {
                entry["createdAt"] = createdAt;
            }
            if (snapshot.UpdatedAt is { } updatedAt)
            {
                entry["updatedAt"] = updatedAt;
            }
            return entry;
        }

        // The §F6 wire shape of one sanitized log entry, plus the journal
        // seq the entry came from — the stable position of the record the
        // filtered list pages over.
        private static JObject LogEntryJson(SanitizedRunLogEntry entry)
        {
            var item = new JObject
            {
                ["seq"] = entry.Seq,
                ["ts"] = entry.Ts,
                ["level"] = entry.Level,
                ["message"] = entry.Message,
            };
            if (entry.Step is { } step)
            {
                item["step"] = step;
            }
            if (entry.Code is { } code)
            {
                item["code"] = code;
            }
            if (entry.Data is { } data)
            {
                item["data"] = data.ToToken();
            }
            return item;
        }

        // The §F7 element list of the journaled change union — the same
        // {step, attempt, nodeId, action, target, undoable} shape
        // changes.json and the §F2 changes members carry; the production
        // element writer is a Core internal, so the shape is written here
        // field by field from the public record.
        private static JArray ChangesJson(IReadOnlyList<ChangeRecord> changes)
        {
            var array = new JArray();
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                var target = new JObject
                {
                    ["kind"] = change.Target.Kind,
                    ["path"] = change.Target.Path,
                };
                if (change.Target.Guid is { } guid)
                {
                    target["guid"] = guid;
                }
                if (change.Target.GlobalId is { } globalId)
                {
                    target["globalId"] = globalId;
                }
                array.Add(new JObject
                {
                    ["step"] = change.Step,
                    ["attempt"] = change.Attempt,
                    ["nodeId"] = change.NodeId,
                    ["action"] = JsonNaming.ToSnakeCase(change.Action.ToString()),
                    ["target"] = target,
                    ["undoable"] = change.Undoable,
                });
            }
            return array;
        }

        private static IEnumerable<JToken> Slice(
            IReadOnlyList<JToken> items, int start, int count)
        {
            for (var i = start; i < start + count; i++)
            {
                yield return items[i];
            }
        }

        private static int LevelRankOf(string level) =>
            s_logLevelRanks.TryGetValue(level, out var rank) ? rank : InfoRank;

        // The §E1 wire names → RunState table, built from the enum so the
        // accepted spellings can never drift from the serialized forms.
        private static IReadOnlyDictionary<string, RunState> BuildRunStateNames()
        {
            var names = new Dictionary<string, RunState>(StringComparer.Ordinal);
            foreach (var value in (RunState[])Enum.GetValues(typeof(RunState)))
            {
                names[JsonNaming.ToSnakeCase(value.ToString())] = value;
            }
            return names;
        }

        // The grammar-only runId check of the off-main-thread commands: the
        // same OW-API-001 shape RequireRun emits for malformed input, but
        // without the store Load these commands must never perform off the
        // main thread (SPEC §0.4 — the well-formed-but-unknown answer comes
        // from the snapshot and journal readers instead).
        private static Diagnostic? MalformedRunId(string? runId) =>
            string.IsNullOrWhiteSpace(runId) || !IsRunId(runId)
                ? AgentApiErrors.MalformedRequest(
                    "The 'runId' argument must be a run_<ULID> identifier.",
                    jsonPath: "$.runId")
                : null;

        private static Diagnostic RunMissing() =>
            ErrorCodes.Create(ErrorCodes.OW_RUN_010, "The Run does not exist.");

        private static Diagnostic RunDeleted() =>
            ErrorCodes.Create(ErrorCodes.OW_STORE_003, "The Run was deleted.");

        // The SG-16 deleted-run verdict of the observation commands:
        // the Core-owned tombstone lookup decides deleted (OW-STORE-003)
        // vs readable — and a tombstone file that cannot prove its
        // contents fails closed with its OW-STORE-002 rather than let
        // the snapshot or journal readers answer "missing" for a Run
        // that may only be deleted. Null means "not tombstoned — the
        // ordinary read proceeds"; absent the Core tombstone seam the
        // check is skipped entirely.
        private AgentApiResponse? DeletedRunFailure(string runId)
        {
            if (_isDeletedRun is null)
            {
                return null;
            }
            var deleted = _isDeletedRun(runId);
            if (!deleted.Ok)
            {
                return Failure(deleted.Diagnostics);
            }
            return deleted.Value
                ? Failure(new[] { RunDeleted() })
                : null;
        }

        // Delivers an item too large for a page of its own as ≤cap string
        // segments of the Canonical JSON of its whole single-item page envelope
        // — {ok, data, errors, warnings} plus the next-item page cursor when
        // one remains — so the reassembled text is the original page response.
        // At the tail of a listing (item offset == count) the segmented
        // envelope is the empty final page instead: warnings too large for any
        // envelope still reach the Agent inside the content.
        // `position` carries the item offset, the segment offset (0 on a fresh
        // segmentation) and — on a replay — the content digest the cursor
        // bound; a digest mismatch or an offset past the current listing means
        // the data changed under the cursor (OW-API-002).
        private AgentApiResponse SegmentPageItem(
            string context,
            AgentApiCursor position,
            IReadOnlyList<JToken> items,
            Func<int, int, JToken> pack,
            IReadOnlyList<Diagnostic> warnings)
        {
            if (position.ItemOffset > items.Count)
            {
                return Failure(new[] { AgentApiErrors.InvalidCursor() });
            }
            var tail = position.ItemOffset == items.Count;
            var nextItem = position.ItemOffset + 1;
            var whole = AgentApiResponse.Success(
                pack(position.ItemOffset, tail ? 0 : 1), warnings,
                !tail && nextItem < items.Count
                    ? _cursors.Issue(context, nextItem)
                    : null);
            var canonical = CanonicalJson.Serialize(whole.ToWire());
            var digest = AgentApiCursorCodec.TextDigest(canonical);
            return EmitSegment(
                context, position.ItemOffset, canonical, digest,
                position.SegmentOffset, position.DataDigest, ok: true,
                continuationCursor: whole.NextCursor);
        }

        // Delivers the whole serialized envelope of a response that did not fit
        // — the {ok, data, errors, warnings} content of Ok/Failure/Invoke — in
        // the same ≤cap string segments a segmented page item uses, so the two
        // paths can never disagree. `cursor` is a replayed nextCursor, or null
        // for the first segment; without a caller context the cursor binds to
        // the content's own digest, so a replay still dies the moment the
        // response changes.
        private AgentApiResponse SegmentEnvelope(
            AgentApiResponse original, string? context, string? cursor)
        {
            var canonical = CanonicalJson.Serialize(original.ToWire());
            var digest = AgentApiCursorCodec.TextDigest(canonical);
            var effectiveContext = context ?? digest;
            var segmentOffset = 0;
            string? boundDigest = null;
            if (cursor is not null)
            {
                var decoded = _cursors.Decode(cursor, effectiveContext);
                if (!decoded.Ok || !decoded.Value.IsSegment)
                {
                    return Failure(new[] { AgentApiErrors.InvalidCursor() });
                }
                segmentOffset = decoded.Value.SegmentOffset;
                boundDigest = decoded.Value.DataDigest;
            }
            return EmitSegment(
                effectiveContext, 0, canonical, digest, segmentOffset,
                boundDigest, original.Ok, continuationCursor: null);
        }

        // Emits canonical[segmentOffset..] as the next ≤cap `data` string
        // segment of the segmented envelope: `ok` preserves the original
        // outcome while errors and warnings stay empty on the wire (the real
        // diagnostics ride inside the segmented content). `boundDigest` is the
        // content digest a replayed cursor bound — null on a fresh
        // segmentation; a mismatch, or a segment offset past the text, means
        // the content changed under the cursor (OW-API-002).
        // `continuationCursor` is attached when a segment finishes the text —
        // the next-item page cursor for a segmented page, null for a whole
        // envelope whose final segment then carries no cursor at all.
        private AgentApiResponse EmitSegment(
            string context,
            int itemOffset,
            string canonical,
            string dataDigest,
            int segmentOffset,
            string? boundDigest,
            bool ok,
            string? continuationCursor)
        {
            if (boundDigest is not null &&
                (!string.Equals(boundDigest, dataDigest, StringComparison.Ordinal) ||
                 segmentOffset >= canonical.Length))
            {
                return Failure(new[] { AgentApiErrors.InvalidCursor() });
            }

            string? NextCursorFor(int end) =>
                end < canonical.Length
                    ? _cursors.IssueSegment(context, itemOffset, end, dataDigest)
                    : continuationCursor;

            // A measured segment candidate. `length` must not split a UTF-16
            // surrogate pair — the canonical text is valid Unicode, but a cut
            // inside a pair would leave an unpaired surrogate CanonicalJson
            // refuses to serialize.
            AgentApiResponse Candidate(int length) =>
                AgentApiResponse.Segment(
                    ok, canonical.Substring(segmentOffset, length),
                    NextCursorFor(segmentOffset + length));

            int SafeLength(int length) =>
                segmentOffset + length < canonical.Length &&
                char.IsLowSurrogate(canonical[segmentOffset + length])
                    ? length - 1
                    : length;

            var remaining = canonical.Length - segmentOffset;
            int length;
            if (Candidate(remaining).Utf8ByteCount <= MaxResponseBytes)
            {
                length = remaining;
            }
            else
            {
                // Binary search the largest fitting safe length in
                // [1, remaining): every probe still carries a segment cursor,
                // so the fit is monotonic in length.
                var low = 1;
                var high = remaining - 1;
                var best = 0;
                while (low <= high)
                {
                    var mid = low + (high - low) / 2;
                    var safe = SafeLength(mid);
                    if (safe > 0 && Candidate(safe).Utf8ByteCount <= MaxResponseBytes)
                    {
                        best = safe;
                        low = mid + 1;
                    }
                    else if (safe <= 0)
                    {
                        // mid == 1 splits a leading surrogate pair; a fitting
                        // segment needs the whole pair, so search right.
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }
                length = best;
                if (length <= 0)
                {
                    // A segment envelope's fixed cost is a few hundred bytes,
                    // so at least one character always fits; reaching this
                    // means the size measurement itself is broken — a genuine
                    // internal error, reported as OW-API-004 by Invoke.
                    throw new InvalidOperationException(
                        "A response segment cannot be represented inside the 64 KiB limit.");
                }
            }
            return Candidate(length);
        }

        // The single cap decision: a response inside the 64 KiB limit goes out
        // whole, an oversized one — success or failure alike — leaves as
        // Canonical-JSON string segments of its whole envelope (SPEC §H3 item
        // 3). OW-API-004 stays reserved for genuine internal failures, never
        // for size.
        private AgentApiResponse EnforceCap(
            AgentApiResponse response, string? context = null, string? cursor = null) =>
            response.Utf8ByteCount <= MaxResponseBytes
                ? response
                : SegmentEnvelope(response, context, cursor);
    }
}
