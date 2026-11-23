#nullable enable
// T-W3.3-02: the OpWeave.Pipeline adapter of SPEC §H2 — the transport binding
// that registers the 14 Agent commands of SPEC §H1 as com.unity.pipeline
// commands under the "opweave" group. Every Pipeline-specific type stays in
// this assembly (SPEC §H2 item 7); the transport-independent service the
// commands call is OpWeave.AgentApi's AgentApiHost.Api.
//
// Registration shape (SPEC §H2 item 2, verified by L-01): explicit
// [CliCommand(group, name, description)] declarations — command names never
// rely on method-name inference — [CliArg] on every parameter, JObject for
// workflow/inputs JSON, and IStructuredCommandInput DTOs for the other
// compound inputs (search_compatible_operations' match object, SPEC §C7).
// get_run_status/get_run_logs/list_runs run off the main thread
// (MainThreadRequired = false, SPEC §H2 item 3) and touch only the
// thread-safe status snapshot plus read-only Run Store reads the service
// performs — this adapter issues no Unity API call on those paths. Every
// other command stays on the main thread.
//
// The wire result is the AgentApiResponse envelope verbatim
// (PipelineDispatch.Invoke): business failures stay inside the envelope as
// ok:false while OW-API-* request-shape/misuse diagnostics are lifted into
// Pipeline's own error channel (SPEC §H2 item 5).

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.AgentApi;
using Unity.Pipeline.Commands;

namespace OpWeave.Pipeline
{
    /// <summary>
    /// The 14 SPEC §H1 Agent commands bound to <c>unity command opweave
    /// &lt;name&gt;</c>. Each method maps its <see cref="CliArgAttribute"/>
    /// arguments onto the matching <c>AgentApi</c> request record and hands
    /// the envelope back through <see cref="PipelineDispatch"/>; nothing here
    /// interprets payloads — the service owns every business rule.
    /// </summary>
    public static class OpWeaveCommands
    {
        private const string Group = "opweave";

        // SPEC §H2 item 4: the adapter cannot answer while the Editor domain
        // reloads (the whole Pipeline server goes down with it, L-01 item 9),
        // so each command's description carries the retry guidance.
        private const string RetryHint =
            " If there is no response the Editor may be reloading; retry in a few seconds.";

        // ── C6/C8 catalog commands (main thread) ──────────────────────────

        /// <summary>Searches the registered Operations (SPEC §C6) and returns ranked summaries.</summary>
        [CliCommand(Group, "search_operations",
            "Search registered Operations by text, type, effect, tag or origin." + RetryHint)]
        public static object SearchOperations(
            [CliArg("query", "Free-text query matched against ids, names and keywords")] string? query = null,
            [CliArg("acceptsType", "Only Operations that accept this Type ID")] string? acceptsType = null,
            [CliArg("producesType", "Only Operations that produce this Type ID")] string? producesType = null,
            [CliArg("allowedEffects", "Only Operations whose effects are all listed")] List<string>? allowedEffects = null,
            [CliArg("tags", "Only Operations carrying every listed tag")] List<string>? tags = null,
            [CliArg("category", "Only Operations of this category")] string? category = null,
            [CliArg("origin", "\"standard\" or \"custom\"")] string? origin = null,
            [CliArg("lang", "Response language, \"en\" or \"ja\"; defaults to the agentLanguage setting")] string? lang = null,
            [CliArg("limit", "Maximum items (default 10, maximum 50)")] int? limit = null,
            [CliArg("cursor", "Opaque page cursor from a truncated previous response")] string? cursor = null) =>
            PipelineDispatch.Invoke(api => api.SearchOperations(
                new SearchOperationsRequest(
                    query, acceptsType, producesType, allowedEffects, tags,
                    category, origin, lang, limit, cursor)));

        /// <summary>Returns the full contract of one Operation (SPEC §C8).</summary>
        [CliCommand(Group, "describe_operation",
            "Describe one Operation: contract, JSON Schemas and usage examples." + RetryHint)]
        public static object DescribeOperation(
            [CliArg("id", "Operation ID", Required = true)] string id,
            [CliArg("version", "Operation version; the newest registered version answers when omitted")] int? version = null,
            [CliArg("fields", "Sections to include; omit to return the whole description")] List<string>? fields = null,
            [CliArg("lang", "Response language, \"en\" or \"ja\"; defaults to the agentLanguage setting")] string? lang = null,
            [CliArg("cursor", "Opaque page cursor from a truncated previous response")] string? cursor = null) =>
            PipelineDispatch.Invoke(api => api.DescribeOperation(
                new DescribeOperationRequest(id, version, fields, lang, cursor)));

