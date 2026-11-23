#nullable enable

namespace OpWeave.Viewer
{
    /// <summary>
    /// One row of the SPEC §H5 Runs-tab log list — a journaled <c>log</c>
    /// record (SPEC §F6) as the window shows it: the envelope's timestamp
    /// and step index plus the payload's level, code and message. The level
    /// stays the raw wire word so a <c>debug</c> entry — which the §0.6
    /// Severity set deliberately cannot carry — still shows when
    /// <c>debugLogging</c> wrote it.
    /// </summary>
    internal sealed class RunViewerLogRow
    {
        internal RunViewerLogRow(
            string ts, int? step, string level, string? code, string message)
        {
            Ts = ts;
            Step = step;
            Level = level;
            Code = code;
            Message = message;
        }

        /// <summary>The envelope's UTC timestamp (SPEC §F2).</summary>
        internal string Ts { get; }

        /// <summary>The step the entry belongs to, or null for a Run-level entry.</summary>
        internal int? Step { get; }

        /// <summary>The payload's level word (info/warning/error/debug).</summary>
        internal string Level { get; }

        /// <summary>The payload's error code, or null when the entry carries none.</summary>
        internal string? Code { get; }

        /// <summary>The fixed English message (SPEC §F6).</summary>
        internal string Message { get; }
    }
}
