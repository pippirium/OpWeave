#nullable enable

using System.Collections.Generic;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// Everything the D8 Dry Run phase hands to its injected
    /// <see cref="StepContext"/> factory for one step (SPEC §0.6 SG-06,
    /// §D8): the node's identity and resolved contract, the per-port input
    /// values and static Type IDs of SPEC §D4 item 6, the recorded output
    /// types, and the <see cref="PendingProducers"/> map marking every
    /// data-edge-fed port with the node that would have produced its value —
    /// the "保留値" placeholder of SPEC §D8 item 1, because no preceding step
    /// is executed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="StepIndex"/> is the node's position in the D5 execution
    /// order — the index the Build Plan stage would assign the step — so a
    /// context that binds its §F7/§F6 plumbing to a step number stamps the
    /// same one the Plan records.
    /// </para>
    /// <para>
    /// Internal like the run loop's <c>StepContextRequest</c>: the public
    /// surface stays the SPEC-defined one; the UnityAdapter and the EditMode
    /// test assembly see it through <c>InternalsVisibleTo</c>.
    /// </para>
    /// </remarks>
    internal sealed record DryRunStepRequest(
        int StepIndex,
        string NodeId,
        string OperationId,
        int Version,
        OperationDescriptor Descriptor,
        OperationOrigin Origin,
        IReadOnlyDictionary<string, InputValue> Inputs,
        IReadOnlyDictionary<string, string> InputTypes,
        IReadOnlyDictionary<string, string> OutputTypes,
        IReadOnlyDictionary<string, string> PendingProducers);
}
