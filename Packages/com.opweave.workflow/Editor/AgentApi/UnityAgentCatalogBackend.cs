#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using OpWeave.Core;
using OpWeave.Core.Registry;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The production <see cref="IAgentCatalogBackend"/>: binds the
    /// discovery and Registry commands to the same seams the publication
    /// point, the Registry build host and the project file system already
    /// expose — the atomically published <see cref="RegistrySnapshot"/>,
    /// the §C3 rule-9 stale mark, the last §C3 rule-7 persistence pass,
    /// the load diagnostics a build recorded and the §A2 schema
    /// artifact — so the Agent-facing answers never diverge from what the
    /// Registry tab, Console and Runner see (SPEC §C3, §H1).
    /// </summary>
    /// <remarks>
    /// Every seam is a delegate or path the caller supplies, so EditMode
    /// tests drive the command layer over fixture state without the
    /// production install (SPEC §H1 test 1 — no Pipeline boundary).
    /// </remarks>
    internal sealed class UnityAgentCatalogBackend : IAgentCatalogBackend
    {
        /// <summary>The workflow file suffix a <c>list_workflows</c> scan looks for (SPEC §A2).</summary>
        internal const string WorkflowFilePattern = "*.opweave.json";

        // The project root in GetFullPath form with a trailing separator —
        // the same containment-prefix convention UnityAgentWorkflowBackend
        // uses, so a sibling directory sharing the root's name prefix can
        // never pass the check.
        private readonly string _projectRootPrefix;
        private readonly string _workflowSchemaPath;
        private readonly Func<RegistrySnapshot?> _snapshot;
        private readonly Func<bool> _registryStale;
        private readonly Func<RegistryDiffResult?> _latestDiff;
        private readonly Func<IReadOnlyList<Diagnostic>> _registryLoadDiagnostics;
        private readonly Func<string> _agentLanguage;

        /// <param name="projectRoot">
        /// The project directory <paramref name="workflowSchemaPath"/> and
        /// every <see cref="ListWorkflowFiles"/> folder anchor to —
        /// resolved to a full path once here.
        /// </param>
        /// <param name="workflowSchemaPath">
        /// The shipped §A2 artifact
        /// (<c>Schemas~/opweave-1.0.schema.json</c>) — the resolved
        /// package path in production.
        /// </param>
        /// <param name="snapshot">
        /// The publication point's snapshot seam — re-read per call so a
        /// rebuilt snapshot takes over without rewiring (SPEC §C3 rule 8).
        /// </param>
        /// <param name="registryStale">The publication point's §C3 rule-9 stale flag.</param>
        /// <param name="latestDiff">
        /// The last recorded §C3 rule-7 persistence pass
        /// (<see cref="OpWeave.UnityAdapter.RegistryDiffRecorder"/>'s retained
        /// result), or null before the first recorded pass.
        /// </param>
        /// <param name="registryLoadDiagnostics">
        /// The configuration-load diagnostics of the latest build — or a
        /// fresh load's diagnostics before the first build ran.
        /// </param>
        /// <param name="agentLanguage">The live <c>agentLanguage</c> reading.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="projectRoot"/> or <paramref name="workflowSchemaPath"/>
        /// is null or empty.
        /// </exception>
        /// <exception cref="ArgumentNullException">Any seam is null.</exception>
        internal UnityAgentCatalogBackend(
            string projectRoot,
            string workflowSchemaPath,
            Func<RegistrySnapshot?> snapshot,
            Func<bool> registryStale,
            Func<RegistryDiffResult?> latestDiff,
            Func<IReadOnlyList<Diagnostic>> registryLoadDiagnostics,
            Func<string> agentLanguage)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new ArgumentException(
                    "The project root must not be null or empty.", nameof(projectRoot));
            }
            if (string.IsNullOrEmpty(workflowSchemaPath))
            {
                throw new ArgumentException(
                    "The workflow schema path must not be null or empty.",
                    nameof(workflowSchemaPath));
            }
            var root = Path.GetFullPath(projectRoot);
            _projectRootPrefix = root.EndsWith(
                Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            _workflowSchemaPath = workflowSchemaPath;
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _registryStale = registryStale ?? throw new ArgumentNullException(nameof(registryStale));
            _latestDiff = latestDiff ?? throw new ArgumentNullException(nameof(latestDiff));
            _registryLoadDiagnostics = registryLoadDiagnostics ??
                throw new ArgumentNullException(nameof(registryLoadDiagnostics));
            _agentLanguage = agentLanguage ?? throw new ArgumentNullException(nameof(agentLanguage));
        }

        /// <inheritdoc/>
        public RegistrySnapshot? Snapshot() => _snapshot();

        /// <inheritdoc/>
        public bool RegistryStale() => _registryStale();

        /// <inheritdoc/>
        public RegistryDiffResult? LatestDiff() => _latestDiff();

        /// <inheritdoc/>
        public IReadOnlyList<Diagnostic> RegistryLoadDiagnostics() =>
            _registryLoadDiagnostics();

        /// <inheritdoc/>
        public string AgentLanguage() => _agentLanguage();

        /// <inheritdoc/>
        public Result<string> ReadWorkflowSchema()
        {
            try
            {
                return Result<string>.Success(File.ReadAllText(_workflowSchemaPath));
            }
            catch (Exception e) when (
                e is IOException || e is UnauthorizedAccessException ||
                e is NotSupportedException || e is System.Security.SecurityException)
            {
                // The packaged schema artifact must always exist — a read
                // failure is a broken installation, an internal error
                // rather than request-shape misuse.
                return Result<string>.Failure(AgentApiErrors.InternalError(
                    "The workflow schema artifact could not be read."));
            }
        }

        /// <inheritdoc/>
        public Result<IReadOnlyList<AgentWorkflowFile>> ListWorkflowFiles(string folder)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(_projectRootPrefix, folder));
            }
            catch (Exception e) when (
                e is ArgumentException || e is NotSupportedException ||
                e is PathTooLongException)
            {
                return Result<IReadOnlyList<AgentWorkflowFile>>.Failure(UnreadableFolder());
            }
            // Containment: the resolved folder must stay strictly inside the
            // project root — a ".." escape lands outside the prefix.
            if (!full.StartsWith(_projectRootPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    full.TrimEnd(Path.DirectorySeparatorChar),
                    _projectRootPrefix.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result<IReadOnlyList<AgentWorkflowFile>>.Failure(UnreadableFolder());
            }
            var files = new List<AgentWorkflowFile>();
            if (!Directory.Exists(full))
            {
                return Result<IReadOnlyList<AgentWorkflowFile>>.Success(files);
            }
            string[] paths;
            try
            {
                paths = Directory.GetFiles(full, WorkflowFilePattern, SearchOption.AllDirectories);
            }
            catch (Exception e) when (
                e is IOException || e is UnauthorizedAccessException ||
                e is NotSupportedException || e is System.Security.SecurityException)
            {
                return Result<IReadOnlyList<AgentWorkflowFile>>.Failure(UnreadableFolder());
            }
            for (var i = 0; i < paths.Length; i++)
            {
                var relative = paths[i]
                    .Substring(_projectRootPrefix.Length)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                string? text = null;
                try
                {
                    text = File.ReadAllText(paths[i]);
                }
                catch (Exception e) when (
                    e is IOException || e is UnauthorizedAccessException ||
                    e is NotSupportedException || e is System.Security.SecurityException)
                {
                    // The entry is still listed — the command layer warns
                    // instead of silently dropping a workflow the Agent
                    // may need to fix.
                }
                files.Add(new AgentWorkflowFile(relative, text));
            }
            files.Sort(
                static (a, b) => string.CompareOrdinal(a.Path, b.Path));
            return Result<IReadOnlyList<AgentWorkflowFile>>.Success(files);
        }

        // A folder the request cannot name inside the project is
        // request-shape misuse — no dedicated §J code exists for an
        // unreadable folder (a local reversible choice; OW-API-001 is the
        // malformed-argument code the path validation already emits).
        private static Diagnostic UnreadableFolder() =>
            AgentApiErrors.MalformedRequest(
                "The 'folder' argument must name a readable directory inside the project.",
                jsonPath: "$.folder");
    }
}
