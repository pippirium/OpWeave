#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// One change a Dry Run preflight predicts without applying it (SPEC §D8):
    /// <paramref name="Kind"/> is one of <c>create</c>, <c>modify</c>, <c>delete</c>,
    /// <c>move</c>, <c>save</c>, <c>compile</c> or <c>package</c>;
    /// <paramref name="Target"/> names the affected object and
    /// <paramref name="Effect"/> the §C4 side-effect it counts as. Returned on
    /// <c>PreflightResult</c> and, for a Dry Run validation, on
    /// <c>ValidationReport.PlannedChanges</c>.
    /// </summary>
    public sealed record PlannedChange(
        string NodeId,
        string Kind,
        string Target,
        string Effect);
}
