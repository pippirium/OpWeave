#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The normative Run and Step transition tables of SPEC §E1 and §E0
    /// (decision Q2). A transition absent from the table is an implementation
    /// error, never a reachable runtime state: <see cref="CanTransition(RunState, RunState)"/>
    /// reports false and <see cref="RequireTransition(RunState, RunState)"/> throws,
    /// detecting the bug as SPEC §E1 requires.
    /// </summary>
    /// <remarks>
    /// Step self-transitions are table rows: Running → Running is the §E7
    /// automatic retry (the step stays Running while attempt increments and
    /// never passes through Failed) and Waiting → Waiting is the §E4 Poll.
    /// Pending → Skipped covers the §E1 rule that a step still Pending when the
    /// Run ends becomes Skipped. The Run table has no self-transitions.
    /// </remarks>
    public static class StateRules
    {
        // SPEC §E1 Run table: the "次に取りうる状態" column, expanded to one row
        // per allowed pair. Succeeded / Failed / Cancelled are terminal.
        private static readonly HashSet<(RunState, RunState)> s_runAllowed =
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

        // SPEC §E0 Step decision table, expanded to one row per allowed pair.
        // Succeeded / Failed / Cancelled / Skipped are terminal.
        private static readonly HashSet<(StepState, StepState)> s_stepAllowed =
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

        /// <summary>
        /// True when <paramref name="from"/> → <paramref name="to"/> is a row of
        /// the SPEC §E1 Run table; false for every unlisted pair, including any
        /// undeclared <see cref="RunState"/> value.
        /// </summary>
        public static bool CanTransition(RunState from, RunState to) =>
            s_runAllowed.Contains((from, to));

        /// <summary>
        /// True when <paramref name="from"/> → <paramref name="to"/> is a row of
        /// the SPEC §E0 Step table; false for every unlisted pair, including any
        /// undeclared <see cref="StepState"/> value.
        /// </summary>
        public static bool CanTransition(StepState from, StepState to) =>
            s_stepAllowed.Contains((from, to));

        /// <summary>
        /// Asserts that <paramref name="from"/> → <paramref name="to"/> is a row
        /// of the SPEC §E1 Run table and returns <paramref name="to"/>, so a
        /// Runner can write
        /// <c>state = StateRules.RequireTransition(state, next)</c>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The pair is absent from the SPEC §E1 table — an implementation bug,
        /// detected by throwing (SPEC §E1: unlisted transitions are exceptions).
        /// </exception>
        public static RunState RequireTransition(RunState from, RunState to)
        {
            if (!CanTransition(from, to))
            {
                throw new ArgumentException(
                    $"Illegal RunState transition {from} -> {to}; the pair is absent from the SPEC E1 table.",
                    nameof(to));
            }
            return to;
        }

        /// <summary>
        /// Asserts that <paramref name="from"/> → <paramref name="to"/> is a row
        /// of the SPEC §E0 Step table and returns <paramref name="to"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The pair is absent from the SPEC §E0 table — an implementation bug,
        /// detected by throwing (SPEC §E1: unlisted transitions are exceptions).
        /// </exception>
        public static StepState RequireTransition(StepState from, StepState to)
        {
            if (!CanTransition(from, to))
            {
                throw new ArgumentException(
                    $"Illegal StepState transition {from} -> {to}; the pair is absent from the SPEC E0 table.",
                    nameof(to));
            }
            return to;
        }
    }
}
