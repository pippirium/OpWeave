#nullable enable

using System.Collections.Generic;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// Everything the run loop hands to its injected <see cref="StepContext"/>
    /// factory for one step attempt (SPEC §0.6 SG-06): the <see cref="PlanStep"/>
    /// being run, the verified Operation descriptor, the resolved
    /// <see cref="InputValue"/>s of SPEC
    /// §E5, the per-port static Type IDs of <see cref="InputTypes"/>, the
    /// step's §F7 change recorder and §F6
    /// log sink, the resume token of a continued wait (SPEC §C2 item 4), and
    /// the step's §E6 item 4 Run-resource registry.
    /// </summary>
    /// <remarks>
    /// <see cref="Descriptor"/> is null when the Registry snapshot cannot
    /// resolve the step's recorded (id, version) — the Poll path, where the
    /// lookup may genuinely miss — so a factory must tolerate its absence;
    /// the default JSON context ignores it entirely. <see cref="InputTypes"/>
    /// is the SPEC §0.6 SG-06 static type of each input port — a
    /// <see cref="FromStep"/>-fed port's source output type as the Plan
    /// recorded it (SPEC §D4 item 6), a <see cref="FromInput"/>-bound port's
    /// workflow input declared type, a <see cref="Constant"/>/
    /// <see cref="Default"/> port's declared type — and a port without a
    /// statically known type (<see cref="Missing"/>, or a supplier no Plan
    /// data resolves) has no entry; the default JSON context ignores the
    /// table like the descriptor. <see cref="Resources"/>
    /// is the registry a context implementing <see cref="IRunResourceRegistry"/>
    /// delegates to, so an Operation's resource cast keeps working whatever
    /// context the factory builds. <see cref="Attempt"/> is the attempt number the
    /// §F2 envelope of this attempt's records carries, so a context that builds
    /// §F7 elements of its own (§F7's attempt identity) stamps the one the loop
    /// does instead of guessing it.
    /// </remarks>
    internal sealed record StepContextRequest(
        PlanStep Step,
        OperationDescriptor? Descriptor,
        IReadOnlyDictionary<string, InputValue> Inputs,
        IReadOnlyDictionary<string, string> InputTypes,
        IChangeRecorder Changes,
        IRunLogSink Log,
        JsonValue? ResumeToken,
        IRunResourceRegistry Resources,
        int Attempt);
}
