#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// Folds decoded journal records into the reconstructed Run state of a
    /// <see cref="RunSnapshot"/> (SPEC §F0, §F2). The fold records what the
    /// journal literally says and applies no transition rules: each record
    /// type carries the state change it means (the SPEC §F2 table), the last
    /// record wins, and completion is never inferred from missing records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run-level state mapping: <c>RunAccepted</c> sets Queued or
    /// AwaitingApproval from its recorded approvals (SPEC §E2 item 1);
    /// <c>ApprovalGranted</c> moves AwaitingApproval to Queued once every
    /// recorded approval is granted (SPEC §E1); <c>ApprovalRejected</c> is
    /// Cancelled; <c>RunStarted</c>/<c>RunResumed</c> are Running,
    /// <c>RunSuspended</c> is Suspended, <c>StepNeedsReview</c> is the journal
    /// record that moves the Run to NeedsReview, a <c>ReviewResolved</c>
    /// decision maps per SPEC §E1 (<c>abort</c> → Cancelled,
    /// <c>retry_step</c> → Running), and <c>RunFinished</c> carries the
    /// terminal state in its data.
    /// </para>
    /// <para>
    /// <see cref="Apply"/> returns false when a required record's payload
    /// cannot be interpreted — a step record without a step index, a resource
    /// record without a kind, a release with no matching acquisition, or a
    /// <c>RunFinished</c> without a terminal state — which the loading layer
    /// treats like any other unreadable required record: NeedsReview
    /// (SPEC §F0). Once inconsistent the fold refuses further records.
    /// </para>
    /// </remarks>
    internal sealed class JournalFold
    {
        private const string ApprovalsMember = "approvals";
        private const string DecisionMember = "decision";
        private const string KindMember = "kind";
        private const string DataMember = "data";
        private const string OutputsMember = "outputs";
        private const string ResumeTokenMember = "resumeToken";
        private const string StateMember = "state";
        private const string RetryMember = "retry";
        private const string NextAttemptMember = "nextAttempt";
        private const string NotBeforeMember = "notBefore";
        private const string SnapshotMember = "snapshot";
        private const string DeadlineMember = "deadline";
        private const string WaitingForMember = "waitingFor";
        private const string ErrorMember = "error";
        private const string MessageMember = "message";
        private const string ReasonMember = "reason";

        private const string AbortDecision = "abort";
        private const string RetryStepDecision = "retry_step";

        private readonly Dictionary<int, StepFold> _steps = new Dictionary<int, StepFold>();
        private readonly List<RunResource> _openResources = new List<RunResource>();
        private int _requiredApprovals;
        private int _grantedApprovals;

        private JournalFold()
        {
        }

        private sealed class StepFold
        {
            internal StepState State = StepState.Pending;
            internal int Attempt;
            internal JsonValue? Outputs;
            // The producing session of <see cref="Outputs"/>: the
            // <c>session</c> of the StepSucceeded record that carried them —
            // the provenance SPEC §F4 item 4's handle discard compares
            // against the current Editor session on every rebuild (a later
            // run_resumed changes only the run-level last-record session,
            // never the session an output was minted in).
            internal string? OutputsSession;
            internal JsonValue? ResumeToken;
            // The resume driver's restoration data (SPEC §F4 item 5, §E6/§E7):
            // the StepStarted Preflight snapshot the §F5 Reconcile consults,
            // the journaled Waiting deadline (never reset by a reload), the
            // last reported waitingFor label, the decided retry's not-before
            // instant and a terminal StepFailed's recorded error.
            internal JsonValue? PreflightSnapshot;
            internal string? Deadline;
            internal string? WaitingFor;
            internal string? RetryNotBefore;
            internal string? FailureCode;
            internal string? FailureMessage;
            // The completion position of SPEC §F4 item 4's restart check:
            // the seq of the step's last StepSucceeded record.
            internal long? SucceededSeq;
            // The attempt's last journaled §F7 `changes` array (SPEC §F2
            // attempt-durability): the cumulative list a resumed StepWaiting
            // restores the change recorder from. A record without the optional
            // member folds to null — "no durable interim changes" (SPEC §F2
            // backward compatibility); the next attempt's StepStarted resets it.
            internal JsonValue? Changes;
        }

        /// <summary>A fold with no record applied yet; the first RunAccepted sets the state.</summary>
        internal static JournalFold Empty() => new JournalFold();

        /// <summary>
        /// A fold seeded with the Run-creation state (SPEC §E2 item 1), as
        /// <see cref="FileRunStore"/> knows it at <c>CreateRun</c> before the
        /// RunAccepted line is read back.
        /// </summary>
        internal static JournalFold ForNewRun(int requiredApprovals)
        {
            var fold = new JournalFold { _requiredApprovals = requiredApprovals };
            fold.State = requiredApprovals == 0 ? RunState.Queued : RunState.AwaitingApproval;
            return fold;
        }

        /// <summary>The Run state the folded records end in.</summary>
        internal RunState State { get; private set; } = RunState.Queued;

        /// <summary>A CancelRequested the journal has not yet followed with RunFinished.</summary>
        internal bool CancelPending { get; private set; }

        /// <summary>
        /// The last state-changing step record leaves the step unresolved —
        /// <c>StepStarted</c>, <c>StepWaiting</c> or a <c>StepFailed</c>
        /// carrying a decided retry (SPEC §F3 item 3, §F4 item 5, §E0).
        /// <c>StepProgress</c> never changes it.
        /// </summary>
        internal bool StepUnresolved { get; private set; }

        /// <summary>A required record could not be interpreted; the fold is poisoned.</summary>
        internal bool Inconsistent { get; private set; }

        /// <summary>The <c>ts</c> of the first RunAccepted folded, or null.</summary>
        internal string? CreatedAt { get; private set; }

        /// <summary>The <c>ts</c> of the last folded record, or null.</summary>
        internal string? UpdatedAt { get; private set; }

        /// <summary>The <c>session</c> of the last folded record, or null.</summary>
        internal string? Session { get; private set; }

        /// <summary>
        /// The <c>session</c> of the first folded record — normally the
        /// <c>RunAccepted</c> written with plan.json — the producing session
        /// of the Plan-side values (execution inputs, constants, defaults)
        /// committed with the Run.
        /// </summary>
        internal string? CreationSession { get; private set; }

        /// <summary>
        /// The <c>seq</c> of the last valid journal envelope — a folded
        /// record or a Skipped/refused one that still spends its position
        /// (SPEC §F2); 0 when none. The next append continues at
        /// <see cref="LastSeq"/> + 1.
        /// </summary>
        internal long LastSeq { get; private set; }

        /// <summary>The RunFinished outputs, or null.</summary>
        internal JsonValue? Outputs { get; private set; }

        /// <summary>
        /// The step index of the last state-changing step record folded —
        /// the record the SPEC §F4 item 5 resume branch keys on
        /// (<c>StepProgress</c> and <c>Log</c> never move it); null when the
        /// journal carries no step record.
        /// </summary>
        internal int? LastStepIndex { get; private set; }

        /// <summary>
        /// The step index a journaled <c>review_resolved{retry_step}</c>
        /// re-arms — the NeedsReview step it applied to — or null. The
        /// decision is durable; the re-arm is the resume driver's work
        /// (SPEC §E1, §F4).
        /// </summary>
        internal int? ReviewRetryStep { get; private set; }

        /// <summary>True once the terminal <c>RunFinished</c> record is folded.</summary>
        internal bool Ended { get; private set; }

        /// <summary>The <c>reason</c> of the pending CancelRequested, or null.</summary>
        internal string? CancelReason { get; private set; }

        /// <summary>Resources acquired and not yet released (SPEC §E6 item 4).</summary>
        internal IReadOnlyList<RunResource> OpenResources => _openResources;

        /// <summary>
        /// The "F5 decision required" flag: the recorded state leaves an
        /// incomplete side effect possible (SPEC §F0, §F3 item 3).
        /// </summary>
        internal bool RecoveryDecisionRequired =>
            StepUnresolved || _openResources.Count != 0 || CancelPending;

        /// <summary>
        /// Applies one decoded record in journal order. Returns false — and
        /// marks the fold <see cref="Inconsistent"/> — when a required
        /// record's payload cannot be interpreted; a false return is a
        /// NeedsReview signal for the caller, never a skip.
        /// </summary>
        internal bool Apply(JournalRecord record)
        {
            if (Inconsistent)
            {
                return false;
            }
            var data = (JObject)record.Data.ToToken();
            if (!ApplyCore(record, data))
            {
                Inconsistent = true;
                return false;
            }
            LastSeq = record.Seq;
            UpdatedAt = record.Ts;
            Session = record.Session;
            CreationSession ??= record.Session;
            return true;
        }

        /// <summary>
        /// Records the envelope <c>seq</c> of a line the codec accepted but
        /// the fold does not apply — a Skipped unknown-optional record (or a
        /// refused NeedsReview one) still spends its sequence position
        /// (SPEC §F2), so a following append at <see cref="LastSeq"/> + 1
        /// never reuses a number the journal already wrote.
        /// </summary>
        internal void ObserveSeq(long seq)
        {
            if (seq > LastSeq)
            {
                LastSeq = seq;
            }
        }

        /// <summary>The folded steps as ordered snapshots.</summary>
        internal List<StepSnapshot> Steps()
        {
            var indexes = new List<int>(_steps.Keys);
            indexes.Sort();
            var steps = new List<StepSnapshot>(indexes.Count);
            foreach (var index in indexes)
            {
                var fold = _steps[index];
                steps.Add(new StepSnapshot(
                    index, fold.State, fold.Attempt, fold.Outputs, fold.ResumeToken,
                    fold.PreflightSnapshot, fold.Deadline, fold.WaitingFor,
                    fold.RetryNotBefore, fold.FailureCode, fold.FailureMessage,
                    fold.Changes, fold.SucceededSeq, fold.OutputsSession));
            }
            return steps;
        }

        /// <summary>
        /// The §0.4 snake_case Run state of a stored string — shared by the
        /// <c>RunFinished</c> payload, the state.json cache and index.json.
        /// </summary>
        internal static bool TryParseRunState(string? text, out RunState state)
        {
            foreach (RunState candidate in Enum.GetValues(typeof(RunState)))
            {
                if (string.Equals(
                        text, JsonNaming.ToSnakeCase(candidate.ToString()),
                        StringComparison.Ordinal))
                {
                    state = candidate;
                    return true;
                }
            }
            state = default;
            return false;
        }

        private bool ApplyCore(JournalRecord record, JObject data)
        {
            switch (record.Type)
            {
                case JournalRecordType.RunAccepted:
                    CreatedAt ??= record.Ts;
                    _requiredApprovals =
                        data[ApprovalsMember] is JArray approvals ? approvals.Count : 0;
                    _grantedApprovals = 0;
                    State = _requiredApprovals == 0
                        ? RunState.Queued
                        : RunState.AwaitingApproval;
                    return true;
                case JournalRecordType.ApprovalGranted:
                    _grantedApprovals++;
                    if (State == RunState.AwaitingApproval &&
                        _grantedApprovals >= _requiredApprovals)
                    {
                        State = RunState.Queued;
                    }
                    return true;
                case JournalRecordType.ApprovalRejected:
                    State = RunState.Cancelled;
                    return true;
                case JournalRecordType.RunStarted:
                case JournalRecordType.RunResumed:
                    State = RunState.Running;
                    return true;
                case JournalRecordType.RunSuspended:
                    State = RunState.Suspended;
                    return true;
                case JournalRecordType.StepStarted:
                case JournalRecordType.StepWaiting:
                case JournalRecordType.StepSucceeded:
                case JournalRecordType.StepFailed:
                case JournalRecordType.StepCancelled:
                case JournalRecordType.StepSkipped:
                case JournalRecordType.StepNeedsReview:
                    return ApplyStep(record, data);
                case JournalRecordType.StepProgress:
                case JournalRecordType.Log:
                    return true;
                case JournalRecordType.CancelRequested:
                    CancelPending = true;
                    CancelReason = StringMember(data, ReasonMember);
                    return true;
                case JournalRecordType.ReviewResolved:
                    return ApplyReviewDecision(data);
                case JournalRecordType.ResourceAcquired:
                    return ApplyResource(record, data, acquire: true);
                case JournalRecordType.ResourceReleased:
                    return ApplyResource(record, data, acquire: false);
                case JournalRecordType.RunFinished:
                    return ApplyRunFinished(data);
                default:
                    return false;
            }
        }

        private bool ApplyStep(JournalRecord record, JObject data)
        {
            if (record.Step is not { } index)
            {
                return false;
            }
            LastStepIndex = index;
            var fold = Step(index);
            switch (record.Type)
            {
                case JournalRecordType.StepStarted:
                    fold.State = StepState.Running;
                    fold.Attempt = record.Attempt ?? fold.Attempt + 1;
                    fold.ResumeToken = null;
                    fold.PreflightSnapshot = Token(data[SnapshotMember]);
                    fold.Deadline = null;
                    fold.WaitingFor = null;
                    fold.RetryNotBefore = null;
                    fold.FailureCode = null;
                    fold.FailureMessage = null;
                    fold.Changes = null;
                    StepUnresolved = true;
                    return true;
                case JournalRecordType.StepWaiting:
                    if (!ReadChangesMember(data, out var waitingChanges))
                    {
                        return false;
                    }
                    fold.State = StepState.Waiting;
                    fold.ResumeToken = Token(data[ResumeTokenMember]);
                    fold.Deadline = StringMember(data, DeadlineMember);
                    fold.WaitingFor = StringMember(data, WaitingForMember);
                    fold.RetryNotBefore = null;
                    fold.Changes = waitingChanges;
                    StepUnresolved = true;
                    return true;
                case JournalRecordType.StepSucceeded:
                    if (!ReadChangesMember(data, out var succeededChanges))
                    {
                        return false;
                    }
                    fold.State = StepState.Succeeded;
                    fold.SucceededSeq = record.Seq;
                    fold.Outputs = Token(data[OutputsMember]);
                    fold.OutputsSession = record.Session;
                    fold.ResumeToken = null;
                    fold.PreflightSnapshot = null;
                    fold.Deadline = null;
                    fold.WaitingFor = null;
                    fold.RetryNotBefore = null;
                    fold.Changes = succeededChanges;
                    StepUnresolved = false;
                    return true;
                case JournalRecordType.StepFailed:
                    // SPEC §E0: during automatic retry the step stays
                    // Running with attempt + 1 — the additive "retry" data
                    // member ({nextAttempt, notBefore}) is the recorded
                    // re-execution decision, so the record folds back to
                    // Running instead of terminal Failed. Compatible and
                    // additive: a StepFailed without the member keeps its
                    // older meaning — the decided-not-to-retry failure.
                    if (!ReadChangesMember(data, out var failedChanges))
                    {
                        return false;
                    }
                    if (data[RetryMember] is { } retryToken)
                    {
                        if (retryToken is not JObject retry ||
                            retry[NextAttemptMember] is not JValue nextAttempt ||
                            nextAttempt.Type != JTokenType.Integer ||
                            nextAttempt.Value is not long pending || pending < 1 ||
                            retry[NotBeforeMember] is not JValue notBefore ||
                            notBefore.Type != JTokenType.String ||
                            notBefore.Value is not string notBeforeText)
                        {
                            // A malformed retry decision on a required
                            // record is never guessed: the fold poisons
                            // like any unreadable required payload.
                            return false;
                        }
                        fold.State = StepState.Running;
                        fold.Attempt = (int)pending;
                        fold.ResumeToken = null;
                        fold.RetryNotBefore = notBeforeText;
                        fold.Changes = null;
                        StepUnresolved = true;
                        return true;
                    }
                    fold.State = StepState.Failed;
                    fold.FailureCode = StringMember(data, ErrorMember);
                    fold.FailureMessage = StringMember(data, MessageMember);
                    fold.Changes = failedChanges;
                    StepUnresolved = false;
                    return true;
                case JournalRecordType.StepCancelled:
                    if (!ReadChangesMember(data, out var cancelledChanges))
                    {
                        return false;
                    }
                    fold.State = StepState.Cancelled;
                    fold.Changes = cancelledChanges;
                    StepUnresolved = false;
                    return true;
                case JournalRecordType.StepSkipped:
                    fold.State = StepState.Skipped;
                    StepUnresolved = false;
                    return true;
                case JournalRecordType.StepNeedsReview:
                    fold.State = StepState.NeedsReview;
                    State = RunState.NeedsReview;
                    StepUnresolved = false;
                    return true;
                default:
                    return false;
            }
        }

        private bool ApplyReviewDecision(JObject data)
        {
            if (data[DecisionMember] is not JValue value ||
                value.Type != JTokenType.String || value.Value is not string decision)
            {
                return false;
            }
            if (string.Equals(decision, AbortDecision, StringComparison.Ordinal))
            {
                State = RunState.Cancelled;
                return true;
            }
            if (!string.Equals(decision, RetryStepDecision, StringComparison.Ordinal))
            {
                return false;
            }
            // A retry decision applies to the one step the journal left
            // NeedsReview — a run is sequential, so at most one can be.
            // With none the decision is uninterpretable recorded state
            // (SPEC §F0: NeedsReview, never skipped).
            var review = -1;
            var found = false;
            foreach (var pair in _steps)
            {
                if (pair.Value.State == StepState.NeedsReview)
                {
                    if (found)
                    {
                        return false;
                    }
                    review = pair.Key;
                    found = true;
                }
            }
            if (!found)
            {
                return false;
            }
            ReviewRetryStep = review;
            State = RunState.Running;
            return true;
        }

        // The §F2 pairing rule: one ResourceReleased closes the first open
        // acquisition of the same kind and equal data. An acquisition also
        // keeps the envelope step its record carried — SPEC §F5 grants the
        // step's Completed verdict to "同じステップの Run リソース", so the
        // rebuilt list has to say which step opened each entry. A record
        // without a step is folded as an entry no step owns (§F5 Unknown),
        // never dropped: the pairing rule and RecoveryDecisionRequired both
        // still see the outstanding acquisition.
        private bool ApplyResource(JournalRecord record, JObject data, bool acquire)
        {
            if (data[KindMember] is not JValue kind ||
                kind.Type != JTokenType.String || kind.Value is not string kindText ||
                kindText.Length == 0)
            {
                return false;
            }
            var payload = Token(data[DataMember]);
            if (acquire)
            {
                _openResources.Add(new RunResource(kindText, payload)
                {
                    AcquiringStep = record.Step,
                });
                return true;
            }
            var match = _openResources.FindIndex(resource =>
                string.Equals(resource.Kind, kindText, StringComparison.Ordinal) &&
                SameData(resource.Data, payload));
            if (match < 0)
            {
                return false;
            }
            _openResources.RemoveAt(match);
            return true;
        }

        private bool ApplyRunFinished(JObject data)
        {
            if (data[StateMember] is not JValue value ||
                value.Type != JTokenType.String || value.Value is not string text ||
                !TryParseRunState(text, out var state) || !IsTerminal(state))
            {
                return false;
            }
            State = state;
            Outputs = Token(data[OutputsMember]);
            CancelPending = false;
            StepUnresolved = false;
            Ended = true;
            return true;
        }

        private static bool IsTerminal(RunState state) =>
            state == RunState.Succeeded || state == RunState.Failed ||
            state == RunState.Cancelled;

        private StepFold Step(int index)
        {
            if (!_steps.TryGetValue(index, out var fold))
            {
                fold = new StepFold();
                _steps.Add(index, fold);
            }
            return fold;
        }

        private static JsonValue? Token(JToken? token) =>
            token is null ? null : JsonValue.From(token);

        private const string ChangesMember = "changes";

        // The optional §F7 `changes` array of the 2026-11-08 attempt-durability
        // decision: absent is the backward-compatible "no durable interim
        // changes" and folds to null, a JArray is kept verbatim for the resume
        // restore, and a member of any other type is an uninterpretable payload
        // on a required record — never guessed.
        private static bool ReadChangesMember(JObject data, out JsonValue? changes)
        {
            changes = null;
            if (data[ChangesMember] is not { } token || token.Type == JTokenType.Null)
            {
                return true;
            }
            if (token is not JArray)
            {
                return false;
            }
            changes = JsonValue.From(token);
            return true;
        }

        private static string? StringMember(JObject data, string member) =>
            data[member] is JValue value &&
                value.Type == JTokenType.String && value.Value is string text
                ? text
                : null;

        private static bool SameData(JsonValue? first, JsonValue? second) =>
            first is null ? second is null : first.Equals(second);
    }
}
