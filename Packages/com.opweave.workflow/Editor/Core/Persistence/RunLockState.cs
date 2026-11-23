#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The persisted lock-owner record of SPEC §E2 item 3: the content of
    /// <c>Library/OpWeave/lock.json</c> while a Run holds the coordinator.lock
    /// inter-process exclusion —
    /// <c>{"runId": "run_...", "editorSessionId": "...", "acquiredAt": "..."}</c>.
    /// </summary>
    /// <remarks>
    /// lock.json records state only; it is never the exclusion itself, and a
    /// matching <see cref="EditorSessionId"/> is never proof of holding the
    /// lock (AGENTS invariant, SPEC §E2). <see cref="EditorSessionId"/> is the
    /// same Editor session id as the journal <c>session</c> of SPEC §F2 and
    /// the restart detection of SPEC §F4 item 4.
    /// </remarks>
    public sealed record RunLockState(
        string RunId,
        string EditorSessionId,
        string AcquiredAt)
    {
        /// <summary>A recorded state stamped with the current UTC acquisition time.</summary>
        public static RunLockState AcquiredNow(string runId, string editorSessionId) =>
            new RunLockState(runId, editorSessionId, UtcTimestamp.Now());
    }
}
