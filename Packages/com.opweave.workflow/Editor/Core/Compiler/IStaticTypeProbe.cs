#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The injected probe the D6 phase uses to answer a Descriptor's
    /// <c>type_exists</c> requirement (SPEC §D6 item 4, §G1 item 4): whether a
    /// fully qualified type name resolves against the environment's current
    /// type space — Unity's TypeCache in production. Core hosts no probe
    /// implementation (SPEC §0.6 SG-06); OpWeave.UnityAdapter supplies the
    /// TypeCache-backed one, and test fixtures supply stubs.
    /// </summary>
    /// <remarks>
    /// The probe is a required argument of the snapshot-backed
    /// <see cref="WorkflowCompiler"/> and the D6 phase (the D3-STATIC-02
    /// precedent): a missing probe is a construction failure, not a skipped
    /// verdict. A probe returning <see langword="false"/> is a definitive
    /// "not found" answer, never "unknown".
    /// </remarks>
    public interface IStaticTypeProbe
    {
        /// <summary>
        /// True when <paramref name="typeName"/> names a type the current
        /// environment already knows — for example a compiled project class or
        /// a Unity engine class. A type defined by code an earlier node of the
        /// same Run would generate is not known yet and answers
        /// <see langword="false"/> (SPEC §D6 item 4).
        /// </summary>
        bool TypeExists(string typeName);
    }
}
