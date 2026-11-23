#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// Answers class-hierarchy questions for <c>type:</c> class IDs and the
    /// built-in Unity class short names that Core cannot judge itself
    /// (SPEC §B2, §C0.1). The Editor side implements this over TypeCache and
    /// <see cref="System.Type.IsAssignableFrom"/> (T-W2.1-05).
    /// </summary>
    public interface IClassRelationProvider
    {
        /// <summary>
        /// Whether <paramref name="classId"/> is a subclass of (or equal to)
        /// <paramref name="baseClassId"/>; <c>null</c> when the relation cannot
        /// be determined, which assignability treats as not assignable with an
        /// "unresolved class" reason (SPEC §B6 rule 4).
        /// </summary>
        bool? IsSubclass(string classId, string baseClassId);
    }
}
