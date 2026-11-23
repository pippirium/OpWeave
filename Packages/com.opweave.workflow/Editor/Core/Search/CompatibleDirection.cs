#nullable enable

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The direction a <see cref="CompatibleOperationsRequest"/> searches
    /// (SPEC §C7): <see cref="Downstream"/> finds the input ports that may be
    /// fed by the queried value, <see cref="Upstream"/> the output ports that
    /// may feed the queried position.
    /// </summary>
    public enum CompatibleDirection
    {
        /// <summary>Operations whose input ports accept the queried type.</summary>
        Downstream,

        /// <summary>Operations whose output ports are assignable to the queried type.</summary>
        Upstream,
    }
}
