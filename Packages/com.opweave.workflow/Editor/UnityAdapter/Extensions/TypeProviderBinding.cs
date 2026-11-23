#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// A Provider ID bound to a concrete <c>[OpWeaveTypeProvider]</c> class
    /// by manifest validation (SPEC §X.3.2, §X.6): the registration stage
    /// instantiates the class and registers its types under
    /// <paramref name="ProviderId"/> (see <see cref="OpWeave.Core.TypeProviderCandidate"/>).
    /// </summary>
    public sealed record TypeProviderBinding(string ProviderId, string TypeFullName, string AssemblyName);
}
