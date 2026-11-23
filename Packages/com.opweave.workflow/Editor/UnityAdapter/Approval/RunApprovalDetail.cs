#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Registry;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The approval detail of one AwaitingApproval Run (SPEC §H4 item 3,
    /// T-W3.4-02): the Run and the Plan identity the human approves
    /// (<see cref="PlanHash"/> — the approval is bound to exactly this Plan),
    /// the decision deadline derived from <c>approvalTimeoutMinutes</c>,
    /// one <see cref="ApprovalNodeDetail"/> per node whose effects asked,
    /// and one <see cref="UsedOperationDetail"/> per Plan step running a
    /// custom or external Operation — the approved Run executes every step,
    /// so the non-standard code it carries is identified for the whole Plan,
    /// not only for the ask nodes.
    /// </summary>
    public sealed class RunApprovalDetail
    {
        private RunApprovalDetail(
            string runId,
            string workflowId,
            string planHash,
            DateTimeOffset? deadlineUtc,
            IReadOnlyList<ApprovalNodeDetail> nodes,
            IReadOnlyList<UsedOperationDetail> usedOperations)
        {
            RunId = runId;
            WorkflowId = workflowId;
            PlanHash = planHash;
            DeadlineUtc = deadlineUtc;
            Nodes = nodes;
            UsedOperations = usedOperations;
        }

        /// <summary>The Run awaiting the decision.</summary>
        public string RunId { get; }

        /// <summary>The workflow the Run's Plan executes.</summary>
        public string WorkflowId { get; }

        /// <summary>
        /// The planHash the approval is bound to (SPEC §H4 item 3): the human
        /// approves exactly this Plan — a decision presented for any other
        /// hash is refused with OW-RUN-003.
        /// </summary>
        public string PlanHash { get; }

        /// <summary>
        /// The instant after which the approval expires (Cancelled,
        /// OW-PERM-003; SPEC §H4 item 3), or null when the Run is not tracked
        /// by the approval service's pending list.
        /// </summary>
        public DateTimeOffset? DeadlineUtc { get; }

        /// <summary>One detail per node the Plan's <c>approvals</c> names.</summary>
        public IReadOnlyList<ApprovalNodeDetail> Nodes { get; }

        /// <summary>
        /// The custom and external Operations the Run uses, with their
        /// sources — one detail per Plan step whose Operation is not
        /// Standard, in Plan order (SPEC §H4 item 3). An Operation the
        /// current Registry no longer resolves keeps an entry with a null
        /// origin and no source rather than vanishing.
        /// </summary>
        public IReadOnlyList<UsedOperationDetail> UsedOperations { get; }

        /// <summary>
        /// Builds the detail from the Run's Plan and the current Registry —
        /// the source table for each used Operation's origin and source
        /// (SPEC §H4 item 3). Nodes appear in the order their first ask
        /// does; used Operations in Plan order.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The Plan's <c>approvals</c> name a node no step carries — a Plan
        /// defect, detected by throwing.
        /// </exception>
        internal static RunApprovalDetail Build(
            string runId, CompiledPlan plan, RegistrySnapshot? registry, DateTimeOffset? deadlineUtc)
        {
            // The asked effects per node, aggregated in Plan order.
            var asked = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var approval in plan.Approvals)
            {
                if (!asked.TryGetValue(approval.NodeId, out var effects))
                {
                    effects = new List<string>();
                    asked[approval.NodeId] = effects;
                    order.Add(approval.NodeId);
                }
                effects.Add(approval.Effect);
            }

            // Every step's Operation resolved once: SPEC §H4 item 3 names
            // the custom and external Operations the whole Run uses — the
            // approved Plan executes them all, not only the ask nodes' —
            // each with its registration source. A Standard Operation is
            // the trusted set and is not listed; an Operation the current
            // Registry no longer resolves keeps an entry without a source
            // (the §E2 item 5 invalidation ends the Run anyway).
            var steps = new Dictionary<string, PlanStep>(StringComparer.Ordinal);
            var resolved = new Dictionary<string, RegisteredOperation?>(StringComparer.Ordinal);
            var used = new List<UsedOperationDetail>(plan.Steps.Count);
            foreach (var step in plan.Steps)
            {
                steps[step.NodeId] = step;
                var registered = registry?.Find(step.OperationId, step.Version);
                resolved[step.NodeId] = registered;
                if (registered is null || registered.Origin != OperationOrigin.Standard)
                {
                    used.Add(new UsedOperationDetail(
                        step.NodeId,
                        step.OperationId,
                        step.Version,
                        registered?.Origin,
                        registered?.AssemblyName,
                        registered?.SourceFile,
                        registered?.PackageId,
                        registered?.ResolvedCommit));
                }
            }

            var nodes = new List<ApprovalNodeDetail>(order.Count);
            foreach (var nodeId in order)
            {
                if (!steps.TryGetValue(nodeId, out var step))
                {
                    throw new InvalidOperationException(
                        $"The Plan's approvals name node '{nodeId}', which no step carries — a Plan defect.");
                }
                var registered = resolved[nodeId];
                nodes.Add(new ApprovalNodeDetail(
                    nodeId,
                    step.OperationId,
                    step.Version,
                    ReadOnlyCollections.CopyList(step.Effects),
                    ReadOnlyCollections.CopyList(asked[nodeId]),
                    registered?.Origin,
                    registered?.AssemblyName,
                    registered?.SourceFile,
                    registered?.PackageId,
                    registered?.ResolvedCommit,
                    ConstantInputs(step),
                    BoundInputs(step),
                    ResolvedInputs(step, plan.Inputs)));
            }
            return new RunApprovalDetail(
                runId, plan.WorkflowId, plan.PlanHash, deadlineUtc, nodes,
                ReadOnlyCollections.CopyList(used));
        }

        // The literal port values the Plan fixes — Constant and
        // Descriptor-default inputs verbatim: the paths, the full code and
        // the package identifiers the human reads (SPEC §H4 item 3). A
        // credential-bearing Git URL cannot be here: SPEC §G8.1 rejects it
        // with OW-OP-006 before the Plan exists.
        private static IReadOnlyDictionary<string, JsonValue> ConstantInputs(PlanStep step)
        {
            var inputs = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            foreach (var pair in step.Inputs)
            {
                switch (pair.Value)
                {
                    case Constant constant:
                        inputs[pair.Key] = constant.Value;
                        break;
                    case Default @default:
                        inputs[pair.Key] = @default.Value;
                        break;
                }
            }
            return ReadOnlyCollections.CopyMap(inputs);
        }

        // The ports fed by a workflow input or an upstream step — the
        // binding marker paired with the resolved value ResolvedInputs
        // carries for the workflow-input-bound ones.
        private static IReadOnlyDictionary<string, string> BoundInputs(PlanStep step)
        {
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in step.Inputs)
            {
                switch (pair.Value)
                {
                    case FromInput input:
                        inputs[pair.Key] = "input:" + input.InputId;
                        break;
                    case FromStep source:
                        inputs[pair.Key] = $"step[{source.StepIndex}].{source.Port}";
                        break;
                }
            }
            return ReadOnlyCollections.CopyMap(inputs);
        }

        // The value every workflow-input-bound port executes with, read from
        // the Plan the approval is bound to (SPEC §H4 item 3): the bound
        // input's member of the Plan's execution inputs — the same
        // resolution the run loop's ValueStore applies (SPEC §E5). A supplied
        // member is the value verbatim — an explicit JSON null stays a
        // present value, never Missing (SPEC §A0.1) — and an absent member
        // is Missing. The Plan fixes these values (execution inputs are a
        // planHash input), so the presented value is exactly the approved
        // one. A credential-bearing Git URL cannot be here either: §G8.1
        // rejects it before the Plan exists, and unity.package.add's id
        // port is ConstantOnly, so no workflow-input binding can feed one.
        private static IReadOnlyDictionary<string, InputValue> ResolvedInputs(
            PlanStep step, JsonValue executionInputs)
        {
            var supplied = executionInputs.ToToken() as JObject;
            var inputs = new Dictionary<string, InputValue>(StringComparer.Ordinal);
            foreach (var pair in step.Inputs)
            {
                if (pair.Value is FromInput input)
                {
                    inputs[pair.Key] =
                        supplied is not null &&
                        supplied.TryGetValue(input.InputId, out var value)
                            ? InputValue.Present(JsonValue.From(value))
                            : InputValue.Missing;
                }
            }
            return ReadOnlyCollections.CopyMap(inputs);
        }
    }
}
