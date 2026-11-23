#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace OpWeave.Core
{
    /// <summary>
    /// Marks a non-abstract class with a public parameterless constructor and an
    /// <see cref="ITypeProvider"/> implementation as an auto-discovered
    /// TypeProvider (SPEC §C0.1, §X.6) — the type-side counterpart of
    /// <c>[OpWeaveOperation]</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="SourceFile"/> is display/provenance aid only, exactly as for
    /// <c>OpWeaveOperationAttribute</c> (SPEC §C2 item 2): implementation
    /// fingerprints are computed from the compiled defining-assembly bytes
    /// (SPEC §C0.2 weak), never from source text.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class OpWeaveTypeProviderAttribute : Attribute
    {
        public OpWeaveTypeProviderAttribute([CallerFilePath] string sourceFile = "") =>
            SourceFile = sourceFile;

        /// <summary>The declaring source file, captured by the compiler; provenance display only.</summary>
        public string SourceFile { get; }
    }
}
