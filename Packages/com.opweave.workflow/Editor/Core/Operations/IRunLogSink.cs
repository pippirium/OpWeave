#nullable enable

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The step-bound structured log sink behind <see cref="StepContext.Log"/>
    /// (SPEC §0.6 SG-06, §F6). Each <see cref="Write"/> produces one
    /// <see cref="RunLogEntry"/> bound to the running step; custom Operation
    /// entries carry the operation id and origin (SPEC §F6).
    /// </summary>
    /// <remarks>
    /// The contract is that Operations never write secrets through this sink:
    /// the pipeline masks known credential keys, but free-text secrets cannot
    /// be prevented (SPEC §F6).
    /// </remarks>
    public interface IRunLogSink
    {
        /// <summary>
        /// Writes one <see cref="RunLogEntry"/> with the fixed English
        /// <paramref name="message"/>, an optional SPEC §J diagnostic
        /// <paramref name="code"/> and optional structured
        /// <paramref name="data"/> (SPEC §F6).
        /// </summary>
        void Write(
            Severity level, string message, string? code = null, JsonValue? data = null);
    }
}
