#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §F6 Unity-Console channel of one runner host: it binds the
    /// domain-wide <see cref="Application.logMessageReceived"/> callback to
    /// the step whose attempt the executing Run last opened, so a Console
    /// Warning or Error raised while a step runs — the Operation's own
    /// <c>Debug.LogWarning</c>, a compiler error of a triggered compile, an
    /// importer warning — lands on that step's structured log instead of
    /// being lost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Attribution window.</b> <c>logMessageReceived</c> carries no Run or
    /// step identity and fires for every Console message in the Editor, so the
    /// tracker follows the same attribution rule as the SPEC §F7
    /// <see cref="ChangeSetTracker"/>: an entry is recorded only while a Run is
    /// executing, and it is attributed to the latest attempt that Run opened —
    /// the step whose side effects produced it. Messages arriving while no Run
    /// executes (the Editor's own Console traffic, a Run waiting for approval
    /// or review) are dropped; nothing outside a Run is ever blamed on one.
    /// Compiler errors and importer warnings are delivered through the same
    /// Console callback — typically while the step that triggered the compile
    /// or import is still the latest attempt — and reach the step through the
    /// same path.
    /// </para>
    /// <para>
    /// <b>Warning 以上 and the cap.</b> Only <see cref="LogType.Warning"/> and
    /// above are recorded (SPEC §F6), mapped to <see cref="Severity.Warning"/>
    /// and <see cref="Severity.Error"/>; <see cref="LogType.Log"/> messages are
    /// dropped. At most <see cref="MaxEntriesPerStep"/> Console entries are
    /// recorded per step — the SPEC §F6 "1ステップ 50 件まで" bound, counted
    /// per (Run, step) so a retried or waiting step shares one budget across
    /// its attempts. The counter lives in <see cref="SessionState"/>, the one
    /// Editor store that outlives a domain reload inside the session, so a
    /// step resumed after a reload cannot record past the bound the earlier
    /// domain already spent; the entries it journaled stay journaled and the
    /// budget is erased at the Run's terminal state. A full Editor restart
    /// clears SessionState — the bound restarts there, the documented
    /// residual of a crash-time resume. While a §G4 import wait owns the
    /// step the bound is partitioned instead of spent first-come: the
    /// last <see cref="ErrorBudgetReserve"/> entries are held for
    /// error-level records — the OW-UNITY-008 evidence the SessionState
    /// mirror alone cannot keep across an abrupt termination — so a
    /// non-error entry stops at
    /// <c>MaxEntriesPerStep - ErrorBudgetReserve</c> and an error-level
    /// entry may use the full bound. Every journaled entry spends exactly
    /// one slot: the bound is never exceeded, the wait's errors only get
    /// a better seat inside it (see
    /// <see cref="ImportErrorCapture.StepErrorJournaled"/>).
    /// </para>
    /// <para>
    /// <b>Provenance.</b> A step whose Operation registered with
    /// <see cref="OperationOrigin.Custom"/> carries the SPEC §F6
    /// <c>operationId</c>/<c>origin</c> members in every entry it produces —
    /// its <c>ctx.Log</c> writes through the <see cref="ProvenanceLogSink"/>
    /// the shared <see cref="UnityStepContext"/> applies, and the captured
    /// Console entries are stamped by <see cref="Report"/>. Standard
    /// Operations need none: their step's journal envelope already names
    /// the Plan step.
    /// </para>
    /// <para>
    /// <b>Dispatch.</b> <c>Application.logMessageReceived</c> is the main-thread
    /// callback (the threaded variant is the other one), so the handler writes
    /// into the step's <see cref="IRunLogSink"/> synchronously — the same
    /// Buffered <c>log</c> journal record an Operation's own
    /// <c>ctx.Log</c> write produces, masked by the same
    /// <see cref="CredentialMask"/> inside the Core sink. The callback list is
    /// static like the §F7 one: every live tracker is asked, and only the one
    /// whose Run is executing accepts. An entry that cannot be journaled, and
    /// a Console message raised while a write is already in flight (a
    /// <see cref="IRunEvents.LogAppended"/> subscriber logging a warning of
    /// its own), are dropped — a log-path fault must never recurse or stop
    /// the Run (SPEC §E8).
    /// </para>
    /// </remarks>
    internal sealed class ConsoleLogCapture : IDisposable
    {
        /// <summary>The SPEC §F6 per-step Console-entry bound.</summary>
        internal const int MaxEntriesPerStep = 50;

        /// <summary>
        /// The tail of the §F6 budget a §G4 import wait holds for
        /// error-level entries: while the wait owns the step, a non-error
        /// entry is recorded only within the first
        /// <c>MaxEntriesPerStep - ErrorBudgetReserve</c> slots, so a
        /// Console flood cannot price the step's import errors — the
        /// OW-UNITY-008 evidence — out of the journal. Error-level
        /// entries may still spend the whole bound: a wait that floods
        /// errors simply reaches it sooner. Ten reserves a fifth of the
        /// bound — enough for the SPEC §G4 error list
        /// (<see cref="AssetImportedCondition.MaxCollectedErrors"/> caps
        /// what a token attaches at eight) while leaving the warnings a
        /// failing import typically emits room to be recorded. Outside an
        /// import wait the reserve applies to nothing: every
        /// Warning-or-higher entry shares the same first-50 rule as
        /// before.
        /// </summary>
        internal const int ErrorBudgetReserve = 10;

        /// <summary>
        /// The <c>data.source</c> marker every captured entry carries, naming
        /// the §F6 出どころ channel that produced it.
        /// </summary>
        internal const string ConsoleSource = "unity_console";

        // Every tracker of the domain: the Console callback is static and
        // carries no host, so it reaches the trackers through this list. Core
        // is main-thread only and so is the main-thread logMessageReceived
        // callback, so the list needs no lock.
        private static readonly List<ConsoleLogCapture> Live =
            new List<ConsoleLogCapture>();
        private static bool s_subscribed;

        // The §F6 budget keys in SessionState: one count per (Run, step) plus
        // the Run's comma-wrapped list of spent steps, kept so the terminal
        // state can erase every key the Run created instead of leaking
        // counters into the session file.
        private const string CountKeyPrefix = "OpWeave.ConsoleLogCapture.count.";
        private const string StepsKeyPrefix = "OpWeave.ConsoleLogCapture.steps.";

        private CaptureTarget? _current;
        private string? _executingRunId;
        private bool _recording;

        /// <summary>
        /// Tracks the Console captures of the Runs this tracker serves —
        /// one executing Run per Editor (SPEC §E2). The Console subscription
        /// exists while at least one tracker lives.
        /// </summary>
        internal ConsoleLogCapture()
        {
            Live.Add(this);
            if (!s_subscribed)
            {
                s_subscribed = true;
                Application.logMessageReceived += OnLogMessageReceived;
            }
        }

        /// <summary>
        /// Stops this tracker from receiving Console reports; the domain
        /// subscription is released once no live tracker remains.
        /// </summary>
        public void Dispose()
        {
            Live.Remove(this);
            _current = null;
            _executingRunId = null;
            if (Live.Count == 0 && s_subscribed)
            {
                s_subscribed = false;
                Application.logMessageReceived -= OnLogMessageReceived;
            }
        }

        /// <summary>
        /// Opens the capture of one step attempt — the context factory calls
        /// it with the run loop's per-step sink, and the returned sink is
        /// <paramref name="stepLog"/> itself: the §F6
        /// <c>operationId</c>/<c>origin</c> stamping for the Operation's own
        /// <c>ctx.Log</c> writes lives in the shared
        /// <see cref="UnityStepContext"/> path (the
        /// <see cref="ProvenanceLogSink"/> it applies), while the captured
        /// Console entries carry the same members through
        /// <see cref="CaptureTarget"/>. Without a known active Run the loop's
        /// own sink is returned unchanged and no capture is opened, because a
        /// step log may not invent an owner.
        /// </summary>
        /// <param name="runId">The Run the attempt belongs to, or null when none is active.</param>
        /// <param name="stepLog">The run loop's log sink for this step.</param>
        /// <param name="stepIndex">The Plan step index.</param>
        /// <param name="operationId">The Plan step's Operation id.</param>
        /// <param name="origin">
        /// The registered Operation's §C3 origin, or null when the current
        /// Registry snapshot cannot resolve it.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="stepLog"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="operationId"/> is null or empty.</exception>
        internal IRunLogSink OpenAttempt(
            string? runId, IRunLogSink stepLog, int stepIndex,
            string operationId, OperationOrigin? origin)
        {
            if (stepLog is null)
            {
                throw new ArgumentNullException(nameof(stepLog));
            }
            if (string.IsNullOrEmpty(operationId))
            {
                throw new ArgumentException(
                    "A step log capture must carry its Operation id.", nameof(operationId));
            }
            if (string.IsNullOrEmpty(runId))
            {
                _current = null;
                return stepLog;
            }
            var custom = origin == OperationOrigin.Custom;
            _current = new CaptureTarget(
                runId, stepLog, stepIndex, custom ? operationId : null);
            _executingRunId = runId;
            return stepLog;
        }

        /// <summary>
        /// Follows the Run's execution for the attribution window alone:
        /// <c>running</c> opens it and any other state ends it — a Run that is
        /// not executing steps owns no Console entry.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
        internal void OnRunStateChanged(RunStateChanged change)
        {
            if (change is null)
            {
                throw new ArgumentNullException(nameof(change));
            }
            if (change.State == RunState.Running)
            {
                _executingRunId = change.RunId;
                if (_current is not null &&
                    !string.Equals(_current.RunId, change.RunId, StringComparison.Ordinal))
                {
                    _current = null;
                }
                return;
            }
            _executingRunId = null;
            if (_current is not null &&
                string.Equals(_current.RunId, change.RunId, StringComparison.Ordinal))
            {
                _current = null;
            }
            // The Run's budget ends with it: Succeeded, Failed and Cancelled
            // are the terminal states after which no step of the Run can
            // produce a Console entry — erase the persisted counters so the
            // session file keeps no residue. Suspended and NeedsReview keep
            // theirs: a resumed or review-continued step owns the same §F6
            // budget it started.
            if (change.State is RunState.Succeeded or RunState.Failed or RunState.Cancelled)
            {
                EraseRun(change.RunId);
            }
        }

        // The domain entry point of SPEC §F6: Application.logMessageReceived
        // (the main-thread callback — the threaded variant is a separate event)
        // routes every report to every live tracker; each one that owns a
        // Running Run attributes it and the others ignore it. Never throws: an
        // exception escaping here would reach the Editor's Console pipeline.
        private static void OnLogMessageReceived(
            string condition, string stackTrace, LogType type)
        {
            for (var i = 0; i < Live.Count; i++)
            {
                try
                {
                    Live[i].Report(condition, stackTrace, type);
                }
                catch (Exception)
                {
                    // A failed attribution must not break the Console pipeline:
                    // the message still reaches the Editor's own Console, and
                    // SPEC §E8 keeps a reporting fault from stopping the Run.
                }
            }
        }

        private void Report(string condition, string stackTrace, LogType type)
        {
            var executing = _executingRunId;
            var target = _current;
            if (executing is null || target is null ||
                !string.Equals(target.RunId, executing, StringComparison.Ordinal))
            {
                return;
            }
            Severity severity;
            string logTypeName;
            switch (type)
            {
                case LogType.Warning:
                    severity = Severity.Warning;
                    logTypeName = "warning";
                    break;
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    severity = Severity.Error;
                    logTypeName = JsonNaming.ToSnakeCase(type.ToString());
                    break;
                default:
                    // LogType.Log is below the SPEC §F6 Warning 以上 channel.
                    return;
            }
            // SPEC §F6: at most MaxEntriesPerStep Console entries per step,
            // counted per (Run, step) so a retried or waiting step shares one
            // budget across its attempts. The count is kept in SessionState —
            // it outlives the domain — so a step resumed after a domain
            // reload cannot record past the bound the earlier domain already
            // spent.
            var countKey = CountKey(target.RunId, target.StepIndex);
            var count = SessionState.GetInt(countKey, 0);
            // While a §G4 wait owns the step — import or compile — the
            // budget is partitioned: an error-level entry — the
            // OW-UNITY-008 / OW-RUN-009 evidence the SessionState mirror
            // alone cannot keep across an abrupt termination — may use
            // the full bound, while a non-error entry stops at the
            // unreserved share so the wait's errors keep their seats.
            // Outside the wait every entry shares the same first-50 rule.
            // Either way every journaled entry spends exactly one slot:
            // the §F6 bound is never exceeded.
            var waiting = ImportErrorCapture.WaitWindowOpen ||
                CompileErrorCapture.WaitWindowOpen;
            var limit = severity == Severity.Error || !waiting
                ? MaxEntriesPerStep
                : MaxEntriesPerStep - ErrorBudgetReserve;
            if (_recording || count >= limit)
            {
                return;
            }
            SessionState.SetInt(countKey, count + 1);
            NoteStep(target.RunId, target.StepIndex);
            var data = new JObject
            {
                ["source"] = ConsoleSource,
                ["logType"] = logTypeName,
            };
            if (!string.IsNullOrEmpty(stackTrace))
            {
                data["stackTrace"] = stackTrace;
            }
            if (target.OperationId is { } operationId)
            {
                data["operationId"] = operationId;
                data["origin"] = ProvenanceLogSink.CustomOrigin;
            }
            _recording = true;
            try
            {
                target.Sink.Write(
                    severity, condition ?? string.Empty, data: JsonValue.From(data));
                // An error entry is the §G4 wait's OW-UNITY-008 /
                // OW-RUN-009 evidence: while an import or compile wait
                // owns the step, the waiting capture's bound flush makes
                // this Buffered record durable now — an Editor
                // termination before the next lifecycle hook takes both
                // the record and the SessionState mirror otherwise.
                // Inside the _recording window so a failure logged by the
                // flush itself is dropped here instead of recursing.
                if (severity == Severity.Error)
                {
                    ImportErrorCapture.StepErrorJournaled();
                    CompileErrorCapture.StepErrorJournaled();
                }
            }
            catch (Exception)
            {
                // A write that could not be journaled is dropped — the §E8
                // rule: a fault on the log path must never stop the Run.
            }
            finally
            {
                _recording = false;
            }
        }

        // The SessionState key of one (Run, step) entry budget.
        private static string CountKey(string runId, int stepIndex) =>
            CountKeyPrefix + runId + "." + stepIndex.ToString(
                System.Globalization.CultureInfo.InvariantCulture);

        // Records the step on its Run's comma-wrapped index list — the
        // leading comma makes ",3," unfindable inside ",13,", so membership
        // is an exact-index test for the terminal-state erase.
        private static void NoteStep(string runId, int stepIndex)
        {
            var key = StepsKeyPrefix + runId;
            var list = SessionState.GetString(key, ",");
            var stamp = stepIndex.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (!list.Contains("," + stamp + ",", StringComparison.Ordinal))
            {
                SessionState.SetString(key, list + stamp + ",");
            }
        }

        // Erases the Run's persisted budgets at its terminal state: every
        // step counter on the recorded list, then the list itself.
        private static void EraseRun(string runId)
        {
            var key = StepsKeyPrefix + runId;
            var list = SessionState.GetString(key, ",");
            var stamps = list.Split(',');
            foreach (var stamp in stamps)
            {
                if (stamp.Length != 0)
                {
                    SessionState.EraseInt(CountKeyPrefix + runId + "." + stamp);
                }
            }
            SessionState.EraseString(key);
        }

        // The one open attribution slot: the executing Run's latest step
        // attempt — the sink its Console entries journal through, its Plan
        // step index for the §F6 cap, and the custom Operation's provenance
        // id (null for a standard or unresolved Operation).
        private sealed class CaptureTarget
        {
            internal CaptureTarget(
                string runId, IRunLogSink sink, int stepIndex, string? operationId)
            {
                RunId = runId;
                Sink = sink;
                StepIndex = stepIndex;
                OperationId = operationId;
            }

            internal string RunId { get; }
            internal IRunLogSink Sink { get; }
            internal int StepIndex { get; }
            internal string? OperationId { get; }
        }
    }
}
