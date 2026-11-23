#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// A <c>[OpWeaveTypeProvider]</c> class found in one assembly by the
    /// discovery pass (TypeCache, metadata only — the class is never
    /// instantiated here, SPEC §C3 step 2). Membership checks bind it to a
    /// manifest's <c>typeProviders[]</c> entries by
    /// <paramref name="AssemblyName"/>.
    /// </summary>
    public sealed record DiscoveredTypeProvider(string TypeFullName, string AssemblyName);
}
