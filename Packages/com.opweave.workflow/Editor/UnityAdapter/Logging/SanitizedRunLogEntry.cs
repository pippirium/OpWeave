#nullable enable

using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One <em>already sanitized</em> Run log entry of SPEC §F6 — the read-side
    /// contract <see cref="RunLogReader"/> produces and the downstream Agent
    /// API (<c>get_run_logs</c>, T-W3.2-04) serves verbatim: the wire
    /// <c>{ts, level, step?, code?, message, data?}</c> of the journaled
    /// <c>log</c> record, with the credential masking of SPEC §F6 re-applied
    /// on the way out so nothing the store holds can surface a credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Level"/> is the wire string — <c>"info"</c>,
    /// <c>"warning"</c>, <c>"error"</c> and, only while the
    /// <c>debugLogging</c> policy permits (SPEC §F6 SG-08), <c>"debug"</c> —
    /// because the Core <see cref="Severity"/> set deliberately has no member
    /// to carry the debug level and the journaled record is the single truth.
    /// <see cref="Seq"/> is the journal envelope's sequence number — the
    /// stable position an API cursor can page from.
    /// </para>
    /// <para>
    /// Free-text secrets no credential key or URL shape reveals can never be
    /// identified after the fact (SPEC §F6): <see cref="Message"/> and
    /// <see cref="Data"/> are the masked forms the writer journaled plus a
    /// defensive re-mask at this boundary — the API must never reach into the
    /// journal itself.
    /// </para>
    /// </remarks>
    public sealed record SanitizedRunLogEntry(
        long Seq,
        string Ts,
        string Level,
        int? Step,
        string? Code,
        string Message,
        JsonValue? Data);
}
