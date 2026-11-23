#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// A diagnostic that crosses layer boundaries inside <see cref="Result{T}"/> instead of an
    /// exception (SPEC §0.4). <see cref="Message"/> is fixed English text for Agents; display
    /// localization belongs to the GUI.
    /// </summary>
    /// <remarks>
    /// Product code must obtain diagnostics through <see cref="ErrorCodes.Create"/>, the
    /// single emission path that rejects reserved numbers and documentation-only aliases
    /// (SPEC §J). Constructing a <see cref="Diagnostic"/> directly with a reserved or alias
    /// code is a programming error caught by review and structure tests, not a runtime
    /// contract; this record intentionally performs no validation.
    /// </remarks>
    public sealed record Diagnostic(
        string Code,             // e.g. "OW-VAL-005" (SPEC §J)
        Severity Severity,
        string Message,
        string? NodeId = null,
        string? Port = null,
        string? JsonPath = null, // e.g. "$.nodes[3].parameters.name"
        string? Hint = null,
        bool Retryable = false);
}
