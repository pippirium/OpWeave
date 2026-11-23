#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// Diagnostic severity (SPEC §0.4). Serialized as snake_case in JSON.
    /// </summary>
    public enum Severity
    {
        Error,
        Warning,
        Info
    }
}