        /// <summary>Finds Operation/port pairs compatible with a type or a given port (SPEC §C7).</summary>
        [CliCommand(Group, "search_compatible_operations",
            "Find Operation and port pairs compatible with a type or an Operation port." + RetryHint)]
        public static object SearchCompatibleOperations(
            [CliArg("match", "Match object: {\"type\":\"<TypeId>\"} or {\"operation\":\"<id>\",\"port\":\"<name>\"}")] CompatibleOperationsQuery? match = null,
            [CliArg("direction", "\"downstream\" or \"upstream\"")] string? direction = null,
            [CliArg("includeJson", "Also return pairs that match only through core.Json")] bool includeJson = false,
            [CliArg("cursor", "Opaque page cursor from a truncated previous response")] string? cursor = null) =>
            PipelineDispatch.Invoke(api => api.SearchCompatibleOperations(
                new SearchCompatibleOperationsRequest(
                    match?.Type, match?.Operation, match?.Port,
                    direction, includeJson, cursor)));

        /// <summary>Lists the registered Type IDs (SPEC §B2).</summary>
        [CliCommand(Group, "list_types",
            "List the registered Type IDs with descriptions and base types." + RetryHint)]
        public static object ListTypes(
            [CliArg("prefix", "Only Type IDs starting with this prefix, e.g. \"unity.\"")] string? prefix = null,
            [CliArg("cursor", "Opaque page cursor from a truncated previous response")] string? cursor = null) =>
            PipelineDispatch.Invoke(api => api.ListTypes(
                new ListTypesRequest(prefix, cursor)));

        /// <summary>Returns the .opweave.json JSON Schema (SPEC §A2).</summary>
        [CliCommand(Group, "get_workflow_schema",
            "Return the JSON Schema of .opweave.json workflow documents." + RetryHint)]
        public static object GetWorkflowSchema(
            [CliArg("cursor", "Opaque page cursor from a truncated previous response")] string? cursor = null) =>
            PipelineDispatch.Invoke(api => api.GetWorkflowSchema(
                new GetWorkflowSchemaRequest(cursor)));

        /// <summary>Lists the project's workflow documents (SPEC §A8).</summary>
        [CliCommand(Group, "list_workflows",
            "List the workflows in the project with id, title and path." + RetryHint)]
        public static object ListWorkflows(
            [CliArg("query", "Filter matched against id, title, description and tags")] string? query = null,
            [CliArg("folder", "Project-relative folder to scan; \"Assets\" by default")] string? folder = null,
            [CliArg("cursor", "Opaque page cursor from a truncated previous response")] string? cursor = null) =>
            PipelineDispatch.Invoke(api => api.ListWorkflows(
                new ListWorkflowsRequest(query, folder, cursor)));

        // ── validate / execute / migrate (main thread) ────────────────────

        /// <summary>Validates a workflow and reports diagnostics, planHash, effects and required approvals (SPEC §D).</summary>
        [CliCommand(Group, "validate_workflow",
            "Validate a workflow: diagnostics, planHash, side effects and required approvals." + RetryHint)]
        public static object ValidateWorkflow(
            [CliArg("workflow", "Workflow document body as a JSON object (or use path)")] JObject? workflow = null,
            [CliArg("path", "Project-relative path to a .opweave.json file (or use workflow)")] string? path = null,
            [CliArg("inputs", "Workflow input values as a JSON object")] JObject? inputs = null,
            [CliArg("dryRun", "Also run Unity-side preflight without making changes")] bool dryRun = false,
            [CliArg("includePlan", "Include the compiled Plan instead of only its summary")] bool includePlan = false) =>
            PipelineDispatch.Invoke(api => api.ValidateWorkflow(
                new ValidateWorkflowRequest(workflow, path, inputs, dryRun, includePlan)));

        /// <summary>Accepts a workflow for execution and returns its runId (SPEC §E2).</summary>
        [CliCommand(Group, "execute_workflow",
            "Validate and accept a workflow for execution; returns the runId." + RetryHint)]
        public static object ExecuteWorkflow(
            [CliArg("workflow", "Workflow document body as a JSON object (or use path)")] JObject? workflow = null,
            [CliArg("path", "Project-relative path to a .opweave.json file (or use workflow)")] string? path = null,
            [CliArg("inputs", "Workflow input values as a JSON object")] JObject? inputs = null,
            [CliArg("expectedPlanHash", "planHash the Run must compile to; a mismatch refuses execution")] string? expectedPlanHash = null,
            [CliArg("queue", "Wait in the queue while another mutating Run is active")] bool queue = false) =>
            PipelineDispatch.Invoke(api => api.ExecuteWorkflow(
                new ExecuteWorkflowRequest(workflow, path, inputs, expectedPlanHash, queue)));

