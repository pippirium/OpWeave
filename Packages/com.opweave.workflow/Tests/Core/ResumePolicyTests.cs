#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for <see cref="ResumePolicy"/> — the single SPEC §F5
    /// re-execution table (T-W1.8-02). Every table cell is exercised with
    /// dummy operations: Idempotent re-executes, Reconcilable defers to
    /// Reconcile (Completed → AdoptOutputs, NotApplied → Reexecute,
    /// Unknown → NeedsReview) and Unsafe is NeedsReview; each undecidable
    /// residue also ends in NeedsReview, never in automatic success or
    /// re-execution.
    /// </summary>
    internal sealed class ResumePolicyTests
    {
        private static PlanStep Step(Idempotency idempotency) =>
            new PlanStep(
                0, "step", "test.dummy.resume", 1,
                new string('d', 64), new string('e', 64),
                new Dictionary<string, InputSource>(),
                new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                30, new RetryPolicy(), idempotency);

        private static OperationContractFixtures.Step Ctx() =>
            new OperationContractFixtures.Step(
                new OperationContractFixtures.Static("step"));

        private static JsonValue Snapshot(string text) =>
            OperationContractFixtures.Json(text);

        // ---- F5 table: Idempotent row ----

        [Test]
        public void Decide_IdempotentStep_ReexecutesWithoutReconcile()
        {
            var op = new ScriptedOperation(Idempotency.Idempotent);
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Idempotent), op, Ctx(), snapshot: null);
            Assert.AreEqual(ResumeDecision.Reexecute, decision);
            Assert.AreEqual(0, op.ReconcileCalls,
                "An Idempotent step must never consult Reconcile.");
        }

        [Test]
        public void Decide_IdempotentStepWithSnapshot_StillReexecutes()
        {
            var op = new ScriptedOperation(Idempotency.Idempotent);
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Idempotent), op, Ctx(), Snapshot("{\"k\":1}"));
            Assert.AreEqual(ResumeDecision.Reexecute, decision);
            Assert.AreEqual(0, op.ReconcileCalls);
        }

        // ---- F5 table: Reconcilable row ----

        [Test]
        public void Decide_ReconcilableCompleted_AdoptsOutputs()
        {
            var op = new ScriptedOperation(
                Idempotency.Reconcilable,
                new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        ["marker"] = Snapshot("\"created\""),
                    }));
            var snapshot = Snapshot("{\"applied\":true}");
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), op, Ctx(), snapshot);
            Assert.AreEqual(ResumeDecision.AdoptOutputs, decision);
            Assert.AreEqual(1, op.ReconcileCalls);
            Assert.AreSame(snapshot, op.LastSnapshot,
                "The persisted Preflight snapshot must reach Reconcile.");
        }

        [Test]
        public void Decide_ReconcilableNotApplied_Reexecutes()
        {
            var op = new ScriptedOperation(
                Idempotency.Reconcilable, new ReconcileResult.NotApplied());
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), op, Ctx(),
                Snapshot("{\"applied\":false}"));
            Assert.AreEqual(ResumeDecision.Reexecute, decision);
            Assert.AreEqual(1, op.ReconcileCalls);
        }

        [Test]
        public void Decide_ReconcilableUnknown_NeedsReview()
        {
            var op = new ScriptedOperation(
                Idempotency.Reconcilable,
                new ReconcileResult.Unknown("Evidence is insufficient."));
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), op, Ctx(), Snapshot("{}"));
            Assert.AreEqual(ResumeDecision.NeedsReview, decision);
        }

        [Test]
        public void Decide_ReconcilableWithoutSnapshot_NeedsReview()
        {
            var op = new ScriptedOperation(
                Idempotency.Reconcilable, new ReconcileResult.NotApplied());
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), op, Ctx(), snapshot: null);
            Assert.AreEqual(ResumeDecision.NeedsReview, decision,
                "No persisted Preflight evidence means no reconciliation.");
            Assert.AreEqual(0, op.ReconcileCalls);
        }

        [Test]
        public void Decide_ReconcilableThrowingReconcile_NeedsReview()
        {
            var op = new ScriptedOperation(Idempotency.Reconcilable)
            {
                OnReconcile = (ctx, snapshot) =>
                    throw new InvalidOperationException("Fixture failure."),
            };
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), op, Ctx(), Snapshot("{}"));
            Assert.AreEqual(ResumeDecision.NeedsReview, decision,
                "A Reconcile that cannot answer leaves the outcome undecidable.");
        }

        [Test]
        public void Decide_ReconcilableNullVerdict_NeedsReview()
        {
            var op = new ScriptedOperation(Idempotency.Reconcilable)
            {
                OnReconcile = (ctx, snapshot) => null!,
            };
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), op, Ctx(), Snapshot("{}"));
            Assert.AreEqual(ResumeDecision.NeedsReview, decision);
        }

        // ---- Internal overload: the restored outputs ride the one verdict ----

        [Test]
        public void Decide_InternalCompleted_ReturnsTheExactRestoredOutputs()
        {
            var marker = Snapshot("\"created\"");
            var count = Snapshot("3");
            var op = new ScriptedOperation(
                Idempotency.Reconcilable,
                new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        ["marker"] = marker,
                        ["count"] = count,
                    }));
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), op, Ctx(),
                Snapshot("{\"applied\":true}"), out var adopted);
            Assert.AreEqual(ResumeDecision.AdoptOutputs, decision);
            Assert.AreEqual(1, op.ReconcileCalls,
                "The adopted outputs come from the one Reconcile call Decide made.");
            Assert.IsNotNull(adopted);
            Assert.AreEqual(2, adopted!.Count);
            Assert.AreSame(marker, adopted["marker"]);
            Assert.AreSame(count, adopted["count"]);
        }

        [Test]
        public void Decide_InternalNotAppliedAndUnknown_LeaveOutputsNull()
        {
            var notApplied = new ScriptedOperation(
                Idempotency.Reconcilable, new ReconcileResult.NotApplied());
            var unknown = new ScriptedOperation(
                Idempotency.Reconcilable,
                new ReconcileResult.Unknown("Evidence is insufficient."));
            var snapshot = Snapshot("{}");

            Assert.AreEqual(ResumeDecision.Reexecute, ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), notApplied, Ctx(), snapshot,
                out var notAppliedOutputs));
            Assert.IsNull(notAppliedOutputs);
            Assert.AreEqual(ResumeDecision.NeedsReview, ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), unknown, Ctx(), snapshot,
                out var unknownOutputs));
            Assert.IsNull(unknownOutputs);
        }

        [Test]
        public void Decide_InternalNonAdoptOutcomes_LeaveOutputsNull()
        {
            var snapshot = Snapshot("{}");

            Assert.AreEqual(ResumeDecision.Reexecute, ResumePolicy.Decide(
                Step(Idempotency.Idempotent),
                new ScriptedOperation(Idempotency.Idempotent),
                Ctx(), snapshot, out var idempotentOutputs));
            Assert.IsNull(idempotentOutputs);

            Assert.AreEqual(ResumeDecision.NeedsReview, ResumePolicy.Decide(
                Step(Idempotency.Unsafe),
                new ScriptedOperation(Idempotency.Unsafe),
                Ctx(), snapshot, out var unsafeOutputs));
            Assert.IsNull(unsafeOutputs);

            Assert.AreEqual(ResumeDecision.NeedsReview, ResumePolicy.Decide(
                Step(Idempotency.Reconcilable),
                new ScriptedOperation(Idempotency.Reconcilable),
                Ctx(), null, out var noSnapshotOutputs));
            Assert.IsNull(noSnapshotOutputs);
        }

        [Test]
        public void Decide_ReconcilableCompletedWithoutOutputs_NeedsReview()
        {
            var op = new ScriptedOperation(Idempotency.Reconcilable)
            {
                OnReconcile = (ctx, snapshot) =>
                    new ReconcileResult.Completed(null!),
            };
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Reconcilable), op, Ctx(), Snapshot("{}"),
                out var adopted);
            Assert.AreEqual(ResumeDecision.NeedsReview, decision,
                "A Completed verdict carrying no outputs cannot be adopted.");
            Assert.IsNull(adopted);
        }

        [Test]
        public void Decide_PublicAndInternalOverloads_ReturnTheSameVerdict()
        {
            var snapshot = Snapshot("{}");
            var rows = new (PlanStep Step, IOperation Op, JsonValue? Snapshot)[]
            {
                (Step(Idempotency.Idempotent),
                    new ScriptedOperation(Idempotency.Idempotent), null),
                (Step(Idempotency.Reconcilable),
                    new ScriptedOperation(
                        Idempotency.Reconcilable,
                        new ReconcileResult.Completed(
                            new Dictionary<string, JsonValue>(
                                StringComparer.Ordinal))),
                    snapshot),
                (Step(Idempotency.Reconcilable),
                    new ScriptedOperation(
                        Idempotency.Reconcilable, new ReconcileResult.NotApplied()),
                    snapshot),
                (Step(Idempotency.Reconcilable),
                    new ScriptedOperation(
                        Idempotency.Reconcilable,
                        new ReconcileResult.Unknown("Evidence is insufficient.")),
                    snapshot),
                (Step(Idempotency.Reconcilable),
                    new ScriptedOperation(Idempotency.Reconcilable), null),
                (Step(Idempotency.Unsafe),
                    new ScriptedOperation(Idempotency.Unsafe), snapshot),
                (Step((Idempotency)99),
                    new ScriptedOperation(Idempotency.Unsafe), snapshot),
            };
            foreach (var (step, op, snap) in rows)
            {
                Assert.AreEqual(
                    ResumePolicy.Decide(step, op, Ctx(), snap),
                    ResumePolicy.Decide(step, op, Ctx(), snap, out _),
                    $"Public and internal Decide disagree for {step.Idempotency}.");
            }
        }

        // ---- F5 table: Unsafe row + F0 item 4 default ----

        [Test]
        public void Decide_UnsafeStep_NeedsReviewWithoutReconcile()
        {
            var op = new ScriptedOperation(
                Idempotency.Unsafe, new ReconcileResult.NotApplied());
            var decision = ResumePolicy.Decide(
                Step(Idempotency.Unsafe), op, Ctx(),
                Snapshot("{\"applied\":false}"));
            Assert.AreEqual(ResumeDecision.NeedsReview, decision);
            Assert.AreEqual(0, op.ReconcileCalls,
                "An Unsafe step is never retried, so Reconcile stays uncalled.");
        }

        [Test]
        public void Descriptor_WithoutDeclaredIdempotency_DefaultsToUnsafe()
        {
            var descriptor = Op
                .Define("test.dummy.defaulted", 1)
                .Summary("Declares no Idempotency.")
                .Build();
            Assert.AreEqual(Idempotency.Unsafe, descriptor.Idempotency,
                "SPEC F0 item 4: custom/external Operations default to Unsafe.");
        }

        [Test]
        public void Decide_UndeclaredIdempotencyValue_NeedsReview()
        {
            var op = new ScriptedOperation(Idempotency.Unsafe);
            var decision = ResumePolicy.Decide(
                Step((Idempotency)99), op, Ctx(), Snapshot("{}"));
            Assert.AreEqual(ResumeDecision.NeedsReview, decision);
        }

        // ---- CanReexecute: a thin wrapper over Decide ----

        [Test]
        public void CanReexecute_MatchesOnlyTheReexecuteVerdict()
        {
            var idempotent = new ScriptedOperation(Idempotency.Idempotent);
            var notApplied = new ScriptedOperation(
                Idempotency.Reconcilable, new ReconcileResult.NotApplied());
            var completed = new ScriptedOperation(
                Idempotency.Reconcilable,
                new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)));
            var unknown = new ScriptedOperation(
                Idempotency.Reconcilable,
                new ReconcileResult.Unknown("Evidence is insufficient."));
            var unsafeOp = new ScriptedOperation(Idempotency.Unsafe);
            var snapshot = Snapshot("{}");

            Assert.IsTrue(ResumePolicy.CanReexecute(
                Step(Idempotency.Idempotent), idempotent, Ctx(), null));
            Assert.IsTrue(ResumePolicy.CanReexecute(
                Step(Idempotency.Reconcilable), notApplied, Ctx(), snapshot));
            Assert.IsFalse(ResumePolicy.CanReexecute(
                Step(Idempotency.Reconcilable), completed, Ctx(), snapshot),
                "Adopted outputs are a success verdict, not a re-execution.");
            Assert.IsFalse(ResumePolicy.CanReexecute(
                Step(Idempotency.Reconcilable), unknown, Ctx(), snapshot));
            Assert.IsFalse(ResumePolicy.CanReexecute(
                Step(Idempotency.Reconcilable), unknown, Ctx(), null));
            Assert.IsFalse(ResumePolicy.CanReexecute(
                Step(Idempotency.Unsafe), unsafeOp, Ctx(), snapshot));
        }

        // ---- Caller-contract guards ----

        [Test]
        public void Decide_NullArguments_Throw()
        {
            var op = new ScriptedOperation(Idempotency.Idempotent);
            var ctx = Ctx();
            var step = Step(Idempotency.Idempotent);
            Assert.Throws<ArgumentNullException>(
                () => ResumePolicy.Decide(null!, op, ctx, null));
            Assert.Throws<ArgumentNullException>(
                () => ResumePolicy.Decide(step, null!, ctx, null));
            Assert.Throws<ArgumentNullException>(
                () => ResumePolicy.Decide(step, op, null!, null));
        }

        /// <summary>
        /// A dummy <see cref="IOperation"/> whose declared Idempotency and
        /// Reconcile verdict are scripted per test; Reconcile records its
        /// calls so tests prove when the table does and does not consult it.
        /// </summary>
        private sealed class ScriptedOperation : IOperation
        {
            public ScriptedOperation(
                Idempotency idempotency, ReconcileResult? verdict = null)
            {
                Descriptor = Op
                    .Define("test.dummy.resume", 1)
                    .Summary("Resume-policy fixture operation.")
                    .Effects(EffectNames.Read)
                    .Idempotency(idempotency)
                    .Build();
                OnReconcile = (ctx, snapshot) =>
                    verdict ?? new ReconcileResult.Unknown("No verdict scripted.");
            }

            public OperationDescriptor Descriptor { get; }

            public int ReconcileCalls { get; private set; }

            public JsonValue? LastSnapshot { get; private set; }

            public Func<StepContext, JsonValue, ReconcileResult> OnReconcile { get; set; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                throw new NotSupportedException(
                    "Resume-policy decisions never run Preflight.");

            public StepResult Execute(StepContext ctx) =>
                throw new NotSupportedException(
                    "Resume-policy decisions never run Execute.");

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Resume-policy decisions never run Poll.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                ReconcileCalls++;
                LastSnapshot = snapshot;
                return OnReconcile(ctx, snapshot);
            }

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
