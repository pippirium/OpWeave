#nullable enable

using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Registry;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The environment seam the Agent API discovery and Registry commands
    /// (<c>search_operations</c>, <c>describe_operation</c>,
    /// <c>search_compatible_operations</c>, <c>list_types</c>,
    /// <c>get_workflow_schema</c>, <c>list_workflows</c> and
    /// <c>get_registry_status</c>, SPEC §H1) consume: the command layer
    /// parses and validates requests, the backend supplies every
    /// Unity-facing reading — the published <see cref="RegistrySnapshot"/>,
    /// the §C3 rule-7/9 persistence state, the load diagnostics, the
    /// §A2 schema artifact and the project workflow file listing — through
    /// plain values and <see cref="Result{T}"/>, never through exceptions.
    /// The seam keeps the service implementation pure Core logic and lets
    /// EditMode tests exercise the commands directly (SPEC §H1 test 1 —
    /// no Pipeline boundary).
    /// </summary>
    internal interface IAgentCatalogBackend
    {
        /// <summary>
        /// The last atomically published <see cref="RegistrySnapshot"/> —
        /// re-read per call so a rebuilt snapshot takes over without
        /// rewiring (SPEC §C3 rule 8). Null until the first successful
        /// build (the §C3 rule-9 pre-build Registry stays stale with no
        /// snapshot).
        /// </summary>
        RegistrySnapshot? Snapshot();

        /// <summary>
        /// The SPEC §C3 rule-9 stale flag of the publication point: true
        /// until the first successful build and while the live
        /// compile-failure signal holds.
        /// </summary>
        bool RegistryStale();

        /// <summary>
        /// The last recorded SPEC §C3 rule-7 persistence pass — the
        /// previous-summary read, the computed change batch and the
        /// <c>previousSnapshot: "unavailable"</c> reason — or null before
        /// the first recorded pass.
        /// </summary>
        RegistryDiffResult? LatestDiff();

        /// <summary>
        /// The configuration-load diagnostics the latest Registry build
        /// observed (OW-CFG-001/002 errors, OW-CFG-003/004/005 warnings), so
        /// <c>get_registry_status</c> reports the configuration error and
        /// its JsonPath exactly as the build saw it (SPEC §H4.1); a fresh
        /// load answers before the first build.
        /// </summary>
        IReadOnlyList<Diagnostic> RegistryLoadDiagnostics();

        /// <summary>
        /// The <c>agentLanguage</c> value — the §C6 item-5 default
        /// language search and describe fall back to when the request
        /// carries no <c>lang</c>; English when the configuration is
        /// unavailable (the fail-closed default reads no Agent-facing
        /// policy, SPEC §H4.1).
        /// </summary>
        string AgentLanguage();

        /// <summary>
        /// Reads the shipped §A2 schema artifact
        /// (<c>Schemas~/opweave-1.0.schema.json</c>) verbatim.
        /// </summary>
        Result<string> ReadWorkflowSchema();

        /// <summary>
        /// Lists every <c>*.opweave.json</c> file under the project-relative
        /// <paramref name="folder"/> recursively and reads its UTF-8 text.
        /// The folder is already shape-checked by the command layer; the
        /// backend anchors it to the project root, refuses any
        /// <c>..</c>/drive escape with OW-API-001 and treats a missing
        /// directory as an empty listing. A file whose text cannot be read
        /// is returned with a null <see cref="AgentWorkflowFile.Text"/> so
        /// the command can warn instead of dropping it.
        /// </summary>
        Result<IReadOnlyList<AgentWorkflowFile>> ListWorkflowFiles(string folder);
    }

    /// <summary>
    /// One project workflow file of a <c>list_workflows</c> scan: the
    /// project-relative forward-slash path and its UTF-8 text — null when
    /// the file could not be read (the command reports it as a warning,
    /// never silently).
    /// </summary>
    internal sealed record AgentWorkflowFile(string Path, string? Text);
}
