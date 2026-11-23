#nullable enable

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The three outcomes a <see cref="StepResult"/> can carry (SPEC §C2,
    /// §E0). <see cref="Succeeded"/> is the zero value, so a default
    /// <see cref="StepResult"/> reads as a bare success.
    /// </summary>
    public enum StepResultKind
    {
        /// <summary>The step finished; all declared outputs are set and type-checked (SPEC §E4 item 7).</summary>
        Succeeded,

        /// <summary>The step is suspended until <see cref="IOperation.Poll"/> resumes it (SPEC §C2 item 3).</summary>
        Waiting,

        /// <summary>The step failed with a <see cref="Diagnostic"/> (SPEC §E4).</summary>
        Failed,
    }
}
