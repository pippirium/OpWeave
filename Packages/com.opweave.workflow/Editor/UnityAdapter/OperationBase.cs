#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The convenience base class SPEC §C2 assigns to OpWeave.UnityAdapter
    /// (SPEC §0.5): implements <see cref="IOperation"/> with the defaults the
    /// lifecycle allows, so a standard, custom or external Operation writes
    /// only <see cref="Descriptor"/> and <see cref="Execute"/> — and whatever
    /// of <see cref="ValidateStatic"/>, <see cref="Preflight"/>,
    /// <see cref="Poll"/>, <see cref="Reconcile"/> and <see cref="Cancel"/> its
    /// declared <see cref="OperationDescriptor.ExecutionMode"/>/
    /// <see cref="OperationDescriptor.Idempotency"/> make meaningful.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defaults mirror the SPEC §C2 lifecycle exactly:
    /// <see cref="Poll"/> and <see cref="Cancel"/> are meaningful only for a
    /// <see cref="ExecutionMode.Waitable"/> Operation and
    /// <see cref="Reconcile"/> only for an
    /// <see cref="Idempotency.Reconcilable"/> one — the Runner and the §F4
    /// recovery engine never call them otherwise, so an unimplemented
    /// <see cref="Poll"/> on a Waitable Operation is an Operation bug the
    /// step executor reports as OW-RUN-006 (SPEC §C2 item 8), and an
    /// unimplemented <see cref="Reconcile"/> answers
    /// <see cref="ReconcileResult.Unknown"/>, which recovery records as
    /// NeedsReview rather than guessing an outcome (SPEC §F5).
    /// </para>
    /// <para>
    /// Instances hold no state (SPEC §C2 item 4): a Waitable Operation keeps
    /// what must survive a domain reload only in the
    /// <see cref="StepResult.Waiting"/> resume token, read back through
    /// <see cref="StepContext.ResumeToken"/>.
    /// </para>
    /// <para>
    /// What it changes: a Scene change is made through a Unity Undo API inside
    /// the per-step group the Runner opened and closed around this call — an
    /// Operation opens no group of its own (SPEC §G3 items 1–2, spelled out on
    /// <see cref="EditorUndoScope"/>); creating, moving or deleting an asset is
    /// not undoable, so such an Operation declares <c>Undo = None</c> or
    /// <c>Undo = Partial</c> and reports the change through
    /// <see cref="StepContext.Changes"/> instead (SPEC §G3 item 3, §F7).
    /// </para>
    /// </remarks>
    public abstract class OperationBase : IOperation
    {
        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        /// <inheritdoc cref="IOperation.Descriptor"/>
        public abstract OperationDescriptor Descriptor { get; }

        /// <summary>
        /// No Unity-independent parameter findings by default (SPEC §C2);
        /// override to inspect <see cref="StaticContext.Parameters"/>.
        /// </summary>
        public virtual IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
            Array.Empty<Diagnostic>();

        /// <summary>
        /// The minimal legal Preflight (SPEC §C2 item 7): no findings, no
        /// predicted changes and an empty snapshot. Override to check
        /// preconditions, predict the Dry Run's
        /// <see cref="PreflightResult.PlannedChanges"/> and return the §F5
        /// snapshot — a Reconcilable Operation's reconciliation evidence is
        /// only as good as what this records.
        /// </summary>
        public virtual PreflightResult Preflight(StepContext ctx) =>
            new PreflightResult(
                Array.Empty<Diagnostic>(),
                Array.Empty<PlannedChange>(),
                s_emptySnapshot);

        /// <inheritdoc cref="IOperation.Execute"/>
        public abstract StepResult Execute(StepContext ctx);

        /// <summary>
        /// Meaningful only for a <see cref="ExecutionMode.Waitable"/>
        /// Operation (SPEC §C2 item 3): the default throws because the Runner
        /// calls it only on a Waiting step, where a missing implementation is
        /// an Operation bug, not a runtime condition.
        /// </summary>
        /// <exception cref="NotSupportedException">Always.</exception>
        public virtual StepResult Poll(StepContext ctx) =>
            throw new NotSupportedException(
                "Poll is meaningful only for a Waitable Operation (SPEC §C2).");

        /// <summary>
        /// Meaningful only for an <see cref="Idempotency.Reconcilable"/>
        /// Operation (SPEC §F5): the default answers
        /// <see cref="ReconcileResult.Unknown"/>, which the recovery engine
        /// records as NeedsReview — the fail-closed verdict the SPEC
        /// prescribes whenever evidence is insufficient — instead of letting
        /// a missing implementation masquerade as either outcome.
        /// </summary>
        public virtual ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
            new ReconcileResult.Unknown(
                "The Operation does not implement Reconcile (SPEC §F5).");

        /// <summary>
        /// Best-effort cancellation of a Waiting step (SPEC §C2); a Waitable
        /// Operation overrides it to unwind what it started. The default is
        /// deliberately a no-op so an Immediate Operation — which is never
        /// cancelled — stays a legal implementation.
        /// </summary>
        public virtual void Cancel(StepContext ctx)
        {
        }
    }
}
