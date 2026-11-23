#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Tests
{
    internal sealed class StateRulesTests
    {
        // Independent transcription of the SPEC §E1 Run table (the
        // "次に取りうる状態" column); Succeeded / Failed / Cancelled are
        // terminal and the table has no self-transitions.
        private static readonly HashSet<(RunState, RunState)> s_runTable =
            new HashSet<(RunState, RunState)>
            {
                (RunState.Queued, RunState.Running),
                (RunState.Queued, RunState.Cancelled),
                (RunState.AwaitingApproval, RunState.Queued),
                (RunState.AwaitingApproval, RunState.Cancelled),
                (RunState.Running, RunState.Suspended),
                (RunState.Running, RunState.Succeeded),
                (RunState.Running, RunState.Failed),
                (RunState.Running, RunState.Cancelled),
                (RunState.Running, RunState.NeedsReview),
                (RunState.Suspended, RunState.Running),
                (RunState.Suspended, RunState.NeedsReview),
                (RunState.Suspended, RunState.Cancelled),
                (RunState.NeedsReview, RunState.Running),
                (RunState.NeedsReview, RunState.Cancelled),
            };

        // Independent transcription of the SPEC §E0 Step decision table;
        // Succeeded / Failed / Cancelled / Skipped are terminal. Running →
        // Running is the §E7 automatic retry and Waiting → Waiting is the
        // §E4 Poll.
        private static readonly HashSet<(StepState, StepState)> s_stepTable =
            new HashSet<(StepState, StepState)>
            {
                (StepState.Pending, StepState.Running),
                (StepState.Pending, StepState.Skipped),
                (StepState.Pending, StepState.Cancelled),
                (StepState.Running, StepState.Running),
                (StepState.Running, StepState.Waiting),
                (StepState.Running, StepState.Succeeded),
                (StepState.Running, StepState.Failed),
                (StepState.Running, StepState.Cancelled),
                (StepState.Running, StepState.NeedsReview),
                (StepState.Waiting, StepState.Waiting),
                (StepState.Waiting, StepState.Succeeded),
                (StepState.Waiting, StepState.Failed),
                (StepState.Waiting, StepState.Cancelled),
                (StepState.Waiting, StepState.NeedsReview),
                (StepState.NeedsReview, StepState.Running),
                (StepState.NeedsReview, StepState.Cancelled),
            };

        private static readonly RunState[] s_runStates =
            (RunState[])Enum.GetValues(typeof(RunState));

        private static readonly StepState[] s_stepStates =
            (StepState[])Enum.GetValues(typeof(StepState));

        // SPEC §E1 test: every transition in the table is allowed and every
        // transition outside it (e.g. Succeeded → Running) is rejected.
        [Test]
        public void RunTransitionsMatchSpecE1TableExactly()
        {
            foreach (var from in s_runStates)
            {
                foreach (var to in s_runStates)
                {
                    Assert.AreEqual(
                        s_runTable.Contains((from, to)),
                        StateRules.CanTransition(from, to),
                        $"RunState {from} -> {to}");
                }
            }
        }

        // SPEC §E0 test: the allowed set equals the decision table exactly.
        [Test]
        public void StepTransitionsMatchSpecE0TableExactly()
        {
            foreach (var from in s_stepStates)
            {
                foreach (var to in s_stepStates)
                {
                    Assert.AreEqual(
                        s_stepTable.Contains((from, to)),
                        StateRules.CanTransition(from, to),
                        $"StepState {from} -> {to}");
                }
            }
        }

        [Test]
        public void SpecE1ExampleSucceededToRunningIsRejected()
        {
            Assert.IsFalse(StateRules.CanTransition(RunState.Succeeded, RunState.Running));
            Assert.IsFalse(StateRules.CanTransition(StepState.Succeeded, StepState.Running));
        }

        [Test]
        public void RunTableHasNoSelfTransitions()
        {
            foreach (var state in s_runStates)
            {
                Assert.IsFalse(StateRules.CanTransition(state, state), $"RunState {state} -> {state}");
            }
        }

        [Test]
        public void StepAutoRetryStaysRunningAndPollStaysWaiting()
        {
            // §E7: automatic retry keeps Running with attempt + 1, never Failed.
            Assert.IsTrue(StateRules.CanTransition(StepState.Running, StepState.Running));
            // §E4: Poll keeps Waiting.
            Assert.IsTrue(StateRules.CanTransition(StepState.Waiting, StepState.Waiting));
            Assert.IsFalse(StateRules.CanTransition(StepState.Pending, StepState.Pending));
            Assert.IsFalse(StateRules.CanTransition(StepState.NeedsReview, StepState.NeedsReview));
        }

        [Test]
        public void PendingStepAtRunEndBecomesSkipped()
        {
            // §E1: a step still Pending when the Run ends is Skipped.
            Assert.IsTrue(StateRules.CanTransition(StepState.Pending, StepState.Skipped));
        }

        [Test]
        public void NeedsReviewResolvesToRetryStepOrAbort()
        {
            // §E1: NeedsReview -> Running is retry_step, -> Cancelled is abort.
            Assert.IsTrue(StateRules.CanTransition(RunState.NeedsReview, RunState.Running));
            Assert.IsTrue(StateRules.CanTransition(RunState.NeedsReview, RunState.Cancelled));
            Assert.IsTrue(StateRules.CanTransition(StepState.NeedsReview, StepState.Running));
            Assert.IsTrue(StateRules.CanTransition(StepState.NeedsReview, StepState.Cancelled));
        }

        [Test]
        public void RequireTransitionReturnsTargetForEveryListedRunPair()
        {
            foreach (var (from, to) in s_runTable)
            {
                Assert.AreEqual(to, StateRules.RequireTransition(from, to));
            }
        }

        [Test]
        public void RequireTransitionReturnsTargetForEveryListedStepPair()
        {
            foreach (var (from, to) in s_stepTable)
            {
                Assert.AreEqual(to, StateRules.RequireTransition(from, to));
            }
        }

        [Test]
        public void RequireTransitionThrowsForEveryUnlistedRunPair()
        {
            foreach (var from in s_runStates)
            {
                foreach (var to in s_runStates)
                {
                    if (s_runTable.Contains((from, to)))
                    {
                        continue;
                    }
                    Assert.Throws<ArgumentException>(
                        () => StateRules.RequireTransition(from, to),
                        $"RunState {from} -> {to}");
                }
            }
        }

        [Test]
        public void RequireTransitionThrowsForEveryUnlistedStepPair()
        {
            foreach (var from in s_stepStates)
            {
                foreach (var to in s_stepStates)
                {
                    if (s_stepTable.Contains((from, to)))
                    {
                        continue;
                    }
                    Assert.Throws<ArgumentException>(
                        () => StateRules.RequireTransition(from, to),
                        $"StepState {from} -> {to}");
                }
            }
        }

        [Test]
        public void UndeclaredEnumValuesAreRejected()
        {
            var undeclaredRun = (RunState)255;
            var undeclaredStep = (StepState)255;

            Assert.IsFalse(StateRules.CanTransition(undeclaredRun, RunState.Running));
            Assert.IsFalse(StateRules.CanTransition(RunState.Queued, undeclaredRun));
            Assert.IsFalse(StateRules.CanTransition(undeclaredStep, StepState.Running));
            Assert.IsFalse(StateRules.CanTransition(StepState.Pending, undeclaredStep));

            Assert.Throws<ArgumentException>(
                () => StateRules.RequireTransition(undeclaredRun, RunState.Running));
            Assert.Throws<ArgumentException>(
                () => StateRules.RequireTransition(RunState.Queued, undeclaredRun));
            Assert.Throws<ArgumentException>(
                () => StateRules.RequireTransition(undeclaredStep, StepState.Running));
            Assert.Throws<ArgumentException>(
                () => StateRules.RequireTransition(StepState.Pending, undeclaredStep));
        }
    }
}