        /// <summary>Migrates a workflow document to the current format (SPEC §A7); never writes the file back.</summary>
        [CliCommand(Group, "migrate_workflow",
            "Migrate a workflow to the current format; returns the migrated JSON and changes." + RetryHint)]
        public static object MigrateWorkflow(
            [CliArg("workflow", "Workflow document body as a JSON object (or use path)")] JObject? workflow = null,
            [CliArg("path", "Project-relative path to a .opweave.json file (or use workflow)")] string? path = null) =>
            PipelineDispatch.Invoke(api => api.MigrateWorkflow(
                new MigrateWorkflowRequest(workflow, path)));

        // ── run observation (background thread, SPEC §H2 item 3) ──────────

        /// <summary>Reports a Run's state and per-step progress; waitSeconds long-polls (SPEC §H1/E-F).</summary>
        [CliCommand(Group, "get_run_status",
            "Get a Run's state and step states. Use waitSeconds to long-poll up to 20 seconds." + RetryHint,
            MainThreadRequired = false)]
        public static object GetRunStatus(
            [CliArg("runId", "Run id (run_ + 26 ULID characters)", Required = true)] string runId,
            [CliArg("waitSeconds", "Wait up to this many seconds (at most 20) for a state change")] int waitSeconds = 0,
            [CliArg("verbosity", "\"compact\" reduces each step to node, state and attempt")] string? verbosity = null) =>
            PipelineDispatch.Invoke(api => api.GetRunStatus(
                new GetRunStatusRequest(runId, waitSeconds, verbosity)));

        /// <summary>Returns a Run's log entries and its journaled change list (SPEC §F6/F7).</summary>
        [CliCommand(Group, "get_run_logs",
            "Get a Run's log entries and the list of journaled changes." + RetryHint,
            MainThreadRequired = false)]
        public static object GetRunLogs(
            [CliArg("runId", "Run id (run_ + 26 ULID characters)", Required = true)] string runId,
            [CliArg("level", "Minimum severity: \"debug\", \"info\", \"warning\" or \"error\"")] string? level = null,
            [CliArg("step", "Only log entries of this step index")] int? step = null,
            [CliArg("cursor", "Opaque page cursor from a truncated previous response")] string? cursor = null) =>
            PipelineDispatch.Invoke(api => api.GetRunLogs(
                new GetRunLogsRequest(runId, level, step, cursor)));

        /// <summary>Lists recorded Runs, newest first (SPEC §F1).</summary>
        [CliCommand(Group, "list_runs",
            "List the recorded Runs, optionally filtered by state or workflow id." + RetryHint,
            MainThreadRequired = false)]
        public static object ListRuns(
            [CliArg("state", "Only Runs in this state (snake_case §E1 state name)")] string? state = null,
            [CliArg("workflowId", "Only Runs of this workflow id")] string? workflowId = null,
            [CliArg("limit", "Required item cap; missing or non-positive is OW-API-001")] int? limit = null,
            [CliArg("cursor", "Opaque page cursor from a truncated previous response")] string? cursor = null) =>
            PipelineDispatch.Invoke(api => api.ListRuns(
                new ListRunsRequest(state, workflowId, limit, cursor)));

        /// <summary>Requests cancellation of a Run (SPEC §E6); the state changes when the run loop processes it.</summary>
        [CliCommand(Group, "cancel_workflow",
            "Request cancellation of a queued, running or suspended Run." + RetryHint)]
        public static object CancelWorkflow(
            [CliArg("runId", "Run id (run_ + 26 ULID characters)", Required = true)] string runId,
            [CliArg("reason", "Reason journaled with the cancel request")] string? reason = null) =>
            PipelineDispatch.Invoke(api => api.CancelWorkflow(
                new CancelWorkflowRequest(runId, reason)));

        /// <summary>Reports the Registry's hash, counts, failures, diff and staleness (SPEC §C3).</summary>
        [CliCommand(Group, "get_registry_status",
            "Get the Registry status: hash, counts, registration failures and diffs." + RetryHint)]
        public static object GetRegistryStatus(
            [CliArg("since", "Registry generation id to diff against; omit for the last recorded pass")] string? since = null,
            [CliArg("cursor", "Opaque page cursor from a truncated previous response")] string? cursor = null) =>
            PipelineDispatch.Invoke(api => api.GetRegistryStatus(
                new GetRegistryStatusRequest(since, cursor)));
    }
}
