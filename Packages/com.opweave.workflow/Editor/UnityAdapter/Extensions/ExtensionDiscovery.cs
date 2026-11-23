#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using OpWeave.Core;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Locates <c>opweave.extension.json</c> at the root of every installed
    /// UPM package that carries extension candidates and runs the SPEC
    /// §X.3.2 / §B0.2 validation for each (SPEC §X.1: only packages the user
    /// explicitly installed are enumerated — OpWeave never searches,
    /// downloads or updates extensions by itself).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Candidates are collected as type metadata only (SPEC §C3 step 2):
    /// <c>[OpWeaveTypeProvider]</c> classes via TypeCache, attributed to
    /// their package through the SPEC §G0 resolution
    /// (<see cref="EditorAssemblyResolver"/>) — candidates in the standard
    /// <c>com.opweave.workflow</c> package and in unpackaged project Editor
    /// assemblies are never extensions; the latter skip the range check by
    /// SPEC §B0.2 item 4. Candidates whose defining assembly cannot be
    /// resolved or attributed are reported in
    /// <see cref="ExtensionDiscoveryReport.UnattributedCandidates"/> for the
    /// registration stage to diagnose.
    /// </para>
    /// <para>
    /// Operation Descriptor observations arrive already identified by the
    /// registration stage through
    /// <see cref="ObservedExtensionOperation"/>; the declared
    /// <c>operations[]</c> list is compared for exact equality against
    /// whatever is observed (SPEC §X.3.2), so a caller that gathered no
    /// observations claims an empty observed set — a manifest declaring
    /// Operations then mismatches and rejects the extension. Offered
    /// Type IDs arrive the same way through
    /// <see cref="ObservedExtensionType"/> once the registration stage
    /// has instantiated bound providers.
    /// </para>
    /// </remarks>
    public static class ExtensionDiscovery
    {
        /// <summary>
        /// Runs one discovery pass over the installed packages and returns a
        /// verdict per extension-relevant package.
        /// </summary>
        /// <param name="observedOperations">
        /// Descriptor identities the registration stage discovered, each
        /// carrying its defining assembly name. An omitted argument
        /// claims an empty observed set — the declared
        /// <c>operations[]</c> list is compared against it exactly
        /// (SPEC §X.3.2).
        /// </param>
        /// <param name="observedTypes">
        /// Type IDs the registration stage observed from instantiated
        /// providers, each carrying the provider's defining assembly
        /// name. An omitted argument claims an empty observed set.
        /// </param>
        public static ExtensionDiscoveryReport Discover(
            IEnumerable<ObservedExtensionOperation>? observedOperations = null,
            IEnumerable<ObservedExtensionType>? observedTypes = null)
        {
            var projectRoot = ProjectRoot;

            // 1. [OpWeaveTypeProvider] classes → attribute to packages via
            //    the G0 resolution (metadata only, never instantiated).
            var providersByPackage = new Dictionary<string, List<DiscoveredTypeProvider>>(StringComparer.Ordinal);
            var projectCandidates = new List<DiscoveredTypeProvider>();
            var unattributed = new List<UnattributedExtensionCandidate>();
            foreach (var type in TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>())
            {
                var provenance = EditorAssemblyResolver.ResolveForType(type);
                if (!provenance.Ok)
                {
                    unattributed.Add(new UnattributedExtensionCandidate(
                        type.FullName ?? type.Name,
                        type.Assembly.GetName().Name ?? "",
                        provenance.Diagnostics));
                    continue;
                }
                var packageId = provenance.Value!.PackageId;
                var discovered = new DiscoveredTypeProvider(
                    type.FullName ?? type.Name, provenance.Value.AssemblyName);
                if (packageId is null)
                {
                    // Project Editor assemblies are compiled against the
                    // current Core: custom types, never extensions, and they
                    // skip the range check (SPEC §B0.2 item 4).
                    projectCandidates.Add(discovered);
                }
                else if (!string.Equals(packageId, EditorAssemblyResolver.StandardPackageId, StringComparison.Ordinal))
                {
                    if (!providersByPackage.TryGetValue(packageId, out var list))
                    {
                        list = new List<DiscoveredTypeProvider>();
                        providersByPackage[packageId] = list;
                    }
                    list.Add(discovered);
                }
            }

            // 2. Installed packages → assemblies rooted inside each, plus
            //    the UPM-resolved provenance every outcome carries (SPEC
            //    §X.3.2 / §G0: source repository and resolved commit come
            //    from the manifest/lock resolution, never self-declared
            //    metadata). The same provenance map is published on the
            //    report as the snapshot's package info slot (SPEC §C3),
            //    covering every installed package — standard included, so
            //    consumers can resolve package info for Standard items too.
            var assemblyToPackage = new Dictionary<string, string>(StringComparer.Ordinal);
            var assembliesByPackage = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var provenanceByPackage = new Dictionary<string, UpmPackageProvenance>(StringComparer.Ordinal);
            var packages = new List<(string Id, string Root)>();
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                var packageId = package.name;
                if (packageId is null)
                {
                    continue;
                }
                var root = Canonicalize(package.resolvedPath);
                if (root.Length == 0)
                {
                    continue;
                }
                provenanceByPackage[packageId] = UpmPackageProvenance.FromPackageInfo(package);
                if (string.Equals(packageId, EditorAssemblyResolver.StandardPackageId, StringComparison.Ordinal))
                {
                    continue;
                }
                packages.Add((packageId, root));
                var assemblies = EditorAssembliesInPackage(root, projectRoot);
                assembliesByPackage[packageId] = assemblies;
                foreach (var name in assemblies)
                {
                    assemblyToPackage[name] = packageId;
                }
            }

            // 3. Observed Operation Descriptor identities → attribute by
            //    assembly name to the package that owns the assembly. Ops in
            //    project/standard assemblies are not extension candidates.
            var operationsByPackage = new Dictionary<string, List<ObservedExtensionOperation>>(StringComparer.Ordinal);
            if (observedOperations is not null)
            {
                foreach (var operation in observedOperations)
                {
                    if (operation is null)
                    {
                        continue;
                    }
                    if (!assemblyToPackage.TryGetValue(operation.AssemblyName, out var packageId))
                    {
                        continue;
                    }
                    if (!operationsByPackage.TryGetValue(packageId, out var list))
                    {
                        list = new List<ObservedExtensionOperation>();
                        operationsByPackage[packageId] = list;
                    }
                    list.Add(operation);
                }
            }

            // 3b. Offered Type IDs → attribute by the provider's defining
            //     assembly name, exactly like the Descriptor identities.
            var typesByPackage = new Dictionary<string, List<ObservedExtensionType>>(StringComparer.Ordinal);
            if (observedTypes is not null)
            {
                foreach (var type in observedTypes)
                {
                    if (type is null)
                    {
                        continue;
                    }
                    if (!assemblyToPackage.TryGetValue(type.AssemblyName, out var packageId))
                    {
                        continue;
                    }
                    if (!typesByPackage.TryGetValue(packageId, out var list))
                    {
                        list = new List<ObservedExtensionType>();
                        typesByPackage[packageId] = list;
                    }
                    list.Add(type);
                }
            }

            // 4. Every package with a manifest or at least one candidate is
            //    an extension candidate and gets a validation input. A
            //    manifest that exists but cannot be read rejects the package
            //    directly — an unreadable manifest is never misreported as
            //    malformed JSON.
            var currentUnityVersion = Application.unityVersion;
            var inputs = new List<ExtensionValidationInput>();
            var directOutcomes = new List<ExtensionValidationOutcome>();
            foreach (var (packageId, root) in packages)
            {
                providersByPackage.TryGetValue(packageId, out var providers);
                operationsByPackage.TryGetValue(packageId, out var operations);
                typesByPackage.TryGetValue(packageId, out var types);
                var manifestPath = root + "/" + ExtensionManifestReader.FileName;
                var manifestPresent = File.Exists(manifestPath);
                var hasCandidates =
                    (providers is not null && providers.Count > 0) ||
                    (operations is not null && operations.Count > 0) ||
                    (types is not null && types.Count > 0);
                if (!manifestPresent && !hasCandidates)
                {
                    continue;
                }
                string? manifestText = null;
                string? readError;
                if (manifestPresent && !TryReadManifest(manifestPath, out manifestText, out readError))
                {
                    directOutcomes.Add(ExtensionValidationOutcome.Reject(
                        packageId,
                        manifestPresent: true,
                        manifest: null,
                        diagnostics: new[]
                        {
                            new Diagnostic(
                                string.Empty,
                                Severity.Error,
                                $"Extension manifest '{ExtensionManifestReader.FileName}' of package '{packageId}' could not be read.",
                                Hint: readError),
                        }));
                    continue;
                }
                inputs.Add(new ExtensionValidationInput(
                    packageId,
                    root,
                    manifestPresent ? manifestText : null,
                    assembliesByPackage[packageId],
                    providers ?? (IReadOnlyList<DiscoveredTypeProvider>)Array.Empty<DiscoveredTypeProvider>(),
                    operations ?? (IReadOnlyList<ObservedExtensionOperation>)Array.Empty<ObservedExtensionOperation>(),
                    types ?? (IReadOnlyList<ObservedExtensionType>)Array.Empty<ObservedExtensionType>(),
                    currentUnityVersion));
            }

            var outcomes = new List<ExtensionValidationOutcome>(
                ExtensionManifestValidator.ValidateAll(inputs));
            outcomes.AddRange(directOutcomes);
            for (var i = 0; i < outcomes.Count; i++)
            {
                if (provenanceByPackage.TryGetValue(outcomes[i].PackageId, out var provenance))
                {
                    outcomes[i] = outcomes[i].WithPackage(provenance);
                }
            }
            outcomes.Sort((left, right) => string.CompareOrdinal(left.PackageId, right.PackageId));
            // The report derives the per-Operation (id, version) and
            // per-TypeProvider (Provider ID) provenance lookups — the
            // keyed form T-W2.2-07 embeds into RegistrySnapshot (SPEC
            // §C3) — from these same outcomes and package map, so an
            // accepted item and its provenance cannot diverge.
            return new ExtensionDiscoveryReport(
                outcomes, projectCandidates, unattributed, provenanceByPackage);
        }

        // Names of Editor assemblies rooted under the package's resolved
        // path: compiled products whose sources all live inside it, and
        // Editor-only precompiled DLLs located inside it. Internal for the
        // Registry build's production package-facts seam.
        // The CompilationPipeline snapshot is fixed for the domain's whole
        // lifetime: any change to the compiled assembly set or to the
        // precompiled-DLL set arrives through a recompile that ends in a
        // domain reload, which discards these statics. Holding the arrays
        // once therefore yields the same names a fresh query would, at
        // every call, for the rest of the domain.
        private static CompilationPipelineSnapshot? s_pipeline;

        private sealed class CompilationPipelineSnapshot
        {
            internal readonly UnityEditor.Compilation.Assembly[] EditorAssemblies;
            internal readonly IReadOnlyDictionary<string, string> PrecompiledPaths;

            internal CompilationPipelineSnapshot()
            {
                EditorAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                var paths = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var name in CompilationPipeline.GetPrecompiledAssemblyNames())
                {
                    var path = CompilationPipeline.GetPrecompiledAssemblyPathFromAssemblyName(name);
                    if (!string.IsNullOrEmpty(path))
                    {
                        paths[name] = path;
                    }
                }
                PrecompiledPaths = paths;
            }
        }

        private static CompilationPipelineSnapshot PipelineSnapshot =>
            s_pipeline ??= new CompilationPipelineSnapshot();

        internal static List<string> EditorAssembliesInPackage(string packageRoot, string projectRoot)
        {
            var names = new List<string>();
            foreach (var assembly in PipelineSnapshot.EditorAssemblies)
            {
                var sources = assembly.sourceFiles;
                if (sources is null || sources.Length == 0)
                {
                    continue;
                }
                var inside = true;
                foreach (var source in sources)
                {
                    if (!IsUnder(CanonicalizeProjectRelative(source, projectRoot), packageRoot))
                    {
                        inside = false;
                        break;
                    }
                }
                if (inside)
                {
                    names.Add(assembly.name);
                }
            }
            foreach (var entry in PipelineSnapshot.PrecompiledPaths)
            {
                if (IsUnder(CanonicalizeProjectRelative(entry.Value, projectRoot), packageRoot))
                {
                    names.Add(Path.GetFileNameWithoutExtension(entry.Key));
                }
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static bool TryReadManifest(string manifestPath, out string? text, out string? error)
        {
            try
            {
                text = File.ReadAllText(manifestPath);
                error = null;
                return true;
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                text = null;
                error = ex.GetType().Name;
                return false;
            }
        }

        private static bool IsUnder(string canonicalPath, string canonicalRoot) =>
            canonicalPath.StartsWith(canonicalRoot + "/", StringComparison.OrdinalIgnoreCase);

        // Canonical absolute form: forward slashes, no trailing slash.
        // Internal for the Registry build's production package-facts seam.
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

        // Like <see cref="Canonicalize"/>, also rooting project-relative
        // paths (compilation sourceFiles, precompiled paths) at the project.
        private static string CanonicalizeProjectRelative(string? path, string projectRoot)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }
            var p = path!.Replace('\\', '/');
            if (!Path.IsPathRooted(p))
            {
                p = projectRoot + "/" + p;
            }
            return Canonicalize(p);
        }

        internal static string ProjectRoot =>
            Canonicalize(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
    }
}
