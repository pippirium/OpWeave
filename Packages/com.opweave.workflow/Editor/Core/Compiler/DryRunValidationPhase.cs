#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The D8 Dry Run phase (SPEC §D8): runs every active step's
    /// <see cref="IOperation.Preflight"/> in the D5 execution order without
    /// modifying the project, reports the precondition violations it finds,
    /// and collects the <see cref="PlannedChange"/> list the
    /// <see cref="ValidationReport"/> carries for a
    /// <see cref="ValidationMode.DryRun"/> request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inputs resolve like the run loop's SPEC §E5 read of the Plan's
    /// InputSources, except that a data-edge supplier has no produced value —
    /// no preceding step is executed — so the port is handed to the context
    /// as a pending value carrying its producing node id
    /// (<see cref="StepContext.IsPending"/>, SPEC §D8 item 1). Constants and
    /// Descriptor defaults supply their recorded (codec-resolved) value, a
    /// workflow-input binding reads the request's resolved execution inputs,
    /// and an unsupplied port stays <see cref="InputValue.Missing"/> — never
    /// a JSON <c>null</c> (SPEC §A0.1).
    /// </para>
    /// <para>
    /// Every returned diagnostic crosses into the report through
    /// <see cref="ValidationContext.Report"/> — an Error excludes the node it
    /// names from the remaining preflight calls (SPEC §D1 item 2) — and every
    /// returned <see cref="PlannedChange"/> is stamped with the step's node
    /// id, which the SG-06 <see cref="StepContext"/> deliberately withholds
    /// from the Operation. A thrown <see cref="StepContextException"/>
    /// contributes its carried diagnostics; any other exception is OW-RUN-006
    /// (SPEC §0.6 SG-06) without exception text, which may carry secrets.
    /// </para>
    /// <para>
    /// SPEC §D8 item 3 restricts the phase to Edit Mode on the main thread:
    /// the injected <see cref="IEditorState"/> probe answers the mode and
    /// the injected thread probe answers the main thread — the gate fails
    /// closed, so Play Mode, a background thread or a phase built without
    /// either probe (the Core-only configurations) all report OW-RUN-001
    /// and run no Preflight. The thread check precedes the probe read
    /// because the Editor flags it wraps are main-thread-only.
    /// </para>
    /// </remarks>
    internal sealed class DryRunValidationPhase : IValidationPhase
    {
        private readonly RegistrySnapshot _snapshot;
        private readonly IEditorState? _editorState;
        private readonly Func<bool>? _isMainThread;
        private readonly Func<DryRunStepRequest, StepContext> _contextFactory;

        /// <summary>
        /// Binds the phase to the registry the Resolve/D3 phases resolved
        /// against and to the environment seams SPEC §0.6 SG-06 delegates:
        /// <paramref name="contextFactory"/> builds the per-step
        /// <see cref="StepContext"/> (Core hosts no Unity implementation) —
        /// null selects the JSON-level <see cref="JsonStepContext"/> the Core
        /// run loop also defaults to — <paramref name="editorState"/>
        /// supplies the §D8 item 3 Edit-Mode gate and
        /// <paramref name="isMainThread"/> its main-thread gate; the
        /// UnityAdapter binds both to the live Editor. A phase without
        /// them refuses every Dry Run (fail closed, OW-RUN-001).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        internal DryRunValidationPhase(
            RegistrySnapshot snapshot,
            IEditorState? editorState = null,
            Func<DryRunStepRequest, StepContext>? contextFactory = null,
            Func<bool>? isMainThread = null)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _editorState = editorState;
            _isMainThread = isMainThread;
            _contextFactory = contextFactory ?? CreateJsonContext;
        }

        public ValidationPhaseKind Kind => ValidationPhaseKind.D8;

        public void Run(ValidationContext context)
        {
            // SPEC §D8 item 3: preflight runs on the Edit-Mode main thread
            // only, and the gate fails closed — an unbound probe cannot prove
            // the boundary, so it refuses too. The thread probe is asked
            // first: the Editor flags behind IsEditMode are main-thread-only.
            if (_isMainThread is null || _editorState is null ||
                !_isMainThread() || !_editorState.IsEditMode)
            {
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_001,
                    "The Editor is not in Edit Mode on the main thread."));
                return;
            }

            var order = context.ExecutionOrder;
            if (order is null)
            {
                // A D5 violation left no execution order to preflight; its
                // diagnostics are already reported.
                return;
            }

            var supplied = (context.ResolvedInputs ?? context.Inputs)?.ToToken() as JObject;
            for (var index = 0; index < order.Count; index++)
            {
                var node = order[index];
                if (!context.IsNodeActive(node.Id))
                {
                    continue;
                }
                PreflightNode(context, node, index, supplied);
            }
        }

        private void PreflightNode(
            ValidationContext context, NodeModel node, int index, JObject? supplied)
        {
            // A node the D3 phase could not resolve is already excluded by its
            // Error diagnostic; the defensive null checks keep a still-active
            // node whose snapshot entry lacks an implementation or descriptor
            // from becoming a null dereference — no published diagnostic names
            // that malformed-snapshot case.
            var registered = _snapshot.Find(node.Operation, node.Version);
            var operation = registered?.Implementation;
            var descriptor = registered?.Descriptor;
            if (registered is null || operation is null || descriptor is null)
            {
                return;
            }

            var inputs = new Dictionary<string, InputValue>(StringComparer.Ordinal);
            var pending = new Dictionary<string, string>(StringComparer.Ordinal);
            var suppliers =
                context.PortSuppliers is not null &&
                context.PortSuppliers.TryGetValue(node.Id, out var resolved)
                    ? resolved
                    : null;
            foreach (var port in descriptor.Inputs)
            {
                switch (suppliers is not null && suppliers.TryGetValue(port.Name, out var supplier)
                        ? supplier
                        : null)
                {
                    case PortSupplier.Edge edge:
                        // SPEC §D8 item 1: the producing step was not
                        // executed, so the value is pending — a placeholder
                        // the Operation checks through ctx.IsPending(port).
                        pending[port.Name] = edge.Node;
                        inputs[port.Name] = InputValue.Missing;
                        break;
                    case PortSupplier.Input input:
                        inputs[port.Name] =
                            supplied is not null &&
                            supplied.TryGetValue(input.InputId, out var value)
                                ? InputValue.Present(JsonValue.From(value))
                                : InputValue.Missing;
                        break;
                    case PortSupplier.Parameter parameter:
                        inputs[port.Name] = InputValue.Present(parameter.Value);
                        break;
                    case PortSupplier.Default @default:
                        inputs[port.Name] = InputValue.Present(@default.Value);
                        break;
                    default:
                        // PortSupplier.Absent, or a port whose supplier the
                        // D4 phase could not resolve uniquely — the port
                        // stays missing, never a JSON null (SPEC §A0.1).
                        inputs[port.Name] = InputValue.Missing;
                        break;
                }
            }

            StepContext stepContext;
            PreflightResult? preflight;
            try
            {
                stepContext = _contextFactory(new DryRunStepRequest(
                    index,
                    node.Id,
                    node.Operation,
                    node.Version,
                    descriptor,
                    registered.Origin,
                    ReadOnlyCollections.CopyMap(inputs),
                    TypeIdsOf(StaticTypedContext.StaticTypesFor(context, node, descriptor)),
                    TypeIdsOf(
                        context.OutputTypes is not null &&
                        context.OutputTypes.TryGetValue(node.Id, out var outputTypes)
                            ? outputTypes
                            : null),
                    ReadOnlyCollections.CopyMap(pending)));
                preflight = operation.Preflight(stepContext);
            }
            catch (StepContextException failure)
            {
                // A context conversion failure carries its own diagnostics
                // (SPEC §0.6 SG-06) — e.g. an unresolvable Ref input.
                ReportAll(context, node, failure.Diagnostics);
                return;
            }
            catch (Exception)
            {
                // SPEC §0.6 SG-06: operation exceptions surface as OW-RUN-006;
                // the exception text may carry secrets and never crosses.
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    $"Operation '{node.Operation}' threw an exception during Dry Run preflight.",
                    nodeId: node.Id));
                return;
            }

            if (preflight is null)
            {
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    "The Operation returned no Preflight result.",
                    nodeId: node.Id));
                return;
            }

            ReportAll(context, node, preflight.Diagnostics);
            var planned = preflight.PlannedChanges;
            for (var i = 0; i < planned.Count; i++)
            {
                if (planned[i] is null)
                {
                    continue;
                }
                // The node identity is deliberately outside the SG-06
                // context surface, so the phase stamps it: the Operation
                // predicts the kind/target/effect, the pipeline binds it to
                // the step it belongs to (SPEC §D8 item 2).
                context.PlannedChanges.Add(planned[i] with { NodeId = node.Id });
            }
        }

        private static void ReportAll(
            ValidationContext context, NodeModel node, IReadOnlyList<Diagnostic> diagnostics)
        {
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var diagnostic = diagnostics[i];
                if (diagnostic is null)
                {
                    continue;
                }
                context.Report(diagnostic.NodeId is not null
                    ? diagnostic
                    : diagnostic with { NodeId = node.Id });
            }
        }

        private static IReadOnlyDictionary<string, string> TypeIdsOf(
            IReadOnlyDictionary<string, TypeRef>? types)
        {
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            if (types is not null)
            {
                foreach (var pair in types)
                {
                    ids[pair.Key] = pair.Value.ToString();
                }
            }
            return ids;
        }

        // The SG-06 default: the Unity-free JSON context the Core run loop
        // also falls back to, over an inert §F7 recorder and §F6 sink — a
        // Dry Run owns no journal to write them to.
        private static StepContext CreateJsonContext(DryRunStepRequest request) =>
            new JsonStepContext(
                request.Inputs,
                new StepChangeRecorder(request.StepIndex, request.NodeId),
                new StepLogSink(
                    "dryrun",
                    request.StepIndex,
                    (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                pendingProducers: request.PendingProducers);
    }
}
