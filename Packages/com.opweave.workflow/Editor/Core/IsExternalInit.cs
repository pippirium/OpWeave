#nullable enable

// Positional records need System.Runtime.CompilerServices.IsExternalInit, which neither
// netstandard2.1 (dotnet build) nor Unity's .NET Standard 2.1 profile provides. Defining it
// internal to this assembly is the documented shim for record support on those profiles.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
