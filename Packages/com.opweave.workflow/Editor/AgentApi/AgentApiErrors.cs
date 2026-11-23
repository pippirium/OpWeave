#nullable enable

using OpWeave.Core;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The fixed-English diagnostics of the API misuse area (SPEC §J, §H1–§H3):
    /// OW-API-001 malformed request shape, OW-API-002 invalid cursor and
    /// OW-API-004 internal error. Centralized so every command emits identical
    /// wording (SPEC §0.4: agent-facing messages are fixed English text).
    /// </summary>
    internal static class AgentApiErrors
    {
        /// <summary>OW-API-001 — the request's shape is wrong (missing or
        /// conflicting arguments, wrong JSON type for an argument).</summary>
        internal static Diagnostic MalformedRequest(
            string message, string? jsonPath = null, string? hint = null) =>
            ErrorCodes.Create(ErrorCodes.OW_API_001, message, jsonPath: jsonPath, hint: hint);

        /// <summary>OW-API-002 — the cursor does not decode, fails its integrity
        /// tag, or does not belong to this request (SPEC §H3 item 3).</summary>
        internal static Diagnostic InvalidCursor() =>
            ErrorCodes.Create(
                ErrorCodes.OW_API_002, "The cursor is not valid for this request.");

        /// <summary>OW-API-004 — an internal failure; the message stays generic
        /// because the detail goes to the Editor log, not the Agent.</summary>
        internal static Diagnostic InternalError(string message) =>
            ErrorCodes.Create(ErrorCodes.OW_API_004, message);
    }
}
