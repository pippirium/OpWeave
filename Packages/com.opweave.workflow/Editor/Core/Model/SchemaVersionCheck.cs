#nullable enable

using System.Text.RegularExpressions;

namespace OpWeave.Core.Model
{
    /// <summary>
    /// The single SPEC §A7 item 1 / §N.3 item 3 <c>schemaVersion</c> read rule,
    /// shared by <see cref="WorkflowSerializer"/> and the D2 structural phase
    /// (SPEC §D2 applies the §A3 read diagnostics, which a programmatically built
    /// <see cref="WorkflowDocument"/> never passed through), so both paths emit
    /// the same OW-WF-002 diagnostic — code, severity, message, JsonPath
    /// <c>$.schemaVersion</c> and hint.
    /// </summary>
    /// <remarks>
    /// A newer major or an unreadable version is an OW-WF-002 error; an older
    /// major needs an IWorkflowMigration and is an OW-WF-002 error here
    /// (validation never migrates, SPEC §A7 item 3); a same-major newer minor
    /// reads with an OW-WF-002 warning. The supported version yields no
    /// diagnostic. The serializer adds the source position to the message; the
    /// document model carries no positions, so the D2 path reports this result
    /// unchanged.
    /// </remarks>
    internal static class SchemaVersionCheck
    {
        /// <summary>The JsonPath every diagnostic of this rule names.</summary>
        internal const string JsonPath = "$.schemaVersion";

        private static readonly Regex s_versionPattern = new Regex(
            @"\A(\d+)\.(\d+)\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Evaluates <paramref name="version"/> against the read rule and returns
        /// the diagnostic to report, or null when the version is the supported
        /// <c>1.0</c>.
        /// </summary>
        internal static Diagnostic? Check(string version)
        {
            var match = s_versionPattern.Match(version);
            int major;
            int minor;
            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, out major) ||
                !int.TryParse(match.Groups[2].Value, out minor))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_WF_002,
                    "Unsupported schemaVersion '" + version + "'.",
                    jsonPath: JsonPath,
                    severity: Severity.Error);
            }
            if (major > 1)
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_WF_002,
                    "Unsupported schemaVersion '" + version +
                    "': this version of OpWeave reads schemaVersion 1.x documents.",
                    jsonPath: JsonPath,
                    severity: Severity.Error);
            }
            if (major < 1)
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_WF_002,
                    "Unsupported schemaVersion '" + version +
                    "': the document requires migration.",
                    jsonPath: JsonPath,
                    severity: Severity.Error);
            }
            if (minor > 0)
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_WF_002,
                    "Reading schemaVersion '" + version +
                    "' with schemaVersion 1.0 semantics.",
                    jsonPath: JsonPath,
                    severity: Severity.Warning,
                    hint: "This version of OpWeave does not support branching or other post-1.0 features.");
            }
            return null;
        }
    }
}
