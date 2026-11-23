#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.Operations
{
    /// <summary>
    /// The <c>core.start</c> standard Operation (SPEC §I.1): the single entry
    /// point of every workflow — read-only, <see cref="ExecutionMode.Immediate"/>
    /// and <see cref="Operations.Idempotency.Idempotent"/>, with no inputs and
    /// no outputs. The exactly-one-per-workflow invariant is the Validator's
    /// (SPEC §D5 item 1, OW-VAL-001), not this class's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IOperation.Execute"/> completes immediately: the start node
    /// exists so the graph has a single anchor the Runner's Transitions walk
    /// from; it performs no Unity change beyond the declared
    /// <see cref="EffectNames.Read"/> effect and records nothing.
    /// </para>
    /// <para>
    /// Descriptor fields outside the §0.6 SG-05 fluent set —
    /// <see cref="OperationDescriptor.DisplayName"/>,
    /// <see cref="OperationDescriptor.Description"/>,
    /// <see cref="OperationDescriptor.Category"/>,
    /// <see cref="OperationDescriptor.Keywords"/> and
    /// <see cref="OperationDescriptor.Examples"/> — are set on the record
    /// itself (SPEC §0.6 SG-05).
    /// </para>
    /// </remarks>
    [OpWeaveOperation("core.start", 1)]
    public sealed class StartOperation : IOperation
    {
        /// <summary>The complete example workflow of SPEC §C1 item 4 / §C8: a
        /// full WF document — never a node fragment — that the static
        /// Validator accepts. The minimal legal workflow is a single start
        /// node: until more standard Operations land, no second node can be
        /// wired without referencing an unregistered ID.</summary>
        private const string ExampleWorkflowJson =
            "{\"schemaVersion\":\"1.0\",\"id\":\"example.core-start\"," +
            "\"nodes\":[{\"id\":\"start\",\"operation\":\"core.start\",\"version\":1}]," +
            "\"controlEdges\":[]}";

        /// <inheritdoc/>
        public OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
            Array.Empty<Diagnostic>();

        /// <inheritdoc/>
        public PreflightResult Preflight(StepContext ctx) =>
            new PreflightResult(
                Array.Empty<Diagnostic>(),
                Array.Empty<PlannedChange>(),
                ParseJson("{}"));

        /// <inheritdoc/>
        public StepResult Execute(StepContext ctx) => StepResult.Succeeded();

        /// <inheritdoc/>
        public StepResult Poll(StepContext ctx) =>
            throw new NotSupportedException("Immediate operations are never polled.");

        /// <inheritdoc/>
        public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
            throw new NotSupportedException("Idempotent operations are never reconciled.");

        /// <inheritdoc/>
        public void Cancel(StepContext ctx)
        {
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define("core.start", 1)
            .Summary("The single entry point of a workflow; every workflow contains exactly one.")
            .Ja("開始", "Workflow の実行開始点。Workflow にはちょうど1つ置く。", "開始", "スタート", "入口")
            .Effects(EffectNames.Read)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Start",
                Description =
                    "Marks where workflow execution begins. A valid workflow contains " +
                    "exactly one core.start node with no incoming control edges; the " +
                    "node takes no inputs and produces no outputs.",
                Category = "Core",
                Tags = new[] { "workflow" },
                Keywords = new[] { "start", "entry", "begin" },
                Examples = new[]
                {
                    new OperationExample(
                        "A workflow with a single Start node",
                        ParseJson(ExampleWorkflowJson)),
                },
            };

        private static JsonValue ParseJson(string text)
        {
            var parsed = JsonValue.Parse(text);
            if (!parsed.Ok || parsed.Value is null)
            {
                throw new InvalidOperationException(
                    "The core.start example workflow JSON did not parse.");
            }
            return parsed.Value;
        }
    }
}
