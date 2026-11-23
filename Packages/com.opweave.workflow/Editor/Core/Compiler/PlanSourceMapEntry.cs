#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// One reserved SourceMap entry (SPEC §N.5.5 item 2, §N.5.4): the
    /// correspondence between a Plan step and its hierarchical source — the
    /// <c>(blueprintInstancePath, innerNodeId)</c> display/restore identity a
    /// future Blueprint expansion produces. v1 performs no expansion, so
    /// <see cref="CompiledPlan.SourceMap"/> stays empty; the slot reserves the
    /// Plan and plan.json shape without behaviour.
    /// </summary>
    public sealed record PlanSourceMapEntry(int StepIndex, string SourcePath);
}
