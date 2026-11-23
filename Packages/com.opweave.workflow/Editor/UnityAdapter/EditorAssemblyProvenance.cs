#nullable enable

using OpWeave.Core;
using CompilationAssembly = UnityEditor.Compilation.Assembly;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Provenance of an extension candidate's defining assembly after the SPEC §G0
    /// resolution: which compiled DLL defines it, whether Unity built it from
    /// project sources or it arrived as a precompiled DLL, and which origin it
    /// carries. Origin is used only for extension permission/discovery, provenance
    /// display and search filtering (SPEC §C3 origin rule, §C2 item 9); it never
    /// changes validation, execution or recovery rules.
    /// </summary>
    /// <remarks>
    /// <see cref="CompiledAssemblyPath"/> is the compiled DLL whose bytes define
    /// the assembly; it is the input of the weak implementation fingerprint
    /// (SPEC §C3 rule 6, §C0.2 weak) computed by a later stage. Instances are
    /// produced by <see cref="EditorAssemblyResolver"/> only for assemblies proven
    /// Editor-only.
    /// </remarks>
    public sealed class EditorAssemblyProvenance
    {
        public EditorAssemblyProvenance(
            string assemblyName,
            string compiledAssemblyPath,
            bool isPrecompiled,
            OperationOrigin origin,
            UpmPackageProvenance? package,
            CompilationAssembly? compilationAssembly)
        {
            AssemblyName = assemblyName;
            CompiledAssemblyPath = compiledAssemblyPath;
            IsPrecompiled = isPrecompiled;
            Origin = origin;
            Package = package;
            CompilationAssembly = compilationAssembly;
        }

        /// <summary>The assembly name (e.g. <c>OpWeave.Operations</c>).</summary>
        public string AssemblyName { get; }

        /// <summary>
        /// Full path of the compiled DLL that defines the assembly: the
        /// <see cref="CompilationAssembly"/> output path for source-built
        /// assemblies, the loaded location for precompiled DLLs.
        /// </summary>
        public string CompiledAssemblyPath { get; }

        /// <summary>
        /// True when the DLL arrived precompiled (no Unity compilation entry);
        /// false for assemblies Unity compiled from project or package sources.
        /// </summary>
        public bool IsPrecompiled { get; }

        /// <summary>
        /// <see cref="OperationOrigin.Standard"/> iff <see cref="PackageId"/> is
        /// <c>com.opweave.workflow</c>, else <see cref="OperationOrigin.Custom"/>
        /// (SPEC §C3 origin rule).
        /// </summary>
        public OperationOrigin Origin { get; }

        /// <summary>
        /// The UPM package ID containing the assembly's sources or DLL, or null
        /// when it belongs to no package (SPEC §H4.1 spells that slot
        /// <c>"@project"</c> inside whitelist entries).
        /// </summary>
        public string? PackageId => Package?.PackageId;

        /// <summary>
        /// The UPM-resolved provenance of the package containing this assembly
        /// (package ID, source, source repository and resolved revision, SPEC
        /// §X.3.2 / §G0), or null when the assembly belongs to no package.
        /// This is the per-assembly record the RegistrySnapshot's package info
        /// slot exposes (SPEC §C3).
        /// </summary>
        public UpmPackageProvenance? Package { get; }

        /// <summary>
        /// The uniquely resolved <see cref="UnityEditor.Compilation.Assembly"/>
        /// for source-built assemblies; null for precompiled DLLs, which have no
        /// compilation entry (SPEC §G0).
        /// </summary>
        public CompilationAssembly? CompilationAssembly { get; }
    }
}
