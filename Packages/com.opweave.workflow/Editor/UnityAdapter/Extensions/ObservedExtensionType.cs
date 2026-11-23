#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// A Type ID actually offered by one assembly of an extension package:
    /// the <see cref="OpWeave.Core.TypeDefinition.Id"/> a bound
    /// <c>[OpWeaveTypeProvider]</c> instance yielded plus the name of the
    /// provider's defining assembly.
    /// </summary>
    /// <remarks>
    /// The registration stage produces these after it instantiates the
    /// providers accepted by manifest validation (SPEC §C3 steps 3–4); the
    /// manifest validator applies the SPEC §X.3.2 Type ID rules to them —
    /// no reserved namespace, no duplicate Type ID across packages —
    /// because provider classes themselves stay metadata-only at discovery
    /// time (SPEC §C3 step 2).
    /// </remarks>
    public sealed record ObservedExtensionType(string TypeId, string AssemblyName);
}
