#nullable enable

// Positional records need System.Runtime.CompilerServices.IsExternalInit, which Unity's
// .NET Standard 2.1 profile does not provide. Defining it internal to this assembly is
// the documented shim for record support, mirroring the production assemblies' copies
// (this test assembly sees only OpWeave.UnityAdapter internals through
// InternalsVisibleTo — the dispatcher drain test seam — not Core's).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
