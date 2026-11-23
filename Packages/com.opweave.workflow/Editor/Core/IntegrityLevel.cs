#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// The <c>extensions.integrityLevel</c> axis of SPEC §C0.2/§H4.1: the
    /// integrity level the SG-11 fingerprint material is produced under.
    /// <see cref="Full"/> is a reserved v1 value: it loads successfully and is
    /// rejected at Plan build (SPEC §0.1 F-05) and at Plan acceptance with
    /// OW-OP-011, never silently downgraded to <see cref="Weak"/>.
    /// </summary>
    /// <remarks>
    /// The vocabulary lives in Core — not beside the adapter's settings model
    /// that parses it — because the Build Plan stage evaluates the configured
    /// level as an explicit input through
    /// <see cref="OpWeave.Core.Compiler.IPlanProvenance"/>, and Core cannot
    /// reference the adapter assembly.
    /// </remarks>
    public enum IntegrityLevel
    {
        Weak,
        Full,
    }
}
