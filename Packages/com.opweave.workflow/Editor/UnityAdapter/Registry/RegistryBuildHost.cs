#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Registry;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The holder and driver of the domain's SPEC §C3 Registry rebuild —
    /// the hook registered at <see cref="RunnerStartupOrder.RegistryRebuild"/>
    /// on the §1.4 startup hook list, run at the first
    /// <c>EditorApplication.delayCall</c> after every domain reload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <see cref="Rebuild"/> call runs the whole rebuild: stages 1–5 via
    /// <see cref="RegistryTypeStage"/>, stage 6 via
    /// <see cref="RegistryOperationStage"/>, stage 7 via the Core
    /// <see cref="RegistrySnapshotBuilder"/> and stage 8 by publishing the
    /// finished snapshot through the injected
    /// <see cref="RegistryPublication"/> — one atomic swap, no intermediate
    /// state (SPEC §C3 step 8). A stage-1 stale result only marks the
    /// publication stale; the last published snapshot stays in place (SPEC
    /// §C3 rule 9).
    /// </para>
    /// <para>
    /// The production rebuild loads the settings file for the §H4.1
    /// settings-error rule: a failed load builds with
    /// <see cref="StandardOnlyExtensionExclusion"/> — standard Operations
    /// only — while a readable file drives the
    /// <see cref="SettingsExtensionExclusion"/> predicate over the loaded
    /// DiscoveryMode, whitelist and <c>disabledOperations</c> (SPEC §C0.2,
    /// §H4.1). The holder is rebuilt per domain like the
    /// startup hooks that fill it.
    /// </para>
    /// </remarks>
    internal static class RegistryBuildHost
    {
        /// <summary>
        /// The last completed stage 1–8 build of this domain, or null
        /// before the first delayCall. <see cref="Rebuild"/> swaps it in
        /// before the diff recorder's <c>RegistryChanged</c> dispatch, so
        /// an event-driven refresh already reads the new build — its
        /// <see cref="RegistryBuildResult.Diff"/> attaches when the
        /// record pass returns.
        /// </summary>
        internal static RegistryBuildResult? Latest { get; private set; }

        /// <summary>
        /// Runs stages 1–8 over the injected seams, publishes the snapshot
        /// through <paramref name="publication"/> and retains the result.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="typeSeams"/>, <paramref name="operationSeams"/> or
        /// <paramref name="publication"/> is null.
        /// </exception>
        internal static RegistryBuildResult Rebuild(
            RegistryTypeStage.Seams typeSeams,
            RegistryOperationStage.Seams operationSeams,
            RegistryPublication publication,
            IReadOnlyList<Diagnostic>? settingsDiagnostics = null,
            RegistryDiffRecorder? diffRecorder = null)
        {
            if (typeSeams is null)
            {
                throw new ArgumentNullException(nameof(typeSeams));
            }
            if (operationSeams is null)
            {
                throw new ArgumentNullException(nameof(operationSeams));
            }
            if (publication is null)
            {
                throw new ArgumentNullException(nameof(publication));
            }

            // T-W2.2-09: every stage below resolves candidate provenance
            // through EditorAssemblyResolver; the scope shares each
            // CompilationPipeline.GetAssemblies result across all of those
            // resolutions for the duration of this build. Outcomes are
            // identical — the cache dies with the scope and nothing is
            // held across builds or domain reloads.
            using var assemblyQueries = EditorAssemblyResolver.OpenCompilationAssemblyScope();

            // Stages 1–5: compile consistency, metadata collection,
            // pre-instantiation exclusion, provider instantiation, the
            // immutable TypeRegistry snapshot.
            var typeStage = RegistryTypeStage.Run(typeSeams);
            if (typeStage.Stale)
            {
                // SPEC §C3 rule 9: the old assemblies keep running, so the
                // previously published snapshot stays — marked stale. No
                // stage-6 work and no new snapshot.
                publication.MarkStale();
                return Latest = new RegistryBuildResult(
                    typeStage,
                    snapshot: null,
                    Array.Empty<ExtensionCandidateFailure>(),
                    Array.Empty<ExtensionCandidateFailure>(),
                    settingsDiagnostics ?? (IReadOnlyList<Diagnostic>)Array.Empty<Diagnostic>(),
                    typeStage.Types,
                    typeStage.Providers,
                    typeStage.TypeFailures);
            }

            // Stage 6: instantiate the admitted Operations, compute the
            // Waitable-Poll flag and the implementationHash per candidate.
            var operationStage = RegistryOperationStage.Run(typeStage.Operations, operationSeams);

            // The observed-fact extension gate (SPEC §X.3.2): after
            // construction and before publication, every admitted extension
            // package re-passes the full manifest validation against what
            // the build actually observed — the discovered Descriptor set,
            // the provider classes and the offered Type IDs. A package
            // failing any of them contributes no Operation and no
            // TypeProvider to the published Registry.
            var extensionFailures = new List<ExtensionCandidateFailure>();
            var types = typeStage.Types!;
            var typeFailures = typeStage.TypeFailures;
            var providers = typeStage.Providers;
            var operationCandidates = operationStage.Candidates;
            if (typeStage.ExtensionPackages.Count != 0)
            {
                var rejected = EvaluateExtensionPackages(
                    typeStage.ExtensionPackages, operationStage.Candidates,
                    operationStage.Observed, extensionFailures);
                if (rejected.Count != 0)
                {
                    var rejectedIds = new HashSet<string>(StringComparer.Ordinal);
                    var droppedInstances = new HashSet<TypeProviderCandidate>();
                    foreach (var package in rejected)
                    {
                        rejectedIds.Add(package.PackageId);
                        foreach (var instance in package.ProviderInstances)
                        {
                            droppedInstances.Add(instance);
                        }
                    }
                    var filteredProviders = new List<TypeProviderCandidate>(providers.Count);
                    foreach (var provider in providers)
                    {
                        if (!droppedInstances.Contains(provider))
                        {
                            filteredProviders.Add(provider);
                        }
                    }
                    var rebuilt = TypeRegistryBuilder.Build(filteredProviders, typeSeams.ClassRelations);
                    types = rebuilt.Registry;
                    // The rebuild cannot reproduce the rejected packages'
                    // own §C3 step-4 rejections — their providers are gone
                    // from the build — yet each rejected offer must still
                    // publish its Type ID and 出自アセンブリ (SPEC §C3
                    // error paragraph): a cross-package duplicate is a
                    // per-offer OW-OP-013 as well as the package verdict
                    // above. Re-attach every first-build failure whose
                    // provider is absent from the effective set.
                    var survivingProviderIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var provider in filteredProviders)
                    {
                        survivingProviderIds.Add(provider.ProviderId);
                    }
                    var mergedFailures = new List<TypeRegistryFailure>(
                        rebuilt.Failures.Count + typeStage.TypeFailures.Count);
                    mergedFailures.AddRange(rebuilt.Failures);
                    foreach (var failure in typeStage.TypeFailures)
                    {
                        if (!survivingProviderIds.Contains(failure.ProviderId))
                        {
                            mergedFailures.Add(failure);
                        }
                    }
                    typeFailures = ReadOnlyCollections.CopyList(mergedFailures);
                    providers = ReadOnlyCollections.CopyList(filteredProviders);
                    var filteredOperations = new List<OperationCandidate>(operationStage.Candidates.Count);
                    foreach (var candidate in operationStage.Candidates)
                    {
                        if (candidate.PackageId is null || !rejectedIds.Contains(candidate.PackageId))
                        {
                            filteredOperations.Add(candidate);
                        }
                    }
                    operationCandidates = ReadOnlyCollections.CopyList(filteredOperations);
                }
            }

            // Stage 7: fingerprints + the consistent RegistrySnapshot with
            // its registryHash and fresh generation ID (T-W1.5-04). The
            // pre-builder candidate rejections of stages 1–5, stage 6 and
            // the observed-fact gate travel as RegistryCandidateFailure
            // records so the published snapshot carries every rejected
            // candidate's safe diagnostic data (SPEC §C3 error paragraph,
            // T-W2.12-03); deliberate policy exclusions are never listed —
            // they are not registration errors (SPEC §C3 rule 5).
            var candidateFailures = new List<RegistryCandidateFailure>(
                typeStage.Failures.Count + operationStage.Failures.Count +
                extensionFailures.Count);
            CollectCandidateFailures(typeStage.Failures, candidateFailures);
            CollectCandidateFailures(operationStage.Failures, candidateFailures);
            CollectCandidateFailures(extensionFailures, candidateFailures);

            // Stage-3 rejections decided from the attribute-declared
            // (id, version) — reserved namespaces and duplicates refused
            // before any constructor ran (SPEC §C3 rules 3–4) — are
            // per-Operation failure records on the snapshot, named by
            // their publishable declared identity.
            var preInstantiationFailures = new List<RegistryChange>();
            for (var i = 0; i < typeStage.Failures.Count; i++)
            {
                var failure = typeStage.Failures[i];
                if (failure.Kind == ExtensionCandidateKind.Operation &&
                    failure.DeclaredOperationId is not null &&
                    failure.DeclaredOperationVersion is not null)
                {
                    preInstantiationFailures.Add(new RegistryChange(
                        RegistryChangeKinds.Failed,
                        PublishedId.OperationId(failure.DeclaredOperationId),
                        failure.DeclaredOperationVersion.Value,
                        failure.SourceFile,
                        failure.Diagnostics));
                }
            }
            // SPEC §C3 rule 5: the deliberate policy exclusions of stages
            // 3d/3-operations are Registry state, never failure records —
            // they publish on their own snapshot list with the identity the
            // pipeline knew without construction (the attribute-declared
            // (id, version) or the bound Provider ID).
            var exclusions = new List<RegistryCandidateExclusion>(typeStage.Exclusions.Count);
            for (var i = 0; i < typeStage.Exclusions.Count; i++)
            {
                var exclusion = typeStage.Exclusions[i];
                var candidate = exclusion.Candidate;
                var isOperation = candidate.Kind == ExtensionCandidateKind.Operation;
                string candidateId;
                int? operationVersion = null;
                if (isOperation)
                {
                    // The declared Operation ID is attribute metadata —
                    // unvalidated text published only in its safe form.
                    candidateId = candidate.DeclaredId is not null
                        ? PublishedId.OperationId(candidate.DeclaredId)
                        : candidate.Type.FullName ?? candidate.Type.Name;
                    operationVersion = candidate.DeclaredVersion;
                }
                else
                {
                    candidateId = candidate.ProviderId ??
                        candidate.Type.FullName ?? candidate.Type.Name;
                }
                exclusions.Add(new RegistryCandidateExclusion(
                    isOperation
                        ? RegistryCandidateKind.Operation
                        : RegistryCandidateKind.TypeProvider,
                    candidateId,
                    operationVersion,
                    candidate.PackageId,
                    candidate.AssemblyName,
                    candidate.SourceFile,
                    exclusion.Reason));
            }
            var snapshot = RegistrySnapshotBuilder.Build(
                operationCandidates, types, typeFailures, candidateFailures,
                preInstantiationFailures, exclusions);

            // Stage 8: the atomic switch — the complete snapshot, generation
            // ID included, becomes visible to search, GUI and Runner at once.
            publication.Publish(snapshot);

            // Retain the new build BEFORE the §C3 rule-7 pass: the
            // recorder's RegistryChanged dispatch refreshes the Registry
            // tab and get_registry_status through Latest, so an
            // event-driven refresh must already see this build's settings
            // diagnostics, providers and admitted extension packages —
            // never the previous build's (SPEC §H5).
            var result = new RegistryBuildResult(
                typeStage,
                snapshot,
                operationStage.Failures,
                extensionFailures,
                settingsDiagnostics ?? (IReadOnlyList<Diagnostic>)Array.Empty<Diagnostic>(),
                types,
                providers,
                typeFailures);
            Latest = result;

            // SPEC §C3 rule 7 (T-W2.2-04): persist the published snapshot's
            // summary, diff it against the previous registry.json, append
            // the change batch and report it — only after the publication,
            // so a persisted summary never describes an unpublished build.
            // The pass's outcome attaches to the retained result in place,
            // so a build object a dispatch-time reader captured completes
            // as soon as the record pass returns.
            result.AttachDiff(diffRecorder?.Record(snapshot));
            return result;
        }

        // The observed-fact half of the SPEC §X.3.2 extension gate, run
        // after stage 6 and before publication: every admitted package's
        // declared operations[] must equal the Descriptor identities stage 6
        // produced — a declared entry a failed candidate never read is "not
        // observed", not a mismatch — its declared TypeProviders must exist
        // in the named assemblies and its offered Type IDs must be unique
        // across packages. Per-candidate defects — a candidate that could
        // not be constructed or described, a Provider whose offered types
        // are malformed, reserved or duplicated inside the package, a
        // Provider whose GetTypes() cannot be enumerated — reject only that
        // candidate through the stage-6 or §C3 step-4 channels (SPEC §C3
        // steps 4/6, §X.6, §M.5), never the whole package. The same
        // ExtensionManifestValidator the discovery pass runs decides; a
        // rejected package's candidates contribute nothing to the published
        // Registry and their diagnostics reach the build result.
        private static IReadOnlyList<ExtensionPackageState> EvaluateExtensionPackages(
            IReadOnlyList<ExtensionPackageState> packages,
            IReadOnlyList<OperationCandidate> operationCandidates,
            IReadOnlyList<ObservedOperation> observedDescriptors,
            List<ExtensionCandidateFailure> failures)
        {
            // The manifest equality check compares the declared operations[]
            // against every Descriptor stage 6 actually read — including
            // candidates the fingerprint step then refused, whose individual
            // OW-OP-017 rejection must not look like a declared-vs-observed
            // mismatch (SPEC §X.3.2, §M.5).
            var observedOperations = new Dictionary<string, List<ObservedExtensionOperation>>(
                StringComparer.Ordinal);
            foreach (var observed in observedDescriptors)
            {
                var packageId = observed.PackageId;
                if (packageId is null ||
                    string.Equals(
                        packageId, EditorAssemblyResolver.StandardPackageId,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (!observedOperations.TryGetValue(packageId, out var list))
                {
                    list = new List<ObservedExtensionOperation>();
                    observedOperations[packageId] = list;
                }
                list.Add(new ObservedExtensionOperation(
                    observed.Descriptor.Id, observed.Descriptor.Version, observed.AssemblyName));
            }

            // The cross-package duplicate check stays on the candidates
            // that will actually publish: a refused candidate produces no
            // registered Operation to collide with.
            var producedOperations = new Dictionary<string, List<ObservedExtensionOperation>>(
                StringComparer.Ordinal);
            foreach (var candidate in operationCandidates)
            {
                var packageId = candidate.PackageId;
                if (packageId is null ||
                    string.Equals(
                        packageId, EditorAssemblyResolver.StandardPackageId,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (!producedOperations.TryGetValue(packageId, out var list))
                {
                    list = new List<ObservedExtensionOperation>();
                    producedOperations[packageId] = list;
                }
                list.Add(new ObservedExtensionOperation(
                    candidate.Descriptor.Id, candidate.Descriptor.Version, candidate.AssemblyName));
            }

            var rejected = new List<ExtensionPackageState>();
            var rejectedDiagnostics = new Dictionary<string, List<Diagnostic>>(StringComparer.Ordinal);
            var alive = new List<ExtensionPackageState>();
            void Reject(ExtensionPackageState package, Diagnostic diagnostic)
            {
                if (!rejectedDiagnostics.TryGetValue(package.PackageId, out var list))
                {
                    list = new List<Diagnostic>();
                    rejectedDiagnostics[package.PackageId] = list;
                    rejected.Add(package);
                }
                list.Add(diagnostic);
            }
            foreach (var package in packages)
            {
                observedOperations.TryGetValue(package.PackageId, out var observed);
                // A policy-excluded candidate's attribute-declared
                // (id, version) is known without construction: it may
                // satisfy only the declared operations[] entry naming that
                // same identity, never an unrelated one (SPEC §X.3.2,
                // §C3 rule 5 — an intentional exclusion is not a
                // declared-vs-observed mismatch).
                var excludedIdentities = new List<ObservedExtensionOperation>(
                    package.ExcludedOperations.Count);
                var unidentifiedExcluded = 0;
                foreach (var excludedCandidate in package.ExcludedOperations)
                {
                    if (excludedCandidate.DeclaredId is not null &&
                        excludedCandidate.DeclaredVersion is not null)
                    {
                        excludedIdentities.Add(new ObservedExtensionOperation(
                            excludedCandidate.DeclaredId,
                            excludedCandidate.DeclaredVersion.Value,
                            excludedCandidate.AssemblyName));
                    }
                    else
                    {
                        // Without a declared identity the excluded
                        // candidate is indistinguishable from a failed
                        // one for the manifest gate — anonymous
                        // unobserved budget, never a named entry.
                        unidentifiedExcluded++;
                    }
                }
                // An admitted candidate that failed before its Descriptor
                // was read (individual OW-OP-008) or refused on its
                // declared identity in stage 3 (OW-OP-004/007) is "not
                // observed" — it is not a declared-vs-observed mismatch and
                // it never rejects the package (SPEC §C3 step 6, §M.5,
                // §X.3.2).
                var unobserved = Math.Max(
                    0,
                    package.Operations.Count + package.RefusedOperationCount +
                        unidentifiedExcluded - (observed?.Count ?? 0));
                var outcome = ExtensionManifestValidator.Validate(new ExtensionValidationInput(
                    package.PackageId,
                    package.RootPath ?? package.PackageId,
                    package.ManifestText,
                    package.EditorAssemblies,
                    package.DiscoveredTypeProviders,
                    observed ??
                        (IReadOnlyList<ObservedExtensionOperation>)Array.Empty<ObservedExtensionOperation>(),
                    package.ObservedTypes,
                    Application.unityVersion,
                    unobserved,
                    excludedIdentities),
                    registrationGate: true);
                if (!outcome.Accepted)
                {
                    foreach (var diagnostic in outcome.Diagnostics)
                    {
                        Reject(package, diagnostic);
                    }
                }
                else
                {
                    alive.Add(package);
                }
            }

            // Cross-extension observed duplicates (SPEC §X.3.2): the same
            // offered Type ID or produced Operation ID — keyed by ID alone —
            // in two surviving packages rejects both.
            var typeOwners = new Dictionary<string, ExtensionPackageState>(StringComparer.Ordinal);
            var operationOwners = new Dictionary<string, ExtensionPackageState>(StringComparer.Ordinal);
            foreach (var package in alive)
            {
                foreach (var type in package.ObservedTypes)
                {
                    if (type is null)
                    {
                        continue;
                    }
                    if (typeOwners.TryGetValue(type.TypeId, out var first))
                    {
                        // A Type ID repeated inside one package is the §C3
                        // step-4 type build's §X.6 collision case — both
                        // duplicate registrations fail individually — not a
                        // cross-extension duplicate; the package verdict
                        // applies only when two DIFFERENT packages offer it.
                        if (!string.Equals(first.PackageId, package.PackageId, StringComparison.Ordinal))
                        {
                            // The observed Type ID is unvalidated provider
                            // text — publish its safe form, never the raw
                            // string (SPEC §C3 error paragraph).
                            var message =
                                $"Type ID '{PublishedId.TypeId(type.TypeId)}' is declared by more than one package.";
                            Reject(package, ErrorCodes.Create(ErrorCodes.OW_OP_013, message,
                                hint: $"Also declared by '{first.PackageId}' (SPEC §X.3.2)."));
                            Reject(first, ErrorCodes.Create(ErrorCodes.OW_OP_013, message,
                                hint: $"Also declared by '{package.PackageId}' (SPEC §X.3.2)."));
                        }
                    }
                    else
                    {
                        typeOwners.Add(type.TypeId, package);
                    }
                }
                if (producedOperations.TryGetValue(package.PackageId, out var produced))
                {
                    foreach (var operation in produced)
                    {
                        if (operation is null)
                        {
                            continue;
                        }
                        if (operationOwners.TryGetValue(operation.Id, out var first))
                        {
                            // An Operation ID repeated inside one package is
                            // the §C3 step-7 registration's collision case —
                            // every candidate sharing the (id, version) fails
                            // individually with OW-OP-004 — not a
                            // cross-extension duplicate; the package verdict
                            // applies only when two DIFFERENT packages offer
                            // it.
                            if (!string.Equals(first.PackageId, package.PackageId, StringComparison.Ordinal))
                            {
                                // The Descriptor-supplied Operation ID is
                                // unvalidated — publish its safe form,
                                // never the raw string (SPEC §C3 error
                                // paragraph).
                                var message =
                                    $"Operation '{PublishedId.OperationId(operation.Id)}' is declared by more than one package.";
                                Reject(package, Internal(message,
                                    $"Also declared by '{first.PackageId}' (SPEC §X.3.2)."));
                                Reject(first, Internal(message,
                                    $"Also declared by '{package.PackageId}' (SPEC §X.3.2)."));
                            }
                        }
                        else
                        {
                            operationOwners.Add(operation.Id, package);
                        }
                    }
                }
            }

            foreach (var package in rejected)
            {
                var diagnostics = rejectedDiagnostics[package.PackageId];
                foreach (var provider in package.Providers)
                {
                    failures.Add(new ExtensionCandidateFailure(
                        ExtensionCandidateKind.TypeProvider,
                        provider.Type.FullName ?? provider.Type.Name,
                        provider.AssemblyName,
                        diagnostics));
                }
                foreach (var operation in package.Operations)
                {
                    failures.Add(new ExtensionCandidateFailure(
                        ExtensionCandidateKind.Operation,
                        operation.Type.FullName ?? operation.Type.Name,
                        operation.AssemblyName,
                        diagnostics));
                }
            }
            return rejected;
        }

        // Maps the pipeline's per-candidate rejection records onto the
        // snapshot's RegistryCandidateFailure shape: the candidate class's
        // full name is the identity — no contract ID was learned for a
        // candidate rejected before or at construction, except a stage-3
        // declared-identity refusal, which names its publishable declared
        // Operation ID — and the emitter's published diagnostics pass
        // through unchanged.
        private static void CollectCandidateFailures(
            IReadOnlyList<ExtensionCandidateFailure> failures,
            List<RegistryCandidateFailure> sink)
        {
            for (var i = 0; i < failures.Count; i++)
            {
                var failure = failures[i];
                sink.Add(new RegistryCandidateFailure(
                    failure.Kind == ExtensionCandidateKind.Operation
                        ? RegistryCandidateKind.Operation
                        : RegistryCandidateKind.TypeProvider,
                    failure.DeclaredOperationId is not null
                        ? PublishedId.OperationId(failure.DeclaredOperationId)
                        : failure.TypeFullName,
                    ProviderId: null,
                    failure.AssemblyName,
                    failure.SourceFile,
                    failure.Diagnostics));
            }
        }

        // Cross-extension observed verdicts that are not a Type ID
        // collision are extension-metadata rejections: OW-OP-018 (SPEC
        // §J.1, T-W2.12-03).
        private static Diagnostic Internal(string message, string? hint) =>
            ErrorCodes.Create(ErrorCodes.OW_OP_018, message, hint: hint);

        /// <summary>
        /// The production rebuild registered in the startup hook list:
        /// stages 1–8 over the live Editor seams, the settings file deciding
        /// the §C3 rule-5 exclusion — the loaded
        /// <see cref="SettingsExtensionExclusion"/> on a readable file
        /// (DiscoveryMode, whitelist and <c>disabledOperations</c>; SPEC
        /// §C0.2, §H4.1), <see cref="StandardOnlyExtensionExclusion"/> while
        /// the load fails — and the domain's shared
        /// <see cref="RegistryPublication"/>.
        /// </summary>
        internal static void RebuildProduction() =>
            RebuildProduction(OpWeaveSettingsLoader.Load);

        /// <summary>
        /// <see cref="RebuildProduction"/> with the settings loader injected —
        /// the test seam that drives the §H4.1 settings-error build without
        /// touching the real settings file. <paramref name="resolveProvenance"/>
        /// optionally replaces the stage-3 provenance resolver for the same
        /// reason: a probe inside this package's own test assembly carries
        /// Standard provenance and can only stand in for a custom or external
        /// candidate when the seam re-attributes it.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="loadSettings"/> is null.</exception>
        internal static void RebuildProduction(
            Func<Result<OpWeaveSettings>> loadSettings,
            Func<Type, Result<EditorAssemblyProvenance>>? resolveProvenance = null,
            RegistryDiffRecorder? diffRecorder = null)
        {
            if (loadSettings is null)
            {
                throw new ArgumentNullException(nameof(loadSettings));
            }
            var settings = loadSettings();
            var exclusion = settings.Ok && settings.Value is not null
                ? (IExtensionExclusionPredicate)SettingsExtensionExclusion.For(settings.Value)
                : StandardOnlyExtensionExclusion.Instance;
            var typeSeams = RegistryTypeStage.Production(exclusion);
            if (resolveProvenance is not null)
            {
                typeSeams = typeSeams with { ResolveProvenance = resolveProvenance };
            }
            Rebuild(
                typeSeams,
                RegistryOperationStage.Production(),
                RegistryPublication.Shared,
                settings.Diagnostics,
                diffRecorder ?? RegistryDiffRecorder.Shared);
        }
    }
}
