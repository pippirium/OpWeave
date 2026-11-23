#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// One workflow output resolved to a Plan position (SPEC §D7, §A6): the
    /// declared output <see cref="Id"/> bound to output port
    /// <see cref="Port"/> of the step at <see cref="StepIndex"/> in the Plan's
    /// D5 execution order — the step-level form of the document's
    /// <c>{id, from:{node,port}}</c>. The Runner reads step indexes, so the
    /// node reference is resolved here exactly like <see cref="FromStep"/>.
    /// </summary>
    public sealed record PlanOutput(string Id, int StepIndex, string Port);
}
