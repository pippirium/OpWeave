#nullable enable

using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The two ways a workflow reaches a workflow-scoped command (SPEC §H1):
    /// the document body in <c>workflow</c> (a JSON object) or a
    /// project-relative <c>path</c> — never both, never neither. Supplying
    /// anything else is request-shape misuse answered with OW-API-001 (SPEC §J);
    /// reading the path's file stays with the command itself.
    /// </summary>
    public sealed class WorkflowSource
    {
        private WorkflowSource(JObject? workflow, string? path)
        {
            Workflow = (JObject?)workflow?.DeepClone();
            Path = path;
        }

        /// <summary>The document body — a defensive copy of the request's token
        /// (SPEC §0.4 boundary rule) — or null when the source is a path.</summary>
        public JObject? Workflow { get; }

        /// <summary>The project-relative path, or null when the source is a body.</summary>
        public string? Path { get; }

        /// <summary><c>true</c> when the workflow came as an inline document.</summary>
        public bool IsBody => Workflow is not null;

        /// <summary>
        /// Validates the <c>workflow</c>/<c>path</c> pair of a request: exactly
        /// one must be present, <paramref name="workflow"/> must be a JSON
        /// object, and <paramref name="path"/> must be a non-empty,
        /// project-relative string (a rooted path is never inside the project).
        /// Every violation is OW-API-001.
        /// </summary>
        public static Result<WorkflowSource> From(JToken? workflow, string? path)
        {
            if (workflow is not null && path is not null)
            {
                return Result<WorkflowSource>.Failure(AgentApiErrors.MalformedRequest(
                    "The request carries both 'workflow' and 'path'; supply exactly one.",
                    hint: "Drop one of the two arguments."));
            }
            if (workflow is null && path is null)
            {
                return Result<WorkflowSource>.Failure(AgentApiErrors.MalformedRequest(
                    "The request must supply 'workflow' or 'path'.",
                    hint: "Pass the document body or a project-relative path."));
            }
            if (workflow is not null && workflow is not JObject)
            {
                return Result<WorkflowSource>.Failure(AgentApiErrors.MalformedRequest(
                    "The 'workflow' argument must be a JSON object.", jsonPath: "$.workflow"));
            }
            if (path is not null)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return Result<WorkflowSource>.Failure(AgentApiErrors.MalformedRequest(
                        "The 'path' argument must be a non-empty project-relative path.",
                        jsonPath: "$.path"));
                }
                if (System.IO.Path.IsPathRooted(path))
                {
                    return Result<WorkflowSource>.Failure(AgentApiErrors.MalformedRequest(
                        "The 'path' argument must be a project-relative path, not a rooted path.",
                        jsonPath: "$.path"));
                }
            }
            return Result<WorkflowSource>.Success(
                new WorkflowSource((JObject?)workflow, path));
        }
    }
}
