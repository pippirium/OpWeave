#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;

namespace OpWeave.Operations
{
    /// <summary>
    /// The warning half of <c>unity.script.compile</c>'s <c>result</c>
    /// payload (SPEC §I.4): the
    /// <see cref="CompilationPipeline.assemblyCompilationFinished"/>
    /// callback delivers every <see cref="CompilerMessage"/> — errors and
    /// warnings alike — and the wait's step keeps the warning lines so the
    /// terminal <c>result.warnings</c> can name them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror is the exact counterpart of
    /// <see cref="CompileErrorCapture"/>'s SessionState half: a bounded
    /// ring of formatted <c>file(line,column): message</c> lines persisted
    /// per write so a domain reload mid-wait loses nothing, plus the §G4
    /// wait window that says a compile wait owns this step — only then do
    /// the pipeline's warnings belong to it. Unlike errors, warnings need
    /// no journal write of their own: every compile warning reaches the
    /// Console too, and the §F6 Console channel already journals
    /// Warning-and-above entries on the running step.
    /// </para>
    /// <para>
    /// The window rides the same lifecycle the error capture's does —
    /// <see cref="CompileScriptsOperation"/> arms it at Execute and drops
    /// it on the terminal verdict or Cancel, and a wait resumed from its
    /// persisted token re-arms it on the first check in the new domain.
    /// </para>
    /// </remarks>
    internal static class CompileWarningCapture
    {
        /// <summary>The bound on the warning lines one wait keeps.</summary>
        internal const int Capacity = 32;

        private const string SessionKey = "OpWeave.CompileWarnings";
        private const string WaitingKey = "OpWeave.CompileWarnings.waiting";

        private static List<string> s_recent = new List<string>(Capacity);
        private static bool s_waiting;

        /// <summary>
        /// A snapshot of the wait's captured compile warnings — the
        /// <c>file(line,column): message</c> lines of the compile this step
        /// requested, oldest first.
        /// </summary>
        internal static IReadOnlyList<string> RecentWarnings =>
            new List<string>(s_recent);

        /// <summary>
        /// Whether the compile-wait window is open — armed by
        /// <see cref="BeginStep"/>/<see cref="ResumeWait"/> and dropped by
        /// <see cref="EndStep"/> (or an Editor restart's restore).
        /// </summary>
        internal static bool WaitWindowOpen => s_waiting;

        /// <summary>
        /// The step-start boundary of the warning attribution: the compile
        /// about to be requested owns only the warnings reported after
        /// this call, never an entry an earlier compile left behind.
        /// </summary>
        internal static void BeginStep()
        {
            Drop();
            ResumeWait();
        }

        /// <summary>
        /// Re-opens the compile-wait window for a wait continued from its
        /// persisted resume token — the <see cref="BeginStep"/> that armed
        /// it ran in the domain the reload dropped.
        /// </summary>
        internal static void ResumeWait()
        {
            s_waiting = true;
            SessionState.SetBool(WaitingKey, true);
        }

        /// <summary>
        /// The step-end boundary: the waiting step calls this once its
        /// wait concludes, so the capture slot it leaves behind is empty
        /// for whatever runs next.
        /// </summary>
        internal static void EndStep() => Drop();

        /// <summary>
        /// The <see cref="CompilationPipeline.assemblyCompilationFinished"/>
        /// handler — invoked once per compiled assembly. While the §G4
        /// compile wait owns the step, each warning message is formatted
        /// the way <see cref="CompileErrorCapture"/> formats its lines and
        /// mirrored to SessionState so a domain reload mid-wait loses
        /// none.
        /// </summary>
        internal static void OnAssemblyCompilationFinished(
            string assemblyPath, CompilerMessage[] messages)
        {
            if (!s_waiting || messages is null)
            {
                return;
            }
            try
            {
                foreach (var message in messages)
                {
                    if (message.type != CompilerMessageType.Warning)
                    {
                        continue;
                    }
                    var line = CompileErrorCapture.Format(message);
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
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError(
                    $"OpWeave compile-warning capture failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Drops the captured entries — the full reset an Editor restart is
        /// (test seam).
        /// </summary>
        internal static void Clear() => Drop();

        /// <summary>
        /// The test seam for the reload half of the persistence: a real
        /// domain reload drops the mirror and re-runs the install hook's
        /// restore; this does the same so a test can prove an entry
        /// captured before the "reload" is still read after it. The live
        /// event subscription is left alone.
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
            CompilationPipeline.assemblyCompilationFinished +=
                OnAssemblyCompilationFinished;
        }

        private static void Restore()
        {
            s_recent = ReadStored();
            s_waiting = SessionState.GetBool(WaitingKey, false);
        }

        private static void Drop()
        {
            s_recent.Clear();
            s_waiting = false;
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
    }
}
