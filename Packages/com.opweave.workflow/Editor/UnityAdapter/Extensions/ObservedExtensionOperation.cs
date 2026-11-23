#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// An Operation Descriptor identity actually discovered inside one
    /// assembly of an extension package: the <c>(Id, Version)</c> pair a
    /// Descriptor supplies plus the name of the defining assembly.
    /// </summary>
    /// <remarks>
    /// The registration stage produces these from the Descriptor candidates
    /// it collects (SPEC §C3 steps 2 and 6); the manifest validator compares
    /// them against the declared <c>operations[]</c> list
    /// (SPEC §X.3.2: "Operation一覧と実際のDescriptor不一致" rejects).
    /// Until <c>[OpWeaveOperation]</c>/<c>IOperation</c> land (T-W1.5-03) no
    /// assembly can contribute observations; the discovery entry point
    /// accepts the set so this component needs no shape change when it does.
    /// </remarks>
    public sealed record ObservedExtensionOperation(string Id, int Version, string AssemblyName);
}
