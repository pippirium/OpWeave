#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// One resolved step of a <see cref="CompiledPlan"/> (SPEC §D7): the node in
    /// D5 execution order, its Operation identity and fingerprints, every input
    /// port bound to exactly one <see cref="InputSource"/>, the resolved output
    /// types, the control transitions, the actual effects, and the fixed
    /// timeout/retry/idempotency contract.
    /// </summary>
    /// <remarks>
    /// <see cref="Transitions"/> maps a control port to the next step index or
    /// <c>-1</c> for End; v1 emits <c>{"next": i+1}</c> and the Runner follows
    /// this table only (SPEC §D7 item 2, §N.1). <see cref="OutputTypes"/> values
    /// are canonical Type ID strings after Descriptor
    /// <c>OutputRefinement</c> (SPEC §D4 item 6).
    /// </remarks>
    public sealed record PlanStep(
        int Index,
        string NodeId,
        string OperationId,
        int Version,
        string DescriptorHash,
        string ImplementationHash,
        IReadOnlyDictionary<string, InputSource> Inputs,
        IReadOnlyDictionary<string, string> OutputTypes,
        IReadOnlyDictionary<string, int> Transitions,
        IReadOnlyList<string> Effects,
        int TimeoutSeconds,
        Operations.RetryPolicy Retry,
        Operations.Idempotency Idempotency);
}
