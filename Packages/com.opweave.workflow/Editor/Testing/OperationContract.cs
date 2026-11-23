#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Testing
{
    /// <summary>
    /// The contract assertions of SPEC §M.6 item 2: the checks a custom
    /// Operation's generated contract test, and AT-02 for every standard
    /// Operation, apply to an <see cref="IOperation"/> class found in a loaded
    /// assembly — no workflow, no Plan and no Registry build required.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each assertion drives the Operation through
    /// <see cref="OperationTestHarness"/>, so the checked behaviour is the
    /// step's real one — Preflight, the write-ahead record, <c>Execute</c>
    /// inside the §G3 undo group, the §F7 change records and the outputs the
    /// step wrote. A violation fails the running NUnit test with
    /// <see cref="Assert.Fail"/>-style exceptions, exactly like any other
    /// test assertion.
    /// </para>
    /// <para>
    /// Beyond the harness's own step pipeline, every successful run is also
    /// asked for the part the Plan-bound §E5 validation would cover on a
    /// real run: each present input and each written output is checked
    /// against its port's Codec — <see cref="IValueCodec.Validate"/> must
    /// accept it and <see cref="IValueCodec.Normalize"/> must round-trip it
    /// idempotently (SPEC §B7.1).
    /// </para>
    /// </remarks>
    public static class OperationContract
    {
        // The synthetic node identity of a contract-test step; mirrors the
        // harness's own "harness" node — there is no workflow to name one.
        private const string ContractNodeId = "contract";

        /// <summary>
        /// Asserts the Descriptor of <paramref name="operationId"/> passes the
        /// Core checks registration applies (SPEC §C3): every implemented
        /// version's class resolves through the SPEC §C2 item 1 eligibility
        /// screen, <see cref="OperationDescriptorValidator"/> reports no
        /// violation against the standard type snapshot, and a Waitable
        /// Operation provides a real Poll implementation.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="operationId"/> is null or empty.
        /// </exception>
        /// <exception cref="AssertionException">A Descriptor check fails.</exception>
        public static void AssertDescriptorValid(string operationId)
        {
            RequireId(operationId);
            var versions = HarnessOperationSource.VersionsOf(operationId);
            Assert.That(
                versions,
                Is.Not.Empty,
                $"no loaded [OpWeaveOperation] class implements Operation '{operationId}'.");
            var violations = new List<string>();
            foreach (var version in versions)
            {
                var resolved = HarnessOperationSource.Resolve(operationId, version);
                if (!resolved.Ok || resolved.Value is null)
                {
                    foreach (var diagnostic in resolved.Diagnostics)
                    {
                        violations.Add(
                            $"v{version} {diagnostic.Code}: {diagnostic.Message}");
                    }
                    continue;
                }
                var operation = resolved.Value;
                OperationDescriptor descriptor;
                try
                {
                    descriptor = operation.Descriptor;
                }
                catch (Exception exception)
                {
                    violations.Add(
                        $"v{version}: the Descriptor getter threw {exception.GetType().Name}.");
                    continue;
                }
                foreach (var diagnostic in OperationDescriptorValidator.Validate(
                    descriptor, HarnessTypes.Registry))
                {
                    violations.Add($"v{version} {diagnostic.Code}: {diagnostic.Message}");
                }
                if (descriptor.ExecutionMode == ExecutionMode.Waitable &&
                    !PollImplementation.HasRealImplementation(operation.GetType()))
                {
                    violations.Add(
                        $"v{version}: the Descriptor declares a waitable Operation " +
                        "whose Poll is a placeholder (SPEC §C3 rule 3).");
                }
            }
            Assert.That(
                violations,
                Is.Empty,
                () => $"AssertDescriptorValid failed for '{operationId}':\n" +
                    string.Join("\n", violations));
        }

        /// <summary>
        /// Asserts the declared <see cref="Idempotency"/> contract
        /// <paramref name="operationId"/> carries actually holds (SPEC §F5
        /// "宣言の検証"): an Idempotent Operation run twice on the same inputs
        /// must leave the world identical and report equal outputs; a
        /// Reconcilable Operation's <see cref="IOperation.Reconcile"/> is
        /// judged against both SPEC §F5 interrupt windows —
        /// <see cref="ReconcileResult.Unknown"/> is a correct safe verdict,
        /// a <see cref="ReconcileResult.Completed"/> without the §F5 evidence
        /// is not. Unsafe carries no contract and is not checked (SPEC §F5
        /// item 3).
        /// </summary>
        /// <remarks>
        /// With no evidence probe to consult, this overload judges a
        /// Waitable step interrupted after Execute returned Waiting as
        /// <see cref="EffectEvidence.Indeterminate"/> — missing evidence is
        /// never treated as proof of absence, so only Unknown is admitted
        /// there (SPEC §M.6 item 4). The three-argument overload judges the
        /// same case against the observed effect state.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// <paramref name="operationId"/> is null or empty.
        /// </exception>
        /// <exception cref="AssertionException">The declaration does not hold.</exception>
        public static void AssertIdempotency(string operationId, object inputs) =>
            CheckIdempotency(operationId, inputs, evidenceProbe: null);

        /// <summary>
        /// Asserts the declared <see cref="Idempotency"/> contract like the
        /// two-argument overload, but judges a Waitable Reconcilable step
        /// interrupted after Execute returned Waiting by the SPEC §M.6
        /// item 4 evidence table: <paramref name="evidenceProbe"/> inspects
        /// the effect state independently of the Operation's Reconcile and
        /// is called exactly once, right after the suspended step's
        /// Reconcile returned and before anything changes the world.
        /// <see cref="EffectEvidence.Applied"/> then admits
        /// <see cref="ReconcileResult.Completed"/> (with the §F5 identity
        /// and declared-output restoration checks) or
        /// <see cref="ReconcileResult.Unknown"/>,
        /// <see cref="EffectEvidence.NotApplied"/> admits
        /// <see cref="ReconcileResult.NotApplied"/> (with the §F5
        /// NotApplied conditions) or <see cref="ReconcileResult.Unknown"/>,
        /// and <see cref="EffectEvidence.Indeterminate"/> admits
        /// <see cref="ReconcileResult.Unknown"/> only. A probe that throws
        /// fails the contract. Steps not interrupted in Waiting are judged
        /// exactly as the two-argument overload judges them — the probe
        /// does not change them.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="operationId"/> is null or empty.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="evidenceProbe"/> is null.
        /// </exception>
        /// <exception cref="AssertionException">The declaration does not hold.</exception>
        public static void AssertIdempotency(
            string operationId, object inputs, Func<EffectEvidence> evidenceProbe) =>
            CheckIdempotency(
                operationId, inputs,
                evidenceProbe ??
                    throw new ArgumentNullException(nameof(evidenceProbe)));

        // The shared AssertIdempotency body: the two-argument overload
        // supplies no probe, which SPEC §M.6 item 4 judges as
        // Indeterminate for a step interrupted after Execute returned
        // Waiting.
        private static void CheckIdempotency(
            string operationId, object inputs, Func<EffectEvidence>? evidenceProbe)
        {
            var (operation, descriptor) = RequireOperation(operationId);
            switch (descriptor.Idempotency)
            {
                case Idempotency.Unsafe:
                    // SPEC §F5 item 3: Unsafe is never re-executed, so there
                    // is nothing to verify.
                    return;
                case Idempotency.Idempotent:
                    CheckTwoRunEquality(operationId, inputs, descriptor);
                    return;
                case Idempotency.Reconcilable:
                    CheckReconcilable(
                        operationId, inputs, operation, descriptor, evidenceProbe);
                    return;
                default:
                    Assert.Fail(
                        $"Operation '{operationId}' declares an unrecognized Idempotency value.");
                    return;
            }
        }

        /// <summary>
        /// Asserts an Operation declaring <see cref="UndoSupport.Full"/> is
        /// reverted by exactly one Undo (SPEC §M.6 item 2, §G3): after one
        /// harness run, a single <c>Undo.PerformUndo()</c> must restore the
        /// world — scenes, objects and assets — to its pre-run state.
        /// Operations declaring None or Partial are not checked.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="operationId"/> is null or empty.
        /// </exception>
        /// <exception cref="AssertionException">One undo did not revert the step.</exception>
        public static void AssertUndo(string operationId, object inputs)
        {
            var (_, descriptor) = RequireOperation(operationId);
            if (descriptor.Undo != UndoSupport.Full)
            {
                return;
            }
            using var harness = OperationTestHarness.Create();
            harness.OwnedScene();
            var before = HarnessWorldSnapshot.Capture();
            // The step's own undo group must be the stack's top when the check
            // performs its undo: without clearing first, an Operation that
            // recorded no Undo records at all would make PerformUndo pop —
            // and silently revert — an unrelated, foreign stack entry.
            Undo.ClearAll();
            var run = harness.RunToCompletion(operationId, inputs);
            RequireSucceeded(run, "the AssertUndo run");
            CheckPortValues(descriptor, inputs, run);
            var after = HarnessWorldSnapshot.Capture();
            if (before.SameAs(after))
            {
                // Nothing happened, so nothing had to be reverted.
                return;
            }
            Undo.PerformUndo();
            var reverted = HarnessWorldSnapshot.Capture();
            var drift = before.ChangesTo(reverted);
            Assert.That(
                drift,
                Is.Empty,
                () => $"Operation '{operationId}' declares Undo=Full but one Undo " +
                    "did not revert the step:\n" + Describe(drift));
        }

        /// <summary>
        /// Asserts <paramref name="operationId"/> causes no side effect
        /// outside the SPEC §C4 vocabulary it declared: every §F7 change the
        /// run recorded, and every difference the world shows afterwards —
        /// including one the Operation never recorded — must be covered by a
        /// declared effect (for example, a <c>read</c> Operation must not
        /// create an asset).
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="operationId"/> is null or empty.
        /// </exception>
        /// <exception cref="AssertionException">An undeclared effect was observed.</exception>
        public static void AssertDeclaredEffectsOnly(string operationId, object inputs)
        {
            var (_, descriptor) = RequireOperation(operationId);
            using var harness = OperationTestHarness.Create();
            harness.OwnedScene();
            var before = HarnessWorldSnapshot.Capture();
            var run = harness.RunToCompletion(operationId, inputs);
            RequireSucceeded(run, "the AssertDeclaredEffectsOnly run");
            CheckPortValues(descriptor, inputs, run);
            var after = HarnessWorldSnapshot.Capture();

            var declared = new HashSet<string>(
                descriptor.SideEffects, StringComparer.Ordinal);
            var violations = new List<string>();
            for (var i = 0; i < run.Changes.Count; i++)
            {
                var change = run.Changes[i];
                RequireEffect(
                    declared,
                    change.Action,
                    change.Target.Kind,
                    change.Target.Path,
                    $"the recorded change '{change.Action} {change.Target.Kind} " +
                        $"{change.Target.Path}'",
                    violations);
            }
            foreach (var observed in before.ChangesTo(after))
            {
                RequireEffect(
                    declared,
                    observed.Action,
                    observed.Kind,
                    observed.Path,
                    $"the observed {Describe(observed)}",
                    violations);
            }
            Assert.That(
                violations,
                Is.Empty,
                () => $"Operation '{operationId}' caused effects it did not declare:\n" +
                    string.Join("\n", violations));
        }

        // ── shared plumbing ─────────────────────────────────────────────

        private static void RequireId(string operationId)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                throw new ArgumentException(
                    "The Operation ID must not be null or empty.", nameof(operationId));
            }
        }

        // Resolves the Operation's version-1 implementation and its
        // Descriptor — the same instance-and-descriptor pair a harness run
        // would pick; resolution diagnostics fail the assertion.
        private static (IOperation Operation, OperationDescriptor Descriptor)
            RequireOperation(string operationId)
        {
            RequireId(operationId);
            var resolved = HarnessOperationSource.Resolve(operationId, 1);
            Assert.That(
                resolved.Ok && resolved.Value is not null,
                () => $"Operation '{operationId}' could not be resolved:\n" +
                    Describe(resolved.Diagnostics));
            var operation = resolved.Value!;
            try
            {
                return (operation, operation.Descriptor);
            }
            catch (Exception exception)
            {
                Assert.Fail(
                    $"the Descriptor getter of '{operationId}' threw " +
                        exception.GetType().Name + ".");
                throw;
            }
        }

        private static void RequireSucceeded(HarnessResult result, string which)
        {
            Assert.That(
                result.Ok,
                Is.True,
                () => $"{which} did not succeed: {result}");
        }

        // The SPEC §F5 verification for Idempotent: two executions on the
        // same inputs both succeed, the second leaves the world identical —
        // no new assets or GameObjects — and reports the same outputs. A
        // second run the Operation's own preconditions refuse is not the
        // successful re-execution the declaration promises: the §F5 contract
        // is 'run it again and nothing differs', so a refused or failed
        // second run is the declaration's violation, however clean the world
        // looks afterwards.
        private static void CheckTwoRunEquality(
            string operationId, object inputs, OperationDescriptor descriptor)
        {
            using var harness = OperationTestHarness.Create();
            harness.OwnedScene();

            var first = harness.RunToCompletion(operationId, inputs);
            RequireSucceeded(first, "the first AssertIdempotency run");
            CheckPortValues(descriptor, inputs, first);
            var afterFirst = HarnessWorldSnapshot.Capture();

            var second = harness.RunToCompletion(operationId, inputs);
            RequireSucceeded(second, "the second AssertIdempotency run");
            CheckPortValues(descriptor, inputs, second);
            var afterSecond = HarnessWorldSnapshot.Capture();

            var drift = afterFirst.ChangesTo(afterSecond);
            Assert.That(
                drift,
                Is.Empty,
                () => $"Operation '{operationId}' declares Idempotent but a second " +
                    "run changed the world:\n" + Describe(drift));

            Assert.That(
                second.Outputs.Keys,
                Is.EquivalentTo(first.Outputs.Keys),
                $"Operation '{operationId}' declares Idempotent but the second " +
                    "run's output ports differ from the first run's.");
            foreach (var pair in first.Outputs)
            {
                Assert.AreEqual(
                    pair.Value.CanonicalText,
                    second.Outputs[pair.Key].CanonicalText,
                    $"Operation '{operationId}' declares Idempotent but output " +
                        $"'{pair.Key}' differs between the two runs.");
            }
        }

        // The SPEC §F5 judgement checks for Reconcilable against both crash
        // windows the write-ahead order leaves open.
        // <paramref name="evidenceProbe"/> is the SPEC §M.6 item 4 evidence
        // read described on the three-argument AssertIdempotency — invoked
        // exactly once, and only for a step whose Execute ended Waiting:
        // the case where neither the journaled records nor the world diff
        // can say whether the issued effect landed. A null probe — the
        // two-argument AssertIdempotency — judges that case as
        // Indeterminate; missing evidence is never proof of absence.
        private static void CheckReconcilable(
            string operationId,
            object inputs,
            IOperation operation,
            OperationDescriptor descriptor,
            Func<EffectEvidence>? evidenceProbe)
        {
            using var harness = OperationTestHarness.Create();
            // The same scene contract the run assertions hold: the harness's
            // scene must exist — and be active — before Preflight, or a
            // scene-placing Operation snapshots one scene in Preflight and
            // creates in the harness scene at Execute, and its honest
            // Reconcile then reads a completed effect as absent.
            harness.OwnedScene();

            // (a) Interrupted before Execute: nothing happened, so NotApplied
            //     or Unknown is the correct verdict — Completed is a lie.
            var interruptedBefore = harness.RunInterrupted(
                operationId, inputs, InterruptPoint.BeforeExecute);
            Assert.That(
                interruptedBefore.StepState,
                Is.EqualTo(StepState.Running),
                () => interruptedBefore.ToString());
            var verdictBefore = ReconcileOrFail(
                operation, descriptor, inputs,
                SnapshotOf(interruptedBefore), "before Execute");
            Assert.IsFalse(
                verdictBefore is ReconcileResult.Completed,
                $"Operation '{operationId}' answered Reconcile.Completed for a " +
                    "step that never executed — Completed without evidence " +
                    "(SPEC §F5 judgement a).");

            // (b) Interrupted after Execute: the effect happened, so
            //     Completed or Unknown is correct — NotApplied is a lie.
            //     While the step's own §E6 item 4 acquisition is live and
            //     nothing has disturbed the artifacts, the judgement is
            //     stronger: the row's Completed evidence is fully provable,
            //     so Completed is the one honest verdict.
            var baseline = HarnessWorldSnapshot.Capture();
            var interruptedAfter = harness.RunInterrupted(
                operationId, inputs, InterruptPoint.AfterExecute);
            // A Waitable Operation's Execute legitimately ends at the §G4
            // suspension: the crash window between the issued effect and
            // the journaled step_waiting is the same bare step_started
            // record, and judgement (b) asks what the evidence proves —
            // not that the step had already settled.
            Assert.That(
                interruptedAfter.Outcome,
                descriptor.ExecutionMode == ExecutionMode.Waitable
                    ? Is.EqualTo(StepResultKind.Succeeded)
                        .Or.EqualTo(StepResultKind.Waiting)
                    : Is.EqualTo(StepResultKind.Succeeded),
                () => interruptedAfter.ToString());
            // The interrupted run's own §E6 item 4 acquisitions judge this
            // verdict: SPEC §F5's Completed row for a Run-resource Operation
            // (open_contents' live contents session) is provable only while
            // the same step's acquisition is live — an empty registry would
            // ask a different, easier question and could never check the
            // valid Completed path at all.
            var verdictAfter = ReconcileOrFail(
                operation, descriptor, inputs,
                SnapshotOf(interruptedAfter), "after Execute",
                interruptedAfter.Resources);
            if (interruptedAfter.Outcome == StepResultKind.Waiting)
            {
                // The SPEC §M.6 item 4 judgement: Execute ended at the §G4
                // suspension, so the step issued its effect without proof
                // it landed — neither the journaled records nor the world
                // diff decides the verdict. The probe's single read, taken
                // right after the Reconcile returned and before anything
                // changes the state that verdict judged, picks the §F5 row
                // the answer must satisfy.
                switch (ProbeSuspendedEvidence(operationId, evidenceProbe))
                {
                    case EffectEvidence.Applied:
                        Assert.IsFalse(
                            verdictAfter is ReconcileResult.NotApplied,
                            $"Operation '{operationId}' answered " +
                                "Reconcile.NotApplied after Execute " +
                                "returned Waiting while the evidence " +
                                "proves its issued effect landed — " +
                                "re-execution would issue a landed " +
                                "effect again (SPEC §F5 judgement b, " +
                                "§M.6 item 4).");
                        break;
                    case EffectEvidence.NotApplied:
                        Assert.IsFalse(
                            verdictAfter is ReconcileResult.Completed,
                            $"Operation '{operationId}' answered " +
                                "Reconcile.Completed after Execute " +
                                "returned Waiting while the evidence " +
                                "proves its issued effect never landed — " +
                                "Completed without the row's identity " +
                                "evidence (SPEC §F5 judgement b, " +
                                "§M.6 item 4).");
                        break;
                    case EffectEvidence.Indeterminate:
                        Assert.That(
                            verdictAfter,
                            Is.TypeOf<ReconcileResult.Unknown>(),
                            $"Operation '{operationId}' answered " +
                                $"{VerdictName(verdictAfter)} after " +
                                "Execute returned Waiting while the " +
                                "evidence cannot decide whether its " +
                                "issued effect landed — only Unknown is " +
                                "honest (SPEC §F5 judgement b, §M.6 " +
                                "item 4)." +
                                (evidenceProbe is null
                                    ? " Pass the three-argument " +
                                      "AssertIdempotency(operationId, " +
                                      "inputs, evidenceProbe) overload " +
                                      "to judge it by the observed " +
                                      "effect state."
                                    : string.Empty));
                        break;
                }
            }
            else
            {
                // A live acquisition of this step's own — the contract
                // step is the harness's step 0 — with the artifacts
                // untouched is the §F5 row whose declaration makes
                // Completed mandatory: the same step's resource plus the
                // intact artifact are the row's whole evidence, so no
                // weaker verdict is honest and an always-Unknown Reconcile
                // fails here. A step holding no live acquisition of its
                // own keeps the weaker judgement — NotApplied is a lie
                // either way.
                var liveSameStepAcquisition = false;
                for (var i = 0; i < interruptedAfter.Resources.Count; i++)
                {
                    var resource = interruptedAfter.Resources[i];
                    if (resource.PhysicallyHeld && resource.Step == 0)
                    {
                        liveSameStepAcquisition = true;
                        break;
                    }
                }
                if (liveSameStepAcquisition)
                {
                    Assert.That(
                        verdictAfter,
                        Is.TypeOf<ReconcileResult.Completed>(),
                        $"Operation '{operationId}' answered " +
                            $"{VerdictName(verdictAfter)} after Execute " +
                            "while the step's own acquired Run resource " +
                            "is live and its artifacts are intact — the " +
                            "§F5 row's Completed evidence is fully " +
                            "provable, so no weaker verdict is honest " +
                            "(SPEC §F5 judgement b).");
                }
                else
                {
                    Assert.IsFalse(
                        verdictAfter is ReconcileResult.NotApplied,
                        $"Operation '{operationId}' answered Reconcile.NotApplied for a " +
                            "step whose Execute already completed — the effect is " +
                            "present and a re-run would duplicate it (SPEC §F5 judgement b).");
                }
            }
            if (verdictAfter is ReconcileResult.Completed completed)
            {
                // §F5 Completed rule (d): every declared output is restored;
                // and the restored values equal what the uninterrupted step
                // wrote — 'Completed' is the same outcome, not an apology.
                foreach (var port in descriptor.Outputs)
                {
                    Assert.That(
                        completed.Outputs.ContainsKey(port.Name),
                        Is.True,
                        $"Operation '{operationId}' answered Reconcile.Completed " +
                            $"without restoring declared output '{port.Name}'.");
                }
                foreach (var pair in completed.Outputs)
                {
                    if (interruptedAfter.Outputs.TryGetValue(pair.Key, out var written))
                    {
                        Assert.AreEqual(
                            written.CanonicalText,
                            pair.Value.CanonicalText,
                            $"Operation '{operationId}' restored output " +
                                $"'{pair.Key}' to a value the run never wrote.");
                    }
                }
            }

            // (c) Evidence damaged: SPEC §F5's case 5 admits Unknown only —
            //     Completed without provable evidence is the failure this
            //     contract exists to catch, and NotApplied claims a clean
            //     slate that is no longer true either. The probe covers
            //     every artifact the run produced — the recorded ones and
            //     the ones only the world diff shows, file and folder, scene
            //     and GameObject, created, modified, moved and deleted — so
            //     an Operation cannot dodge it by recording nothing, and a
            //     Reconcile that answers Completed merely because Execute
            //     ran still faces the damaged evidence. The damage is
            //     repaired before returning: an assertion leaves the world
            //     as the run left it, so the next assertion starts from a
            //     clean baseline.
            var afterRun = HarnessWorldSnapshot.Capture();
            var tamper = TamperPlan.For(
                interruptedAfter.Changes, baseline.ChangesTo(afterRun));
            if (!tamper.Any)
            {
                // Fail closed, never pass silently: a Completed whose only
                // evidence is the step's own Run acquisition — open_contents'
                // contents session, which no world-diff artifact can reach —
                // is still judged against the one damage that evidence
                // cannot survive: re-ask with the acquisition gone. Unknown
                // is the required verdict there; a Completed that survives
                // even the missing resource rests on nothing this contract
                // can test — the unprovable Completed SPEC §F5 forbids.
                if (verdictAfter is ReconcileResult.Completed)
                {
                    var verdictMissing = ReconcileOrFail(
                        operation, descriptor, inputs,
                        SnapshotOf(interruptedAfter),
                        "after Execute with the step's Run resource missing");
                    Assert.That(
                        verdictMissing,
                        Is.TypeOf<ReconcileResult.Unknown>(),
                        $"Operation '{operationId}' answered " +
                            $"{VerdictName(verdictMissing)} with the step's " +
                            "acquired Run resource missing — the evidence its " +
                            "Completed rested on admits Unknown only " +
                            "(SPEC §F5 case 5).");
                }
                return;
            }
            tamper.Apply();
            try
            {
                var verdictTampered = ReconcileOrFail(
                    operation, descriptor, inputs,
                    SnapshotOf(interruptedAfter), "after Execute with tampered artifacts");
                Assert.That(
                    verdictTampered,
                    Is.TypeOf<ReconcileResult.Unknown>(),
                    $"Operation '{operationId}' answered " +
                        $"{VerdictName(verdictTampered)} after its artifact " +
                        "was damaged — SPEC §F5's evidence rules admit Unknown only.");
            }
            finally
            {
                tamper.Restore();
            }
        }

        // The probe's coverage of every artifact kind a SPEC §F7 change
        // record or the world diff can report; an artifact no row reaches is
        // one the probe cannot damage, and a 'Completed' verdict for a step
        // that left only such artifacts is rejected by the fail-closed rule
        // in CheckReconcilable rather than trusted.
        //
        // Every probe keeps the artifact's name and path untouched and
        // damages its identity or content instead: SPEC §F5 rules existence,
        // names, paths and timestamps out as evidence, so a probe that only
        // renamed would leave a conforming Reconcile's real evidence intact
        // and reject an honest verdict — and would hide an object a
        // name-only Reconcile needs to keep seeing.
        //
        //   artifact kind                       how the probe damages it
        //   ─────────────────────────────────   ──────────────────────────
        //   asset file — created, modified,     overwrite the file's bytes
        //   saved or moved into
        //   asset folder — same actions         plant a foreign file inside it
        //   deleted asset path (file or         re-occupy it with a foreign file
        //   folder, freed by a delete or move)
        //   moved asset                         damage the destination and
        //                                       re-occupy the source path
        //   GameObject — created                replace the instance: the
        //                                       original is stashed under a
        //                                       limbo root with its content
        //                                       perturbed and a foreign clone
        //                                       — same name, parent and
        //                                       sibling index, one extra
        //                                       component — takes its slot
        //   GameObject — a component change     keep name and place; plant a
        //   (modified, saved or moved)          foreign component and rewrite
        //                                       every component's serialized
        //                                       content, so component-list
        //                                       and content evidence break
        //   deleted GameObject                  recreate a foreign same-named
        //                                       object under its parent — or at
        //                                       its scene's root — so absence and
        //                                       child-list evidence no longer hold
        //   scene                               a foreign root planted in the
        //                                       loaded scene — the §F5 scene
        //                                       evidence judges the live root
        //                                       configuration and the loaded
        //                                       set, never the file's bytes —
        //                                       while an unloaded scene asset
        //                                       keeps the file damage; its
        //                                       objects arrive through the
        //                                       world diff
        //   package                             not damaged — editing a live
        //                                       manifest mid-test is unsafe; the
        //                                       fail-closed rule covers it
        //
        // The artifacts the (c) probe damages, with the state needed to
        // undo the damage afterwards: for a file its pre-probe bytes —
        // null when the probe placed the file itself, so the restore
        // deletes it again — for a created GameObject its scene, parent
        // and sibling index, each component's pre-probe serialized content
        // and the foreign clone that takes its slot, for a changed
        // GameObject each component's pre-probe serialized content and the
        // foreign component the probe adds, and for a deleted GameObject
        // the foreign object the probe creates, which the restore destroys.
        private sealed class TamperPlan
        {
            private static readonly byte[] s_tampered =
                Encoding.ASCII.GetBytes("tampered by the contract test\n");

            // The filename planted inside a folder artifact — a file no
            // honest artifact check can mistake for the folder's expected
            // content.
            private const string TamperFileName = "__opweave_tamper__.txt";

            // The foreign root the probe plants inside a loaded scene
            // artifact — a member no honest root-configuration check can
            // place.
            private const string TamperRootName = "__opweave_tamper_root__";

            // The root the probe stashes replaced GameObjects under — one
            // per scene, so the stash never has to cross a scene boundary.
            private const string LimboName = "__opweave_tamper_limbo__";

            // The root name of the foreign Prefab the probe saves over a
            // .prefab artifact. A bare foreign root would normalize to the
            // file's stem, so the damage is carried by a foreign component —
            // the content difference a recorded PrefabContentFingerprint can
            // never match.
            private const string TamperPrefabName = "__opweave_tamper_prefab__";

            // The component types the probe plants as foreign additions:
            // every entry attaches to any GameObject and requires nothing.
            private static readonly Type[] s_foreignComponents =
            {
                typeof(BoxCollider), typeof(Rigidbody), typeof(Light),
                typeof(Camera), typeof(AudioSource),
            };

            private readonly List<FileDamage> _files = new List<FileDamage>();
            private readonly List<ObjectReplacement> _replacements =
                new List<ObjectReplacement>();
            private readonly List<ObjectDamage> _damaged = new List<ObjectDamage>();
            private readonly List<ObjectRecreation> _recreations =
                new List<ObjectRecreation>();
            private readonly List<SceneTamper> _sceneTampers =
                new List<SceneTamper>();
            private readonly List<GameObject> _recreated = new List<GameObject>();
            private readonly List<GameObject> _limbos = new List<GameObject>();
            private readonly HashSet<string> _paths =
                new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<GameObject> _seen = new HashSet<GameObject>();

            private struct FileDamage
            {
                internal FileDamage(string path, byte[]? original, byte[]? meta)
                {
                    Path = path;
                    Original = original;
                    Meta = meta;
                }

                internal string Path { get; }
                // The file's pre-probe bytes — null when the probe placed the
                // file itself, so the restore deletes it again.
                internal byte[]? Original { get; }
                // The .meta's pre-probe bytes — null when none existed. A
                // damage that recreates the asset (the foreign-asset saves)
                // rewrites the .meta with a new GUID, and the restore still
                // owes the artifact its recorded identity back.
                internal byte[]? Meta { get; }
            }

            // A created GameObject the probe replaces with a foreign
            // instance: its scene, its parent — null for a scene root — and
            // its absolute sibling index, captured while the world is still
            // untouched so the restore puts the original back exactly where
            // the run left it, the clone the probe places and the stashed
            // original's pre-probe component content.
            private sealed class ObjectReplacement
            {
                internal ObjectReplacement(GameObject original)
                {
                    Original = original;
                    Scene = original.scene;
                    Parent = original.transform.parent;
                    SiblingIndex = original.transform.GetSiblingIndex();
                }

                internal GameObject Original { get; }
                internal Scene Scene { get; }
                internal Transform? Parent { get; }
                internal int SiblingIndex { get; }
                internal GameObject? Foreign { get; set; }
                internal List<KeyValuePair<Component, string>> Saved { get; } =
                    new List<KeyValuePair<Component, string>>();
            }

            // A GameObject whose components the probe damages: each
            // component's pre-probe serialized content and the foreign
            // component planted while <see cref="TamperPlan.Apply"/> ran.
            private sealed class ObjectDamage
            {
                internal ObjectDamage(GameObject target)
                {
                    Target = target;
                }

                internal GameObject Target { get; }
                internal List<KeyValuePair<Component, string>> Saved { get; } =
                    new List<KeyValuePair<Component, string>>();
                internal Component? Added { get; set; }
            }

            // A GameObject the probe places while damaging deleted-object
            // evidence: its name and the parent it goes under — null when
            // it becomes a root of <see cref="ObjectRecreation.Scene"/>.
            private struct ObjectRecreation
            {
                internal ObjectRecreation(Scene scene, Transform? parent, string name)
                {
                    Scene = scene;
                    Parent = parent;
                    Name = name;
                }

                internal Scene Scene { get; }
                internal Transform? Parent { get; }
                internal string Name { get; }
            }

            // One deleted-GameObject diff entry, parsed once so the
            // ancestor filter and the recreation planner share it.
            private sealed class DeletedObject
            {
                internal DeletedObject(
                    string key,
                    string scenePath,
                    RefHierarchyPath.Segment[] segments)
                {
                    Key = key;
                    ScenePath = scenePath;
                    Segments = segments;
                }

                internal string Key { get; }
                internal string ScenePath { get; }
                internal RefHierarchyPath.Segment[] Segments { get; }
            }

            // A loaded scene the probe plants its foreign root into, with
            // the dirty flag captured at plan time — the restore hands
            // the saved state back only to a scene that was clean when
            // the probe touched it; a dirty one's unsaved changes were
            // never the probe's to keep or discard.
            private struct SceneTamper
            {
                internal SceneTamper(Scene scene, bool wasDirty)
                {
                    Scene = scene;
                    WasDirty = wasDirty;
                }

                internal Scene Scene { get; }
                internal bool WasDirty { get; }
            }

            internal bool Any =>
                _files.Count + _replacements.Count + _damaged.Count +
                _recreations.Count + _sceneTampers.Count > 0;

            internal static TamperPlan For(
                IReadOnlyList<ChangeRecord> recorded,
                List<HarnessWorldSnapshot.ObservedChange> observed)
            {
                var plan = new TamperPlan();
                for (var i = 0; i < recorded.Count; i++)
                {
                    plan.AddRecorded(recorded[i]);
                }
                // The deleted GameObjects are collected before the
                // per-change pass so an object whose whole subtree went is
                // probed once — at the shallowest missing ancestor — not
                // once per descendant.
                var deletedObjects = ParseDeletedObjects(observed);
                for (var i = 0; i < observed.Count; i++)
                {
                    plan.AddObserved(observed[i], deletedObjects);
                }
                return plan;
            }

            internal void Apply()
            {
                for (var i = 0; i < _files.Count; i++)
                {
                    var damage = _files[i];
                    // A .prefab artifact is damaged through a foreign,
                    // still-importable Prefab rather than raw bytes: bytes
                    // that are not Prefab YAML fail the import itself, which
                    // Unity reports as an importer error — an Editor state no
                    // Reconcile verdict can ever be asked about — while §F5's
                    // damage probe needs a valid artifact whose content
                    // simply cannot match the recorded fingerprint. Every
                    // other serialized Unity asset is damaged the same way —
                    // a scene file through a foreign saved scene, a .mat,
                    // .asset or any other %YAML file through a foreign
                    // serialized object written by the same asset save the
                    // Operations use — because a corrupt serialized file
                    // raises the same importer error on Refresh.
                    if (damage.Path.EndsWith(
                            ".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        DamagePrefab(damage.Path);
                    }
                    else if (IsSceneAsset(damage.Path))
                    {
                        DamageSceneAsset(damage.Path);
                    }
                    else if (IsSerializedAsset(damage))
                    {
                        DamageSerializedAsset(damage.Path);
                    }
                    else
                    {
                        File.WriteAllBytes(FullPathOf(damage.Path), s_tampered);
                    }
                }
                for (var i = 0; i < _recreations.Count; i++)
                {
                    var recreation = _recreations[i];
                    var created = new GameObject(recreation.Name);
                    if (recreation.Parent is not null)
                    {
                        created.transform.SetParent(recreation.Parent, false);
                    }
                    else
                    {
                        SceneManager.MoveGameObjectToScene(created, recreation.Scene);
                    }
                    _recreated.Add(created);
                }
                for (var i = 0; i < _replacements.Count; i++)
                {
                    var replacement = _replacements[i];
                    var original = replacement.Original;
                    if (original == null)
                    {
                        continue;
                    }
                    // The foreign occupant is the original's clone plus one
                    // component the clone does not already carry, so it keeps
                    // the artifact's name, parent and sibling slot while its
                    // instance identity and its content can never match the
                    // artifact's. The stashed original's content is perturbed
                    // too — no view of the world may still present the real
                    // artifact's evidence.
                    var foreign = UnityEngine.Object.Instantiate(original);
                    foreign.name = original.name;
                    AddForeignComponent(foreign);
                    if (replacement.Parent is not null)
                    {
                        foreign.transform.SetParent(replacement.Parent, false);
                    }
                    foreign.transform.SetSiblingIndex(replacement.SiblingIndex);
                    replacement.Foreign = foreign;
                    PerturbComponents(original, replacement.Saved);
                    original.transform.SetParent(
                        LimboFor(replacement.Scene).transform, false);
                }
                for (var i = 0; i < _damaged.Count; i++)
                {
                    var damage = _damaged[i];
                    if (damage.Target == null)
                    {
                        continue;
                    }
                    PerturbComponents(damage.Target, damage.Saved);
                    damage.Added = AddForeignComponent(damage.Target);
                }
                AssetDatabase.Refresh();
            }

            internal void Restore()
            {
                for (var i = 0; i < _recreated.Count; i++)
                {
                    if (_recreated[i] != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_recreated[i]);
                    }
                }
                _recreated.Clear();
                for (var i = 0; i < _replacements.Count; i++)
                {
                    if (_replacements[i].Foreign != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_replacements[i].Foreign);
                        _replacements[i].Foreign = null;
                    }
                }
                // The originals go back at their recorded indices in
                // ascending order: with the foreign occupants gone, each
                // insertion lands exactly where the run left it.
                _replacements.Sort(
                    (left, right) => left.SiblingIndex.CompareTo(right.SiblingIndex));
                for (var i = 0; i < _replacements.Count; i++)
                {
                    var replacement = _replacements[i];
                    var original = replacement.Original;
                    if (original != null)
                    {
                        RestoreComponents(replacement.Saved);
                        replacement.Saved.Clear();
                        original.transform.SetParent(replacement.Parent, false);
                        original.transform.SetSiblingIndex(replacement.SiblingIndex);
                    }
                }
                for (var i = 0; i < _limbos.Count; i++)
                {
                    if (_limbos[i] != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_limbos[i]);
                    }
                }
                _limbos.Clear();
                for (var i = 0; i < _damaged.Count; i++)
                {
                    var damage = _damaged[i];
                    if (damage.Added != null)
                    {
                        UnityEngine.Object.DestroyImmediate(damage.Added);
                        damage.Added = null;
                    }
                    RestoreComponents(damage.Saved);
                    damage.Saved.Clear();
                }
                for (var i = 0; i < _files.Count; i++)
                {
                    var damage = _files[i];
                    var fullPath = FullPathOf(damage.Path);
                    if (damage.Original is { } original)
                    {
                        File.WriteAllBytes(fullPath, original);
                    }
                    else if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                    var metaPath = fullPath + ".meta";
                    if (damage.Meta is { } meta)
                    {
                        File.WriteAllBytes(metaPath, meta);
                    }
                    else if (File.Exists(metaPath))
                    {
                        File.Delete(metaPath);
                    }
                }
                for (var i = 0; i < _sceneTampers.Count; i++)
                {
                    var tamper = _sceneTampers[i];
                    // Removing the planted root alone leaves the scene
                    // dirty, and an unsaved-changes scene is exactly what
                    // the next harness run refuses to sit beside — a
                    // scene the probe found clean is saved back, which
                    // restores the flag and the file's content in one
                    // step.
                    if (!tamper.WasDirty &&
                        tamper.Scene.IsValid() && tamper.Scene.isLoaded)
                    {
                        EditorSceneManager.SaveScene(tamper.Scene);
                    }
                }
                _sceneTampers.Clear();
                AssetDatabase.Refresh();
            }

            // Writes a valid, foreign Prefab over <paramref name="assetPath"/>
            // through the same save the Operations use: the artifact stays an
            // imported Prefab, which a byte overwrite could never leave — the
            // importer rejects non-YAML content with an error no verdict can
            // be judged against — and the foreign component keeps the content
            // provably different even where a bare foreign root would
            // normalize to the artifact's own file-stem name (§G6.1's
            // fingerprint evidence still must not match).
            private static void DamagePrefab(string assetPath)
            {
                var foreign = new GameObject(TamperPrefabName);
                try
                {
                    AddForeignComponent(foreign);
                    var saved = PrefabUtility.SaveAsPrefabAsset(foreign, assetPath);
                    Assert.IsNotNull(
                        saved,
                        "the damaged-evidence probe could not write its " +
                            "foreign Prefab over " + assetPath);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(foreign);
                }
            }

            // Writes a valid, foreign scene over <paramref name="assetPath"/>:
            // the artifact stays an imported scene file whose saved content —
            // a single foreign root — can never match the recorded evidence.
            private static void DamageSceneAsset(string assetPath)
            {
                var foreign = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                // A new GameObject lands in the active scene: making the
                // throwaway scene active plants the root straight into it
                // without dirtying the scene the run left active.
                var previous = SceneManager.GetActiveScene();
                SceneManager.SetActiveScene(foreign);
                try
                {
                    var marker = new GameObject(TamperRootName);
                    Assert.IsTrue(
                        EditorSceneManager.SaveScene(foreign, assetPath),
                        "the damaged-evidence probe could not write its " +
                            "foreign scene over " + assetPath);
                    Assert.IsTrue(marker != null && marker.scene == foreign);
                }
                finally
                {
                    SceneManager.SetActiveScene(previous);
                    EditorSceneManager.CloseScene(foreign, true);
                }
            }

            // Writes a valid, foreign serialized object over
            // <paramref name="assetPath"/> through the same AssetDatabase save
            // the Operations use: the artifact still imports cleanly, while
            // its type or content can never match the recorded evidence. The
            // .mat path gets a Material on the internal error shader — a
            // shader reference no recorded input can equal; every other
            // serialized path gets a bare GUISkin, the ScriptableObject class
            // the native-format importer loads at any serialized path.
            private static void DamageSerializedAsset(string assetPath)
            {
                UnityEngine.Object foreign;
                if (assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                {
                    var shader = Shader.Find("Hidden/InternalErrorShader");
                    Assert.IsNotNull(
                        shader,
                        "the damaged-evidence probe could not resolve the " +
                            "internal error shader.");
                    foreign = new Material(shader);
                }
                else
                {
                    foreign = ScriptableObject.CreateInstance<GUISkin>();
                }
                // A file the artifact database already imported is dropped
                // through DeleteAsset; one written but never imported yet is
                // removed physically — either state leaves CreateAsset a free
                // path to write the foreign object to.
                var fullPath = FullPathOf(assetPath);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
                AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.CreateAsset(foreign, assetPath);
            }

            // Whether the file damage must leave a still-importable
            // serialized asset instead of raw bytes: the path names a
            // serialized asset (a planted file has no bytes to read yet), or
            // the file's own content starts with the %YAML header every
            // native-format artifact carries.
            private static bool IsSerializedAsset(FileDamage damage) =>
                IsSerializedAssetPath(damage.Path) ||
                (damage.Original is { } original && IsYamlAsset(original));

            private static bool IsSerializedAssetPath(string assetPath) =>
                assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ||
                assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase);

            private static readonly byte[] s_yamlMark =
                Encoding.ASCII.GetBytes("%YAML");

            private static bool IsYamlAsset(byte[] content)
            {
                if (content.Length < s_yamlMark.Length)
                {
                    return false;
                }
                for (var i = 0; i < s_yamlMark.Length; i++)
                {
                    if (content[i] != s_yamlMark[i])
                    {
                        return false;
                    }
                }
                return true;
            }

            private void AddRecorded(ChangeRecord record)
            {
                var target = record.Target;
                if (record.Action == ChangeAction.Deleted)
                {
                    // A deleted asset's evidence is a path free again —
                    // re-occupy it. A deleted GameObject's evidence is an
                    // absent object; the world diff reports it with its
                    // scene, and the recreation below covers it.
                    if (target.Kind == "asset" || target.Kind == "scene")
                    {
                        AddRecreated(target.Path);
                    }
                    return;
                }
                if (target.Kind == "asset" || target.Kind == "scene")
                {
                    AddAssetArtifact(target.Path);
                }
                else if (
                    target.Kind == "gameObject" &&
                    target.GlobalId is { } globalId &&
                    GlobalObjectId.TryParse(globalId, out var parsed) &&
                    GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed)
                        is GameObject gameObject)
                {
                    if (record.Action == ChangeAction.Created)
                    {
                        AddReplacement(gameObject);
                    }
                    else
                    {
                        AddDamagedObject(gameObject);
                    }
                }
                // "package" targets are deliberately not damaged — editing
                // the live manifest mid-test is not safe; the fail-closed
                // rule judges a step that left only unprobeable artifacts.
            }

            private void AddObserved(
                HarnessWorldSnapshot.ObservedChange observed,
                List<DeletedObject> deletedObjects)
            {
                switch (observed.Action)
                {
                    case ChangeAction.Created:
                        if (observed.Kind == "asset")
                        {
                            AddAssetArtifact(observed.Path);
                        }
                        else if (observed.Kind == "gameObject")
                        {
                            AddObjectAt(observed.Path, created: true);
                        }
                        break;
                    case ChangeAction.Modified:
                        if (observed.Kind == "asset")
                        {
                            AddAssetArtifact(observed.Path);
                        }
                        else if (observed.Kind == "gameObject")
                        {
                            AddObjectAt(observed.Path, created: false);
                        }
                        break;
                    case ChangeAction.Deleted:
                        if (observed.Kind == "asset")
                        {
                            AddRecreated(observed.Path);
                        }
                        else if (observed.Kind == "gameObject")
                        {
                            AddRecreatedObject(observed.Path, deletedObjects);
                        }
                        break;
                    case ChangeAction.Moved:
                        if (observed.Kind == "asset")
                        {
                            var separator = observed.Path.LastIndexOf(
                                " -> ", StringComparison.Ordinal);
                            if (separator >= 0)
                            {
                                AddRecreated(observed.Path.Substring(0, separator));
                                AddAssetArtifact(
                                    observed.Path.Substring(separator + 4));
                            }
                        }
                        break;
                }
            }

            // A file is damaged through its bytes; a folder cannot be
            // overwritten, so the probe plants a foreign file inside it —
            // the one content check every honest artifact verdict must
            // fail. A folder whose marker name is already taken is left
            // alone rather than damaging something that is not the probe's.
            //
            // A scene asset is the exception: while its scene is loaded,
            // the §F5 evidence lives in the LIVE scene — the expected root
            // configuration and the loaded set it sits in — never in the
            // file's bytes, which an honest Reconcile does not re-read
            // (unity.scene.create's own Reconcile judges the loaded scene,
            // not the saved file). Overwriting those bytes would damage a
            // property no recorded evidence covers — exactly the untested
            // Completed the fail-closed rule exists to reject — so the
            // probe plants a foreign root in the live scene instead. An
            // unloaded scene asset keeps the file damage.
            private void AddAssetArtifact(string assetPath)
            {
                if (!_paths.Add(assetPath))
                {
                    return;
                }
                if (IsSceneAsset(assetPath))
                {
                    var scene = EditorSceneManager.GetSceneByPath(assetPath);
                    if (scene.IsValid() && scene.isLoaded &&
                        !EditorSceneManager.IsPreviewScene(scene))
                    {
                        AddSceneTamper(scene);
                        return;
                    }
                }
                if (AssetDatabase.IsValidFolder(assetPath))
                {
                    var plantedFull = FullPathOf(assetPath + "/" + TamperFileName);
                    if (File.Exists(plantedFull) || Directory.Exists(plantedFull))
                    {
                        return;
                    }
                    _files.Add(new FileDamage(
                        assetPath + "/" + TamperFileName,
                        null,
                        ReadOrNull(plantedFull + ".meta")));
                    return;
                }
                var fullPath = FullPathOf(assetPath);
                if (!File.Exists(fullPath))
                {
                    return;
                }
                _files.Add(new FileDamage(
                    assetPath,
                    File.ReadAllBytes(fullPath),
                    ReadOrNull(fullPath + ".meta")));
            }

            private void AddRecreated(string assetPath)
            {
                if (!_paths.Add(assetPath))
                {
                    return;
                }
                var fullPath = FullPathOf(assetPath);
                var directory = Path.GetDirectoryName(fullPath);
                if (File.Exists(fullPath) || Directory.Exists(fullPath) ||
                    directory is null || !Directory.Exists(directory))
                {
                    // The path is occupied, or recreating it would take
                    // more than a file — nothing simple the probe can
                    // leave and take back.
                    return;
                }
                _files.Add(new FileDamage(
                    assetPath, null, ReadOrNull(fullPath + ".meta")));
            }

            // Plants the foreign root in a loaded scene artifact — the
            // damage no honest root-configuration or loaded-set check can
            // miss. The planted object rides the recreation list, so
            // Apply creates it in the scene and Restore destroys it; the
            // scene's clean-or-dirty flag is captured now so the restore
            // re-saves only a scene the probe found clean.
            private void AddSceneTamper(Scene scene)
            {
                for (var i = 0; i < _sceneTampers.Count; i++)
                {
                    if (_sceneTampers[i].Scene.handle == scene.handle)
                    {
                        return;
                    }
                }
                _sceneTampers.Add(new SceneTamper(scene, scene.isDirty));
                _recreations.Add(new ObjectRecreation(scene, null, TamperRootName));
            }

            private void AddReplacement(GameObject gameObject)
            {
                if (gameObject != null && _seen.Add(gameObject))
                {
                    _replacements.Add(new ObjectReplacement(gameObject));
                }
            }

            private void AddDamagedObject(GameObject gameObject)
            {
                if (gameObject != null && _seen.Add(gameObject))
                {
                    _damaged.Add(new ObjectDamage(gameObject));
                }
            }

            // Resolves a world-diff object key ("<scenePath>|<HierarchyPath>")
            // back to the GameObject it names, so an artifact the Operation
            // never recorded can be damaged the same way.
            private void AddObjectAt(string observedKey, bool created)
            {
                var separator = observedKey.IndexOf('|');
                if (separator < 0 ||
                    !TryResolveScene(
                        observedKey.Substring(0, separator), out var scene) ||
                    !RefHierarchyPath.TryParse(
                        observedKey.Substring(separator + 1), out var segments) ||
                    segments.Length == 0)
                {
                    return;
                }
                Transform? current = FindSegment(null, scene, segments[0]);
                for (var i = 1; current is not null && i < segments.Length; i++)
                {
                    current = FindSegment(current, scene, segments[i]);
                }
                if (current is null)
                {
                    return;
                }
                if (created)
                {
                    AddReplacement(current.gameObject);
                }
                else
                {
                    AddDamagedObject(current.gameObject);
                }
            }

            // The limbo root of <paramref name="scene"/>, created lazily —
            // replaced originals wait under it while their foreign clones
            // occupy the slots evidence checks read.
            private GameObject LimboFor(Scene scene)
            {
                for (var i = 0; i < _limbos.Count; i++)
                {
                    if (_limbos[i] != null && _limbos[i].scene.handle == scene.handle)
                    {
                        return _limbos[i];
                    }
                }
                var limbo = new GameObject(LimboName);
                if (limbo.scene.handle != scene.handle)
                {
                    SceneManager.MoveGameObjectToScene(limbo, scene);
                }
                _limbos.Add(limbo);
                return limbo;
            }

            // Adds a component of the first foreign type the object does not
            // already carry — the extra member every honest component-list
            // check must fail — and answers the component it added, or null
            // when no type could be attached (the content perturbation still
            // stands on its own then).
            private static Component? AddForeignComponent(GameObject target)
            {
                for (var i = 0; i <= s_foreignComponents.Length; i++)
                {
                    var type = i < s_foreignComponents.Length
                        ? s_foreignComponents[i]
                        : typeof(BoxCollider);
                    try
                    {
                        // The fallback round repeats BoxCollider on purpose:
                        // colliders allow duplicates, so a second one still
                        // lengthens the component list.
                        if (i < s_foreignComponents.Length &&
                            target.GetComponent(type) is not null)
                        {
                            continue;
                        }
                        return target.AddComponent(type);
                    }
                    catch (Exception)
                    {
                        // Try the next foreign type.
                    }
                }
                return null;
            }

            // Rewrites the serialized content of every readable component on
            // the object so no serialized-content check can still match —
            // the "added component's content" damage of the component-change
            // row, applied to all of them because the world diff names the
            // object but not the component that changed. Each pre-probe JSON
            // lands in <paramref name="saved"/> for the restore.
            private static void PerturbComponents(
                GameObject target, List<KeyValuePair<Component, string>> saved)
            {
                foreach (var component in target.GetComponents<Component>())
                {
                    if (component is null)
                    {
                        continue;
                    }
                    string json;
                    try
                    {
                        json = EditorJsonUtility.ToJson(component);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    saved.Add(new KeyValuePair<Component, string>(component, json));
                    PerturbComponent(component);
                }
            }

            // Writes each saved pre-probe serialization back over its
            // component — the exact inverse of PerturbComponents.
            private static void RestoreComponents(
                List<KeyValuePair<Component, string>> saved)
            {
                for (var i = 0; i < saved.Count; i++)
                {
                    var pair = saved[i];
                    if (pair.Key != null)
                    {
                        try
                        {
                            EditorJsonUtility.FromJsonOverwrite(pair.Value, pair.Key);
                        }
                        catch (Exception)
                        {
                            // A component that rejects the overwrite stays
                            // perturbed; the restore continues with the rest.
                        }
                    }
                }
            }

            // Rewrites the first editable scalar, vector or color the
            // component's SerializedObject exposes — the one change that
            // makes its serialized content provably differ, whatever the
            // component type is.
            private static void PerturbComponent(Component component)
            {
                SerializedObject serialized;
                try
                {
                    serialized = new SerializedObject(component);
                }
                catch (Exception)
                {
                    return;
                }
                var property = serialized.GetIterator();
                while (property.NextVisible(true))
                {
                    if (property.editable && PerturbProperty(property))
                    {
                        break;
                    }
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            private static bool PerturbProperty(SerializedProperty property)
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Boolean:
                        property.boolValue = !property.boolValue;
                        return true;
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.Enum:
                    case SerializedPropertyType.LayerMask:
                        property.longValue += 1L;
                        return true;
                    case SerializedPropertyType.Float:
                        property.doubleValue += 1.25;
                        return true;
                    case SerializedPropertyType.String:
                        property.stringValue = property.stringValue + "-tampered";
                        return true;
                    case SerializedPropertyType.Vector2:
                        property.vector2Value += new Vector2(1.25f, 2.5f);
                        return true;
                    case SerializedPropertyType.Vector3:
                        property.vector3Value += new Vector3(1.25f, 2.5f, 3.75f);
                        return true;
                    case SerializedPropertyType.Vector4:
                        property.vector4Value += new Vector4(1.25f, 2.5f, 3.75f, 5f);
                        return true;
                    case SerializedPropertyType.Quaternion:
                        property.quaternionValue = Quaternion.Euler(11.25f, 22.5f, 33.75f);
                        return true;
                    case SerializedPropertyType.Color:
                        property.colorValue = new Color(0.3f, 0.47f, 0.73f, 0.89f);
                        return true;
                    case SerializedPropertyType.Rect:
                        var rect = property.rectValue;
                        property.rectValue = new Rect(
                            rect.x + 1.25f, rect.y + 2.5f, rect.width, rect.height);
                        return true;
                    default:
                        return false;
                }
            }

            // A deleted GameObject's evidence is its absence and a parent
            // child list that still matches the snapshot minus the target;
            // the probe puts a foreign object of the same name back under
            // the resolved parent — or at the scene's root — so both checks
            // fail. A deleted subtree is probed once, at the shallowest
            // missing ancestor, not once per descendant.
            private void AddRecreatedObject(
                string observedKey, List<DeletedObject> deletedObjects)
            {
                DeletedObject? self = null;
                for (var i = 0; i < deletedObjects.Count; i++)
                {
                    if (string.Equals(
                        deletedObjects[i].Key, observedKey, StringComparison.Ordinal))
                    {
                        self = deletedObjects[i];
                        break;
                    }
                }
                if (self is null)
                {
                    return;
                }
                for (var i = 0; i < deletedObjects.Count; i++)
                {
                    var ancestor = deletedObjects[i];
                    if (ancestor.Segments.Length < self.Segments.Length &&
                        string.Equals(
                            ancestor.ScenePath, self.ScenePath,
                            StringComparison.Ordinal) &&
                        IsPrefixOf(ancestor.Segments, self.Segments))
                    {
                        return;
                    }
                }
                if (!TryResolveScene(self.ScenePath, out var scene))
                {
                    return;
                }
                Transform? parent = null;
                for (var i = 0; i + 1 < self.Segments.Length; i++)
                {
                    parent = FindSegment(parent, scene, self.Segments[i]);
                    if (parent is null)
                    {
                        return;
                    }
                }
                _recreations.Add(new ObjectRecreation(
                    scene, parent, self.Segments[self.Segments.Length - 1].Name));
            }

            private static List<DeletedObject> ParseDeletedObjects(
                List<HarnessWorldSnapshot.ObservedChange> observed)
            {
                var deleted = new List<DeletedObject>();
                for (var i = 0; i < observed.Count; i++)
                {
                    var change = observed[i];
                    if (change.Action != ChangeAction.Deleted ||
                        change.Kind != "gameObject")
                    {
                        continue;
                    }
                    var separator = change.Path.IndexOf('|');
                    if (separator < 0 ||
                        !RefHierarchyPath.TryParse(
                            change.Path.Substring(separator + 1),
                            out var segments) ||
                        segments.Length == 0)
                    {
                        continue;
                    }
                    deleted.Add(new DeletedObject(
                        change.Path,
                        change.Path.Substring(0, separator),
                        segments));
                }
                return deleted;
            }

            private static bool IsPrefixOf(
                RefHierarchyPath.Segment[] prefix, RefHierarchyPath.Segment[] path)
            {
                for (var i = 0; i < prefix.Length; i++)
                {
                    if (!string.Equals(
                            prefix[i].Name, path[i].Name, StringComparison.Ordinal) ||
                        prefix[i].Index != path[i].Index)
                    {
                        return false;
                    }
                }
                return true;
            }

            // The scene a world-diff object key names. An untitled scene
            // carries no path to look up by, so the empty path resolves to
            // the loaded untitled scene itself.
            private static bool TryResolveScene(string scenePath, out Scene scene)
            {
                var byPath = EditorSceneManager.GetSceneByPath(scenePath);
                if (byPath.IsValid() && byPath.isLoaded)
                {
                    scene = byPath;
                    return true;
                }
                if (scenePath.Length == 0)
                {
                    for (var i = 0; i < EditorSceneManager.sceneCount; i++)
                    {
                        var candidate = EditorSceneManager.GetSceneAt(i);
                        if (candidate.isLoaded && candidate.path.Length == 0)
                        {
                            scene = candidate;
                            return true;
                        }
                    }
                }
                scene = default;
                return false;
            }

            private static Transform? FindSegment(
                Transform? parent, Scene scene, RefHierarchyPath.Segment segment)
            {
                var wanted = segment.Index < 0 ? 0 : segment.Index;
                var match = -1;
                if (parent is null)
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (root.name == segment.Name && ++match == wanted)
                        {
                            return root.transform;
                        }
                    }
                    return null;
                }
                for (var i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (child.name == segment.Name && ++match == wanted)
                    {
                        return child;
                    }
                }
                return null;
            }

            private static string FullPathOf(string assetPath) =>
                Path.Combine(Directory.GetCurrentDirectory(), assetPath);

            private static byte[]? ReadOrNull(string path) =>
                File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        // The Preflight snapshot a harness journal persisted inside its
        // step_started record — the JSON Reconcile is handed in a real Run
        // (SPEC §F5 common rule 1).
        private static JsonValue SnapshotOf(HarnessResult result)
        {
            for (var i = result.Journal.Count - 1; i >= 0; i--)
            {
                if (result.Journal[i].Type != JournalRecordType.StepStarted)
                {
                    continue;
                }
                var snapshot = result.Journal[i].Data.ToToken()["snapshot"];
                Assert.IsNotNull(
                    snapshot,
                    "the step_started record carried no snapshot member.");
                return JsonValue.From(snapshot!);
            }
            Assert.Fail("the interrupted run's journal carried no step_started record.");
            throw new InvalidOperationException();
        }

        // The SPEC §M.6 item 4 evidence read for a step whose Execute
        // ended at the §G4 suspension: the probe is invoked exactly once,
        // right after that step's Reconcile returned and before anything
        // changes the world the verdict judged, and its answer picks the
        // §F5 row the verdict must satisfy — Applied admits Completed or
        // Unknown, NotApplied admits NotApplied or Unknown, Indeterminate
        // admits Unknown only. A null probe — the two-argument
        // AssertIdempotency — judges this case as Indeterminate: missing
        // evidence is never treated as proof of absence, so it can never
        // false-pass. A probe that throws, or that answers outside the
        // enum, fails the contract.
        private static EffectEvidence ProbeSuspendedEvidence(
            string operationId, Func<EffectEvidence>? evidenceProbe)
        {
            if (evidenceProbe is null)
            {
                return EffectEvidence.Indeterminate;
            }
            EffectEvidence evidence;
            try
            {
                evidence = evidenceProbe();
            }
            catch (Exception exception)
            {
                Assert.Fail(
                    $"the evidence probe for '{operationId}' threw " +
                        $"{exception.GetType().Name} — a probe that cannot " +
                        "answer fails the contract (SPEC §M.6 item 4).");
                throw;
            }
            if (evidence != EffectEvidence.Applied &&
                evidence != EffectEvidence.NotApplied &&
                evidence != EffectEvidence.Indeterminate)
            {
                Assert.Fail(
                    $"the evidence probe for '{operationId}' answered " +
                        $"{(int)evidence}, which names no EffectEvidence " +
                        "member — the contract fails (SPEC §M.6 item 4).");
            }
            return evidence;
        }

        // Calls Reconcile on a context assembled exactly like the harness's —
        // the same input conversion, registry and sink types — and turns a
        // throw into a contract failure. <paramref name="resources"/> is the
        // §E6 item 4 acquisition state the interrupted run left behind — the
        // same step's journaled Run resources, which the normal after-Execute
        // judgement needs so a contents-session Completed can be proven
        // (SPEC §F5, §G6 item 5). The evidence-only probes pass none: the
        // before-Execute and damaged-artifact cases keep the empty registry
        // because the absent resource record is the damage they apply.
        private static ReconcileResult ReconcileOrFail(
            IOperation operation,
            OperationDescriptor descriptor,
            object inputs,
            JsonValue snapshot,
            string scenario,
            IReadOnlyList<TrackedRunResource>? resources = null)
        {
            var built = HarnessInputs.Build(descriptor, inputs, ContractNodeId);
            Assert.That(
                built.Ok && built.Value is not null,
                () => $"the inputs could not be rebuilt for Reconcile " +
                    $"({scenario}):\n" + Describe(built.Diagnostics));
            var journal = new HarnessJournal("opweave-contract");
            var changes = new StepChangeRecorder(0, ContractNodeId);
            var log = new StepLogSink(
                "opweave-contract", 0, journal.AppendFromLogSink);
            StepContext context = new UnityStepContext(
                built.Value!,
                HarnessInputs.InputTypes(descriptor),
                HarnessInputs.OutputTypes(descriptor),
                HarnessTypes.Registry,
                new RefResolver(PrefabContentsResources.FindEditingRoot),
                changes,
                log,
                operationId: descriptor.Id,
                operationOrigin: OperationTestHarness.OriginOf(operation.GetType()));
            // The registry reflects what the interrupted run actually left:
            // the normal after-Execute judgement gets the step's own tracked
            // acquisitions (SPEC §F5: the same step's Run resource), the
            // evidence-only probes none — their damage is the absent record.
            ((UnityStepContext)context).Resources = new StepResourceRegistry(
                OperationTestHarness.RunScopeRunId, 0,
                resources is null
                    ? new List<TrackedRunResource>()
                    : new List<TrackedRunResource>(resources),
                (type, step, data) => journal.AppendFromLogSink(
                    OperationTestHarness.RunScopeRunId, type, step, data));
            ReconcileResult verdict;
            try
            {
                verdict = operation.Reconcile(context, snapshot);
            }
            catch (Exception exception)
            {
                Assert.Fail(
                    $"Reconcile threw {exception.GetType().Name} ({scenario}).");
                throw;
            }
            Assert.IsNotNull(verdict, $"Reconcile returned no verdict ({scenario}).");
            return verdict!;
        }

        // The Codec round-trip the Plan-bound §E5 check would apply on a real
        // run: each present input — as the step actually consumed it — and
        // each written output must validate under its port's Codec and
        // Normalize idempotently (SPEC §B7.1).
        private static void CheckPortValues(
            OperationDescriptor descriptor, object inputs, HarnessResult result)
        {
            var failures = new List<string>();
            var built = HarnessInputs.Build(descriptor, inputs, ContractNodeId);
            if (built.Ok && built.Value is not null)
            {
                foreach (var port in descriptor.Inputs)
                {
                    if (built.Value[port.Name].IsPresent)
                    {
                        CheckPortValue(
                            port, built.Value[port.Name].Value!, "input", failures);
                    }
                }
            }
            foreach (var port in descriptor.Outputs)
            {
                if (result.Outputs.TryGetValue(port.Name, out var value))
                {
                    CheckPortValue(port, value, "output", failures);
                }
            }
            Assert.That(
                failures,
                Is.Empty,
                () => $"port Codec violations for '{descriptor.Id}':\n" +
                    string.Join("\n", failures));
        }

        private static void CheckPortValue(
            PortDescriptor port, JsonValue value, string direction, List<string> failures)
        {
            var parsed = TypeRef.Parse(port.Type);
            if (!parsed.Ok || parsed.Value is null)
            {
                // A malformed port type is AssertDescriptorValid's finding.
                return;
            }
            var resolved = HarnessTypes.Registry.Resolve(parsed.Value);
            if (!resolved.Ok || resolved.Value is null || resolved.Value.Codec is null)
            {
                return;
            }
            var codec = resolved.Value.Codec;
            IReadOnlyList<Diagnostic> diagnostics;
            try
            {
                diagnostics = codec.Validate(value, port.Constraints);
            }
            catch (Exception exception)
            {
                failures.Add(
                    $"{direction} port '{port.Name}': the Codec's Validate threw " +
                        exception.GetType().Name + ".");
                return;
            }
            if (diagnostics is null)
            {
                failures.Add(
                    $"{direction} port '{port.Name}': the Codec's Validate returned no result.");
                return;
            }
            for (var i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i].Severity == Severity.Error)
                {
                    failures.Add(
                        $"{direction} port '{port.Name}': {diagnostics[i].Code}: " +
                            diagnostics[i].Message);
                }
            }
            JsonValue normalized;
            try
            {
                normalized = codec.Normalize(value);
            }
            catch (Exception exception)
            {
                failures.Add(
                    $"{direction} port '{port.Name}': the Codec's Normalize threw " +
                        exception.GetType().Name + ".");
                return;
            }
            if (normalized is null)
            {
                failures.Add(
                    $"{direction} port '{port.Name}': the Codec's Normalize returned null.");
                return;
            }
            JsonValue twice;
            try
            {
                twice = codec.Normalize(normalized);
            }
            catch (Exception exception)
            {
                failures.Add(
                    $"{direction} port '{port.Name}': the Codec's second Normalize threw " +
                        exception.GetType().Name + ".");
                return;
            }
            if (twice is null || !string.Equals(
                normalized.CanonicalText, twice.CanonicalText, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{direction} port '{port.Name}': the Codec's Normalize is not idempotent.");
            }
        }

        // The SPEC §C4 effect set that could honestly cover one observed or
        // recorded change; null when the target kind is outside the known
        // kinds — an unclassifiable change is skipped, because the world diff
        // still reports its physical effect under a known kind.
        private static string[]? RequiredEffectsFor(
            ChangeAction action, string kind, string? path)
        {
            switch (kind)
            {
                case "asset":
                    switch (action)
                    {
                        case ChangeAction.Created:
                            return new[] { EffectNames.AssetCreate };
                        case ChangeAction.Modified:
                            // A changed .unity file is a scene save; any
                            // other changed asset is an asset modification —
                            // SPEC §C4 keeps them distinct, so a scene.save
                            // declaration cannot cover a data asset.
                            return IsSceneAsset(path)
                                ? new[] { EffectNames.AssetModify, EffectNames.SceneSave }
                                : new[] { EffectNames.AssetModify };
                        case ChangeAction.Deleted:
                            return new[] { EffectNames.AssetDelete };
                        case ChangeAction.Moved:
                            return new[] { EffectNames.AssetMove };
                        case ChangeAction.Saved:
                            return IsSceneAsset(path)
                                ? new[] { EffectNames.SceneSave, EffectNames.AssetModify }
                                : new[] { EffectNames.AssetModify };
                        default:
                            return null;
                    }
                case "gameObject":
                case "scene":
                    switch (action)
                    {
                        case ChangeAction.Saved:
                            return new[] { EffectNames.SceneSave };
                        case ChangeAction.Created:
                        case ChangeAction.Modified:
                        case ChangeAction.Deleted:
                        case ChangeAction.Moved:
                            return new[] { EffectNames.SceneModify };
                        default:
                            return null;
                    }
                case "package":
                    return new[] { EffectNames.PackageModify };
                default:
                    return null;
            }
        }

        // Whether a target path names a scene asset — the only asset kind a
        // scene.save declaration may cover.
        private static bool IsSceneAsset(string? path) =>
            path is not null &&
            path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);

        private static void RequireEffect(
            HashSet<string> declared,
            ChangeAction action,
            string kind,
            string? path,
            string what,
            List<string> violations)
        {
            var candidates = RequiredEffectsFor(action, kind, path);
            if (candidates is null)
            {
                return;
            }
            for (var i = 0; i < candidates.Length; i++)
            {
                if (declared.Contains(candidates[i]))
                {
                    return;
                }
            }
            violations.Add(
                $"{what} requires one of [{string.Join(", ", candidates)}] " +
                "but none was declared.");
        }

        private static string VerdictName(ReconcileResult verdict) =>
            verdict is ReconcileResult.Completed ? "Completed"
                : verdict is ReconcileResult.NotApplied ? "NotApplied"
                : verdict is ReconcileResult.Unknown ? "Unknown"
                : verdict.GetType().Name;

        private static string Describe(
            HarnessWorldSnapshot.ObservedChange change) =>
            $"change '{change.Action} {change.Kind} {change.Path}'";

        private static string Describe(
            List<HarnessWorldSnapshot.ObservedChange> changes)
        {
            var lines = new List<string>();
            for (var i = 0; i < changes.Count; i++)
            {
                lines.Add(Describe(changes[i]));
            }
            return string.Join("\n", lines);
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var lines = new List<string>();
            for (var i = 0; i < diagnostics.Count; i++)
            {
                lines.Add(diagnostics[i].Code + ": " + diagnostics[i].Message);
            }
            return string.Join("\n", lines);
        }
    }
}
