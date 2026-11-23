#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Migration;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;

namespace OpWeave.AgentApi
{
    /// <summary>
    /// The production <see cref="IAgentWorkflowBackend"/>: binds the workflow
    /// commands to the same seams the Runner uses — the shared publication's
    /// <see cref="RegistrySnapshot"/>, the runner host's settings-derived
    /// <see cref="RunPolicyEnvironment"/>, the
    /// <see cref="UnityWorkflowCompiler"/> D8 wiring
    /// and the runner host's §E2 acceptance entry point — so an Agent-facing
    /// validate/execute/migrate never diverges from the behaviour a GUI or
    /// CLI run of the same document sees (SPEC §H1, §D1, §E2, §A7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every seam is a delegate or instance the caller supplies, so EditMode
    /// tests drive the command layer over the fixture host without the
    /// production install (SPEC §H1 test 1 — no Pipeline boundary).
    /// </para>
    /// <para>
    /// The policy environment is re-read on each <see cref="Validate"/> — a
    /// tightened or repaired settings file binds the next validation just as
    /// it binds the next Run acceptance (SPEC §H4.1, decision D-R6). A failed
    /// or invalid load fails closed as a command failure rather than
    /// compiling under defaults that were never approved. The environment is
    /// an adapter-side projection, so this Agent-facing assembly declares no
    /// approval/review/settings member (SPEC §H4 item 5).
    /// </para>
    /// </remarks>
    internal sealed class UnityAgentWorkflowBackend : IAgentWorkflowBackend
    {
        private static readonly IReadOnlyDictionary<string, TypeRef> s_noPortTypes =
            new Dictionary<string, TypeRef>(StringComparer.Ordinal);

        // The project root in GetFullPath form with a trailing separator, so
        // the containment check's prefix match can never accept a sibling
        // directory sharing the root's name prefix ("proj-evil" under
        // "proj").
        private readonly string _projectRootPrefix;
        private readonly Func<Result<RunPolicyEnvironment>> _policyEnvironment;
        private readonly Func<RegistrySnapshot> _currentRegistry;
        private readonly Func<IReadOnlyList<TypeProviderCandidate>> _typeProviders;
        private readonly Func<CompiledPlan, WorkflowDocument, ActorKind, AcceptOptions, Result<RunHandle>>
            _acceptRun;
        private readonly IStaticTypeProbe _typeProbe;
        private readonly IEditorState? _editorState;
        private readonly IReadOnlyList<IWorkflowMigration> _workflowMigrations;
        private readonly Func<IReadOnlyList<RegistryChange>>? _previousDefinitions;
        private readonly RefResolver _refs = new RefResolver();

        /// <param name="projectRoot">
        /// The project directory every <paramref name="ReadWorkflowText"/>
        /// path is anchored to — resolved to a full path once here.
        /// </param>
        /// <param name="policyEnvironment">
        /// The settings-derived <see cref="RunPolicyEnvironment"/> seam —
        /// the runner host's <c>CurrentRunPolicyEnvironment</c>, re-read per
        /// <see cref="Validate"/> so a tightened or repaired settings file
        /// binds the next validation just as it binds the next Run
        /// acceptance (SPEC §H4.1, decision D-R6). A failed or invalid load
        /// fails closed as a command failure rather than compiling under
        /// defaults that were never approved. The Agent-facing assembly
        /// binds this adapter-side projection and never names the settings
        /// model itself (SPEC §H4 item 5).
        /// </param>
        /// <param name="currentRegistry">
        /// Re-read per call so a rebuilt snapshot takes over without
        /// rewiring (SPEC §C3 rule 8) — the same seam the runner host binds.
        /// </param>
        /// <param name="typeProviders">
        /// The <see cref="TypeProviderCandidate"/> set the published
        /// Registry's type stage accepted — the §C0.2 weak-fingerprint
        /// bindings <see cref="UnityPlanProvenance"/> needs for the Plan
        /// hash material.
        /// </param>
        /// <param name="acceptRun">
        /// The §E2 acceptance entry point — the runner host's
        /// <c>AcceptRun</c>, whose gate chain re-checks settings validity,
        /// the stale-Registry verdict and the Plan's integrity/permission
        /// material before the coordinator admits anything.
        /// </param>
        /// <param name="typeProbe">
        /// The §D6 <c>type_exists</c> probe; the live
        /// <see cref="UnityTypeProbe"/> when omitted.
        /// </param>
        /// <param name="editorState">
        /// The §D8 Edit-Mode gate; the live <see cref="EditorStateProbe"/>
        /// inside <see cref="UnityWorkflowCompiler"/> when omitted.
        /// </param>
        /// <param name="workflowMigrations">
        /// The registered <see cref="IWorkflowMigration"/> chain steps of
        /// §A7 phase 1. No registration surface exists in v1 — schema 1.0
        /// is the only supported version — so the production chain is
        /// empty and only same-major documents migrate.
        /// </param>
        /// <param name="previousDefinitions">
        /// The durable previous-definition records the D3 OW-OP-001 hint
        /// reads (SPEC §C3 rule 7): the diff recorder's
        /// <c>PreviousDefinitions</c>, re-read per <see cref="Validate"/> so
        /// an Operation deleted since the last build reports the source
        /// file it was defined in. Omitted, an unknown Operation reports
        /// the plain registration hint.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="projectRoot"/> is null or empty.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="policyEnvironment"/>, <paramref name="currentRegistry"/>,
        /// <paramref name="typeProviders"/> or <paramref name="acceptRun"/>
        /// is null.
        /// </exception>
        internal UnityAgentWorkflowBackend(
            string projectRoot,
            Func<Result<RunPolicyEnvironment>> policyEnvironment,
            Func<RegistrySnapshot> currentRegistry,
            Func<IReadOnlyList<TypeProviderCandidate>> typeProviders,
            Func<CompiledPlan, WorkflowDocument, ActorKind, AcceptOptions, Result<RunHandle>>
                acceptRun,
            IStaticTypeProbe? typeProbe = null,
            IEditorState? editorState = null,
            IReadOnlyList<IWorkflowMigration>? workflowMigrations = null,
            Func<IReadOnlyList<RegistryChange>>? previousDefinitions = null)
        {
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new ArgumentException(
                    "The project root must not be null or empty.", nameof(projectRoot));
            }
            var root = Path.GetFullPath(projectRoot);
            _projectRootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            _policyEnvironment =
                policyEnvironment ?? throw new ArgumentNullException(nameof(policyEnvironment));
            _currentRegistry =
                currentRegistry ?? throw new ArgumentNullException(nameof(currentRegistry));
            _typeProviders =
                typeProviders ?? throw new ArgumentNullException(nameof(typeProviders));
            _acceptRun = acceptRun ?? throw new ArgumentNullException(nameof(acceptRun));
            _typeProbe = typeProbe ?? new UnityTypeProbe();
            _editorState = editorState;
            _workflowMigrations = workflowMigrations ?? Array.Empty<IWorkflowMigration>();
            _previousDefinitions = previousDefinitions;
        }

