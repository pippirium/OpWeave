#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using UnityEditor;
using UnityEditor.Compilation;
using CompilationAssembly = UnityEditor.Compilation.Assembly;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §D0.3 role of a mutation target: <see cref="Source"/> is an
    /// existing asset the Operation removes, overwrites or moves away (its
    /// file(s) leave the assemblies that compile them);
    /// <see cref="Destination"/> is where a new or moved artifact lands —
    /// code written there joins the domain that governs the location.
    /// </summary>
    internal enum GuardedPathRole
    {
        /// <summary>The existing artifact the mutation removes or moves away.</summary>
        Source,

        /// <summary>The location the mutation writes or moves an artifact into.</summary>
        Destination,
    }

    /// <summary>
    /// The package facts SPEC §D0.3's dependency check needs — name,
    /// resolved on-disk root and declared dependency names — reduced to a
    /// fabricable model because <see cref="PackageInfo"/> itself cannot be
    /// constructed by tests.
    /// </summary>
    internal sealed class GuardPackageInfo
    {
        internal GuardPackageInfo(
            string name, string? resolvedPath, IReadOnlyList<string> dependencies)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ResolvedPath = resolvedPath;
            Dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        }

        /// <summary>The package name (<c>com.foo.bar</c>).</summary>
        internal string Name { get; }

        /// <summary>The canonical absolute path the package resolves to, or null.</summary>
        internal string? ResolvedPath { get; }

        /// <summary>The names the package's manifest declares as direct dependencies.</summary>
        internal IReadOnlyList<string> Dependencies { get; }
    }

    /// <summary>
    /// The Unity-API seam of <see cref="RunDefinitionGuard"/> — the
    /// <see cref="EditorAssemblyResolver"/> test-seam pattern: production
    /// wraps <see cref="CompilationPipeline"/>, <see cref="AssetDatabase"/>,
    /// <see cref="PackageInfo"/> and the file system; EditMode tests
    /// subclass it to prove the classification without moving real files.
    /// </summary>
    internal class RunDefinitionGuardQueries
    {
        /// <summary>The assembly a script file at <paramref name="assetPath"/> compiles into, or null.</summary>
        internal virtual string? AssemblyOfScript(string assetPath) =>
            CompilationPipeline.GetAssemblyNameFromScriptPath(assetPath);

        /// <summary>The Editor-domain compilation assemblies (name, sourceFiles, asmdefPath, references).</summary>
        internal virtual CompilationAssembly[] EditorAssemblies() =>
            CompilationPipeline.GetAssemblies(AssembliesType.Editor);

        /// <summary>The precompiled assembly file names (each ending in <c>.dll</c>).</summary>
        internal virtual string[] PrecompiledNames() =>
            CompilationPipeline.GetPrecompiledAssemblyNames();

        /// <summary>The on-disk path of one precompiled assembly file name, or null.</summary>
        internal virtual string? PrecompiledPath(string fileName) =>
            CompilationPipeline.GetPrecompiledAssemblyPathFromAssemblyName(fileName);

        /// <summary>True when <paramref name="assetPath"/> is an imported folder.</summary>
        internal virtual bool IsValidFolder(string assetPath) =>
            AssetDatabase.IsValidFolder(assetPath);

        /// <summary>The asset path a guid resolves to, or null.</summary>
        internal virtual string? GuidToAssetPath(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return path.Length == 0 ? null : path;
        }

        /// <summary>The text of an existing asset file, or null when it cannot be read.</summary>
        internal virtual string? ReadFileText(string assetPath)
        {
            try
            {
                var physical = ToPhysicalPath(assetPath);
                return physical is not null && File.Exists(physical)
                    ? File.ReadAllText(physical)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Every file under an asset directory, as project-relative asset
        /// paths — or null when the directory cannot be enumerated.
        /// </summary>
        internal virtual IReadOnlyList<string>? EnumerateFiles(string assetDir)
        {
            var physical = ToPhysicalPath(assetDir);
            if (physical is null || !Directory.Exists(physical))
            {
                return null;
            }
            try
            {
                var prefixLength = physical.Length;
                var list = new List<string>();
                foreach (var file in Directory.EnumerateFiles(
                    physical, "*", SearchOption.AllDirectories))
                {
                    var suffix = file.Substring(prefixLength).Replace('\\', '/').TrimStart('/');
                    list.Add(assetDir.TrimEnd('/') + "/" + suffix);
                }
                return list;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The registered package list, or null when it cannot be captured.</summary>
        internal virtual IReadOnlyList<GuardPackageInfo>? RegisteredPackages()
        {
            PackageInfo[] infos;
            try
            {
                infos = PackageInfo.GetAllRegisteredPackages();
            }
            catch (Exception)
            {
                return null;
            }
            if (infos is null)
            {
                return null;
            }
            var list = new List<GuardPackageInfo>(infos.Length);
            foreach (var info in infos)
            {
                var dependencies = new List<string>();
                if (info.dependencies is { } declared)
                {
                    foreach (var dependency in declared)
                    {
                        dependencies.Add(dependency.name);
                    }
                }
                list.Add(new GuardPackageInfo(
                    info.name, RunDefinitionGuard.Canonicalize(info.resolvedPath), dependencies));
            }
            return list;
        }

        // The same Packages/-aware asset→physical resolution
        // AssetArtifact.ToPhysicalPath implements, kept local so the guard
        // stays inside UnityAdapter.
        private static string? ToPhysicalPath(string assetPath)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var info = PackageInfo.FindForAssetPath(assetPath);
                var prefix = info?.assetPath is { } packageRoot
                    ? packageRoot + "/"
                    : null;
                if (prefix is null ||
                    !assetPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }
                return Path.GetFullPath(Path.Combine(
                    info!.resolvedPath, assetPath.Substring(prefix.Length)));
            }
            return Path.GetFullPath(assetPath);
        }
    }

    /// <summary>
    /// The Unity-side half of the SPEC §D0.3 definition-mutation guard
    /// (T-W4.2-06): compares a definition-changing Operation's target —
    /// asset path or package name — against the protected-definition set
    /// of every unfinished Run this session tracks and answers the
    /// OW-RUN-013 <see cref="Diagnostic"/> before any side effect runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The protected set comes from
    /// <see cref="RunCoordinator.CollectProtectedRuns"/> — SPEC §D0.3 item
    /// 1's every non-terminal state (Queued, AwaitingApproval, Running —
    /// a step-level Waiting keeps its Run Running — Suspended and
    /// NeedsReview) — resolved through
    /// <see cref="RunDefinitionProtection.ForRun"/> against the current
    /// Registry snapshot: used Operations' defining assemblies and
    /// packages, used Type/Codec defining assemblies, the whole DLL —
    /// unused classes included — plus the OpWeave package itself.
    /// </para>
    /// <para>
    /// <b>Serialization (SPEC §D0.3 item 4).</b> Every check runs on the
    /// Unity main thread — the same thread Run acceptance
    /// (<see cref="OpWeaveRunnerHost.AcceptRun"/>) and step execution run
    /// on — so a Run accepted between an Operation's Preflight and its
    /// pre-side-effect recheck is always seen by the second check; no
    /// mutation can slip between acceptance and the side effect.
    /// </para>
    /// <para>
    /// <b>Fail closed (SPEC §D0.3 item 3).</b> A target whose relation to
    /// the protected set cannot be proven — an unresolvable script
    /// domain, an unreadable package registry, a Run whose used set is
    /// unprovable — is refused with OW-RUN-013 and nothing is modified.
    /// The diagnostic reports only the normalized target identifier and
    /// conflicting/unfinished Run ids — never target contents, source
    /// URLs or other secret-bearing strings.
    /// </para>
    /// </remarks>
    internal static class RunDefinitionGuard
    {
        private const string OpWeavePackagePath =
            "Packages/" + EditorAssemblyResolver.StandardPackageId;

        private static Func<IReadOnlyList<ProtectedRun>?> s_runs = ProductionRuns;
        private static Func<RegistrySnapshot?> s_registry = ProductionRegistry;
        private static RunDefinitionGuardQueries s_queries =
            new RunDefinitionGuardQueries();

        /// <summary>
        /// Swaps the protected-Run/Registry sources and the Unity query
        /// seam until the returned handle is disposed — the
        /// <see cref="EditorAssemblyResolver"/>-style test seam. Null
        /// arguments keep the production binding.
        /// </summary>
        internal static IDisposable InstallForTests(
            Func<IReadOnlyList<ProtectedRun>?>? runs = null,
            Func<RegistrySnapshot?>? registry = null,
            RunDefinitionGuardQueries? queries = null)
        {
            var previousRuns = s_runs;
            var previousRegistry = s_registry;
            var previousQueries = s_queries;
            s_runs = runs ?? ProductionRuns;
            s_registry = registry ?? ProductionRegistry;
            s_queries = queries ?? new RunDefinitionGuardQueries();
            return new RestoreSeams(previousRuns, previousRegistry, previousQueries);
        }

        private sealed class RestoreSeams : IDisposable
        {
            private readonly Func<IReadOnlyList<ProtectedRun>?> _runs;
            private readonly Func<RegistrySnapshot?> _registry;
            private readonly RunDefinitionGuardQueries _queries;

            internal RestoreSeams(
                Func<IReadOnlyList<ProtectedRun>?> runs,
                Func<RegistrySnapshot?> registry,
                RunDefinitionGuardQueries queries)
            {
                _runs = runs;
                _registry = registry;
                _queries = queries;
            }

            public void Dispose()
            {
                s_runs = _runs;
                s_registry = _registry;
                s_queries = _queries;
            }
        }

        private static IReadOnlyList<ProtectedRun>? ProductionRuns()
        {
            var host = OpWeaveRunnerHost.Current;
            if (host is null)
            {
                return Array.Empty<ProtectedRun>();
            }
            var list = new List<ProtectedRun>();
            host.CollectProtectedRuns(list);
            return list;
        }

        private static RegistrySnapshot? ProductionRegistry() =>
            OpWeaveRunnerHost.Current?.CurrentRegistrySnapshot();

        /// <summary>True while any unfinished Run is tracked — the cheap gate the Operations read first.</summary>
        internal static bool HasProtectedRuns
        {
            get
            {
                var runs = s_runs();
                return runs is not null && runs.Count != 0;
            }
        }

        // ── Public check surface ────────────────────────────────────────

        /// <summary>
        /// The path check of SPEC §D0.3 item 3: classifies what the
        /// mutation does to <paramref name="assetPath"/> — code joining or
        /// leaving an assembly, a defining-assembly boundary change, a
        /// package-owned path — and refuses with OW-RUN-013 when the
        /// effect on an unfinished Run's protected set cannot be proven
        /// absent. <paramref name="contentPath"/> supplies the file whose
        /// content lands at the target (the source of a copy/move, or a
        /// file already on disk); null means the destination text is not
        /// yet readable.
        /// </summary>
        internal static Diagnostic? CheckPath(
            string? assetPath,
            GuardedPathRole role,
            bool isDirectory = false,
            string? contentPath = null)
        {
            var state = Capture();
            if (state is null)
            {
                return null;
            }
            var normalized = NormalizeAssetPath(assetPath);
            if (normalized is null)
            {
                return RefuseUndecidable(
                    "<unresolvable target>", state.AllRunIds());
            }
            if (IsOpWeavePath(normalized))
            {
                // SPEC §D0.3 item 2: the OpWeave package itself — every
                // unfinished Run depends on it.
                return Refuse(normalized, state.AllRunIds(), Array.Empty<string>());
            }
            var directory = isDirectory ||
                (role == GuardedPathRole.Source && s_queries.IsValidFolder(normalized));
            return directory
                ? CheckDirectory(state, normalized, role, contentPath)
                : CheckFile(state, normalized, role, contentPath);
        }

        /// <summary>
        /// The package check of SPEC §D0.3 item 3 for a mutation naming a
        /// package directly (<c>unity.package.remove</c>, a resolved
        /// <c>unity.package.add</c> target): refused when the package is
        /// OpWeave itself, ships a used Operation, contains a protected
        /// assembly, or is required by one — and when the dependency
        /// direction cannot be established.
        /// </summary>
        internal static Diagnostic? CheckPackageMutation(string? packageName)
        {
            var state = Capture();
            if (state is null)
            {
                return null;
            }
            if (string.IsNullOrEmpty(packageName))
            {
                return RefuseUndecidable("<package>", state.AllRunIds());
            }
            var name = packageName!;
            if (string.Equals(
                name, EditorAssemblyResolver.StandardPackageId, StringComparison.Ordinal))
            {
                return Refuse(name, state.AllRunIds(), Array.Empty<string>());
            }

            var conflicts = new List<string>();
            var undecidable = new List<string>(state.UncertainRunIds);
            foreach (var def in state.Runs)
            {
                if (def.Uncertain)
                {
                    continue; // already counted as undecidable
                }
                var result = PackageVerdict(def, name, state);
                if (result == PackageRelation.Conflicting)
                {
                    conflicts.Add(def.RunId);
                }
                else if (result == PackageRelation.Undecidable)
                {
                    undecidable.Add(def.RunId);
                }
            }
            if (conflicts.Count == 0 && undecidable.Count == 0)
            {
                return null;
            }
            return Refuse(name, conflicts, undecidable);
        }

        /// <summary>
        /// The catch-all of SPEC §D0.3 item 3 for a definition-affecting
        /// mutation whose full effect cannot be proven — a package add
        /// that is not an exact reinstall (dependency resolution may move
        /// a package an unfinished Run's Plan uses), a boundary file's
        /// member edit whose new value is unknowable, a dirty-file sweep
        /// whose set cannot be enumerated — refused while any protected
        /// Run exists.
        /// </summary>
        internal static Diagnostic? CheckUnprovableDefinitionChange(string? targetId) =>
            Capture() is { } state
                ? RefuseUndecidable(targetId ?? "<target>", state.AllRunIds())
                : null;

        // ── Captured state ──────────────────────────────────────────────

        private sealed class GuardState
        {
            internal readonly List<ProtectedRunDefinitions> Runs =
                new List<ProtectedRunDefinitions>();
            internal readonly List<string> UncertainRunIds = new List<string>();
            // Runs with a protected assembly whose sources cannot be
            // enumerated — subtree checks are unprovable for them.
            internal readonly List<string> UnmappedRunIds = new List<string>();
            internal readonly Dictionary<string, CompilationAssembly> Compiled =
                new Dictionary<string, CompilationAssembly>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Precompiled =
                new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string?> PackageOf =
                new Dictionary<string, string?>(StringComparer.Ordinal);
            internal IReadOnlyList<GuardPackageInfo>? Packages;

            internal List<string> AllRunIds()
            {
                var ids = new List<string>(Runs.Count);
                foreach (var run in Runs)
                {
                    ids.Add(run.RunId);
                }
                return ids;
            }
        }

        private static GuardState? Capture()
        {
            var runs = s_runs();
            if (runs is null || runs.Count == 0)
            {
                return null;
            }
            var state = new GuardState();
            var registry = s_registry();
            foreach (var run in runs)
            {
                var def = registry is { } snapshot
                    ? RunDefinitionProtection.ForRun(run, snapshot)
                    : new ProtectedRunDefinitions(
                        run.RunId, uncertain: true,
                        new HashSet<string>(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal));
                state.Runs.Add(def);
                if (def.Uncertain)
                {
                    state.UncertainRunIds.Add(def.RunId);
                }
            }

            var packages = s_queries.RegisteredPackages();
            state.Packages = packages;
            try
            {
                foreach (var assembly in s_queries.EditorAssemblies())
                {
                    state.Compiled[assembly.name] = assembly;
                    state.PackageOf[assembly.name] =
                        PackageNameOfSources(assembly.sourceFiles);
                }
            }
            catch (Exception)
            {
                // The compilation picture cannot be established — every
                // Run's subtree/source coverage is then unprovable.
                MarkAllUnmapped(state);
            }
            try
            {
                foreach (var fileName in s_queries.PrecompiledNames())
                {
                    var name = Path.GetFileNameWithoutExtension(fileName);
                    var path = s_queries.PrecompiledPath(fileName);
                    if (!string.IsNullOrEmpty(path))
                    {
                        state.Precompiled[name] = Canonicalize(path);
                        state.PackageOf[name] =
                            PackageNameOfAbsolutePath(path, packages);
                    }
                }
            }
            catch (Exception)
            {
                MarkAllUnmapped(state);
            }

            // A protected assembly that is in neither the compiled nor the
            // precompiled picture cannot have its sources placed — subtree
            // checks are unprovable for its Runs (fail closed).
            var unmapped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var def in state.Runs)
            {
                foreach (var assemblyName in def.AssemblyNames)
                {
                    if (!state.Compiled.ContainsKey(assemblyName) &&
                        !state.Precompiled.ContainsKey(assemblyName))
                    {
                        unmapped.Add(assemblyName);
                    }
                }
            }
            foreach (var def in state.Runs)
            {
                foreach (var assemblyName in def.AssemblyNames)
                {
                    if (unmapped.Contains(assemblyName))
                    {
                        state.UnmappedRunIds.Add(def.RunId);
                        break;
                    }
                }
            }
            return state;
        }

        private static void MarkAllUnmapped(GuardState state)
        {
            foreach (var def in state.Runs)
            {
                if (!state.UnmappedRunIds.Contains(def.RunId))
                {
                    state.UnmappedRunIds.Add(def.RunId);
                }
            }
        }

        // ── Path classification ─────────────────────────────────────────

        private static Diagnostic? CheckDirectory(
            GuardState state, string dir, GuardedPathRole role, string? contentPath)
        {
            if (role == GuardedPathRole.Source)
            {
                // Deletion/move-out: every file under the directory leaves
                // the project — each one is classified at its own path so
                // scripts, precompiled DLLs and boundary files are judged
                // like the single-file cases. An unenumerable directory
                // cannot be proven free of protected sources.
                var files = s_queries.EnumerateFiles(dir);
                if (files is null)
                {
                    return RefuseUndecidable(dir, state.AllRunIds());
                }
                foreach (var file in files)
                {
                    var refused = CheckFile(state, file, GuardedPathRole.Source, file);
                    if (refused is not null)
                    {
                        return refused;
                    }
                }
                return UnmappedUndecidable(state, dir);
            }

            // Destination: the directory's incoming contents join the
            // domain that governs it — every file the source subtree
            // carries is classified at its destination path, except files
            // an incoming boundary file carves out, which join the NEW
            // assembly the boundary defines instead.
            if (contentPath is { } sourceDir)
            {
                return CheckIncomingTree(state, dir, sourceDir);
            }
            var governing = s_queries.AssemblyOfScript(dir + "/x.cs");
            if (governing is null)
            {
                return RefuseUndecidable(dir, state.AllRunIds());
            }
            return RefuseConflicts(state, dir, governing);
        }

        // A boundary file inside the moved subtree — the file itself (for
        // name/content reads) and the subtree-relative directory it
        // governs ("" for a boundary at the moved root).
        private readonly struct IncomingBoundary
        {
            internal IncomingBoundary(string dir, string file, bool isAsmref)
            {
                Dir = dir;
                File = file;
                IsAsmref = isAsmref;
            }

            internal string Dir { get; }
            internal string File { get; }
            internal bool IsAsmref { get; }
        }

        // The files a folder copy/move would carry into the destination:
        // each is classified at its destination path. Scripts and response
        // files under an incoming .asmdef boundary join that NEW assembly —
        // never the destination's current domain; scripts under an incoming
        // .asmref join the assembly the boundary references.
        private static Diagnostic? CheckIncomingTree(
            GuardState state, string destDir, string sourceDir)
        {
            var files = s_queries.EnumerateFiles(sourceDir);
            if (files is null)
            {
                return RefuseUndecidable(destDir, state.AllRunIds());
            }
            var sourcePrefix = sourceDir.TrimEnd('/') + "/";
            var boundaries = new List<IncomingBoundary>();
            foreach (var file in files)
            {
                var extension = ExtensionOf(file);
                if (extension == ".asmdef" || extension == ".asmref")
                {
                    var relative = file.StartsWith(
                        sourcePrefix, StringComparison.Ordinal)
                        ? file.Substring(sourcePrefix.Length)
                        : file;
                    boundaries.Add(new IncomingBoundary(
                        ParentOf(relative) ?? "", file, extension == ".asmref"));
                }
            }
            foreach (var file in files)
            {
                var relative = file.StartsWith(sourcePrefix, StringComparison.Ordinal)
                    ? file.Substring(sourcePrefix.Length)
                    : file;
                var destPath = destDir.TrimEnd('/') + "/" + relative;
                switch (ExtensionOf(file))
                {
                    case ".cs":
                    {
                        var covering = CoveringBoundary(boundaries, relative);
                        if (covering is { } boundary)
                        {
                            if (!boundary.IsAsmref)
                            {
                                break; // joins the moved boundary's own assembly
                            }
                            var referenced = ResolveAsmrefName(boundary.File);
                            if (referenced is null)
                            {
                                return RefuseUndecidable(destDir, state.AllRunIds());
                            }
                            var repointed =
                                RefuseConflicts(state, destDir, referenced);
                            if (repointed is not null)
                            {
                                return repointed;
                            }
                            break;
                        }
                        var governed = s_queries.AssemblyOfScript(destPath);
                        if (governed is null)
                        {
                            return RefuseUndecidable(destDir, state.AllRunIds());
                        }
                        var refused = RefuseConflicts(state, destDir, governed);
                        if (refused is not null)
                        {
                            return refused;
                        }
                        break;
                    }
                    case ".rsp":
                    {
                        // A response file binds to the domain governing
                        // the directory it lands in — unless an incoming
                        // boundary carries it into the new assembly.
                        if (CoveringBoundary(boundaries, relative) is not null)
                        {
                            break;
                        }
                        var destParent = ParentOf(destPath) ?? destDir;
                        var governed =
                            s_queries.AssemblyOfScript(destParent + "/x.cs");
                        if (governed is null)
                        {
                            return RefuseUndecidable(destDir, state.AllRunIds());
                        }
                        var refused = RefuseConflicts(state, destDir, governed);
                        if (refused is not null)
                        {
                            return refused;
                        }
                        break;
                    }
                    case ".asmref":
                    {
                        var refused = CheckAsmrefContent(state, destDir, file);
                        if (refused is not null)
                        {
                            return refused;
                        }
                        break;
                    }
                    case ".asmdef":
                    {
                        // The incoming boundary's own assembly name must
                        // not collide with a protected assembly.
                        var name = ReadAsmdefName(file) ?? FileBaseName(file);
                        var refused = RefuseConflicts(state, destDir, name);
                        if (refused is not null)
                        {
                            return refused;
                        }
                        break;
                    }
                    case ".dll":
                    {
                        var refused =
                            RefuseConflicts(state, destDir, FileBaseName(file));
                        if (refused is not null)
                        {
                            return refused;
                        }
                        break;
                    }
                }
            }
            return null;
        }

        private static IncomingBoundary? CoveringBoundary(
            IReadOnlyList<IncomingBoundary> boundaries, string relative)
        {
            IncomingBoundary? covering = null;
            foreach (var boundary in boundaries)
            {
                if (boundary.Dir.Length == 0 ||
                    relative.StartsWith(boundary.Dir + "/", StringComparison.Ordinal))
                {
                    // The deepest covering boundary wins — nearest
                    // definition boundary is the one the compiler honours.
                    if (covering is null || boundary.Dir.Length > covering.Value.Dir.Length)
                    {
                        covering = boundary;
                    }
                }
            }
            return covering;
        }

        private static Diagnostic? CheckFile(
            GuardState state, string path, GuardedPathRole role, string? contentPath)
        {
            // Project-wide compile settings live under ProjectSettings/
            // (scripting define symbols, API compatibility): which member
            // a mutation touches cannot be proven here, so a write inside
            // it is undecidable while any Run is protected.
            if (path.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
            {
                return RefuseUndecidable(path, state.AllRunIds());
            }
            var extension = ExtensionOf(path);
            switch (extension)
            {
                case ".cs":
                {
                    var owner = s_queries.AssemblyOfScript(path);
                    return owner is null
                        ? RefuseUndecidable(path, state.AllRunIds())
                        : RefuseConflicts(state, path, owner);
                }
                case ".dll":
                    return RefuseConflicts(state, path, FileBaseName(path));
                case ".asmdef":
                case ".asmref":
                case ".rsp":
                    return CheckBoundaryFile(state, path, extension, role, contentPath);
                default:
                    // A file that never joins a compiled assembly is a
                    // normal asset — provably unrelated to every Run's
                    // definitions, uncertain Runs included (SPEC §D0.3).
                    return null;
            }
        }

        // .asmdef/.asmref/.rsp reshape assembly membership or options:
        // refuse when the governing domain, the parent domain the subtree
        // would merge into, or the named/referenced assembly is protected —
        // and when any of that cannot be established.
        private static Diagnostic? CheckBoundaryFile(
            GuardState state,
            string path,
            string extension,
            GuardedPathRole role,
            string? contentPath)
        {
            var dir = ParentOf(path);
            if (dir is null)
            {
                return RefuseUndecidable(path, state.AllRunIds());
            }
            var governing = s_queries.AssemblyOfScript(dir + "/x.cs");
            if (governing is null)
            {
                return RefuseUndecidable(path, state.AllRunIds());
            }
            var refused = RefuseConflicts(state, path, governing);
            if (refused is not null)
            {
                return refused;
            }
            if (extension == ".rsp")
            {
                // Compile options bind to the governing assembly alone —
                // no subtree membership moves.
                return UnmappedUndecidable(state, path);
            }

            var content = contentPath ?? (role == GuardedPathRole.Source ? path : null);
            if (content is not null)
            {
                if (extension == ".asmref")
                {
                    refused = CheckAsmrefContent(state, path, content);
                    if (refused is not null)
                    {
                        return refused;
                    }
                }
                else if (role == GuardedPathRole.Destination)
                {
                    // A .asmdef arriving at the destination creates an
                    // assembly — its declared (or file-name) name must not
                    // collide with a protected assembly.
                    var name = ReadAsmdefName(content) ?? FileBaseName(path);
                    refused = RefuseConflicts(state, path, name);
                    if (refused is not null)
                    {
                        return refused;
                    }
                }
            }
            else if (role == GuardedPathRole.Destination && extension == ".asmref")
            {
                // An .asmref whose referenced assembly cannot be read is
                // an unprovable definition change.
                return RefuseUndecidable(path, state.AllRunIds());
            }

            if (role == GuardedPathRole.Source)
            {
                // Removing the boundary merges its subtree into the parent
                // domain — protected when that domain is.
                var parent = ParentOf(dir);
                if (parent is not null)
                {
                    var parentDomain = s_queries.AssemblyOfScript(parent + "/x.cs");
                    if (parentDomain is null)
                    {
                        return RefuseUndecidable(path, state.AllRunIds());
                    }
                    refused = RefuseConflicts(state, path, parentDomain);
                    if (refused is not null)
                    {
                        return refused;
                    }
                }
            }
            return UnmappedUndecidable(state, path);
        }

        // The referenced assembly name of an .asmref — either a direct
        // assembly name or a "GUID:<guid>" indirection resolved through the
        // asset database and the referenced .asmdef's declared name — null
        // when it cannot be established (fail closed).
        private static string? ResolveAsmrefName(string contentPath)
        {
            var text = s_queries.ReadFileText(contentPath);
            if (text is null)
            {
                return null;
            }
            string? reference = null;
            try
            {
                reference = (string?)(JObject.Parse(text)["reference"] as JValue)?.Value;
            }
            catch (Exception)
            {
            }
            if (string.IsNullOrEmpty(reference))
            {
                return null;
            }
            var name = reference!;
            if (!name.StartsWith("GUID:", StringComparison.Ordinal))
            {
                return name;
            }
            var assetPath = s_queries.GuidToAssetPath(name.Substring(5));
            if (assetPath is null)
            {
                return null;
            }
            var asmdefText = s_queries.ReadFileText(assetPath);
            return asmdefText is not null
                ? ReadAsmdefTextName(asmdefText) ?? FileBaseName(assetPath)
                : FileBaseName(assetPath);
        }

        // The referenced assembly of an .asmref must not name a protected
        // assembly; an unreadable or unresolvable reference is undecidable.
        private static Diagnostic? CheckAsmrefContent(
            GuardState state, string path, string contentPath) =>
            ResolveAsmrefName(contentPath) is { } name
                ? RefuseConflicts(state, path, name)
                : RefuseUndecidable(path, state.AllRunIds());

        private static string? ReadAsmdefName(string assetPath)
        {
            var text = s_queries.ReadFileText(assetPath);
            return text is null ? null : ReadAsmdefTextName(text);
        }

        private static string? ReadAsmdefTextName(string text)
        {
            try
            {
                return (string?)(JObject.Parse(text)["name"] as JValue)?.Value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── Verdicts ────────────────────────────────────────────────────

        private enum PackageRelation
        {
            Unrelated,
            Conflicting,
            Undecidable,
        }

        // One unfinished Run vs a named package: conflicting when the
        // package ships a used Operation, contains a protected assembly,
        // or is a dependency of a protected package; undecidable when the
        // dependency direction cannot be established.
        private static PackageRelation PackageVerdict(
            ProtectedRunDefinitions def, string name, GuardState state)
        {
            if (def.PackageIds.Contains(name))
            {
                return PackageRelation.Conflicting;
            }
            var undecidable = false;
            var protectedPackages = new HashSet<string>(
                def.PackageIds, StringComparer.Ordinal);
            foreach (var assemblyName in def.AssemblyNames)
            {
                if (!state.PackageOf.TryGetValue(assemblyName, out var assemblyPackage))
                {
                    // The assembly is in neither compiled nor precompiled
                    // records — nothing a package mutation can change.
                    continue;
                }
                if (assemblyPackage is { } pkg)
                {
                    protectedPackages.Add(pkg);
                    if (pkg == name)
                    {
                        return PackageRelation.Conflicting;
                    }
                }
                if (!state.Compiled.TryGetValue(assemblyName, out var compiled))
                {
                    continue;
                }
                // Assembly-level references of the protected assembly
                // into the removed package break its compilation.
                if (compiled.assemblyReferences is { } references)
                {
                    foreach (var reference in references)
                    {
                        if (reference is null)
                        {
                            continue;
                        }
                        if (state.PackageOf.TryGetValue(reference.name, out var refPackage) &&
                            refPackage == name)
                        {
                            return PackageRelation.Conflicting;
                        }
                    }
                }
                if (compiled.compiledAssemblyReferences is { } paths)
                {
                    foreach (var path in paths)
                    {
                        if (PackageNameOfAbsolutePath(path, state.Packages) == name)
                        {
                            return PackageRelation.Conflicting;
                        }
                    }
                }
            }
            // Package-level dependency direction: removing a package a
            // protected package declares a dependency on breaks the
            // protected package's resolution entirely. A protected package
            // the registry does not describe is the undecidable form of the
            // same question — its declared dependencies are unreadable, so
            // no other package is provably unrelated; SPEC §D0.3 item 2
            // allows only a change proven unrelated, never an unprovable one.
            foreach (var protectedPackage in protectedPackages)
            {
                if (protectedPackage == name || state.Packages is null)
                {
                    if (state.Packages is null && protectedPackage != name)
                    {
                        undecidable = true;
                    }
                    continue;
                }
                var info = FindPackage(state.Packages, protectedPackage);
                if (info is null)
                {
                    undecidable = true;
                    continue;
                }
                foreach (var dependency in info.Dependencies)
                {
                    if (string.Equals(dependency, name, StringComparison.Ordinal))
                    {
                        return PackageRelation.Conflicting;
                    }
                }
            }
            return undecidable ? PackageRelation.Undecidable : PackageRelation.Unrelated;
        }

        private static GuardPackageInfo? FindPackage(
            IReadOnlyList<GuardPackageInfo> packages, string name)
        {
            foreach (var info in packages)
            {
                if (string.Equals(info.Name, name, StringComparison.Ordinal))
                {
                    return info;
                }
            }
            return null;
        }

        /// <summary>Refuse when <paramref name="assemblyName"/> belongs to a protected assembly.</summary>
        private static Diagnostic? RefuseConflicts(
            GuardState state, string target, string? assemblyName)
        {
            var conflicts = new List<string>();
            var undecidable = new List<string>(state.UncertainRunIds);
            foreach (var def in state.Runs)
            {
                if (!def.Uncertain &&
                    assemblyName is not null &&
                    def.AssemblyNames.Contains(assemblyName))
                {
                    conflicts.Add(def.RunId);
                }
            }
            return conflicts.Count == 0 && undecidable.Count == 0
                ? null
                : Refuse(target, conflicts, undecidable);
        }

        private static Diagnostic? UnmappedUndecidable(GuardState state, string target) =>
            state.UnmappedRunIds.Count == 0
                ? null
                : RefuseUndecidable(target, state.UnmappedRunIds);

        // ── Diagnostics (SPEC §D0.3: safe ids only, never secrets) ───────

        private static Diagnostic Refuse(
            string target,
            IReadOnlyList<string> conflictIds,
            IReadOnlyList<string> undecidableIds)
        {
            var hint = new StringBuilder("target: ").Append(target);
            if (conflictIds.Count != 0)
            {
                hint.Append("; conflicting Runs: ").Append(string.Join(", ", conflictIds));
            }
            if (undecidableIds.Count != 0)
            {
                hint.Append("; unprovable against unfinished Runs: ")
                    .Append(string.Join(", ", undecidableIds));
            }
            return ErrorCodes.Create(
                ErrorCodes.OW_RUN_013,
                "The change would modify a definition an unfinished Run uses and is refused before any side effect.",
                hint: hint.ToString());
        }

        private static Diagnostic RefuseUndecidable(
            string target, IReadOnlyList<string> runIds) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_013,
                "The change cannot be proven unrelated to definitions an unfinished Run uses and is refused before any side effect.",
                hint: "target: " + target + "; unfinished Runs: " +
                    string.Join(", ", runIds));

        // ── Path/package helpers ────────────────────────────────────────

        private static string? NormalizeAssetPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            var p = path!.Replace('\\', '/');
            if (p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(0, p.Length - ".meta".Length);
            }
            while (p.StartsWith("./", StringComparison.Ordinal))
            {
                p = p.Substring(2);
            }
            p = p.TrimEnd('/');
            return p.Length == 0 ? null : p;
        }

        private static bool IsOpWeavePath(string assetPath) =>
            assetPath.StartsWith(
                OpWeavePackagePath + "/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                assetPath, OpWeavePackagePath, StringComparison.OrdinalIgnoreCase);

        private static string ExtensionOf(string path)
        {
            var name = Path.GetFileName(path);
            var dot = name.LastIndexOf('.');
            return dot < 0 ? "" : name.Substring(dot).ToLowerInvariant();
        }

        private static string? ParentOf(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? null : path.Substring(0, slash);
        }

        private static string FileBaseName(string path)
        {
            var name = Path.GetFileName(path);
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? name : name.Substring(0, dot);
        }

        // The package a compilation assembly's sources ship under — every
        // source file of an asmdef-defined assembly sits under the same
        // asmdef directory, so the first Packages/ path names it; null for
        // project (Assets/) assemblies.
        private static string? PackageNameOfSources(string[]? sourceFiles)
        {
            if (sourceFiles is null)
            {
                return null;
            }
            foreach (var file in sourceFiles)
            {
                var name = PackageNameOfAssetPath(file);
                if (name is not null)
                {
                    return name;
                }
            }
            return null;
        }

        private static string? PackageNameOfAssetPath(string? assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }
            var p = assetPath!.Replace('\\', '/');
            if (!p.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return null;
            }
            var slash = p.IndexOf('/', "Packages/".Length);
            return slash < 0
                ? p.Substring("Packages/".Length)
                : p.Substring("Packages/".Length, slash - "Packages/".Length);
        }

        private static string? PackageNameOfAbsolutePath(
            string path, IReadOnlyList<GuardPackageInfo>? packages)
        {
            if (packages is null)
            {
                return null;
            }
            var canonical = Canonicalize(path);
            foreach (var info in packages)
            {
                if (info.ResolvedPath is { } root &&
                    (canonical.StartsWith(
                        root + "/", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(
                         canonical, root, StringComparison.OrdinalIgnoreCase)))
                {
                    return info.Name;
                }
            }
            return null;
        }

        internal static string Canonicalize(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }
            var p = path!.Replace('\\', '/');
            try
            {
                p = Path.GetFullPath(p).Replace('\\', '/');
            }
            catch (Exception)
            {
                return "";
            }
            return p.TrimEnd('/');
        }
    }
}
