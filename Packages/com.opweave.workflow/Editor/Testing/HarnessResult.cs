#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;

namespace OpWeave.Testing
{
    /// <summary>
    /// What one <see cref="OperationTestHarness"/> run reports (SPEC §M.6 item 1):
    /// the Operation's outputs, the SPEC §F7 changes it recorded, the
    /// diagnostics that crossed the step, the SPEC §F6 log entries it wrote and
    /// the SPEC §F2 records its journal holds — the same shapes the Runner
    /// produces for the same step, so a contract test compares the harness
    /// result against a normal run instead of re-deriving it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="StepState"/> is the journaled state, derived from
    /// <see cref="Journal"/> exactly as the SPEC §F2 fold derives a step's
    /// recorded state: a <c>step_succeeded</c> record makes it
    /// <see cref="StepState.Succeeded"/>, <c>step_failed</c> makes it
    /// <see cref="StepState.Failed"/>, <c>step_waiting</c> makes it
    /// <see cref="StepState.Waiting"/>, and a journal that ends
    /// at the write-ahead <c>step_started</c> record leaves it
    /// <see cref="StepState.Running"/> — which is what
    /// <see cref="OperationTestHarness.RunInterrupted"/> produces at both
    /// interrupt points, while <see cref="Outcome"/> and <see cref="Changes"/>
    /// show whether the effect itself already happened.
    /// </para>
    /// <para>
    /// <see cref="Outputs"/> are the raw per-port values the Operation wrote
    /// through <see cref="StepContext.SetOutput{T}"/>, read from the same
    /// internal seam the run loop reads after <c>Execute</c> (SPEC §E5). They
    /// are not run through the Plan-bound output validation — a harness run has
    /// no Plan, no node and no ValueStore, so there is no output bound to check
    /// and no step to store the values in.
    /// </para>
    /// </remarks>
    public sealed class HarnessResult
    {
        internal HarnessResult(
            string operationId,
            int version,
            StepState stepState,
            StepResultKind? outcome,
            InterruptPoint? interruptedAt,
            IReadOnlyDictionary<string, JsonValue> outputs,
            IReadOnlyList<ChangeRecord> changes,
            IReadOnlyList<Diagnostic> diagnostics,
            IReadOnlyList<JournalRecord> journal,
            IReadOnlyList<TrackedRunResource> resources)
        {
            OperationId = operationId;
            Version = version;
            StepState = stepState;
            Outcome = outcome;
            InterruptedAt = interruptedAt;
            Outputs = outputs;
            Changes = changes;
            Diagnostics = diagnostics;
            Journal = journal;
            Resources = resources;
        }

        /// <summary>The Operation ID the run asked for.</summary>
        public string OperationId { get; }

        /// <summary>The Operation version the run asked for.</summary>
        public int Version { get; }

        /// <summary>
        /// The step's journaled state (SPEC §E1) as the records in
        /// <see cref="Journal"/> show it. <see cref="StepState.Running"/>
        /// means the write-ahead record landed and no outcome record followed —
        /// an interrupted run, or a crash at the same point in a real Run.
        /// </summary>
        public StepState StepState { get; }

        /// <summary>
        /// The <see cref="StepResult.Kind"/> <c>Execute</c> returned, or
        /// <see langword="null"/> when <c>Execute</c> never ran: an unknown
        /// Operation, a failed input, a refused Preflight or
        /// <see cref="InterruptPoint.BeforeExecute"/>.
        /// </summary>
        public StepResultKind? Outcome { get; }

        /// <summary>
        /// The interrupt point of a <see cref="OperationTestHarness.RunInterrupted"/>
        /// run; <see langword="null"/> for a full <see cref="OperationTestHarness.Run"/>.
        /// </summary>
        public InterruptPoint? InterruptedAt { get; }

        /// <summary>True when the journaled state is <see cref="StepState.Succeeded"/>.</summary>
        public bool Ok => StepState == StepState.Succeeded;

        /// <summary>
        /// The raw outputs the Operation wrote, keyed by port; empty when
        /// <c>Execute</c> never ran or wrote nothing.
        /// </summary>
        public IReadOnlyDictionary<string, JsonValue> Outputs { get; }

        /// <summary>The SPEC §F7 change records the Operation made, in call order.</summary>
        public IReadOnlyList<ChangeRecord> Changes { get; }

        /// <summary>
        /// The diagnostics that crossed the step boundary (SPEC §0.4): the
        /// error-severity Preflight findings, the failure a
        /// <see cref="StepResultKind.Failed"/> carried, the OW-RUN-006 of a
        /// thrown exception, or the harness's own OW-OP-001/OW-OP-005/OW-RUN-007
        /// refusal. Empty on a clean run.
        /// </summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        /// <summary>
        /// The SPEC §F2 records this run's journal holds in write order:
        /// <c>step_started</c> before any side effect, then the outcome record
        /// the step reached, plus the Buffered <c>log</c> records the Operation
        /// wrote through <see cref="StepContext.Log"/> in between — the §F6 log
        /// lines are the journal records, exactly as in a Run, where the journal
        /// is the single source of truth. Every record encodes through
        /// <see cref="JournalCodec.WriteRecord"/> exactly as a Run journal line
        /// does.
        /// </summary>
        public IReadOnlyList<JournalRecord> Journal { get; }

        /// <summary>
        /// The SPEC §E6 item 4 Run-resource acquisitions the synthetic Run's
        /// tracked list held when the run reported — the same state the §F4
        /// recovery fold rebuilds from the journaled resource records after a
        /// crash. The entries are the live tracked objects a reconciliation
        /// may judge, not a detached projection; an interrupted Run keeps the
        /// acquisitions the journaled resource records describe until a
        /// <c>resource_released</c> record frees them. Internal: the element
        /// type is Core-internal.
        /// </summary>
        internal IReadOnlyList<TrackedRunResource> Resources { get; }

        /// <summary>A compact summary for assertion messages.</summary>
        public override string ToString()
        {
            var outcome = Outcome is null ? "not executed" : Outcome.Value.ToString();
            var outputNames = new List<string>(Outputs.Keys);
            outputNames.Sort(StringComparer.Ordinal);
            var codes = new List<string>();
            for (var i = 0; i < Diagnostics.Count; i++)
            {
                codes.Add(Diagnostics[i].Code);
            }
            var types = new List<string>();
            for (var i = 0; i < Journal.Count; i++)
            {
                types.Add(JournalRecordTypes.Of(Journal[i].Type).WireName);
            }
            return
                $"{OperationId} v{Version}: state={StepState}, outcome={outcome}" +
                (InterruptedAt is null ? string.Empty : $", interrupted={InterruptedAt}") +
                $", outputs=[{string.Join(", ", outputNames)}]" +
                $", changes={Changes.Count}" +
                $", diagnostics=[{string.Join(", ", codes)}]" +
                $", journal=[{string.Join(", ", types)}]";
        }
    }
}
