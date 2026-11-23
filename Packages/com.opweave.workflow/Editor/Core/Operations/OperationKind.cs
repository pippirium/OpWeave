#nullable enable

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The reserved kind discriminator of an Operation (SPEC §N.5.2, §N.5.5 (1),
    /// §X.10.4). v1 registers and executes only <see cref="Native"/> Operations;
    /// <see cref="Blueprint"/> (a Composite Operation in the UI vocabulary) is a
    /// future extension point — registration or execution of a blueprint kind in
    /// v1 is rejected with OW-OP-010 and never downgraded.
    /// </summary>
    public enum OperationKind
    {
        Native,
        Blueprint,
    }
}
