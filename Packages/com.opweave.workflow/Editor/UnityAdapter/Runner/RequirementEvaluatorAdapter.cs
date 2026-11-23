#nullable enable

using System;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The narrow seam between the merged SPEC §G1 item 4 evaluators
    /// (<see cref="EditorRequirementEvaluator"/>, T-W2.1-02) and the Core run
    /// loop's injected runtime precondition check (SPEC §E4 step 2): resolves
    /// the declared port's input value out of the step
    /// <see cref="StepContext"/> and maps the <see cref="RequirementResult"/>
    /// verdict onto Core's <see cref="RequirementEvaluation"/> — a violation
    /// keeps its diagnostic so the step fails with it before Preflight, and
    /// the <c>not_compiling</c> Wait carries no diagnostic and leaves the
    /// step unstarted (the 2026-11-06 owner decision).
    /// </summary>
    /// <remarks>
    /// A port-bound requirement whose port carries no value — unsupplied or
    /// an explicit JSON <c>null</c> — is passed a null port value, matching
    /// <see cref="EditorRequirementEvaluator"/>'s "no value → satisfied"
    /// reading (the Descriptor default takes over). A pending Dry-Run
    /// placeholder port is also passed as null; real Runs have no pending
    /// ports (SPEC §D8).
    /// </remarks>
    internal sealed class RequirementEvaluatorAdapter : IRequirementEvaluator
    {
        private readonly EditorRequirementEvaluator _evaluator;

        /// <summary>Wraps the merged §G1 evaluator set.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="evaluator"/> is null.</exception>
        internal RequirementEvaluatorAdapter(EditorRequirementEvaluator evaluator) =>
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

        /// <inheritdoc cref="IRequirementEvaluator.Evaluate"/>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="requirement"/> or <paramref name="context"/> is null.
        /// </exception>
        public RequirementEvaluation Evaluate(Requirement requirement, StepContext context)
        {
            if (requirement is null)
            {
                throw new ArgumentNullException(nameof(requirement));
            }
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            JsonValue? portValue = null;
            if (requirement.Port is { } port &&
                context.IsPresent(port) && !context.IsPending(port))
            {
                portValue = context.Get<JsonValue>(port);
            }
            // The Operation the requirement was declared by — the step
            // context is built for that same step — so a port-bound kind can
            // read its declared destination (T-W2.7-05). A non-Unity context
            // cannot name it and keeps the anonymous file-destination
            // reading.
            var result = _evaluator.Evaluate(
                requirement,
                portValue,
                nodeId: null,
                operationId: (context as UnityStepContext)?.BoundOperationId);
            switch (result.Verdict)
            {
                case RequirementVerdict.Satisfied:
                    return RequirementEvaluation.Satisfied();
                case RequirementVerdict.Wait:
                    return RequirementEvaluation.Wait();
                default:
                    return RequirementEvaluation.Violated(result.Diagnostic!);
            }
        }
    }
}
