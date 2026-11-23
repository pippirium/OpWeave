#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The bounded capture of Editor import errors the SPEC §G4 conditions
    /// read from: Unity reports an import failure only as a Console error, so
    /// a domain-wide <see cref="Application.logMessageReceived"/> subscriber
    /// keeps the error entries logged since the step began (the Operation
    /// calls <see cref="BeginStep"/> before it queues the import) and a wait
    /// condition drains the ones that name its asset paths into its resume
    /// token — where they survive a domain reload until the step's
    /// OW-UNITY-008 attaches them ("ステップ中のインポートエラーを添付").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The buffer is a ring of the last <see cref="Capacity"/> error-kind
    /// entries (Error, Exception and Assert — the kinds an import failure is
    /// logged as). Every entry is written to <see cref="SessionState"/> as it
    /// is captured — the §G4 rule that the old domain's evidence be
    /// persisted before a reload can drop it — and the domain-local mirror
    /// is rebuilt from that slot on each <c>[InitializeOnLoadMethod]</c>. An
    /// error logged after the initial resume token was snapshotted therefore
    /// still reaches the next <see cref="AssetImportedCondition.Check"/> even
    /// when a domain reload came first.
    /// </para>
    /// <para>
    /// The SessionState slot holds the capture of the <i>current</i> step
    /// only: <see cref="BeginStep"/> installs a fresh one before the step's
    /// import is queued and <see cref="EndStep"/> drops it when the step
    /// finishes, so an entry one step logged is never attributed to the
    /// next. A step that never ends — a cancelled Run, an Editor crash —
    /// leaves a bounded slot behind; the next <see cref="BeginStep"/> drops
    /// it and SessionState itself dies with the Editor session.
    /// </para>
    /// <para>
    /// SessionState dying with the session is exactly why the mirror is
    /// only the fast half: a real Editor <b>restart</b> — not just a
    /// domain reload — takes the slot with it, so an error logged after
    /// the last persisted resume token would be lost before the next
    /// check. The restart-durable channel is the step's own SPEC §F6
    /// RunLog: the §F6 Console capture already journals the same
    /// error-kind entry on the running step, and <see cref="RecentErrors"/>
    /// reads that journal back through <see cref="JournaledStepErrors"/>
    /// (bound to the in-flight step by <see cref="BindStepLog"/>) and
    /// merges it with the mirror — the journaled entries are
    /// authoritative, the mirror only adds what a Buffered journal line
    /// has not yet flushed.
    /// </para>
    /// <para>
    /// Buffered is the §F2 table's <i>minimum</i> durability for a
    /// <c>log</c> record, and an abrupt termination between the append
    /// and the next beforeAssemblyReload/quitting flush would take the
    /// record with it — the same hole the mirror cannot close. While a
    /// §G4 import wait owns the step (<see cref="BeginStep"/>…
    /// <see cref="EndStep"/>, or a wait continued from its persisted token
    /// through <see cref="ResumeWait"/>), the §F6 capture therefore tells
    /// <see cref="StepErrorJournaled"/> right after each error-level
    /// record it journals and the bound §F3 item 1 flush makes it durable
    /// at once. Only error-level entries on an open wait window flush
    /// early — ordinary log traffic keeps the Buffered cadence.
    /// </para>
    /// <para>
    /// Nothing here throws: an exception escaping a log callback would reach
    /// Unity's logging pipeline. The buffer is main-thread only, like the
    /// rest of the adapter.
    /// </para>
    /// </remarks>
    internal static class ImportErrorCapture
    {
        /// <summary>The maximum number of recent error entries kept.</summary>
        internal const int Capacity = 64;

        /// <summary>
        /// The <c>OpWeave.*</c> SessionState key of the current step's
        /// capture — a JSON array of error lines. SessionState lives exactly
        /// as long as the Editor session, so it survives the domain reload
        /// that drops <see cref="s_recent"/>.
        /// </summary>
        private const string SessionKey = "OpWeave.ImportErrors";

        /// <summary>
        /// The SessionState key holding the open §G4 wait window — like the
        /// capture itself it is persisted per write so a domain reload keeps
        /// the step's window open; a full restart loses it and the wait's
        /// next <see cref="AssetImportedCondition.Check"/> re-arms it through
        /// <see cref="ResumeWait"/>.
        /// </summary>
        private const string WaitingKey = "OpWeave.ImportErrors.waiting";

        private static List<string> s_recent = new List<string>(Capacity);
        private static bool s_installed;

        // The restart-durable channel: the journal of the Run the current
        // step belongs to and its index in it, bound by the step-context
        // factory on every context build (see BindStepLog). The §F6 RunLog
        // read back through JournaledStepErrors is the one evidence an
        // Editor restart cannot take — SessionState dies with the session.
        // s_stepFlush is the host's own journal flush (the
        // beforeAssemblyReload/quitting hook of SPEC §F3 item 1), bound with
        // the same call, and s_stepWaiting the open BeginStep..EndStep
        // attribution window — together they let StepErrorJournaled make a
        // step-attributed error record durable the moment it is appended.
        private static string? s_stepJournal;
        private static int s_stepIndex = -1;
        private static Action? s_stepFlush;
        private static bool s_stepWaiting;

        /// <summary>
        /// A snapshot of the step's import errors — the §F6 RunLog entries
        /// first (the restart-durable channel, authoritative), then the
        /// SessionState mirror's entries a Buffered journal line has not
        /// yet flushed — deduped and bounded like the capture ring.
        /// <see cref="BeginStep"/> drops whatever session capture predates
        /// it; the journaled half is re-read from the Run's journal on
        /// every call, so an Editor restart between checks loses nothing.
        /// </summary>
        internal static IReadOnlyList<string> RecentErrors
        {
            get
            {
                var merged = s_stepJournal is null
                    ? new List<string>(s_recent.Count)
                    : JournaledStepErrors.Read(s_stepJournal, s_stepIndex);
                for (var i = 0;
                    i < s_recent.Count && merged.Count < Capacity;
                    i++)
                {
                    if (!merged.Contains(s_recent[i]))
                    {
                        merged.Add(s_recent[i]);
                    }
                }
                return merged;
            }
        }

        /// <summary>
        /// The step-start boundary of the §G4 error attribution: the import
        /// about to be queued owns only the errors logged after this call, so
        /// an entry already in the buffer — an earlier failed import of the
        /// same path, say — is never attached to the new step's OW-UNITY-008.
        /// </summary>
        internal static void BeginStep()
        {
            Drop();
            ResumeWait();
        }

        /// <summary>
        /// The step-end boundary of the §G4 error attribution: the waiting
        /// step calls this once its wait concludes, so the capture slot it
        /// leaves behind is empty for whatever runs next.
        /// </summary>
        internal static void EndStep() => Drop();

        /// <summary>
        /// Re-opens the §G4 wait window for a wait continued from its
        /// persisted resume token — the <see cref="AssetImportedCondition"/>
        /// check calls it because the <see cref="BeginStep"/> that armed the
        /// window ran in the domain (or Editor session) the reload/restart
        /// dropped, and errors the resumed wait still logs belong to the
        /// same immediate-flush rule.
        /// </summary>
        internal static void ResumeWait()
        {
            s_stepWaiting = true;
            SessionState.SetBool(WaitingKey, true);
        }

        /// <summary>
        /// Whether the §G4 wait window is open — armed by
        /// <see cref="BeginStep"/>/<see cref="ResumeWait"/> and dropped by
        /// <see cref="EndStep"/>. The §F6 Console capture reads it to
        /// partition the step's per-step budget: only while the window is
        /// open is an error-level entry the wait's OW-UNITY-008 evidence,
        /// so the last <see cref="ConsoleLogCapture.ErrorBudgetReserve"/>
        /// slots are held for it — inside the bound, never past it.
        /// </summary>
        internal static bool WaitWindowOpen => s_stepWaiting;

        /// <summary>
        /// The durability half of the restart-durable channel: the §F6
        /// Console capture calls this right after an error-level entry is
        /// journaled on the bound step, and while a §G4 import wait owns the
        /// step the bound flush makes the Buffered record durable at once —
        /// an Editor termination between the append and the next
        /// beforeAssemblyReload/quitting hook would otherwise take both the
        /// record and the SessionState mirror with it. Only error-level
        /// journaled entries reach this seam, and only an open wait window
        /// arms it, so ordinary log traffic never flushes early.
        /// </summary>
        internal static void StepErrorJournaled()
        {
            var flush = s_stepFlush;
            if (!s_stepWaiting || flush is null)
            {
                return;
            }
            try
            {
                flush();
            }
            catch (Exception)
            {
                // The log path never throws — an exception escaping a log
                // callback would reach Unity's logging pipeline.
            }
        }

        /// <summary>
        /// The wiring half of the restart-durable channel: the step-context
        /// factory names the Run journal and the step index the §F6
        /// RunLog read-back applies to. It is rebound on every context
        /// build, so it always names the in-flight step — a build outside
        /// any Run (<paramref name="journalPath"/> null or empty) leaves
        /// the channel empty rather than reading a stale one. The binding
        /// is deliberately outside the BeginStep/EndStep capture boundary:
        /// the context is built before the step's Execute calls
        /// <see cref="BeginStep"/>, so dropping it there would unbind the
        /// step that was just named.
        /// </summary>
        /// <param name="flushStepLog">
        /// The host's SPEC §F3 item 1 journal flush — the same hook
        /// <c>beforeAssemblyReload</c>/<c>quitting</c> run — invoked early by
        /// <see cref="StepErrorJournaled"/> for an error journaled while a
        /// §G4 import wait owns the step, so the Buffered §F6 record of the
        /// error OW-UNITY-008 reports is durable before an abrupt
        /// termination can take it.
        /// </param>
        internal static void BindStepLog(
            string? journalPath, int stepIndex, Action? flushStepLog)
        {
            var bound = !string.IsNullOrEmpty(journalPath);
            s_stepJournal = bound ? journalPath : null;
            s_stepIndex = stepIndex;
            s_stepFlush = bound ? flushStepLog : null;
        }

        /// <summary>
        /// Drops the captured entries and the bound step log (test seam —
        /// the full reset an Editor restart is).
        /// </summary>
        internal static void Clear()
        {
            Drop();
            s_stepJournal = null;
            s_stepIndex = -1;
            s_stepFlush = null;
        }

        /// <summary>
        /// Records one log entry when it is an error kind — in the mirror and
        /// in SessionState, immediately, so the entry survives a domain
        /// reload that lands before the next wait-condition check.
        /// </summary>
        internal static void OnLogMessage(string condition, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception &&
                type != LogType.Assert)
            {
                return;
            }
            // The token fold's dedupe and bound, applied at capture: an
            // identical line adds nothing twice and the ring keeps the
            // newest entries.
            if (s_recent.Contains(condition))
            {
                return;
            }
            if (s_recent.Count >= Capacity)
            {
                s_recent.RemoveAt(0);
            }
            s_recent.Add(condition);
            WriteStored();
        }

        /// <summary>
        /// The test seam for the reload half of the persistence: a real
        /// domain reload drops the mirror and re-runs
        /// <see cref="Install"/>'s restore; this does the same so a test can
        /// prove an entry captured before the "reload" is still read after
        /// it. The live log subscription is left alone — re-adding it inside
        /// the same domain would double-capture.
        /// </summary>
        internal static void SimulateDomainReload()
        {
            s_recent = new List<string>(Capacity);
            Restore();
        }

        [InitializeOnLoadMethod]
        private static void Install()
        {
            Restore();
            if (s_installed)
            {
                return;
            }
            Application.logMessageReceived += OnLogMessageReceived;
            s_installed = true;
        }

        // The reload restore: whatever the mirror held died with the old
        // domain; the step's persisted entries and its open wait window come
        // back from SessionState.
        private static void Restore()
        {
            s_recent = ReadStored();
            s_stepWaiting = SessionState.GetBool(WaitingKey, false);
        }

        private static void Drop()
        {
            s_recent.Clear();
            s_stepWaiting = false;
            SessionState.EraseString(SessionKey);
            SessionState.EraseBool(WaitingKey);
        }

        private static List<string> ReadStored()
        {
            var stored = new List<string>(Capacity);
            var raw = SessionState.GetString(SessionKey, "");
            if (raw.Length == 0)
            {
                return stored;
            }
            try
            {
                if (JToken.Parse(raw) is JArray entries)
                {
                    for (var i = 0;
                        i < entries.Count && stored.Count < Capacity;
                        i++)
                    {
                        if (entries[i] is JValue { Type: JTokenType.String } line)
                        {
                            stored.Add((string)line.Value!);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A corrupted slot is dead evidence, not a failure — the
                // capture starts from empty.
            }
            return stored;
        }

        private static void WriteStored() =>
            SessionState.SetString(
                SessionKey, new JArray(s_recent).ToString(Formatting.None));

        private static void OnLogMessageReceived(
            string condition, string stackTrace, LogType type) =>
            OnLogMessage(condition, type);
    }
}
