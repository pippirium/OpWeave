#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Migration;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The environment seam the Agent API workflow commands
    /// (<c>validate_workflow</c>, <c>execute_workflow</c>,
    /// <c>migrate_workflow</c>, SPEC §H1) consume: the command layer parses
    /// and validates requests, the backend performs every Unity-facing
    /// operation — project file reads, snapshot-backed compilation, §E2 Run
    /// acceptance and the §A7 migration chain — and reports results through
    /// <see cref="Result{T}"/>/<see cref="Diagnostic"/>, never through
    /// exceptions. The seam keeps the service implementation pure Core
    /// logic and lets EditMode tests exercise the commands directly
    /// (SPEC §H1 item 1 — no Pipeline boundary).
    /// </summary>
    internal interface IAgentWorkflowBackend
    {
        /// <summary>
        /// Reads a project-relative workflow file as UTF-8 JSON text.
        /// <paramref name="path"/> is already syntax-checked by
        /// <see cref="WorkflowSource"/> (relative, non-empty); the backend
        /// anchors it to the project root, refuses roots, UNC shares and
        /// any <c>..</c>/drive escape with <c>OW-API-001</c>, and reports a
        /// missing file or an unreadable payload as a file diagnostic —
        /// never as an escaping exception.
        /// </summary>
        Result<string> ReadWorkflowText(string path);

        /// <summary>
        /// Compiles the document through the shared snapshot-backed
        /// <see cref="WorkflowCompiler"/> for <paramref name="actor"/>
        /// (SPEC §D1): <paramref name="inputs"/> are the caller-supplied WF
        /// input values Codec-converted by the compiler for the Plan hash
        /// (SPEC §D0.1); <see cref="ValidationMode.DryRun"/> additionally
        /// runs the §D8 preflight phase and returns planned changes.
        /// Settings problems fail closed into <c>Result.Failure</c>;
        /// business rejections stay <c>report.Ok == false</c> diagnostics.
        /// </summary>
        Result<ValidationReport> Validate(
            WorkflowDocument document,
            JsonValue? inputs,
            ValidationMode mode,
            ActorKind actor);

        /// <summary>
        /// Admits an already-compiled Plan through the §E2 acceptance gates
        /// — integrity/stale-registry/dynamic-requirement re-checks, the
        /// expected-Plan-hash comparison (<see cref="AcceptOptions"/>) and
        /// the admission/queue policy — on the live
        /// <see cref="RunCoordinator"/>.
        /// </summary>
        Result<RunHandle> AcceptRun(
            CompiledPlan plan, WorkflowDocument document, ActorKind actor, AcceptOptions options);

        /// <summary>
        /// Runs the §A7 schema + Operation-version migration chain against
        /// the current registry's migration catalog on the raw document
        /// JSON — a pre-migration document is not a validated
        /// <see cref="WorkflowDocument"/>, so the input is the boundary
        /// <see cref="JObject"/> the serializer never touched. Never writes
        /// files; reports migration failures with the §J codes
        /// (<c>OW-WF-002</c>/<c>OW-WF-007</c>/<c>OW-OP-001</c>/<c>OW-OP-002</c>).
        /// </summary>
        Result<WorkflowMigrationResult> Migrate(JObject document);
    }
}
