#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// One Run resource recorded by a <c>ResourceAcquired</c> journal record
    /// that no matching <c>ResourceReleased</c> has consumed (SPEC §F2, §E6
    /// item 4). Resources outlive a domain reload so they can still be
    /// released after recovery; an entry left open at Run end, or at the point
    /// a broken last journal line was dropped, makes the Run require the F5
    /// recovery decision (<see cref="RunSnapshot.RecoveryDecisionRequired"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="Kind"/> and <see cref="Data"/> are the two members of the
    /// record's <c>data</c> payload verbatim; a <c>ResourceReleased</c> pairs
    /// with an acquisition of the same kind and equal data.
    /// <see cref="AcquiringStep"/> comes from the envelope <c>step</c> of the
    /// <c>ResourceAcquired</c> record: SPEC §F5's Completed evidence is the
    /// <em>same step's</em> resource, so a recovery session must be able to
    /// tell whose acquisition an open entry is. A record that never carried a
    /// step keeps <see langword="null"/> — no step may claim it, which §F5
    /// answers as Unknown.
    /// </remarks>
    public sealed record RunResource(string Kind, JsonValue? Data)
    {
        /// <summary>The <c>step</c> of the <c>ResourceAcquired</c> record that
        /// opened this resource, or <see langword="null"/> when the record
        /// carried none.</summary>
        public int? AcquiringStep { get; init; }
    }
}
