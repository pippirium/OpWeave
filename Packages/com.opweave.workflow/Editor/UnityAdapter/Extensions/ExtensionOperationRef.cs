#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One <c>operations[]</c> entry of <c>opweave.extension.json</c>
    /// (SPEC §X.3.2): the <c>(id, version)</c> identity an Operation's
    /// Descriptor must match. The same pair is what the registration stage
    /// reports for each Descriptor it discovers inside the extension's
    /// declared assemblies, so the manifest check can compare declared and
    /// actual identity sets exactly.
    /// </summary>
    public sealed record ExtensionOperationRef(string Id, int Version);
}
