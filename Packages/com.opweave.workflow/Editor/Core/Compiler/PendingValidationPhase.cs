#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The no-op placeholder occupying a pipeline slot whose rules are delivered by a
    /// later ticket (D3–D6, D8; SPEC §D1 item 1). It keeps the phase order and the
    /// D2-stop / exclusion machinery observable and tested before the real checks
    /// land, and reports nothing.
    /// </summary>
    internal sealed class PendingValidationPhase : IValidationPhase
    {
        public PendingValidationPhase(ValidationPhaseKind kind) => Kind = kind;

        public ValidationPhaseKind Kind { get; }

        public void Run(ValidationContext context)
        {
        }
    }
}
