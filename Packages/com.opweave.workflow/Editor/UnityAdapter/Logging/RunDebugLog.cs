#nullable enable

using System;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Unity-side half of the SPEC §F6 SG-08 debug channel (T-W2.4-04):
    /// one write that lands the debug-level Run log entry in the Run Store
    /// <em>and</em> mirrors it to the Unity Console — only while the
    /// <c>debugLogging</c> setting (SPEC §H4.1, default
    /// <see langword="false"/>) permits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a Unity-side channel.</b> The debug level is Runner-internal:
    /// the §0.6 <see cref="Severity"/> set deliberately exposes no member for
    /// it, so an Operation can never write one through
    /// <see cref="IRunLogSink.Write"/>, and a journaled debug record raises
    /// no <see cref="IRunEvents.LogAppended"/> — there is nothing the Console
    /// mirror could subscribe to. Internal debug diagnostics therefore come
    /// through this one channel: the journal append goes through the step's
    /// own §F6 sink (<see cref="StepLogSink.WriteDebug"/>, masked and
    /// re-gated by the coordinator's injected policy), and the same gated
    /// decision emits the masked line to <see cref="Debug.Log"/> — the
    /// §F6 "Run Store・Console" pair for the debug level.
    /// </para>
    /// <para>
    /// <b>The gate.</b> A missing or <see langword="false"/>
    /// <paramref name="debugLogging"/> drops the write before either
    /// destination — no journal line, no Console text. The setting changes
    /// the recorded level only; it never touches permissions, approvals or
    /// execution decisions (SPEC §F6). <see cref="Debug.Log"/> is
    /// <see cref="LogType.Log"/>, below the Warning-以上 channel
    /// <see cref="ConsoleLogCapture"/> records, so the mirror can never
    /// re-enter the step's log.
    /// </para>
    /// <para>
    /// <b>Secrets.</b> The journaled half is masked inside
    /// <see cref="StepLogSink"/>; the Console line is masked here through
    /// <see cref="CredentialMask"/> — Git URL userinfo and
    /// credential-keyed values collapse to <c>"***"</c> on both paths. A
    /// free-text secret no key or URL shape reveals stays the producer's own
    /// contract (SPEC §F6). Plan inputs and code bodies are never logged
    /// automatically — the caller chooses the diagnostic text.
    /// </para>
    /// </remarks>
    internal static class RunDebugLog
    {
        /// <summary>
        /// Writes one debug-level Run log entry bound to the step
        /// <paramref name="sink"/> serves — journaled through the §F6 sink
        /// and mirrored to the Unity Console — when the
        /// <paramref name="debugLogging"/> policy permits; dropped entirely
        /// otherwise.
        /// </summary>
        /// <param name="sink">
        /// The step's §F6 <see cref="IRunLogSink"/> — the journaled
        /// <see cref="StepLogSink"/> the run loop built, or the
        /// <see cref="ProvenanceLogSink"/> a custom Operation's context wraps
        /// it in. Any other sink drops the journaled half like a failed
        /// append (SPEC §E8 — a log-path fault never stops the Run) and only
        /// the Console mirror is emitted.
        /// </param>
        /// <param name="debugLogging">
        /// The lazily read SPEC §H4.1 <c>debugLogging</c> setting — the same
        /// predicate the coordinator's write-path gate checks.
        /// </param>
        /// <param name="message">The fixed English diagnostic text (SPEC §F6).</param>
        /// <param name="code">An optional SPEC §J diagnostic code.</param>
        /// <param name="data">Optional structured data — masked like the message.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="sink"/>, <paramref name="debugLogging"/> or
        /// <paramref name="message"/> is null.
        /// </exception>
        internal static void Write(
            IRunLogSink sink,
            Func<bool> debugLogging,
            string message,
            string? code = null,
            JsonValue? data = null)
        {
            if (sink is null)
            {
                throw new ArgumentNullException(nameof(sink));
            }
            if (debugLogging is null)
            {
                throw new ArgumentNullException(nameof(debugLogging));
            }
            if (message is null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            // SPEC §F6 SG-08: missing/false suppresses both destinations —
            // nothing is persisted and nothing reaches the Console.
            if (!debugLogging())
            {
                return;
            }
            switch (sink)
            {
                case StepLogSink step:
                    step.WriteDebug(message, code, data);
                    break;
                case ProvenanceLogSink provenance:
                    provenance.WriteDebug(message, code, data);
                    break;
            }
            // The Console mirror carries the same masked text the journal
            // holds — a credential in the message or code collapses to "***"
            // here exactly as it does inside StepLogSink.
            var text = "OpWeave debug: " + CredentialMask.MaskText(message);
            if (code is not null)
            {
                text += " (" + CredentialMask.MaskText(code) + ")";
            }
            Debug.Log(text);
        }
    }
}
