#nullable enable

using System;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The outcome an <see cref="IRequirementEvaluator"/> returns for one
    /// <see cref="Operations.Requirement"/> (SPEC §G1 item 4, §E4 step 2):
    /// <see cref="Satisfied"/>, <see cref="Violated"/> carrying the diagnostic
    /// the step fails with, or <see cref="Wait"/> — the "not satisfied, wait"
    /// answer of the 2026-11-06 owner decision that only <c>not_compiling</c>
    /// produces (no error code, no StepStarted, re-evaluated next Tick).
    /// </summary>
    internal readonly struct RequirementEvaluation
    {
        private readonly Diagnostic? _error;

        private RequirementEvaluation(RequirementStatus status, Diagnostic? error)
        {
            Status = status;
            _error = error;
        }

        /// <summary>Which of the three SPEC §E4 step-2 outcomes this is.</summary>
        public RequirementStatus Status { get; }

        /// <summary>
        /// The <see cref="RequirementStatus.Violated"/> payload — the
        /// diagnostic the step fails with; <see langword="null"/> for the
        /// other statuses.
        /// </summary>
        public Diagnostic? Error => _error;

        /// <summary>The precondition holds.</summary>
        public static RequirementEvaluation Satisfied() =>
            new RequirementEvaluation(RequirementStatus.Satisfied, null);

        /// <summary>
        /// Not satisfied, wait — the <c>not_compiling</c> outcome of the
        /// 2026-11-06 owner decision: carries no diagnostic.
        /// </summary>
        public static RequirementEvaluation Wait() =>
            new RequirementEvaluation(RequirementStatus.Wait, null);

        /// <summary>The precondition is violated; the step fails with <paramref name="error"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
        public static RequirementEvaluation Violated(Diagnostic error)
        {
            if (error is null)
            {
                throw new ArgumentNullException(nameof(error));
            }
            return new RequirementEvaluation(RequirementStatus.Violated, error);
        }
    }
}
