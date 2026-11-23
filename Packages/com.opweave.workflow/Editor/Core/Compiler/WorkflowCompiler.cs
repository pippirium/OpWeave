#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The default <see cref="IWorkflowCompiler"/> (SPEC §D1). The pipeline keeps the
    /// explicit SPEC §N.5.5 item 4 stage boundary — Resolve → Expand (reserved) →
    /// Validate → Build Plan — where the Validate stage runs the phases in the
    /// SPEC §D1 item 1 order D2 → D3 → D4 → D5 → D6, with D8 appended only for a
    /// <see cref="ValidationMode.DryRun"/> request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §D1 pipeline semantics: errors from the D2 phase stop the
    /// pipeline, Error diagnostics from later phases exclude only the node
    /// they name while remaining nodes stay checked, and the report holds at
    /// most <see cref="MaxDiagnostics"/> diagnostics ordered by node document
    /// order then code with <see cref="ValidationReport.Truncated"/> set
    /// beyond that. A <see cref="ValidationMode.DryRun"/> request appends the
    /// D8 preflight and carries its <see cref="ValidationReport.PlannedChanges"/>
    /// with <see cref="ValidationReport.Guarantee"/> <c>"none"</c> (SPEC §D8).
    /// </para>
    /// </remarks>
    public sealed class WorkflowCompiler : IWorkflowCompiler
    {
        /// <summary>Maximum diagnostics a report carries (SPEC §D1 item 3).</summary>
        internal const int MaxDiagnostics = 100;

        private readonly IReadOnlyList<IValidationPhase> _phases;
        private readonly IValidationPhase? _dryRunPhase;
        private readonly IOperationSource? _operations;
        private readonly ITypeRegistry? _types;
        private readonly RegistrySnapshot? _snapshot;
        private readonly IPlanProvenance? _provenance;

        /// <summary>
        /// The snapshot-free pipeline: real D2 and D5, pending D3–D4, D6 and D8
        /// slots, and a Resolve stage with no operation source or type registry,
        /// so it converts nothing — and without a registry snapshot the D6
        /// phase has no descriptors to evaluate either. Production callers use
        /// the snapshot-backed overload below.
        /// </summary>
        public WorkflowCompiler()
            : this(
                DefaultPhases(new PendingValidationPhase(ValidationPhaseKind.D3)),
                new PendingValidationPhase(ValidationPhaseKind.D8))
        {
        }

        /// <summary>
        /// The RegistrySnapshot-backed production pipeline (SPEC §C3, §D3): the
        /// snapshot supplies the Resolve stage's (id, version) → Descriptor
        /// lookup (<see cref="IOperationSource"/>) and its type registry, so
        /// Resolve produces the <see cref="ResolvedWorkflow"/> for real
        /// compiler calls, and it backs the D3 phase's operation and parameter
        /// checks and the D4 phase's type and supplier checks.
        /// </summary>
        /// <param name="snapshot">The currently published Registry snapshot.</param>
        /// <param name="staticContextFactory">
        /// Builds the read-only <see cref="StaticContext"/> handed to
        /// <see cref="IOperation.ValidateStatic"/> (SPEC §0.6 SG-06: Core hosts
        /// no context implementation; UnityAdapter supplies the production
        /// one). Required: no node can satisfy the SPEC §D3 static-check
        /// contract without it, so a snapshot-backed compiler that cannot run
        /// the check is rejected at construction rather than reporting a
        /// published diagnostic that would misname the cause.
        /// </param>
        /// <param name="typeProbe">
        /// The D6 phase's <c>type_exists</c> probe (SPEC §D6 item 4).
        /// Required, following the D3-STATIC-02 precedent: Core hosts no
        /// probe implementation (SPEC §0.6 SG-06 — the Unity type space
        /// cannot be observed without an adapter), so a snapshot-backed
        /// compiler that cannot run the check is rejected at construction
        /// rather than silently skipping the verdict.
        /// </param>
        /// <param name="provenance">
        /// The Build Plan stage's planHash material source (SPEC §D7 item 5,
        /// §0.6 SG-11): the six Unity environment identifiers and the weak
        /// per-Type/Codec fingerprint atoms of the used definitions, produced
        /// Unity-side by W2.2. Required on the same precedent as
        /// <paramref name="typeProbe"/>: a snapshot-backed compiler that
        /// cannot produce the mandated hash inputs is rejected at
        /// construction rather than emitting a planHash assembled from
        /// guessed or empty identifiers — and the configured
        /// <see cref="IntegrityLevel"/> it carries is an explicit Build Plan
        /// input: <see cref="IntegrityLevel.Full"/> answers OW-OP-011 with no
        /// weak fallback (SPEC §0.1 F-05).
        /// </param>
        /// <param name="changes">
        /// The latest registry diff (SPEC §C3 rule 7) used for the
        /// previous-definition-source hint on OW-OP-001; the persisted-diff
        /// producer lands with T-W2.2-04. Null means no hint data.
        /// </param>
        /// <param name="policyFor">
        /// The D6 phase's per-actor permission policy lookup (SPEC §D6 item 1);
        /// null selects the SPEC §C4/§H4 defaults until the real settings
        /// source lands (T-W3.4-01).
        /// </param>
        /// <param name="allowedPaths">
        /// The allowed path prefixes constant <c>path_writable</c> ports are
        /// checked against (SPEC §D6 item 2); null selects the §H4 default
        /// <c>["Assets/"]</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="snapshot"/>, <paramref name="staticContextFactory"/>,
        /// <paramref name="typeProbe"/> or <paramref name="provenance"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="snapshot"/> carries an Operation entry without an
        /// implementation instance, so its static check cannot run.
        /// </exception>
        /// <remarks>
        /// The D8 Dry Run environment binds the seams the host registered
        /// through the Core-internal <c>DryRunEnvironment</c> point: inside
        /// the Unity Editor the adapter's <c>[InitializeOnLoad]</c>
        /// bootstrap has installed the real <c>UnityStepContext</c> factory
        /// and the §D8 item 3 Edit-Mode main-thread probes, so a production
        /// Dry Run through this public entry point runs Preflight and
        /// reports planned changes. A host that installed no environment —
        /// pure .NET or a skipped bootstrap — leaves the gate failing
        /// closed with OW-RUN-001.
        /// </remarks>
        public WorkflowCompiler(
            RegistrySnapshot snapshot,
            Func<string, IReadOnlyDictionary<string, InputValue>, StaticContext> staticContextFactory,
            IStaticTypeProbe typeProbe,
            IPlanProvenance provenance,
            IReadOnlyList<RegistryChange>? changes = null,
            Func<ActorKind, IPermissionPolicy>? policyFor = null,
            IReadOnlyList<string>? allowedPaths = null)
            : this(
                snapshot,
                staticContextFactory,
                typeProbe,
                provenance,
                DryRunEnvironment.ContextFactoryFor(snapshot?.Types),
                DryRunEnvironment.EditorState,
                DryRunEnvironment.IsMainThread,
                changes,
                policyFor,
                allowedPaths)
        {
        }

        /// <summary>
        /// The snapshot-backed production pipeline with the D8 Dry Run
        /// environment seams bound (SPEC §D8): the pipeline is identical to
        /// the public constructor's, except the D8 phase builds each step's
        /// <see cref="StepContext"/> through
        /// <paramref name="dryRunContextFactory"/> — the UnityAdapter's
        /// <c>UnityStepContext</c> for the Editor-driven Dry Run — and gates
        /// on <paramref name="dryRunEditorState"/> and
        /// <paramref name="dryRunIsMainThread"/> for the §D8 item 3
        /// Edit-Mode main-thread requirement (Play Mode, a background
        /// thread or an unbound probe → OW-RUN-001, no Preflight). They
        /// default to the Core-only configuration: the JSON-level step
        /// context the run loop also falls back to, and no environment
        /// probes — so the gate fails closed and refuses every Dry Run.
        /// </summary>
        internal WorkflowCompiler(
            RegistrySnapshot snapshot,
            Func<string, IReadOnlyDictionary<string, InputValue>, StaticContext> staticContextFactory,
            IStaticTypeProbe typeProbe,
            IPlanProvenance provenance,
            Func<DryRunStepRequest, StepContext>? dryRunContextFactory,
            IEditorState? dryRunEditorState,
            Func<bool>? dryRunIsMainThread,
            IReadOnlyList<RegistryChange>? changes = null,
            Func<ActorKind, IPermissionPolicy>? policyFor = null,
            IReadOnlyList<string>? allowedPaths = null)
            : this(
                DefaultPhases(
                    new OperationValidationPhase(
                        snapshot ?? throw new ArgumentNullException(nameof(snapshot)),
                        staticContextFactory ?? throw new ArgumentNullException(nameof(staticContextFactory)),
                        changes),
                    new DataBindingValidationPhase(snapshot, snapshot.Types),
                    new PermissionValidationPhase(
                        snapshot,
                        staticContextFactory,
                        policyFor ?? PermissionValidationPhase.DefaultPolicyFor,
                        typeProbe ?? throw new ArgumentNullException(nameof(typeProbe)),
                        allowedPaths ?? PermissionValidationPhase.DefaultAllowedPaths)),
                new DryRunValidationPhase(
                    snapshot, dryRunEditorState, dryRunContextFactory, dryRunIsMainThread),
                snapshot,
                snapshot.Types,
                snapshot,
                provenance ?? throw new ArgumentNullException(nameof(provenance)))
        {
        }

        /// <summary>
        /// The default phase set with the Resolve-stage environment injected: the
        /// (id, version) → Descriptor lookup and the type registry the codecs
        /// resolve through (SPEC §D0.2).
        /// </summary>
        internal WorkflowCompiler(IOperationSource? operations, ITypeRegistry? types)
            : this(
                DefaultPhases(new PendingValidationPhase(ValidationPhaseKind.D3)),
                new PendingValidationPhase(ValidationPhaseKind.D8),
                operations,
                types)
        {
        }

        // Phase list in the order the pipeline runs them; the D2-stop and node
        // exclusion rules key on each phase's declared ValidationPhaseKind, so test
        // doubles can observe the ordering contract directly.
        internal WorkflowCompiler(
            IReadOnlyList<IValidationPhase> phases,
            IValidationPhase? dryRunPhase = null)
            : this(phases, dryRunPhase, null, null)
        {
        }

        internal WorkflowCompiler(
            IReadOnlyList<IValidationPhase> phases,
            IValidationPhase? dryRunPhase,
            IOperationSource? operations,
            ITypeRegistry? types)
            : this(phases, dryRunPhase, operations, types, null, null)
        {
        }

        internal WorkflowCompiler(
            IReadOnlyList<IValidationPhase> phases,
            IValidationPhase? dryRunPhase,
            IOperationSource? operations,
            ITypeRegistry? types,
            RegistrySnapshot? snapshot,
            IPlanProvenance? provenance)
        {
            if (phases is null)
            {
                throw new ArgumentNullException(nameof(phases));
            }
            foreach (var phase in phases)
            {
                if (phase is null)
                {
                    throw new ArgumentException(
                        "The phase list must not contain null entries.", nameof(phases));
                }
            }
            _phases = ReadOnlyCollections.CopyList(phases);
            _dryRunPhase = dryRunPhase;
            _operations = operations;
            _types = types;
            _snapshot = snapshot;
            _provenance = provenance;
        }

        /// <summary>Runs the pipeline stages and returns the structured report (SPEC §D1).</summary>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="request"/> has a null document.</exception>
        public ValidationReport Validate(ValidationRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (request.Document is null)
            {
                throw new ArgumentException(
                    "ValidationRequest.Document must not be null.", nameof(request));
            }

            var context = new ValidationContext(request);

            // SPEC §N.5.5 item 4 stage boundary. Resolve and Build Plan are stubs
            // until their owning tickets land; Expand is reserved for the future
            // Blueprint expansion and intentionally a no-op in v1.
            ResolveStage(context);
            ExpandStage(context);
            RunValidateStage(context);
            var plan = context.HasErrors ? null : BuildPlanStage(context);
            return BuildReport(context, plan);
        }

        private static IReadOnlyList<IValidationPhase> DefaultPhases(
            IValidationPhase d3Phase,
            IValidationPhase? d4Phase = null,
            IValidationPhase? d6Phase = null) =>
            new IValidationPhase[]
            {
                new StructuralValidationPhase(),
                d3Phase,
                d4Phase ?? new PendingValidationPhase(ValidationPhaseKind.D4),
                new GraphValidationPhase(),
                d6Phase ?? new PendingValidationPhase(ValidationPhaseKind.D6),
            };

        // Resolve stage (SPEC §N.5.5 item 4, §D0.2): produces the codec-resolved
        // ResolvedWorkflow and resolved execution inputs used as the planHash
        // input, delivered by T-W1.6-06. Without an injected operation source and
        // type registry the stage converts nothing.
        private void ResolveStage(ValidationContext context) =>
            new ResolveStage(_operations, _types).Run(context);

        // Expand stage (SPEC §N.5.5 item 4): reserved for the future Blueprint
        // expansion; v1 performs no expansion by contract.
        private static void ExpandStage(ValidationContext context)
        {
        }

        // Validate stage (SPEC §D1 item 1-2): the phases run in list order
        // D2 → D3 → D4 → D5 → D6; errors produced by the D2 phase stop the
        // pipeline, later phases keep running on the nodes not excluded by their
        // Error diagnostics, and the D8 preflight is appended only for Dry Run.
        private void RunValidateStage(ValidationContext context)
        {
            foreach (var phase in _phases)
            {
                var errorsBefore = context.ErrorCount;
                phase.Run(context);
                if (phase.Kind == ValidationPhaseKind.D2 && context.ErrorCount > errorsBefore)
                {
                    return;
                }
            }
            if (context.Mode == ValidationMode.DryRun)
            {
                _dryRunPhase?.Run(context);
            }
        }

        // Build Plan stage (SPEC §D7, §N.5.5 item 4): turns the validated
        // workflow into the immutable CompiledPlan. Reached only when
        // validation produced no errors, and builds only when every stage
        // input exists — a pipeline without a registry snapshot or the
        // SG-11 provenance source stays inert, matching the stage-boundary
        // convention of the other stages.
        private CompiledPlan? BuildPlanStage(ValidationContext context)
        {
            if (_snapshot is null || _provenance is null ||
                context.ResolvedWorkflow is null || context.ExecutionOrder is null ||
                context.PortSuppliers is null || context.OutputTypes is null ||
                context.Effects is null || context.Approvals is null)
            {
                return null;
            }
            return new PlanBuilder(_snapshot, _provenance).Build(context);
        }

        private static ValidationReport BuildReport(ValidationContext context, CompiledPlan? plan)
        {
            // SPEC §D1 item 3: diagnostics are ordered by node document order, then
            // by code. Diagnostics that do not resolve to a document node — a null
            // nodeId or one naming no declared node — sort before node-bound ones,
            // and OrderBy/ThenBy keeps emission order between equal keys.
            var ordered = context.Diagnostics
                .OrderBy(d => context.NodeOrder(d.NodeId))
                .ThenBy(d => d.Code, StringComparer.Ordinal)
                .ToList();

            var truncated = ordered.Count > MaxDiagnostics;
            if (truncated)
            {
                ordered = ordered.GetRange(0, MaxDiagnostics);
            }

            var errors = new List<Diagnostic>();
            var warnings = new List<Diagnostic>();
            foreach (var diagnostic in ordered)
            {
                // The report has no third list: every non-Error severity lands in
                // Warnings.
                (diagnostic.Severity == Severity.Error ? errors : warnings).Add(diagnostic);
            }

            return new ValidationReport(
                !context.HasErrors,
                ReadOnlyCollections.CopyList(errors),
                ReadOnlyCollections.CopyList(warnings),
                truncated,
                plan,
                context.Mode == ValidationMode.DryRun
                    ? ReadOnlyCollections.CopyList(context.PlannedChanges)
                    : null);
        }
    }
}
