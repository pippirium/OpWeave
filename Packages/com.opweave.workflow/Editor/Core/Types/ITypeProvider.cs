#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// Supplies <see cref="TypeDefinition"/>s for the TypeRegistry snapshot
    /// (SPEC §B2, §C0.1). Core ships the <c>core.*</c> provider,
    /// OpWeave.UnityAdapter ships the <c>unity.*</c> provider; external packages
    /// contribute their own under <c>[OpWeaveTypeProvider]</c>.
    /// </summary>
    /// <remarks>
    /// Providers are stateless and deterministic (SPEC §C0.1): they return
    /// type candidates and never obtain mutable Registry internals. Provider ID
    /// and origin are supplied by the registration stage
    /// (<see cref="TypeProviderCandidate"/>), not by this interface.
    /// </remarks>
    public interface ITypeProvider
    {
        /// <summary>The type candidates this provider offers for registration.</summary>
        IEnumerable<TypeDefinition> GetTypes();
    }
}
