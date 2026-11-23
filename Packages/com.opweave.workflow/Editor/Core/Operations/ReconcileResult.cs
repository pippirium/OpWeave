#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The closed outcome set of <see cref="IOperation.Reconcile"/> (SPEC §F5):
    /// whether a step whose StepStarted journal record has no completion record
    /// was already applied, was not applied, or cannot be decided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hierarchy is sealed to the three SPEC §F5 outcomes — the private
    /// constructor keeps external code from adding a fourth verdict.
    /// <see cref="Completed"/> is returned only when the §F5 evidence criteria
    /// hold (pre-execution snapshot, identified artifact, expected content,
    /// restorable outputs); <see cref="NotApplied"/> only when non-application
    /// and re-execution safety are both provable. Anything else is
    /// <see cref="Unknown"/>, which the Runner turns into NeedsReview — never
    /// into automatic success or re-execution.
    /// </para>
    /// </remarks>
    public abstract record ReconcileResult
    {
        private ReconcileResult()
        {
        }

        /// <summary>
        /// The step provably ran to completion; <see cref="Outputs"/> restores
        /// every declared output so the Run can continue (SPEC §F5).
        /// </summary>
        public sealed record Completed(
            IReadOnlyDictionary<string, JsonValue> Outputs) : ReconcileResult
        {
            private readonly IReadOnlyDictionary<string, JsonValue> _outputs =
                CheckOutputs(Outputs);

            /// <summary>The restored output values keyed by output port name.</summary>
            public IReadOnlyDictionary<string, JsonValue> Outputs
            {
                get => _outputs;
                init => _outputs = CheckOutputs(value);
            }

            private static IReadOnlyDictionary<string, JsonValue> CheckOutputs(
                IReadOnlyDictionary<string, JsonValue> outputs)
            {
                var copy = ReadOnlyCollections.CopyMap(outputs);
                foreach (var pair in copy)
                {
                    if (pair.Value is null)
                    {
                        throw new ArgumentException(
                            "Restored output values must not be null.", nameof(outputs));
                    }
                }
                return copy;
            }
        }

        /// <summary>
        /// The step provably did not apply its effect and can be re-executed
        /// safely (SPEC §F5).
        /// </summary>
        public sealed record NotApplied : ReconcileResult;

        /// <summary>
        /// The outcome cannot be proven; the Run goes to NeedsReview
        /// (SPEC §F5). <see cref="Reason"/> is fixed English text.
        /// </summary>
        public sealed record Unknown(string Reason) : ReconcileResult
        {
            private readonly string _reason =
                string.IsNullOrEmpty(Reason)
                    ? throw new ArgumentException(
                        "An Unknown verdict must carry a reason.", nameof(Reason))
                    : Reason;

            /// <summary>Why the evidence was insufficient, as fixed English text.</summary>
            public string Reason
            {
                get => _reason;
                init => _reason =
                    string.IsNullOrEmpty(value)
                        ? throw new ArgumentException(
                            "An Unknown verdict must carry a reason.", nameof(Reason))
                        : value;
            }
        }
    }
}
