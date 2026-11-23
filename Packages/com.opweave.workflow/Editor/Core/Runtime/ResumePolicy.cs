#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The single implementation of the SPEC §F5 re-execution table (R2):
    /// whether a step whose StepStarted journal record has no completion
    /// record may be re-executed. The §E7 retry path and the §F4 recovery
    /// engine both decide through <see cref="Decide"/> — no other code path
    /// may decide re-execution, and <see cref="CanReexecute"/> is a thin
    /// wrapper over it, never a second rule set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decision is keyed on <see cref="PlanStep.Idempotency"/>, the
    /// Idempotency contract the Plan recorded: the Plan is the immutable
    /// authority once recovery has revalidated the descriptor and
    /// implementation fingerprints (SPEC §F4 item 3, §D0.3). A step
    /// declaring <see cref="Idempotency.Unsafe"/> — the SPEC §F0 item 4
    /// default every custom/external Operation starts from — always decides
    /// <see cref="ResumeDecision.NeedsReview"/>.
    /// </para>
    /// <para>
    /// For <see cref="Idempotency.Reconcilable"/> steps <see cref="Decide"/>
    /// calls <see cref="IOperation.Reconcile"/> itself with the Preflight
    /// snapshot persisted on the StepStarted record:
    /// <see cref="ReconcileResult.Completed"/> adopts the restored outputs,
    /// <see cref="ReconcileResult.NotApplied"/> re-executes, and
    /// <see cref="ReconcileResult.Unknown"/> is NeedsReview. Every
    /// undecidable residue falls to NeedsReview as well: a missing snapshot
    /// (no Preflight evidence was persisted), a Reconcile call that throws
    /// or returns <see langword="null"/>, and any Idempotency value outside
    /// the declared enum. Uncertain recovery never becomes automatic
    /// success or automatic re-execution (SPEC §F5, AGENTS invariant).
    /// </para>
    /// </remarks>
    public static class ResumePolicy
    {
        /// <summary>
        /// Applies the SPEC §F5 table to one step left with only a
        /// StepStarted record — or consulted for the §E7 retry verdict —
        /// returning the single decision the Runner may act on.
        /// </summary>
        /// <param name="step">
        /// The Plan step; its recorded <see cref="PlanStep.Idempotency"/>
        /// selects the table row.
        /// </param>
        /// <param name="op">
        /// The Operation implementation, used only to call
        /// <see cref="IOperation.Reconcile"/> on a Reconcilable row.
        /// </param>
        /// <param name="ctx">The step context handed through to Reconcile.</param>
        /// <param name="snapshot">
        /// The Preflight snapshot persisted on StepStarted;
        /// <see langword="null"/> when none was recorded.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="step"/>, <paramref name="op"/> or
        /// <paramref name="ctx"/> is null — a caller bug, not a runtime
        /// outcome.
        /// </exception>
        /// <remarks>
        /// The Runner obtains the restored outputs an AdoptOutputs verdict
        /// carries from the internal overload below — the same single §F5
        /// implementation and the same single Reconcile call — so recovery
        /// never probes the Operation a second time.
        /// </remarks>
        public static ResumeDecision Decide(
            PlanStep step, IOperation op, StepContext ctx, JsonValue? snapshot) =>
            Decide(step, op, ctx, snapshot, out _);

        /// <summary>
        /// The single §F5 implementation the public <see cref="Decide"/> and
        /// <see cref="CanReexecute"/> delegate to; <paramref name="adoptedOutputs"/>
        /// receives the restored outputs a <see cref="ResumeDecision.AdoptOutputs"/>
        /// verdict carries and is <see langword="null"/> for every other
        /// outcome. Core-internal so the Runner can consume the single
        /// Reconcile verdict without a second probe; the public surface stays
        /// the SPEC §E7 one.
        /// </summary>
        /// <param name="adoptedOutputs">
        /// The <see cref="ReconcileResult.Completed.Outputs"/> instance the
        /// single Reconcile call returned — non-null exactly when the result
        /// is <see cref="ResumeDecision.AdoptOutputs"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="step"/>, <paramref name="op"/> or
        /// <paramref name="ctx"/> is null — a caller bug, not a runtime
        /// outcome.
        /// </exception>
        internal static ResumeDecision Decide(
            PlanStep step, IOperation op, StepContext ctx, JsonValue? snapshot,
            out IReadOnlyDictionary<string, JsonValue>? adoptedOutputs)
        {
            adoptedOutputs = null;
            if (step is null)
            {
                throw new ArgumentNullException(nameof(step));
            }
            if (op is null)
            {
                throw new ArgumentNullException(nameof(op));
            }
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }

            switch (step.Idempotency)
            {
                case Idempotency.Idempotent:
                    return ResumeDecision.Reexecute;
                case Idempotency.Reconcilable:
                    return DecideReconcilable(op, ctx, snapshot, out adoptedOutputs);
                case Idempotency.Unsafe:
                default:
                    return ResumeDecision.NeedsReview;
            }
        }

        /// <summary>
        /// The SPEC §E7 retry gate as a thin wrapper over
        /// <see cref="Decide"/>: automatic re-execution is allowed only on a
        /// <see cref="ResumeDecision.Reexecute"/> verdict.
        /// <see cref="ResumeDecision.AdoptOutputs"/> is not re-execution —
        /// the caller reads <see cref="Decide"/> for the adopted-outputs
        /// path — and <see cref="ResumeDecision.NeedsReview"/> never permits
        /// a retry.
        /// </summary>
        public static bool CanReexecute(
            PlanStep step, IOperation op, StepContext ctx, JsonValue? snapshot) =>
            Decide(step, op, ctx, snapshot, out _) == ResumeDecision.Reexecute;

        private static ResumeDecision DecideReconcilable(
            IOperation op, StepContext ctx, JsonValue? snapshot,
            out IReadOnlyDictionary<string, JsonValue>? adoptedOutputs)
        {
            adoptedOutputs = null;
            if (snapshot is null)
            {
                return ResumeDecision.NeedsReview;
            }

            ReconcileResult verdict;
            try
            {
                verdict = op.Reconcile(ctx, snapshot);
            }
            catch (Exception)
            {
                return ResumeDecision.NeedsReview;
            }

            switch (verdict)
            {
                case ReconcileResult.Completed { Outputs: null }:
                    return ResumeDecision.NeedsReview;
                case ReconcileResult.Completed completed:
                    adoptedOutputs = completed.Outputs;
                    return ResumeDecision.AdoptOutputs;
                case ReconcileResult.NotApplied _:
                    return ResumeDecision.Reexecute;
                default:
                    return ResumeDecision.NeedsReview;
            }
        }
    }
}