        /// <inheritdoc/>
        public Result<string> ReadWorkflowText(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(_projectRootPrefix, path));
            }
            catch (Exception e) when (
                e is ArgumentException || e is NotSupportedException ||
                e is PathTooLongException)
            {
                return Result<string>.Failure(UnreadablePath());
            }
            // Containment: the resolved path must stay strictly inside the
            // project root — a ".." escape lands outside the prefix.
            if (!full.StartsWith(_projectRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Result<string>.Failure(UnreadablePath());
            }
            try
            {
                return Result<string>.Success(File.ReadAllText(full));
            }
            catch (Exception e) when (
                e is IOException || e is UnauthorizedAccessException ||
                e is NotSupportedException || e is System.Security.SecurityException)
            {
                return Result<string>.Failure(UnreadablePath());
            }
        }

        /// <inheritdoc/>
        public Result<ValidationReport> Validate(
            WorkflowDocument document,
            JsonValue? inputs,
            ValidationMode mode,
            ActorKind actor)
        {
            var environment = _policyEnvironment();
            if (!environment.Ok)
            {
                return Result<ValidationReport>.Failure(environment.Diagnostics);
            }
            var bound = environment.Value!;
            var snapshot = _currentRegistry();
            var declared = DeclaredInputTypes(document, snapshot);
            var compiler = UnityWorkflowCompiler.Create(
                snapshot,
                (nodeId, parameters) => new UnityStaticContext(
                    nodeId,
                    parameters,
                    declared.TryGetValue(nodeId, out var types) ? types : s_noPortTypes,
                    snapshot.Types,
                    _refs),
                _typeProbe,
                UnityPlanProvenance.Capture(bound.IntegrityLevel, _typeProviders()),
                _editorState,
                changes: _previousDefinitions?.Invoke(),
                policyFor: bound.PolicyFor,
                allowedPaths: bound.AllowedPaths);
            var report = compiler.Validate(
                new ValidationRequest(document, inputs, mode, actor));
            return environment.Diagnostics.Count == 0
                ? Result<ValidationReport>.Success(report)
                : Result<ValidationReport>.Success(report, environment.Diagnostics);
        }

        /// <inheritdoc/>
        public Result<RunHandle> AcceptRun(
            CompiledPlan plan, WorkflowDocument document, ActorKind actor,
            AcceptOptions options) =>
            _acceptRun(plan, document, actor, options);

        /// <inheritdoc/>
        public Result<WorkflowMigrationResult> Migrate(JObject document) =>
            WorkflowMigrator.Migrate(
                document,
                _workflowMigrations,
                new OperationMigrationCatalog(_currentRegistry()));

        // The per-node map of declared input-port Type IDs the
        // UnityStaticContext factory closes over. A node whose Operation the
        // snapshot does not hold — a failure the compile itself reports —
        // gets the empty table; its Get<T> reads then fail closed with the
        // OW-RUN-007 type mismatch the missing declaration earns.
        private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypeRef>>
            DeclaredInputTypes(WorkflowDocument document, RegistrySnapshot snapshot)
        {
            var declared =
                new Dictionary<string, IReadOnlyDictionary<string, TypeRef>>(
                    StringComparer.Ordinal);
            var nodes = document.Nodes;
            for (var i = 0; i < nodes.Count; i++)
            {
                var types = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
                var registered = snapshot.Find(nodes[i].Operation, nodes[i].Version);
                if (registered is not null)
                {
                    var inputs = registered.Descriptor.Inputs;
                    for (var p = 0; p < inputs.Count; p++)
                    {
                        var parsed = TypeRef.Parse(inputs[p].Type);
                        if (parsed.Ok)
                        {
                            types[inputs[p].Name] = parsed.Value!;
                        }
                    }
                }
                declared[nodes[i].Id] = types;
            }
            return declared;
        }

        // A workflow file the path cannot name inside the project is
        // request-shape misuse — no dedicated §J code exists for a missing
        // or unreadable file (a local reversible choice; OW-API-001 is the
        // malformed-argument code the source validation already emits).
        private static Diagnostic UnreadablePath() =>
            AgentApiErrors.MalformedRequest(
                "The 'path' argument must name a readable workflow file inside the project.",
                jsonPath: "$.path");
    }
}
