#nullable enable

using Mono.Cecil;
using NUnit.Framework;

namespace OpWeave.Testing
{
    internal static class TestingAssemblyMarker
    {
        internal static readonly System.Type[] ReferencedTestTypes =
        {
            typeof(TestAttribute),
            typeof(AssemblyDefinition),
        };
    }
}
