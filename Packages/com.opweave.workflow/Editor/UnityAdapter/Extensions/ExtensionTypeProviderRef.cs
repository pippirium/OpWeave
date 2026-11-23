#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One <c>typeProviders[]</c> entry of <c>opweave.extension.json</c>
    /// (SPEC §X.3.2, §X.6): the Provider ID the extension assigns to a
    /// <c>[OpWeaveTypeProvider]</c> class and the name of the declared
    /// assembly that class must live in.
    /// </summary>
    public sealed record ExtensionTypeProviderRef(string Id, string Assembly);
}
