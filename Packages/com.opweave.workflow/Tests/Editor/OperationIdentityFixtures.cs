#nullable enable

// T-W2.2-08 [Edit] fixtures (SPEC §C3 tests 1–2): Operation classes for the
// declarative-identity checks — a pair declaring the same (id, version) for
// the pre-instantiation OW-OP-004 refusal (their s_ctorCalls counters prove
// neither constructor runs), a pair declaring the same id at two versions
// for the coexistence rule, and one unrelated Operation that must register
// regardless. The reserved-namespace fixture classes live outside this
// assembly — a class here would carry Standard provenance — in the
// TestProject Assets assembly OpWeaveFixtures.ReservedNamespace and the
// embedded package com.opweave.fixture.reservedns, both Custom.
using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// [Edit] fixture Operations for the SPEC §C2/§C3 declarative-identity
    /// contract (T-W2.2-08).
    /// </summary>
    internal static class OperationIdentityFixtures
    {
        internal const string DuplicateOperationId = "test.identity.dup";
        internal const string CoexistingOperationId = "test.identity.coexist";
        internal const string UnrelatedOperationId = "test.identity.unrelated";

        // SPEC §C1 item 4: the fixture types compile into the test assembly,
        // so any of them the production stages register must publish a
        // complete-WF Example like a shipped Operation.
        internal static OperationDescriptor Describe(string id, int version) =>
            Op.Define(id, version)
                .Summary("An identity fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(id, version, outputPort: "done"),
                    },
                };

        internal abstract class IdentityFixtureOperation : IOperation
        {
            public abstract OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.Parse("{}").Value!);

            public StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        // SPEC §C3 test 2: two classes declaring the same attribute
        // (id, version) — the stage-3 refusal must fire before either
        // constructor runs.
        [OpWeaveOperation(DuplicateOperationId, 1)]
        internal sealed class DuplicateIdentityOperationA : IdentityFixtureOperation
        {
            internal static int s_ctorCalls;
            public DuplicateIdentityOperationA() => s_ctorCalls++;

            public override OperationDescriptor Descriptor { get; } =
                Describe(DuplicateOperationId, 1);
        }

        [OpWeaveOperation(DuplicateOperationId, 1)]
        internal sealed class DuplicateIdentityOperationB : IdentityFixtureOperation
        {
            internal static int s_ctorCalls;
            public DuplicateIdentityOperationB() => s_ctorCalls++;

            public override OperationDescriptor Descriptor { get; } =
                Describe(DuplicateOperationId, 1);
        }

        // SPEC §C3 rule 4: the same id at a different version coexists.
        [OpWeaveOperation(CoexistingOperationId, 1)]
        internal sealed class CoexistingVersionOneOperation : IdentityFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Describe(CoexistingOperationId, 1);
        }

        [OpWeaveOperation(CoexistingOperationId, 2)]
        internal sealed class CoexistingVersionTwoOperation : IdentityFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Describe(CoexistingOperationId, 2);
        }

        // An unrelated Operation sharing neither id nor version: must
        // register whatever happens to the refused candidates (SPEC §C3
        // step 6 "個別登録エラー").
        [OpWeaveOperation(UnrelatedOperationId, 1)]
        internal sealed class UnrelatedIdentityOperation : IdentityFixtureOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Describe(UnrelatedOperationId, 1);
        }
    }
}
