#nullable enable

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The declared re-execution contract of an Operation (SPEC §C1). The
    /// meaning of each member and the conditions under which automatic
    /// re-execution is allowed are defined solely by the SPEC §F5 table (R2);
    /// <see cref="Unsafe"/> is the conservative default a creator must
    /// deliberately strengthen (SPEC §M.4).
    /// </summary>
    public enum Idempotency
    {
        Idempotent,
        Reconcilable,
        Unsafe,
    }
}
