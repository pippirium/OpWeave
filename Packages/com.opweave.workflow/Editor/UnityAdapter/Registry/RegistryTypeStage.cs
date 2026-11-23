#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using OpWeave.Core;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// SPEC §C3 stages 1–5 — the type side of the atomic Registry rebuild:
    /// verify the compile state, collect Operation/TypeProvider candidates as
    /// type metadata, apply provenance and the pre-instantiation exclusion
    /// predicate, then instantiate the admitted providers in deterministic
    /// order and build the immutable <see cref="ITypeRegistry"/> snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stage 1 stops the whole build when the loaded assemblies are not the
    /// product of a successful compile — <see cref="EditorApplication.isCompiling"/>
    /// still true (a reload is in flight and the hook will run again after it)
    /// or <see cref="EditorUtility.scriptCompilationFailed"/> true (SPEC §C3
    /// rule 9: the previous snapshot is marked <c>stale</c>). No candidate
    /// class is constructed on that path.
    /// </para>
    /// <para>
    /// Stages 2–3 never construct a candidate: provenance is resolved by
    /// <see cref="EditorAssemblyResolver"/> (OW-OP-017 on failure), the
    /// manifest <c>typeProviders[]</c> bindings supply the Registry Provider
    /// IDs of packaged providers (SPEC §X.6), and the injected
    /// <see cref="IExtensionExclusionPredicate"/> decides admission —
    /// <see cref="SettingsExtensionExclusion"/> applies the loaded
    /// DiscoveryMode and <c>disabledOperations</c> here (SPEC §C0.2,
    /// §H4.1). An admitted provider's first constructor
    /// run happens only after the predicate has accepted it (SPEC §C3
    /// rule 5 contract).
    /// </para>
    /// <para>
    /// Rejections are retained as <see cref="ExtensionCandidateFailure"/>
    /// records (internal diagnostics where no merged emitter already carries
    /// a published code; the OW-OP-013..019 snapshot mapping is T-W2.12-03)
    /// and never affect unrelated candidates (SPEC §C3 step 4).
    /// </para>
    /// </remarks>
    internal static class RegistryTypeStage
    {
        /// <summary>
        /// The injected seams of one stage 1–5 run. <see cref="PackageFacts"/>
        /// resolves the on-disk facts (manifest text, resolved root, rooted
        /// Editor assemblies) of a UPM package that contributes extension
        /// candidates; a null answer is treated like an absent or unreadable
        /// manifest.
        /// </summary>
        internal sealed record Seams(
            Func<bool> IsCompiling,
            Func<bool> ScriptCompilationFailed,
            Func<IEnumerable<Type>> OperationTypes,
            Func<IEnumerable<Type>> ProviderTypes,
            Func<Type, Result<EditorAssemblyProvenance>> ResolveProvenance,
            Func<string, ExtensionPackageFacts?> PackageFacts,
            IExtensionExclusionPredicate Exclusion,
            IClassRelationProvider? ClassRelations);

        /// <summary>
        /// The production seams of SPEC §C3 stages 1–5:
        /// <see cref="EditorApplication.isCompiling"/> /
        /// <see cref="EditorUtility.scriptCompilationFailed"/> for stage 1,
        /// TypeCache over every loaded Editor assembly for stage 2, the §G0
        /// resolver for provenance, the package manifest reader for Provider
        /// ID binding, the injected exclusion predicate and the Editor-side
        /// <see cref="UnityClassRelationProvider"/> for class-id assignability.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="exclusion"/> is null.</exception>
        internal static Seams Production(IExtensionExclusionPredicate exclusion) =>
            new Seams(
                static () => EditorApplication.isCompiling,
                static () => EditorUtility.scriptCompilationFailed,
                static () => TypeCache.GetTypesWithAttribute<Core.Operations.OpWeaveOperationAttribute>(),
                static () => TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                static type => EditorAssemblyResolver.ResolveForType(type),
                ProductionPackageFacts,
                exclusion ?? throw new ArgumentNullException(nameof(exclusion)),
                new UnityClassRelationProvider());

        /// <summary>
        /// Runs stages 1–5 over the injected seams; never fails wholesale —
        /// per-candidate defects land in
        /// <see cref="RegistryTypeBuildResult.Failures"/> /
        /// <see cref="RegistryTypeBuildResult.Exclusions"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="seams"/> or one of its delegates is null.
        /// </exception>
        internal static RegistryTypeBuildResult Run(Seams seams)
        {
            if (seams is null)
            {
                throw new ArgumentNullException(nameof(seams));
            }
            var isCompiling = seams.IsCompiling ?? throw new ArgumentNullException(nameof(seams.IsCompiling));
            var scriptCompilationFailed = seams.ScriptCompilationFailed ??
                throw new ArgumentNullException(nameof(seams.ScriptCompilationFailed));
            var operationTypes = seams.OperationTypes ?? throw new ArgumentNullException(nameof(seams.OperationTypes));
            var providerTypes = seams.ProviderTypes ?? throw new ArgumentNullException(nameof(seams.ProviderTypes));
            var resolveProvenance = seams.ResolveProvenance ??
                throw new ArgumentNullException(nameof(seams.ResolveProvenance));
            var packageFacts = seams.PackageFacts ??
                throw new ArgumentNullException(nameof(seams.PackageFacts));
            var exclusion = seams.Exclusion ?? throw new ArgumentNullException(nameof(seams.Exclusion));

            // ── Stage 1: a failed or in-flight compile means the loaded
            //    assemblies are not a consistent compile product — the
            //    published snapshot stays and is marked stale (SPEC §C3
            //    rule 9). Nothing is scanned or constructed. ──
            if (isCompiling() || scriptCompilationFailed())
            {
                return RegistryTypeBuildResult.StaleResult();
            }

            var failures = new List<ExtensionCandidateFailure>();
            var exclusions = new List<ExtensionCandidateExclusion>();

            // ── Stage 2: Operation and TypeProvider candidates as type
            //    metadata, in a deterministic scan order (assembly name,
            //    then full type name, Ordinal). ──
            var providerScan = SortTypes(providerTypes());
            var operationScan = SortTypes(operationTypes());

            // ── Stage 3a: provenance and package attribution for every
            //    candidate — metadata only, nothing constructed. Packaged
            //    (non-standard) candidates group by UPM package for the SPEC
            //    §X.3.2 extension gate; project and standard-package classes
            //    are never extensions and skip it (SPEC §B0.2 item 4), and a
            //    project or standard provider's Registry Provider ID is the
            //    class's CLR FullName — the only stable identity available
            //    pre-instantiation. ──
            var boundProviders = new List<BoundProvider>();
            var packaged = new List<(Type Type, EditorAssemblyProvenance Provenance)>();
            var discoveredByPackage = new Dictionary<string, List<DiscoveredTypeProvider>>(
                StringComparer.Ordinal);
            foreach (var type in providerScan)
            {
                var provenance = resolveProvenance(type);
                if (!provenance.Ok)
                {
                    failures.Add(Failure(
                        ExtensionCandidateKind.TypeProvider, type,
                        type.Assembly.GetName().Name, provenance.Diagnostics));
                    continue;
                }
                var prov = provenance.Value!;
                var packageId = prov.PackageId;
                if (packageId is null ||
                    string.Equals(packageId, EditorAssemblyResolver.StandardPackageId, StringComparison.Ordinal))
                {
                    boundProviders.Add(new BoundProvider(
                        type, prov, type.FullName ?? type.Name));
                }
                else
                {
                    packaged.Add((type, prov));
                    if (!discoveredByPackage.TryGetValue(packageId, out var discovered))
                    {
                        discovered = new List<DiscoveredTypeProvider>();
                        discoveredByPackage[packageId] = discovered;
                    }
                    discovered.Add(new DiscoveredTypeProvider(
                        type.FullName ?? type.Name, prov.AssemblyName));
                }
            }

            var unextensionOperations = new List<ExtensionCandidate>();
            var packagedOperations = new List<ExtensionCandidate>();
            var operationsByPackage = new Dictionary<string, List<ExtensionCandidate>>(
                StringComparer.Ordinal);
            foreach (var type in operationScan)
            {
                var provenance = resolveProvenance(type);
                if (!provenance.Ok)
                {
                    failures.Add(Failure(
                        ExtensionCandidateKind.Operation, type,
                        type.Assembly.GetName().Name, provenance.Diagnostics));
                    continue;
                }
                // The attribute-declared (id, version) — SPEC §C2 item 2,
                // §C3 step 2 read without construction. Null when the
                // injected scan supplies a class without the attribute;
                // stage 6 then rejects it as missing [OpWeaveOperation]
                // (OW-OP-008).
                var declared = Core.Operations.OperationEligibility.DeclaredIdentityOf(type);
                var candidate = new ExtensionCandidate(
                    ExtensionCandidateKind.Operation,
                    type,
                    provenance.Value!,
                    SourceFileOf(type),
                    ProviderId: null,
                    DeclaredId: declared?.Id,
                    DeclaredVersion: declared?.Version);
                var packageId = candidate.PackageId;
                if (packageId is null ||
                    string.Equals(packageId, EditorAssemblyResolver.StandardPackageId, StringComparison.Ordinal))
                {
                    unextensionOperations.Add(candidate);
                }
                else
                {
                    packagedOperations.Add(candidate);
                    if (!operationsByPackage.TryGetValue(packageId, out var list))
                    {
                        list = new List<ExtensionCandidate>();
                        operationsByPackage[packageId] = list;
                    }
                    list.Add(candidate);
                }
            }

            // ── Stage 3b: the SPEC §X.3.2 / §B0.2 metadata gate — before any
            //    extension constructor runs, every package contributing
            //    candidates must carry a readable manifest whose
            //    schemaVersion, extensionId, coreApi range (OW-OP-012),
            //    Blueprint reservation (OW-OP-010) and reserved-prefix rules
            //    hold, whose declared Unity minimum is met, and whose
            //    declared extensionId / Operation ID / Provider ID is unique
            //    across packages. A rejected package's candidates are never
            //    admitted or constructed. ──
            var manifests = new Dictionary<string, ExtensionManifest>(StringComparer.Ordinal);
            var factsByPackage = new Dictionary<string, ExtensionPackageFacts>(StringComparer.Ordinal);
            var rejectedPackages = new Dictionary<string, List<Diagnostic>>(StringComparer.Ordinal);
            var packageIds = SortedPackageIds(packaged, operationsByPackage);
            foreach (var packageId in packageIds)
            {
                var facts = packageFacts(packageId);
                var text = facts?.ManifestText;
                if (text is null)
                {
                    rejectedPackages[packageId] = new List<Diagnostic>
                    {
                        Detail(
                            $"Package '{packageId}' contains OpWeave extension candidates but no readable '{ExtensionManifestReader.FileName}' at its root; none of them register (SPEC §X.2, §X.3.2)."),
                    };
                    continue;
                }
                var parsed = ExtensionManifestReader.Parse(text);
                if (!parsed.Ok || parsed.Value is null)
                {
                    rejectedPackages[packageId] = new List<Diagnostic>(parsed.Diagnostics);
                    continue;
                }
                var ineligible = new List<Diagnostic>();
                ExtensionManifestValidator.AppendUnityEligibilityDiagnostics(
                    parsed.Value!, Application.unityVersion, ineligible);
                if (ineligible.Count != 0)
                {
                    rejectedPackages[packageId] = ineligible;
                    continue;
                }
                manifests[packageId] = parsed.Value!;
                factsByPackage[packageId] = facts!;
            }
            CheckDeclaredDuplicates(manifests, rejectedPackages);

            // Rejected packages: every candidate becomes a failure record and
            // is never admitted (SPEC §X.3.2 — a rejected extension registers
            // none of its Operations or TypeProviders).
            var survivingPackaged = new List<(Type Type, EditorAssemblyProvenance Provenance)>();
            foreach (var item in packaged)
            {
                if (rejectedPackages.TryGetValue(item.Provenance.PackageId!, out var diagnostics))
                {
                    failures.Add(Failure(
                        ExtensionCandidateKind.TypeProvider, item.Type,
                        item.Provenance.AssemblyName, diagnostics));
                    continue;
                }
                survivingPackaged.Add(item);
            }
            var survivingOpsByPackage = new Dictionary<string, List<ExtensionCandidate>>(
                StringComparer.Ordinal);
            foreach (var candidate in packagedOperations)
            {
                if (rejectedPackages.TryGetValue(candidate.PackageId!, out var diagnostics))
                {
                    failures.Add(new ExtensionCandidateFailure(
                        ExtensionCandidateKind.Operation,
                        candidate.Type.FullName ?? candidate.Type.Name,
                        candidate.AssemblyName,
                        diagnostics));
                    continue;
                }
                if (!survivingOpsByPackage.TryGetValue(candidate.PackageId!, out var list))
                {
                    list = new List<ExtensionCandidate>();
                    survivingOpsByPackage[candidate.PackageId!] = list;
                }
                list.Add(candidate);
            }

            // ── Stage 3c (providers): bind each surviving packaged provider
            //    class to its manifest-declared typeProviders[] ID — the
            //    deterministic per-assembly pairing of
            //    ExtensionManifestValidator (discovered classes by full name
            //    Ordinal, declared refs by Provider ID Ordinal, index-paired;
            //    SPEC §X.6). A class that cannot pair one-to-one rejects its
            //    whole package: nothing of it is admitted or constructed. ──
            BindPackagedProviders(
                survivingPackaged, manifests, survivingOpsByPackage, boundProviders, failures);

            // ── Stage 3d (providers): eligibility metadata, exclusion
            //    predicate — before any constructor runs. ──
            var admittedProviders = new List<ExtensionCandidate>();
            foreach (var bound in boundProviders)
            {
                var eligibility = TypeProviderEligibility.Check(bound.Type);
                if (eligibility.Count != 0)
                {
                    failures.Add(new ExtensionCandidateFailure(
                        ExtensionCandidateKind.TypeProvider,
                        bound.Type.FullName ?? bound.Type.Name,
                        bound.Provenance.AssemblyName,
                        eligibility));
                    continue;
                }
                var candidate = new ExtensionCandidate(
                    ExtensionCandidateKind.TypeProvider,
                    bound.Type,
                    bound.Provenance,
                    SourceFileOf(bound.Type),
                    bound.ProviderId);
                var reason = exclusion.Exclude(candidate);
                if (reason is not null)
                {
                    exclusions.Add(new ExtensionCandidateExclusion(candidate, reason));
                    continue;
                }
                admittedProviders.Add(candidate);
            }

            // ── Stage 3 (operations): exclusion predicate on every surviving
            //    candidate as metadata only — the predicate receives the
            //    attribute-declared identity (SPEC §C3 rule 5, §C2 item 2).
            //    Class-condition and Descriptor checks belong to stage 6
            //    (OW-OP-008/003, T-W2.2-07). ──
            var operations = new List<ExtensionCandidate>(unextensionOperations);
            foreach (var list in survivingOpsByPackage.Values)
            {
                operations.AddRange(list);
            }
            var predicateAdmitted = new List<ExtensionCandidate>();
            var excludedOpsByPackage = new Dictionary<string, List<ExtensionCandidate>>(
                StringComparer.Ordinal);
            foreach (var candidate in operations)
            {
                var reason = exclusion.Exclude(candidate);
                if (reason is not null)
                {
                    exclusions.Add(new ExtensionCandidateExclusion(candidate, reason));
                    // A packaged exclusion keeps its attribute-declared
                    // (id, version): the extension gate lets it satisfy
                    // only the manifest-declared entry naming that same
                    // identity — the Operation still exists in the
                    // package, so its own declaration is "not observed",
                    // never a declared-vs-observed package mismatch —
                    // while a declaration naming a different Operation
                    // stays uncovered (SPEC §X.3.2, §C3 rule 5: an
                    // intentional exclusion is not a registration error).
                    if (candidate.PackageId is { } excludedPackageId)
                    {
                        if (!excludedOpsByPackage.TryGetValue(excludedPackageId, out var excludedList))
                        {
                            excludedList = new List<ExtensionCandidate>();
                            excludedOpsByPackage[excludedPackageId] = excludedList;
                        }
                        excludedList.Add(candidate);
                    }
                    continue;
                }
                predicateAdmitted.Add(candidate);
            }

            // ── Stage 3 identity decisions on the declared (id, version)
            //    before any Operation constructor runs (SPEC §C3 rules 3–4):
            //    a Custom candidate declaring a reserved namespace is OW-OP-007
            //    and every candidate sharing one declared (id, version) is
            //    OW-OP-004 — decided from type metadata, so a refused class is
            //    never constructed (the builder re-checks Descriptor identity
            //    post-construction). Candidates without a declared identity —
            //    an injected scan type missing the attribute — pass through
            //    for the stage-6 class-condition check (OW-OP-008). ──
            var declaredCounts = new Dictionary<(string Id, int Version), int>();
            foreach (var candidate in predicateAdmitted)
            {
                if (candidate.DeclaredId is not null && candidate.DeclaredVersion is not null)
                {
                    var key = (candidate.DeclaredId, candidate.DeclaredVersion.Value);
                    declaredCounts.TryGetValue(key, out var count);
                    declaredCounts[key] = count + 1;
                }
            }
            var admittedOperations = new List<ExtensionCandidate>();
            var refusedOpsByPackage = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var candidate in predicateAdmitted)
            {
                var diagnostics = new List<Diagnostic>();
                if (candidate.DeclaredId is not null && candidate.DeclaredVersion is not null)
                {
                    var safeId = PublishedId.OperationId(candidate.DeclaredId);
                    if (candidate.Origin != OperationOrigin.Standard &&
                        IdGrammar.HasReservedOperationPrefix(candidate.DeclaredId))
                    {
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_OP_007,
                            $"Operation '{safeId}' uses a namespace reserved for standard Operations."));
                    }
                    var duplicates =
                        declaredCounts[(candidate.DeclaredId, candidate.DeclaredVersion.Value)];
                    if (duplicates > 1)
                    {
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_OP_004,
                            $"Operation '{safeId}' version {candidate.DeclaredVersion.Value} is declared by {duplicates} candidates; none were registered."));
                    }
                }
                if (diagnostics.Count != 0)
                {
                    // A packaged refusal counts toward the extension gate's
                    // unobserved budget like a construction failure — its
                    // manifest-declared entry is "not observed", not a
                    // declared-vs-observed package mismatch (SPEC §X.3.2).
                    if (candidate.PackageId is { } refusedPackageId)
                    {
                        refusedOpsByPackage.TryGetValue(refusedPackageId, out var refusedCount);
                        refusedOpsByPackage[refusedPackageId] = refusedCount + 1;
                    }
                    failures.Add(new ExtensionCandidateFailure(
                        ExtensionCandidateKind.Operation,
                        candidate.Type.FullName ?? candidate.Type.Name,
                        candidate.AssemblyName,
                        diagnostics,
                        DeclaredOperationId: candidate.DeclaredId,
                        DeclaredOperationVersion: candidate.DeclaredVersion,
                        SourceFile: candidate.SourceFile));
                    continue;
                }
                admittedOperations.Add(candidate);
            }

            // ── Stage 4: instantiate the admitted providers in deterministic
            //    order (Provider ID Ordinal, then class full name) — the same
            //    order TypeRegistryBuilder re-applies, so the build is stable
            //    regardless of TypeCache enumeration order. Packaged
            //    providers' offered Type IDs are collected as the observed
            //    facts the pre-publication gate re-verifies (SPEC §X.3.2). ──
            admittedProviders.Sort(CompareCandidates);
            var candidates = new List<TypeProviderCandidate>
            {
                // The standard providers are wired in, never discovered
                // (SPEC §0.6 SG-10: attribute-discovery of the same Type IDs
                // would invalidate both offers under §B0.1).
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    UnityTypeProvider.ProviderId, new UnityTypeProvider(), OperationOrigin.Standard),
            };
            var providersByPackage =
                new Dictionary<string, List<ProviderRegistration>>(StringComparer.Ordinal);
            var observedTypesByPackage =
                new Dictionary<string, List<ObservedExtensionType>>(StringComparer.Ordinal);
            foreach (var candidate in admittedProviders)
            {
                var instance = TypeProviderEligibility.Instantiate(candidate.Type);
                if (!instance.Ok || instance.Value is null)
                {
                    failures.Add(new ExtensionCandidateFailure(
                        ExtensionCandidateKind.TypeProvider,
                        candidate.Type.FullName ?? candidate.Type.Name,
                        candidate.AssemblyName,
                        instance.Diagnostics));
                    continue;
                }
                var providerCandidate = new TypeProviderCandidate(
                    candidate.ProviderId!, instance.Value!, candidate.Origin);
                candidates.Add(providerCandidate);
                var packageId = candidate.PackageId;
                if (packageId is not null && manifests.ContainsKey(packageId))
                {
                    if (!providersByPackage.TryGetValue(packageId, out var registrations))
                    {
                        registrations = new List<ProviderRegistration>();
                        providersByPackage[packageId] = registrations;
                    }
                    registrations.Add(new ProviderRegistration(candidate, providerCandidate));
                    CollectObservedTypes(
                        candidate, instance.Value!, observedTypesByPackage);
                }
            }

            // ── Stage 5: the immutable snapshot (SPEC §B0.1 / §B2 rules via
            //    TypeRegistryBuilder); its rejections are retained for the
            //    OW-OP-013..016 diagnostic mapping of T-W2.12-03. ──
            var build = TypeRegistryBuilder.Build(candidates, seams.ClassRelations);

            // ── The admitted extension packages for the pre-publication
            //    observed-fact gate (SPEC §X.3.2): declared-vs-observed
            //    checks complete after construction. A package whose every
            //    candidate was excluded contributes nothing and needs no
            //    verdict. ──
            var extensionPackages = new List<ExtensionPackageState>();
            foreach (var packageId in packageIds)
            {
                if (!manifests.ContainsKey(packageId) ||
                    !factsByPackage.TryGetValue(packageId, out var facts))
                {
                    continue;
                }
                providersByPackage.TryGetValue(packageId, out var registrations);
                var packageOperations = new List<ExtensionCandidate>();
                foreach (var candidate in admittedOperations)
                {
                    if (string.Equals(candidate.PackageId, packageId, StringComparison.Ordinal))
                    {
                        packageOperations.Add(candidate);
                    }
                }
                if ((registrations is null || registrations.Count == 0) &&
                    packageOperations.Count == 0)
                {
                    continue;
                }
                var providers = new List<ExtensionCandidate>();
                var instances = new List<TypeProviderCandidate>();
                if (registrations is not null)
                {
                    foreach (var registration in registrations)
                    {
                        providers.Add(registration.Candidate);
                        instances.Add(registration.Instance);
                    }
                }
                extensionPackages.Add(new ExtensionPackageState(
                    packageId,
                    facts.ManifestText!,
                    facts.RootPath,
                    facts.EditorAssemblies ?? (IReadOnlyList<string>)Array.Empty<string>(),
                    discoveredByPackage.TryGetValue(packageId, out var discovered)
                        ? discovered
                        : (IReadOnlyList<DiscoveredTypeProvider>)Array.Empty<DiscoveredTypeProvider>(),
                    providers,
                    instances,
                    packageOperations,
                    observedTypesByPackage.TryGetValue(packageId, out var observed)
                        ? observed
                        : (IReadOnlyList<ObservedExtensionType>)Array.Empty<ObservedExtensionType>(),
                    refusedOpsByPackage.TryGetValue(packageId, out var refusedOperations)
                        ? refusedOperations
                        : 0,
                    excludedOpsByPackage.TryGetValue(packageId, out var excludedOperations)
                        ? excludedOperations
                        : (IReadOnlyList<ExtensionCandidate>)Array.Empty<ExtensionCandidate>()));
            }

            return new RegistryTypeBuildResult(
                compilationConsistent: true,
                build.Registry,
                build.Failures,
                candidates,
                admittedOperations,
                failures,
                exclusions,
                extensionPackages);
        }

        private sealed class ProviderRegistration
        {
            internal ProviderRegistration(ExtensionCandidate candidate, TypeProviderCandidate instance)
            {
                Candidate = candidate;
                Instance = instance;
            }

            internal ExtensionCandidate Candidate { get; }
            internal TypeProviderCandidate Instance { get; }
        }

        private sealed class BoundProvider
        {
            internal BoundProvider(Type type, EditorAssemblyProvenance provenance, string providerId)
            {
                Type = type;
                Provenance = provenance;
                ProviderId = providerId;
            }

            internal Type Type { get; }
            internal EditorAssemblyProvenance Provenance { get; }
            internal string ProviderId { get; }
        }

        // Deterministic instantiation order (SPEC §C3 step 4): Provider ID
        // Ordinal, then the class's full name as a stable tiebreaker.
        private static int CompareCandidates(ExtensionCandidate a, ExtensionCandidate b)
        {
            var byId = string.Compare(a.ProviderId, b.ProviderId, StringComparison.Ordinal);
            if (byId != 0)
            {
                return byId;
            }
            return string.Compare(
                a.Type.FullName ?? a.Type.Name, b.Type.FullName ?? b.Type.Name,
                StringComparison.Ordinal);
        }

        private static List<Type> SortTypes(IEnumerable<Type> types)
        {
            var ordered = new List<Type>();
            foreach (var type in types)
            {
                if (type is not null)
                {
                    ordered.Add(type);
                }
            }
            ordered.Sort((a, b) =>
            {
                var byAssembly = string.Compare(
                    a.Assembly.GetName().Name ?? "", b.Assembly.GetName().Name ?? "",
                    StringComparison.Ordinal);
                return byAssembly != 0
                    ? byAssembly
                    : string.Compare(
                        a.FullName ?? a.Name, b.FullName ?? b.Name, StringComparison.Ordinal);
            });
            return ordered;
        }

        // The sorted set of UPM package IDs contributing at least one
        // extension candidate (provider or Operation).
        private static List<string> SortedPackageIds(
            List<(Type Type, EditorAssemblyProvenance Provenance)> packagedProviders,
            Dictionary<string, List<ExtensionCandidate>> operationsByPackage)
        {
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in packagedProviders)
            {
                if (item.Provenance.PackageId is { } packageId)
                {
                    ids.Add(packageId);
                }
            }
            foreach (var packageId in operationsByPackage.Keys)
            {
                ids.Add(packageId);
            }
            return new List<string>(ids);
        }

        // SPEC §X.3.2: extensionId, Operation ID (keyed by the ID alone —
        // two versions of one Operation ID are still a duplicate) and
        // Provider ID must each be declared by at most one package; every
        // package involved in a collision is rejected.
        private static void CheckDeclaredDuplicates(
            Dictionary<string, ExtensionManifest> manifests,
            Dictionary<string, List<Diagnostic>> rejected)
        {
            var extensionIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var operationIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var providerIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var sorted = new List<KeyValuePair<string, ExtensionManifest>>(manifests);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            foreach (var pair in sorted)
            {
                var packageId = pair.Key;
                var manifest = pair.Value;
                CheckDuplicateDeclaredId(
                    extensionIds, manifest.ExtensionId, packageId, "extensionId", rejected);
                foreach (var operation in manifest.Operations)
                {
                    if (operation is not null)
                    {
                        CheckDuplicateDeclaredId(
                            operationIds, operation.Id, packageId, "Operation", rejected);
                    }
                }
                foreach (var provider in manifest.TypeProviders)
                {
                    if (provider is not null)
                    {
                        CheckDuplicateDeclaredId(
                            providerIds, provider.Id, packageId, "Provider ID", rejected);
                    }
                }
            }
        }

        private static void CheckDuplicateDeclaredId(
            Dictionary<string, string> owners,
            string id,
            string packageId,
            string kind,
            Dictionary<string, List<Diagnostic>> rejected)
        {
            if (owners.TryGetValue(id, out var first))
            {
                // A Provider ID duplicated across packages is OW-OP-013;
                // extensionId/Operation ID duplicates are the §X.3.2
                // metadata verdict OW-OP-018 (SPEC §J.1, T-W2.12-03).
                var code = string.Equals(kind, "Provider ID", StringComparison.Ordinal)
                    ? ErrorCodes.OW_OP_013
                    : ErrorCodes.OW_OP_018;
                // Declared IDs are reader-validated; the publishable form
                // is applied uniformly so an unvalidated ID can never be
                // interpolated verbatim (SPEC §C3 error paragraph).
                var message = $"{kind} '{PublishedId.OperationId(id)}' is declared by more than one package.";
                AddRejection(rejected, packageId, ErrorCodes.Create(code, message,
                    hint: $"Also declared by '{first}' (SPEC §X.3.2)."));
                AddRejection(rejected, first, ErrorCodes.Create(code, message,
                    hint: $"Also declared by '{packageId}' (SPEC §X.3.2)."));
            }
            else
            {
                owners.Add(id, packageId);
            }
        }

        private static void AddRejection(
            Dictionary<string, List<Diagnostic>> rejected, string packageId, Diagnostic diagnostic)
        {
            if (!rejected.TryGetValue(packageId, out var list))
            {
                list = new List<Diagnostic>();
                rejected[packageId] = list;
            }
            list.Add(diagnostic);
        }

        // The Type IDs one instantiated packaged provider actually offers —
        // the observed facts the pre-publication gate compares against the
        // manifest (SPEC §X.3.2). An offer that cannot be enumerated is an
        // individual candidate failure, not a package verdict: the same
        // provider instance fails inside the §C3 step-4 type build as a
        // ProviderFailure, rejecting only its own candidates (SPEC §X.6).
        private static void CollectObservedTypes(
            ExtensionCandidate candidate,
            ITypeProvider provider,
            Dictionary<string, List<ObservedExtensionType>> observedTypes)
        {
            var packageId = candidate.PackageId!;
            var offered = new List<ObservedExtensionType>();
            try
            {
                var enumerable = provider.GetTypes();
                if (enumerable is null)
                {
                    return;
                }
                foreach (var definition in enumerable)
                {
                    offered.Add(new ObservedExtensionType(
                        definition?.Id ?? string.Empty, candidate.AssemblyName));
                }
            }
            catch (Exception)
            {
                return;
            }
            if (!observedTypes.TryGetValue(packageId, out var list))
            {
                list = new List<ObservedExtensionType>();
                observedTypes[packageId] = list;
            }
            list.AddRange(offered);
        }

        // Manifest-declared Provider ID binding for packaged providers:
        // per (package, assembly), the discovered classes pair with the
        // declared typeProviders[] refs exactly as ExtensionManifestValidator
        // pairs them — classes by full name Ordinal, refs by Provider ID
        // Ordinal, index-wise — so a class only counts as bound when every
        // provider class in its assembly binds one-to-one. A pairing
        // mismatch rejects the whole package: none of its candidates —
        // providers or Operations — is admitted or constructed (SPEC §X.3.2,
        // §X.6 "許可されたパッケージ・アセンブリからのみ呼び出す").
        private static void BindPackagedProviders(
            List<(Type Type, EditorAssemblyProvenance Provenance)> packaged,
            Dictionary<string, ExtensionManifest> manifests,
            Dictionary<string, List<ExtensionCandidate>> operationsByPackage,
            List<BoundProvider> bound,
            List<ExtensionCandidateFailure> failures)
        {
            var byPackage =
                new Dictionary<string, Dictionary<string, List<(Type Type, EditorAssemblyProvenance Provenance)>>>(
                    StringComparer.Ordinal);
            foreach (var item in packaged)
            {
                var packageId = item.Provenance.PackageId!;
                if (!byPackage.TryGetValue(packageId, out var byAssembly))
                {
                    byAssembly = new Dictionary<string, List<(Type, EditorAssemblyProvenance)>>(
                        StringComparer.Ordinal);
                    byPackage[packageId] = byAssembly;
                }
                var assemblyName = item.Provenance.AssemblyName;
                if (!byAssembly.TryGetValue(assemblyName, out var list))
                {
                    list = new List<(Type, EditorAssemblyProvenance)>();
                    byAssembly[assemblyName] = list;
                }
                list.Add(item);
            }

            var packageIds = new List<string>(byPackage.Keys);
            packageIds.Sort(StringComparer.Ordinal);
            foreach (var packageId in packageIds)
            {
                if (!manifests.TryGetValue(packageId, out var manifest))
                {
                    continue;
                }
                var diagnostics = new List<Diagnostic>();
                var packageBound = new List<BoundProvider>();
                var byAssembly = byPackage[packageId];
                var assemblyNames = new List<string>(byAssembly.Keys);
                assemblyNames.Sort(StringComparer.Ordinal);
                foreach (var assemblyName in assemblyNames)
                {
                    var classes = byAssembly[assemblyName];
                    classes.Sort((a, b) => string.Compare(
                        a.Type.FullName ?? a.Type.Name, b.Type.FullName ?? b.Type.Name,
                        StringComparison.Ordinal));
                    var declared = new List<ExtensionTypeProviderRef>();
                    foreach (var entry in manifest.TypeProviders)
                    {
                        if (entry is not null &&
                            string.Equals(entry.Assembly, assemblyName, StringComparison.Ordinal))
                        {
                            declared.Add(entry);
                        }
                    }
                    if (declared.Count != classes.Count)
                    {
                        diagnostics.Add(Internal(
                            $"Assembly '{assemblyName}' of package '{packageId}' contains {classes.Count} [OpWeaveTypeProvider] class(es) but the manifest binds {declared.Count} Provider ID(s) to it."));
                        continue;
                    }
                    declared.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                    for (var i = 0; i < classes.Count; i++)
                    {
                        packageBound.Add(new BoundProvider(
                            classes[i].Type, classes[i].Provenance, declared[i].Id));
                    }
                }
                if (diagnostics.Count != 0)
                {
                    manifests.Remove(packageId);
                    foreach (var pair in byAssembly)
                    {
                        foreach (var item in pair.Value)
                        {
                            failures.Add(Failure(
                                ExtensionCandidateKind.TypeProvider, item.Type,
                                item.Provenance.AssemblyName, diagnostics));
                        }
                    }
                    if (operationsByPackage.TryGetValue(packageId, out var packageOperations))
                    {
                        foreach (var candidate in packageOperations)
                        {
                            failures.Add(new ExtensionCandidateFailure(
                                ExtensionCandidateKind.Operation,
                                candidate.Type.FullName ?? candidate.Type.Name,
                                candidate.AssemblyName,
                                diagnostics));
                        }
                        operationsByPackage.Remove(packageId);
                    }
                    continue;
                }
                bound.AddRange(packageBound);
            }
        }

        // The production package-facts seam: the installed package's
        // resolved root, its opweave.extension.json text when readable and
        // the Editor assemblies rooted inside it — UPM-resolved, never
        // self-declared (SPEC §G0). Null when the package is absent.
        private static ExtensionPackageFacts? ProductionPackageFacts(string packageId)
        {
            try
            {
                foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (!string.Equals(package.name, packageId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var root = ExtensionDiscovery.Canonicalize(package.resolvedPath);
                    if (root.Length == 0)
                    {
                        return null;
                    }
                    string? text = null;
                    var manifestPath = root + "/" + ExtensionManifestReader.FileName;
                    try
                    {
                        if (File.Exists(manifestPath))
                        {
                            text = File.ReadAllText(manifestPath);
                        }
                    }
                    catch (Exception ex) when (
                        ex is IOException || ex is UnauthorizedAccessException ||
                        ex is NotSupportedException || ex is System.Security.SecurityException)
                    {
                        text = null;
                    }
                    var assemblies = ExtensionDiscovery.EditorAssembliesInPackage(
                        root, ExtensionDiscovery.ProjectRoot);
                    return new ExtensionPackageFacts(text, root, assemblies);
                }
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is NotSupportedException || ex is ArgumentException ||
                ex is System.Security.SecurityException)
            {
            }
            return null;
        }

        private static string? SourceFileOf(Type type)
        {
            var attribute = Attribute.GetCustomAttribute(
                type, typeof(OpWeaveTypeProviderAttribute), inherit: false) as OpWeaveTypeProviderAttribute;
            if (attribute is not null)
            {
                return EditorAssemblyResolver.ToProjectRelativePath(attribute.SourceFile);
            }
            var operation = Attribute.GetCustomAttribute(
                type, typeof(Core.Operations.OpWeaveOperationAttribute), inherit: false)
                as Core.Operations.OpWeaveOperationAttribute;
            return operation is null
                ? null
                : EditorAssemblyResolver.ToProjectRelativePath(operation.SourceFile);
        }

        private static ExtensionCandidateFailure Failure(
            ExtensionCandidateKind kind, Type type, string? assemblyName,
            IReadOnlyList<Diagnostic> diagnostics) =>
            new ExtensionCandidateFailure(
                kind, type.FullName ?? type.Name, assemblyName, diagnostics);

        // A declared-vs-observed extension-metadata verdict:
        // OW-OP-018 (SPEC §J.1, T-W2.12-03).
        private static Diagnostic Internal(string message) =>
            ErrorCodes.Create(ErrorCodes.OW_OP_018, message);

        private static Diagnostic Internal(string message, string? hint) =>
            ErrorCodes.Create(ErrorCodes.OW_OP_018, message, hint: hint);

        // A rejection with no assigned published code — a package
        // contributing candidates without a readable manifest declares
        // nothing, so OW-OP-018 does not apply (SPEC §J.1): the safe
        // empty-code internal diagnostic (SG-02 convention).
        private static Diagnostic Detail(string message) =>
            new Diagnostic(string.Empty, Severity.Error, message);
    }
}
