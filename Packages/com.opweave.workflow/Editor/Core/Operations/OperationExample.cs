#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// A complete, statically validatable example Workflow using this Operation
    /// (SPEC §C1 item 4): <see cref="WorkflowJson"/> is a full WF document
    /// carrying schemaVersion, a <c>core.start</c> node and the required
    /// inputs/connections — never a node fragment — and
    /// <see cref="FixtureManifest"/> optionally describes the fixtures the
    /// example needs. Returned verbatim by describe_operation (SPEC §C8).
    /// Each check runs in both the backing-field initializer and the
    /// <c>init</c> accessor so record <c>with</c> expressions cannot bypass it.
    /// </summary>
    public sealed record OperationExample(
        string Title,
        JsonValue WorkflowJson,
        JsonValue? FixtureManifest = null)
    {
        private readonly string _title = CheckTitle(Title);
        private readonly JsonValue _workflowJson =
            WorkflowJson ?? throw new ArgumentNullException(nameof(WorkflowJson));

        /// <summary>The example title.</summary>
        public string Title
        {
            get => _title;
            init => _title = CheckTitle(value);
        }

        /// <summary>The complete example workflow document.</summary>
        public JsonValue WorkflowJson
        {
            get => _workflowJson;
            init => _workflowJson =
                value ?? throw new ArgumentNullException(nameof(WorkflowJson));
        }

        private static string CheckTitle(string title) =>
            string.IsNullOrEmpty(title)
                ? throw new ArgumentException(
                    "Example title must not be null or empty.", nameof(Title))
                : title;
    }
}
