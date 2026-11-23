#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Recovery
{
    /// <summary>
    /// What the SPEC §F4 recovery engine did with one unfinished Run. The engine
    /// never invents an outcome the journal does not support: every case it
    /// cannot verify is reported <see cref="NeedsReview"/> (the human decision
    /// path of SPEC §E1/§H4) and is never an automatic success or a silent
    /// re-execution (SPEC §F0).
    /// </summary>
    internal enum RunRecoveryOutcome
    {
        /// <summary>
        /// The §F4 driver rebuilt the Run from its journal and the Run
        /// continues (<see cref="RunState.Running"/>) or the end its journal had
        /// already decided completed. The exclusion was taken over with the
        /// journal verification of SPEC §E2 item 3 and
        /// <c>lock.json</c> re-recorded for this Editor session (§F4 item 6).
        /// </summary>
        Resumed,

        /// <summary>
        /// Re-attached under the taken-over exclusion, but the SPEC §E3 item 4
        /// Editor-state gate still holds it <see cref="RunState.Suspended"/>
        /// (Play Mode, or a compile/import OpWeave did not start — SPEC §1.4,
        /// §G8 item 5); a later reconciliation resumes it.
        /// </summary>
        Suspended,

        /// <summary>
        /// Re-attached without being driven: a <see cref="RunState.Queued"/> or
        /// <see cref="RunState.AwaitingApproval"/> Run holds no exclusion, so
        /// its own gate (SPEC §E2 item 4) or the human approval decision
        /// applies from now on; or a journaled <see cref="RunState.NeedsReview"/>
        /// Run adopted for the human decision path (SPEC §E1).
        /// </summary>
        Reattached,

        /// <summary>
        /// Not verifiable — no exclusion could be taken, the recorded lock owner
        /// does not reconcile with the journal and <c>index.json</c>, or the
        /// takeover of another session's Run could not be proven safe. Nothing
        /// was resumed, no record was written, no step re-executed (SPEC §E2
        /// item 3, §F4).
        /// </summary>
        NeedsReview,
    }

    /// <summary>
    /// One Run's recovery verdict: the outcome plus the state the engine leaves
    /// the Run in — null when the Run could not be reconstructed at all
    /// (<c>lock.json</c> names a Run whose journal does not exist).
    /// </summary>
    internal sealed record RunRecoveryResult(
        string RunId,
        RunRecoveryOutcome Outcome,
        RunState? State);

    /// <summary>
    /// The outcome of one recovery pass (SPEC §F4 item 1): whether the
    /// <c>coordinator.lock</c> exclusion was obtained at all, one
    /// <see cref="RunRecoveryResult"/> per unfinished Run the pass found, and the
    /// diagnostics of the decisions taken. The Runner host logs it; the
    /// diagnostics are the SPEC §J codes only (OW-RUN-002, OW-STORE-002).
    /// </summary>
    internal sealed class RunRecoveryReport
    {
        internal RunRecoveryReport(
            bool exclusionHeld,
            IReadOnlyList<RunRecoveryResult> runs,
            IReadOnlyList<Diagnostic> diagnostics)
        {
            ExclusionHeld = exclusionHeld;
            Runs = ReadOnlyCollections.CopyList(runs);
            Diagnostics = ReadOnlyCollections.CopyList(diagnostics);
        }

        /// <summary>
        /// True when the pass ran inside the <c>coordinator.lock</c> exclusion.
        /// False means nothing was resumed (SPEC §F4 item 1).
        /// </summary>
        public bool ExclusionHeld { get; }

        /// <summary>Per-Run verdicts, in the order the pass decided them.</summary>
        public IReadOnlyList<RunRecoveryResult> Runs { get; }

        /// <summary>The diagnostics the pass reported.</summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; }
    }
}
