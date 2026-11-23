#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using OpWeave.Core;
using UnityEditor.Compilation;
using UnityEngine;
using CompilationAssembly = UnityEditor.Compilation.Assembly;
using ReflectionAssembly = System.Reflection.Assembly;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// SPEC §G0 assembly inspection: maps a loaded
    /// <see cref="System.Reflection.Assembly"/> to the Unity compilation model,
    /// proves it is Editor-only, and assigns its origin — never by casting to
    /// <see cref="UnityEditor.Compilation.Assembly"/> (the two types are
    /// unrelated), but by uniquely resolving
    /// <see cref="CompilationPipeline.GetAssemblies(AssembliesType)"/> candidates
    /// on name and output path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A candidate is eligible only when its defining assembly is provably
    /// Editor-only: a resolved compilation assembly must carry
    /// <see cref="AssemblyFlags.EditorAssembly"/> — or, for a predefined
    /// assembly with no .asmdef, where the flag is not reported — and must
    /// not appear in the Player compilation
    /// (<c>GetAssemblies(AssembliesType.Player)</c>), and a
    /// precompiled DLL must be Unity-known (referenced by an Editor
    /// compilation assembly or matching an imported precompiled-assembly
    /// path), must be referenced by no Player compilation assembly, and must
    /// be established as Editor-only by its
    /// <see cref="UnityEditor.PluginImporter"/> — compatible with the Editor
    /// and incompatible with every Player build target. Absence of a Player
    /// reference alone proves nothing: a Player-compatible plugin with Auto
    /// Reference disabled has no Player reference. Anything else —
    /// unresolvable or ambiguously resolved precompiled DLLs included — is
    /// rejected with an OW-OP-017 diagnostic (SPEC §C3); an assembly that
    /// cannot be identified uniquely is never assumed Editor-only, and source
    /// distribution is recommended (SPEC §G0).
    /// </para>
    /// <para>
    /// The origin check looks up the package through
    /// <c>UnityEditor.PackageManager.PackageInfo.FindForAssembly</c> only for
    /// the uniquely resolved assembly (SPEC §G0, §C3 rule origin) and records
    /// the full UPM-resolved provenance — package ID, source, source
    /// repository and resolved commit — in
    /// <see cref="EditorAssemblyProvenance.Package"/> (SPEC §X.3.2, §C3
    /// package info). Membership of <see cref="StandardPackageId"/> selects
    /// <see cref="OperationOrigin.Standard"/>. The resolution is
    /// metadata-only — no candidate constructor ever runs here (SPEC §C3
    /// stage 2).
    /// </para>
    /// <para>
    /// Each <see cref="CompilationPipeline.GetAssemblies(AssembliesType)"/>
    /// call goes through the installed <see cref="ICompilationAssemblyQueries"/>
    /// (T-W2.2-09). Outside any scope that is the live
    /// <see cref="CompilationPipeline"/> — the historic per-resolution call
    /// pattern. <see cref="RegistryBuildHost"/> opens
    /// <see cref="OpenCompilationAssemblyScope"/> for the duration of one
    /// registry build so every per-type resolution shares each kind's
    /// result; the cache dies with the scope and nothing is held across
    /// builds or domain reloads. The queries — and therefore every
    /// resolution outcome — are identical either way.
    /// </para>
    /// </remarks>
    public static class EditorAssemblyResolver
    {
        /// <summary>
        /// The UPM package ID of the OpWeave standard set (SPEC §0.5); a defining
        /// assembly inside it carries <see cref="OperationOrigin.Standard"/>.
        /// </summary>
        public const string StandardPackageId = "com.opweave.workflow";

        /// <summary>
        /// Resolves the provenance of <paramref name="type"/>'s defining
        /// assembly (SPEC §G0). The type is used as metadata only.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
        public static Result<EditorAssemblyProvenance> ResolveForType(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            return Resolve(type.Assembly);
        }

        /// <summary>
        /// Resolves the provenance of a loaded assembly (SPEC §G0). Success means
        /// the assembly was uniquely resolved and is provably Editor-only; every
        /// rejection carries an OW-OP-017 diagnostic (SPEC §C3).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
        public static Result<EditorAssemblyProvenance> Resolve(ReflectionAssembly assembly)
        {
            if (assembly is null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            // Inside a build scope the queries are already frozen, so a
            // second resolution of the same loaded assembly must return
            // the same result — the memo only removes the repeated
            // computation. Outside a scope each call resolves fresh, as
            // before.
            if (queries is ScopedQueries scoped)
            {
                if (!scoped.ResolveCache.TryGetValue(assembly, out var memoized))
                {
                    memoized = ResolveCore(assembly);
                    scoped.ResolveCache[assembly] = memoized;
                }
                return memoized;
            }
            return ResolveCore(assembly);
        }

        private static Result<EditorAssemblyProvenance> ResolveCore(ReflectionAssembly assembly)
        {
            var name = assembly.GetName().Name ?? "";
            var location = NormalizePath(SafeLocation(assembly));

            var editorAssemblies = queries.GetAssemblies(AssembliesType.Editor);
            var nameMatches = new List<CompilationAssembly>();
            foreach (var candidate in editorAssemblies)
            {
                if (string.Equals(candidate.name, name, StringComparison.Ordinal))
                {
                    nameMatches.Add(candidate);
                }
            }

            CompilationAssembly? resolved = null;
            if (nameMatches.Count == 1)
            {
                // A single name match is only the loaded assembly when its
                // output path matches; a same-named foreign DLL is not the
                // compilation product and falls through to the precompiled
                // resolution (SPEC §G0 name + outputPath identification).
                var candidate = nameMatches[0];
                if (SamePath(candidate.outputPath, location))
                {
                    resolved = candidate;
                }
            }
            else if (nameMatches.Count > 1)
            {
                // Disambiguate a shared name by the compiled output path.
                var pathMatches = new List<CompilationAssembly>();
                foreach (var candidate in nameMatches)
                {
                    if (SamePath(candidate.outputPath, location))
                    {
                        pathMatches.Add(candidate);
                    }
                }
                if (pathMatches.Count == 1)
                {
                    resolved = pathMatches[0];
                }
                else
                {
                    return Reject(
                        "Defining assembly could not be resolved to a unique Unity compilation assembly.",
                        name, location);
                }
            }

            return resolved is not null
                ? ResolveCompiled(assembly, resolved)
                : ResolvePrecompiled(assembly, name, location, editorAssemblies);
        }

        /// <summary>
        /// The compilation-assembly queries the §G0 resolution runs
        /// (T-W2.2-09). The production default forwards each call to
        /// <see cref="CompilationPipeline"/>; a scope opened by
        /// <see cref="OpenCompilationAssemblyScope"/> memoizes each
        /// <see cref="AssembliesType"/> kind for the duration of one
        /// registry build, and tests substitute a counting fake through
        /// <see cref="InstallQueries"/>.
        /// </summary>
        internal interface ICompilationAssemblyQueries
        {
            /// <summary>
            /// The <see cref="CompilationPipeline.GetAssemblies(AssembliesType)"/>
            /// result for <paramref name="assembliesType"/>.
            /// </summary>
            CompilationAssembly[] GetAssemblies(AssembliesType assembliesType);
        }

        /// <summary>
        /// Opens the one-build memoization scope (T-W2.2-09): until the
        /// returned handle is disposed, each <see cref="AssembliesType"/>
        /// kind is queried once — lazily, on first use — and every
        /// resolution inside the scope reuses that result. Disposing
        /// restores the previously installed queries, so the cache lives
        /// exactly as long as the scope and is never shared across builds
        /// or domain reloads. A scope opened inside another scope
        /// memoizes over its outer view. Outside any scope each
        /// resolution queries <see cref="CompilationPipeline"/> directly,
        /// as before.
        /// </summary>
        internal static IDisposable OpenCompilationAssemblyScope() =>
            InstallQueries(new ScopedQueries(queries));

        /// <summary>
        /// Installs <paramref name="next"/> as the resolver's
        /// compilation-assembly queries until the returned handle is
        /// disposed — the seam the counting fakes of the §G0 [Edit]
        /// acceptance tests plug into (T-W2.2-09). Production code only
        /// opens <see cref="OpenCompilationAssemblyScope"/>. Handles must
        /// be disposed in the order they were opened (every caller uses
        /// <c>using</c>).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="next"/> is null.</exception>
        internal static IDisposable InstallQueries(ICompilationAssemblyQueries next)
        {
            if (next is null)
            {
                throw new ArgumentNullException(nameof(next));
            }
            var previous = queries;
            queries = next;
            return new RestoreQueries(previous);
        }

        // The queries currently in force: the CompilationPipeline default
        // outside any scope, the innermost open scope's memoizing view
        // inside a build, or a test seam. Per-domain main-thread state —
        // a domain reload clears it and a scope restores the previous
        // queries on Dispose, so nothing is cached across registry builds
        // or domain reloads (T-W2.2-09).
        private static ICompilationAssemblyQueries queries = new CompilationPipelineQueries();

        // The production queries: the Editor and Player arrays are
        // CompilationPipeline results for the domain's whole lifetime —
        // any change to the compiled assembly set arrives through a
        // recompile that ends in a domain reload, which discards these
        // statics — so each kind is queried once per domain instead of
        // once per call. Other kinds still go straight to
        // CompilationPipeline — the historic per-resolution call pattern
        // outside a build scope.
        private sealed class CompilationPipelineQueries : ICompilationAssemblyQueries
        {
            private static CompilationAssembly[]? s_editorAssemblies;
            private static CompilationAssembly[]? s_playerAssemblies;

            public CompilationAssembly[] GetAssemblies(AssembliesType assembliesType) =>
                assembliesType switch
                {
                    AssembliesType.Editor =>
                        s_editorAssemblies ??=
                            CompilationPipeline.GetAssemblies(AssembliesType.Editor),
                    AssembliesType.Player =>
                        s_playerAssemblies ??=
                            CompilationPipeline.GetAssemblies(AssembliesType.Player),
                    _ => CompilationPipeline.GetAssemblies(assembliesType),
                };
        }

        // The memoizing view one build scope installs: each
        // AssembliesType kind is queried from the inner queries at most
        // once, on first use.
        private sealed class ScopedQueries : ICompilationAssemblyQueries
        {
            private readonly ICompilationAssemblyQueries inner;
            private readonly Dictionary<AssembliesType, CompilationAssembly[]> cache =
                new Dictionary<AssembliesType, CompilationAssembly[]>();

            // Per-scope memoizations: every entry is a pure function of the
            // frozen queries, so a repeated answer inside one build is the
            // same answer — the caches only remove recomputation. They die
            // with the scope like the assembly cache.
            internal readonly Dictionary<ReflectionAssembly, Result<EditorAssemblyProvenance>>
                ResolveCache =
                    new Dictionary<ReflectionAssembly, Result<EditorAssemblyProvenance>>();
            private UnityEditor.PackageManager.PackageInfo[]? registeredPackages;

            internal ScopedQueries(ICompilationAssemblyQueries inner) => this.inner = inner;

            public CompilationAssembly[] GetAssemblies(AssembliesType assembliesType)
            {
                if (!cache.TryGetValue(assembliesType, out var assemblies))
                {
                    assemblies = inner.GetAssemblies(assembliesType);
                    cache[assembliesType] = assemblies;
                }
                return assemblies;
            }

            // The registered-package list for the scope's duration: package
            // registration cannot change in the middle of one registry
            // build, so one enumeration serves every path mapping.
            internal UnityEditor.PackageManager.PackageInfo[] RegisteredPackages =>
                registeredPackages ??=
                    UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
        }

        // Restores the queries that were installed before the matching
        // InstallQueries call; Dispose is the scope's end.
        private sealed class RestoreQueries : IDisposable
        {
            private readonly ICompilationAssemblyQueries restore;
            private bool disposed;

            internal RestoreQueries(ICompilationAssemblyQueries restore) => this.restore = restore;

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                queries = restore;
            }
        }

        /// <summary>
        /// Maps a raw <c>[CallerFilePath]</c> value (SPEC §C2 item 2, §T.4 L-10)
        /// to a project-relative display path such as
        /// <c>Assets/Foo/Bar.cs</c>, <c>Packages/com.opweave.workflow/Bar.cs</c>
        /// or <c>Library/PackageCache/&lt;pkg&gt;/Bar.cs</c>. Returns null when
        /// the path is empty, malformed or outside the project — the recorded
        /// outcome for DLLs compiled on another machine. Display only: an
        /// unresolvable path never affects registration or fingerprints
        /// (SPEC §T.4 L-10).
        /// </summary>
        public static string? ToProjectRelativePath(string? callerFilePath)
        {
            if (string.IsNullOrEmpty(callerFilePath))
            {
                return null;
            }
            string full;
            try
            {
                full = NormalizePath(Path.GetFullPath(callerFilePath));
            }
            catch (Exception)
            {
                return null;
            }
            // UPM packages first: the canonical display form "Packages/<name>/..."
            // also covers file:/embedded packages whose resolvedPath sits
            // outside the project directory, and cache packages under
            // Library/PackageCache (SPEC §G0 uses resolved UPM information).
            try
            {
                var registeredPackages = queries is ScopedQueries scoped
                    ? scoped.RegisteredPackages
                    : UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                foreach (var package in registeredPackages)
                {
                    var resolvedPath = package.resolvedPath;
                    var assetPath = package.assetPath;
                    if (string.IsNullOrEmpty(resolvedPath) || string.IsNullOrEmpty(assetPath))
                    {
                        continue;
                    }
                    var packageRoot = NormalizePath(Path.GetFullPath(resolvedPath));
                    if (full.StartsWith(packageRoot + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        return NormalizePath(assetPath) + "/" + full.Substring(packageRoot.Length + 1);
                    }
                }
            }
            catch (Exception)
            {
                // Package enumeration is best effort; fall back to the project root.
            }

            var root = ProjectRoot;
            return full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length + 1)
                : null;
        }

        // Assemblies Unity compiled from project/package sources.
        private static Result<EditorAssemblyProvenance> ResolveCompiled(
            ReflectionAssembly assembly, CompilationAssembly resolved)
        {
            // The asmdef-defined proof is the EditorAssembly flag; a
            // predefined assembly (no .asmdef — the flag is not reported
            // for the predefined Editor products) is proven Editor-only by
            // the Player-compilation check below instead.
            if ((resolved.flags & AssemblyFlags.EditorAssembly) == 0 &&
                HasAssemblyDefinitionFile(resolved.name))
            {
                return Reject(
                    "Defining assembly is not an Editor-only assembly.",
                    resolved.name, NormalizePath(resolved.outputPath));
            }

            var playerAssemblies = queries.GetAssemblies(AssembliesType.Player);
            foreach (var player in playerAssemblies)
            {
                if (string.Equals(player.name, resolved.name, StringComparison.Ordinal))
                {
                    return Reject(
                        "Defining assembly also enters the Player compilation.",
                        resolved.name, NormalizePath(resolved.outputPath));
                }
            }

            var package = FindPackageProvenance(assembly);
            return Result<EditorAssemblyProvenance>.Success(new EditorAssemblyProvenance(
                resolved.name,
                NormalizePath(resolved.outputPath),
                isPrecompiled: false,
                OriginOf(package?.PackageId),
                package,
                resolved));
        }

        // Unity's compilation Assembly exposes no .asmdef path, so the
        // defining-assembly lookup answers the question: an empty result is
        // the predefined Assembly-CSharp set. An unverifiable answer is never
        // read as "predefined" — §G0 fails closed on anything unprovable.
        private static bool HasAssemblyDefinitionFile(string assemblyName)
        {
            try
            {
                return !string.IsNullOrEmpty(CompilationPipeline
                    .GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName));
            }
            catch (Exception)
            {
                return true;
            }
        }

        // Precompiled DLLs have no compilation entry; identity is the loaded
        // location resolved against Unity's precompiled-assembly records.
        private static Result<EditorAssemblyProvenance> ResolvePrecompiled(
            ReflectionAssembly assembly, string name, string location,
            CompilationAssembly[] editorAssemblies)
        {
            // The DLL's own path must match a Unity-known precompiled path or be
            // referenced by an Editor compilation assembly; otherwise nothing
            // proves it is Editor-only and it is rejected (SPEC §G0).
            var editorReferenced = ReferencesPath(editorAssemblies, location);

            var knownPath = string.Empty;
            foreach (var precompiledName in CompilationPipeline.GetPrecompiledAssemblyNames())
            {
                // Precompiled names are file names carrying the .dll
                // extension; the loaded assembly name has none.
                if (string.Equals(Path.GetFileNameWithoutExtension(precompiledName), name, StringComparison.Ordinal))
                {
                    var path = CompilationPipeline.GetPrecompiledAssemblyPathFromAssemblyName(precompiledName);
                    if (!string.IsNullOrEmpty(path) && SamePath(path, location))
                    {
                        knownPath = NormalizePath(path);
                    }
                }
            }

            if (!editorReferenced && knownPath.Length == 0)
            {
                return Reject(
                    "Precompiled defining assembly could not be resolved uniquely; source distribution is recommended.",
                    name, location);
            }

            var playerAssemblies = queries.GetAssemblies(AssembliesType.Player);
            if (ReferencesPath(playerAssemblies, location))
            {
                return Reject(
                    "Precompiled defining assembly also enters the Player compilation.",
                    name, location);
            }

            // Absence of a Player reference does not prove Editor-only: a
            // Player-compatible plugin with Auto Reference disabled has no
            // Player reference at all. Only the plugin importer for the
            // asset establishes Editor-only status (SPEC §G0); a missing
            // importer or an unprovable result fails closed.
            var importer = FindPluginImporter(location);
            if (importer is null || !IsEditorOnly(importer))
            {
                return Reject(
                    "Precompiled defining assembly is not provably Editor-only; source distribution is recommended.",
                    name, location);
            }

            var package = FindPackageProvenanceForAsset(location);
            return Result<EditorAssemblyProvenance>.Success(new EditorAssemblyProvenance(
                name,
                location,
                isPrecompiled: true,
                OriginOf(package?.PackageId),
                package,
                compilationAssembly: null));
        }

        // True when any compilation assembly passes the path to the compiler as
        // a precompiled reference (compiledAssemblyReferences).
        private static bool ReferencesPath(CompilationAssembly[] assemblies, string path)
        {
            foreach (var assembly in assemblies)
            {
                var references = assembly.compiledAssemblyReferences;
                if (references is null)
                {
                    continue;
                }
                foreach (var reference in references)
                {
                    if (SamePath(reference, path))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // The plugin importer bound to the asset at this exact path, or null.
        // The project-relative form is also the Unity asset path
        // ("Assets/…", "Packages/<id>/…"), so a Library/ or external path that
        // is not an imported asset resolves no importer and fails closed.
        private static UnityEditor.PluginImporter? FindPluginImporter(string location)
        {
            var assetPath = ToProjectRelativePath(location);
            if (assetPath is null)
            {
                return null;
            }
            try
            {
                return UnityEditor.AssetImporter.GetAtPath(assetPath) as UnityEditor.PluginImporter;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Editor-only is established, never assumed (SPEC §G0): the importer
        // must report Editor compatibility, no Any-Platform compatibility
        // (per-target entries are exclusions under Any-Platform, not
        // enables), and no compatibility with any Player build target this
        // Editor knows. An importer query failure is not proof either.
        private static bool IsEditorOnly(UnityEditor.PluginImporter importer)
        {
            try
            {
                if (!importer.GetCompatibleWithEditor() || importer.GetCompatibleWithAnyPlatform())
                {
                    return false;
                }
                foreach (UnityEditor.BuildTarget target in Enum.GetValues(typeof(UnityEditor.BuildTarget)))
                {
                    if (importer.GetCompatibleWithPlatform(target))
                    {
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        // Source-built assemblies: the loaded assembly handed to
        // FindForAssembly has already been uniquely resolved to a Unity
        // compilation assembly (name + outputPath) and proven Editor-only,
        // so the lookup runs on the resolved compilation assembly's own
        // identity — never on an unverified System.Reflection.Assembly
        // (SPEC §G0, §X.8). On Unity 6000.0 the only public FindForAssembly
        // overload accepts the loaded assembly; the resolved
        // CompilationAssembly determines which one is passed.
        private static UpmPackageProvenance? FindPackageProvenance(ReflectionAssembly assembly)
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
                return info is null ? null : UpmPackageProvenance.FromPackageInfo(info);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Precompiled DLLs have no compilation assembly to resolve; the
        // package that owns the DLL's asset path is still UPM-resolved
        // information (never self-declared metadata, SPEC §G0).
        private static UpmPackageProvenance? FindPackageProvenanceForAsset(string location)
        {
            var assetPath = ToProjectRelativePath(location);
            if (assetPath is null)
            {
                return null;
            }
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                return info is null ? null : UpmPackageProvenance.FromPackageInfo(info);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static OperationOrigin OriginOf(string? packageId) =>
            string.Equals(packageId, StandardPackageId, StringComparison.Ordinal)
                ? OperationOrigin.Standard
                : OperationOrigin.Custom;

        private static Result<EditorAssemblyProvenance> Reject(
            string message, string assemblyName, string assemblyPath) =>
            Result<EditorAssemblyProvenance>.Failure(ErrorCodes.Create(
                ErrorCodes.OW_OP_017,
                message,
                hint: $"{assemblyName} ({assemblyPath})"));

        private static string SafeLocation(ReflectionAssembly assembly)
        {
            try
            {
                return assembly.Location ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static bool SamePath(string? left, string? right) =>
            !string.IsNullOrEmpty(left) &&
            !string.IsNullOrEmpty(right) &&
            string.Equals(CanonicalPath(left!), CanonicalPath(right!), StringComparison.OrdinalIgnoreCase);

        // Canonical comparison form: separators unified, project-relative paths
        // (compiledAssemblyReferences, precompiled paths) rooted at the project.
        private static string CanonicalPath(string path)
        {
            var p = path.Replace('\\', '/');
            if (!Path.IsPathRooted(p))
            {
                p = ProjectRoot + "/" + p;
            }
            try
            {
                p = Path.GetFullPath(p);
            }
            catch (Exception)
            {
                // Keep the unrooted form; it will simply fail to match.
            }
            return p.Replace('\\', '/').TrimEnd('/');
        }

        private static string NormalizePath(string? path) =>
            string.IsNullOrEmpty(path) ? "" : path!.Replace('\\', '/').TrimEnd('/');

        private static string ProjectRoot =>
            NormalizePath(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
    }
}
