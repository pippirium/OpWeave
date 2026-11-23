#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// Marks a non-abstract class with a public parameterless constructor and an
    /// <see cref="IOperation"/> implementation as an auto-discovered Operation
    /// (SPEC §C2 item 1) — the operation-side counterpart of
    /// <see cref="OpWeaveTypeProviderAttribute"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Id"/> and <see cref="Version"/> declare the Operation's
    /// identity as type metadata (SPEC §C2 item 2): the Registry reads them
    /// without constructing the class and uses them for the pre-instantiation
    /// exclusion, reserved-namespace and duplicate decisions (SPEC §C3
    /// stages 2–3, rules 3–5). After construction they must equal
    /// <see cref="OperationDescriptor.Id"/> and
    /// <see cref="OperationDescriptor.Version"/>; a disagreement rejects the
    /// Operation with OW-OP-003.
    /// </para>
    /// <para>
    /// <see cref="SourceFile"/> is a display aid for showing the source location
    /// and opening it in the GUI only (SPEC §C2 item 3): implementation
    /// fingerprints are computed from the compiled defining-assembly bytes
    /// (SPEC §C0.2 weak), never from source text.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class OpWeaveOperationAttribute : Attribute
    {
        public OpWeaveOperationAttribute(
            string id, int version, [CallerFilePath] string sourceFile = "")
        {
            Id = id;
            Version = version;
            SourceFile = sourceFile;
        }

        /// <summary>The declared Operation ID; must match <see cref="OperationDescriptor.Id"/> (SPEC §C2 item 2).</summary>
        public string Id { get; }

        /// <summary>The declared Operation version; must match <see cref="OperationDescriptor.Version"/>.</summary>
        public int Version { get; }

        /// <summary>The declaring source file, captured by the compiler; provenance display only.</summary>
        public string SourceFile { get; }
    }
}
