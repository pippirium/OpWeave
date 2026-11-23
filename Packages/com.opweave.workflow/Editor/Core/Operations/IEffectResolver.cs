#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// Implemented by Operations whose actual side effects depend on the
    /// static input types — for example a property setter whose effect is
    /// <c>scene.modify</c> or <c>asset.modify</c> depending on the target
    /// (SPEC §C4). <see cref="ResolveEffects"/> returns a subset of the closed
    /// SPEC §C4 vocabulary; custom Operations cannot invent effect names.
    /// </summary>
    public interface IEffectResolver
    {
        /// <summary>
        /// Computes the actual side-effect set for the node's static inputs
        /// (SPEC §0.6 SG-06). Every returned name must belong to the closed
        /// SPEC §C4 vocabulary.
        /// </summary>
        IReadOnlyList<string> ResolveEffects(StaticContext context);
    }
}
