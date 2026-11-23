#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The principal the validation runs for (SPEC §D1): an automated
    /// <see cref="Agent"/> or an interactive <see cref="Human"/>. D6 derives the
    /// allow/ask/deny decision and the approval requirements from this distinction.
    /// </summary>
    public enum ActorKind
    {
        Agent,
        Human,
    }
}
