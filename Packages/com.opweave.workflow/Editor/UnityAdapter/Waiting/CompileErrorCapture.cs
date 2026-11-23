#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using UnityEditor;
using UnityEditor.Compilation;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §G4 compile-error capture behind
    /// <see cref="ScriptsCompiledCondition"/>: the pipeline reports compile
    /// results only inside <see cref="CompilationPipeline.assemblyCompilationFinished"/>,
    /// and "古いドメインのイベントはリロードで消える", so each error line is
    /// written to SessionState — and to the bound step's journal — in the
    /// callback itself ("その場で SessionState とジャーナルに書く").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The class deliberately mirrors <see cref="ImportErrorCapture"/>: the
    /// live mirror is a bounded SessionState-backed ring restored on each
    /// <c>[InitializeOnLoadMethod]</c>, and the restart-durable half is the
    /// step's §F6 RunLog read back through
    /// <see cref="JournaledStepErrors"/>. A journaled compile error carries
    /// <c>data.source = "unity_compile"</c> — a local, reversible choice;
    /// SPEC §F6 does not enumerate the source tags, and the tag keeps the
    /// pipeline-captured record distinguishable from the same error the §F6
    /// Console channel journals as <c>unity_console</c>.
    /// </para>
    /// <para>
    /// Capture runs only while a §G4 compile wait owns the step — the
    /// Operation arms it when it builds the wait's initial token
    /// (<see cref="ScriptsCompiledCondition.InitialToken"/> runs
    /// <see cref="BeginStep"/>) before requesting compilation, and a
    /// resumed wait re-arms it through
    /// <see cref="ResumeWait"/> (the <c>waiting</c> flag itself rides
    /// SessionState, so a domain reload keeps the window open until a
    /// terminal check or <see cref="ScriptsCompiledCondition.EndWait"/>
    /// runs <see cref="EndStep"/>). An error compiled outside any OpWeave wait —
    /// a hand-saved script in the middle of a step, say — is the §F6
    /// Console channel's evidence, which
    /// <see cref="RecentErrors"/> merges back in from the journal so a
    /// compiler error can only ever reach the diagnostic once.
    /// </para>
    /// <para>
    /// Compilation completion is tracked separately by the
    /// <c>OpWeave.CompileFinishedCount</c> SessionState counter, bumped on
    /// <see cref="CompilationPipeline.compilationFinished"/> and on
    /// <see cref="CompilationPipeline.assemblyCompilationNotRequired"/> —
    /// the §T.4 L-05 no-change path where <c>RequestScriptCompilation</c>
    /// compiles nothing and may leave the domain standing.
    /// </para>
    /// <para>
    /// Nothing here throws: an exception escaping a compilation callback
    /// would reach Unity's logging pipeline. The buffer is main-thread
    /// only, like the rest of the adapter.
    /// </para>
    /// </remarks>
    internal static class CompileErrorCapture
    {
        /// <summary>
        /// The <c>data.source</c> tag journaled compile errors carry — the
        /// pipeline channel's counterpart to
        /// <see cref="ConsoleLogCapture.ConsoleSource"/>.
        /// </summary>
        internal const string CompileSource = "unity_compile";

        /// <summary>The bound error-line buffer a resumed wait reads.</summary>
        internal const int Capacity = 64;

        /// <summary>
        /// SessionState keys — the step's capture slot, the open §G4 wait
        /// window and the session's observed-completion counter. All three
        /// die with the Editor session, like every other §G4 slot.
        /// </summary>
        private const string SessionKey = "OpWeave.CompileErrors";
        private const string WaitingKey = "OpWeave.CompileErrors.waiting";
        internal const string FinishedKey = "OpWeave.CompileFinishedCount";

        /// <summary>
        /// The sources <see cref="RecentErrors"/> reads back from the bound
        /// step's journal: this channel's records, plus the §F6 Console
        /// channel's — a compile error the pipeline emits also reaches the
        /// Console, and only the journaled half survives an Editor restart.
        /// </summary>
        private static readonly IReadOnlyList<string> ReadBackSources =
            new[] { ConsoleLogCapture.ConsoleSource, CompileSource };

        private static List<string> s_recent = new List<string>(Capacity);

        // The restart-durable channel, bound by the step-context factory on
        // every context build (see BindStepLog): the journal path and step
        // index the RunLog read-back applies to, the in-flight step's §F6
        // log sink the journaled records are written through, and the
        // host's own SPEC §F3 item 1 journal flush that makes each captured
        // error durable the moment it is appended — the same discipline
        // ConsoleLogCapture applies through ImportErrorCapture.StepErrorJournaled.
        private static string? s_stepJournal;
        private static int s_stepIndex = -1;
        private static IRunLogSink? s_stepSink;
        private static Action? s_stepFlush;
        private static bool s_stepWaiting;

        /// <summary>
        /// The number of compilation completions the pipeline has observed
        /// this Editor session — either event counts per §T.4 L-05, because
        /// <c>assemblyCompilationNotRequired</c> is the no-change
        /// <c>RequestScriptCompilation</c> path that can finish without a
        /// domain reload.
        /// </summary>
        internal static int FinishedCount =>
            SessionState.GetInt(FinishedKey, 0);

        /// <summary>
        /// A snapshot of the wait's compile errors — the journaled
        /// step-attributed entries first (the restart-durable channel,
        /// authoritative), then the SessionState mirror's entries a
        /// journaled line has not yet covered — deduped and bounded like
        /// the capture ring.
        /// </summary>
        internal static IReadOnlyList<string> RecentErrors
        {
            get
            {
                var merged = s_stepJournal is null
                    ? new List<string>(s_recent.Count)
                    : JournaledStepErrors.Read(
                        s_stepJournal, s_stepIndex, ReadBackSources);
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
        /// The step-start boundary of the §G4 error attribution: the compile
        /// about to be requested owns only the errors reported after this
        /// call, so an entry left by an earlier compile — a hand-saved
        /// script between steps, say — is never attached to this wait's
        /// OW-RUN-009.
        /// </summary>
        internal static void BeginStep()
        {
            Drop();
            ResumeWait();
        }

        /// <summary>
        /// The step-end boundary: the waiting step calls this once its
        /// wait concludes, so the capture slot it leaves behind is empty
        /// for whatever runs next.
        /// </summary>
        internal static void EndStep() => Drop();

        /// <summary>
        /// Re-opens the §G4 compile-wait window for a wait continued from
        /// its persisted resume token — <see cref="ScriptsCompiledCondition"/>'s
        /// check calls it because the <see cref="BeginStep"/> that armed
        /// the window ran in the domain (or Editor session) the reload or
        /// restart dropped, and errors the resumed wait still reports
        /// belong to the same capture.
        /// </summary>
        internal static void ResumeWait()
        {
            s_stepWaiting = true;
            SessionState.SetBool(WaitingKey, true);
        }

        /// <summary>
        /// Whether the §G4 compile-wait window is open — armed by
        /// <see cref="BeginStep"/>/<see cref="ResumeWait"/> and dropped by
        /// <see cref="EndStep"/> (or an Editor restart). The §F6 Console
        /// capture reads it to partition the step's per-step budget the
        /// same way it does for the import wait.
        /// </summary>
        internal static bool WaitWindowOpen => s_stepWaiting;

        /// <summary>
        /// The compile wait's counterpart of
        /// <see cref="ImportErrorCapture.StepErrorJournaled"/>: the §F6
        /// Console capture calls this right after an error-level entry is
        /// journaled on the bound step, and while a §G4 compile wait owns
        /// the step the bound flush makes the Buffered record durable at
        /// once — the same Editor-termination hole the pipeline channel's
        /// own flush covers for <c>unity_compile</c> records.
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
        /// Binds journal evidence to the in-flight step — the context
        /// factory's counterpart to <see cref="ImportErrorCapture.BindStepLog"/>.
        /// Rebound on every context build so it always names the step that
        /// is running; a build outside any Run (<paramref name="journalPath"/>
        /// null or empty, or <paramref name="stepLog"/> null) leaves the
        /// journaled half empty — the mirror still captures, so a
        /// diagnostic never loses the error lines.
        /// </summary>
        /// <param name="stepLog">
        /// The in-flight step's §F6 <see cref="IRunLogSink"/> — the same
        /// sink <c>ctx.Log</c> exposes — through which each captured error
        /// is journaled as a step-attributed <c>log</c> record.
        /// </param>
        /// <param name="flush">
        /// The host's SPEC §F3 item 1 journal flush — the same hook
        /// <c>beforeAssemblyReload</c>/<c>quitting</c> run — invoked right
        /// after each journaled error so an Editor termination between the
        /// append and the next lifecycle flush cannot take the evidence.
        /// </param>
        internal static void BindStepLog(
            string? journalPath, int stepIndex, IRunLogSink? stepLog, Action? flush)
        {
            var bound = !string.IsNullOrEmpty(journalPath);
            s_stepJournal = bound ? journalPath : null;
            s_stepIndex = stepIndex;
            s_stepSink = bound ? stepLog : null;
            s_stepFlush = bound ? flush : null;
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
            s_stepSink = null;
            s_stepFlush = null;
        }

        /// <summary>
        /// The <see cref="CompilationPipeline.assemblyCompilationFinished"/>
        /// handler — invoked once per compiled assembly. While a §G4
        /// compile wait owns the step, each error message is formatted as
        /// the compiler's own <c>file(line,column): message</c> line,
        /// mirrored to SessionState and journaled on the bound step —
        /// immediately, because the domain that received the event may not
        /// outlive the compile.
        /// </summary>
        internal static void OnAssemblyCompilationFinished(
            string assemblyPath, CompilerMessage[] messages)
        {
            if (!s_stepWaiting || messages is null)
            {
                return;
            }
            try
            {
                foreach (var message in messages)
                {
                    if (message.type != CompilerMessageType.Error)
                    {
                        continue;
                    }
                    var line = Format(message);
                    // The token fold's dedupe and bound, applied at
                    // capture: an identical line adds nothing twice and
                    // the ring keeps the newest entries.
                    if (s_recent.Contains(line))
                    {
                        continue;
                    }
                    if (s_recent.Count >= Capacity)
                    {
                        s_recent.RemoveAt(0);
                    }
                    s_recent.Add(line);
                    WriteStored();
                    Journal(line);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError(
                    $"OpWeave compile-error capture failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Shapes a <see cref="CompilerMessage"/> as the compiler's
        /// <c>file(line,column): message</c> line, so the OW-RUN-009
        /// diagnostic the wait surfaces carries the error location the way
        /// the Console reports it. Messages that already carry their
        /// location prefix — or have no resolvable file and line — are
        /// kept as-is.
        /// </summary>
        internal static string Format(in CompilerMessage message)
        {
            var text = message.message ?? string.Empty;
            var file = message.file ?? string.Empty;
            if (file.Length == 0 || message.line <= 0 ||
                text.StartsWith(file, StringComparison.Ordinal))
            {
                return text;
            }
            return $"{file}({message.line},{message.column}): {text}";
        }

        /// <summary>
        /// The test seam for the reload half of the persistence: a real
        /// domain reload drops the mirror and re-runs the install hook's
        /// restore; this does the same so a test can prove an entry
        /// captured before the "reload" is still read after it. The live
        /// event subscriptions are left alone — re-adding them inside the
        /// same domain would double-capture.
        /// </summary>
        internal static void SimulateDomainReload()
        {
            s_recent = new List<string>(Capacity);
            s_stepJournal = null;
            s_stepIndex = -1;
            s_stepSink = null;
            s_stepFlush = null;
            Restore();
        }

        [InitializeOnLoadMethod]
        private static void Install()
        {
            Restore();
            CompilationPipeline.assemblyCompilationFinished +=
                OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationNotRequired +=
                OnAssemblyCompilationNotRequired;
        }

        // The reload restore: whatever the mirror held died with the old
        // domain; the step's persisted entries and its open wait window
        // come back from SessionState.
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

        /// <summary>
        /// Writes one captured error to the bound step's journal — a §F6
        /// <c>log</c> record carrying <c>{source: unity_compile}</c> — then
        /// flushes so an abrupt termination cannot lose it. The log path
        /// never throws.
        /// </summary>
        private static void Journal(string line)
        {
            try
            {
                s_stepSink?.Write(
                    Severity.Error, line, data: JsonValue.From(
                        new JObject { ["source"] = CompileSource }));
                s_stepFlush?.Invoke();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError(
                    $"OpWeave compile-error journal write failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// The §T.4 L-05 completion counter bump — shared by
        /// <see cref="CompilationPipeline.compilationFinished"/> and
        /// <see cref="CompilationPipeline.assemblyCompilationNotRequired"/>,
        /// either of which is observable compilation completion, reload or
        /// no reload.
        /// </summary>
        private static void NoteFinished() =>
            SessionState.SetInt(FinishedKey, FinishedCount + 1);

        private static void OnCompilationFinished(object context) =>
            NoteFinished();

        private static void OnAssemblyCompilationNotRequired(
            string assemblyPath) =>
            NoteFinished();
    }
}
