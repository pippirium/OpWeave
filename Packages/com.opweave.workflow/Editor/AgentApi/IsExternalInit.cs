#nullable enable

// Positional records need System.Runtime.CompilerServices.IsExternalInit, which Unity's
// .NET Standard 2.1 profile does not provide. Defining it internal to this assembly is
// the documented shim for record support, mirroring the other OpWeave assemblies' copies.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
