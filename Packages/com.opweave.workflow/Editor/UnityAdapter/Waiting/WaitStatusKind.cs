#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Which of the three SPEC §G4 verdicts a <see cref="WaitStatus"/> is:
    /// still waiting, completed, or failed.
    /// </summary>
    public enum WaitStatusKind
    {
        /// <summary>The condition has not concluded; the wait continues.</summary>
        Pending,

        /// <summary>The awaited state is observed; the wait is complete.</summary>
        Done,

        /// <summary>The condition's failure judgement fired (SPEC §G4's failure column).</summary>
        Failed,
    }
}
