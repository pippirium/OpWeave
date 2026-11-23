#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The result of one extension discovery pass over the installed
    /// packages (SPEC §X.1, §X.3.2, §B0.2 item 4).
    /// </summary>
    /// <remarks>
    /// <see cref="Extensions"/> carries a verdict for every package that
    /// either ships <c>opweave.extension.json</c> or contributes extension
    /// candidates. <see cref="ProjectCandidates"/> lists the
    /// <c>[OpWeaveTypeProvider]</c> classes living in project (unpackaged)
    /// Editor assemblies — custom types that are never extension candidates
    /// and skip the range check by SPEC §B0.2 item 4.
    /// <see cref="UnattributedCandidates"/> lists candidates whose defining
    /// assembly could not be attributed to a package at all.
    /// <see cref="Packages"/> is the report's package info slot (SPEC §C3):
    /// the UPM-resolved provenance of every installed package, so the same
    /// snapshot that carries the outcomes also answers which package —
    /// with which resolved commit — contributed each registered item.
    /// <see cref="OperationProvenance"/> and
    /// <see cref="TypeProviderProvenance"/> are the per-item views of that
    /// same provenance, keyed by the items the pass accepted for
    /// registration.
    /// </remarks>
    public sealed class ExtensionDiscoveryReport
    {
        public ExtensionDiscoveryReport(
            IEnumerable<ExtensionValidationOutcome> extensions,
            IEnumerable<DiscoveredTypeProvider> projectCandidates,
            IEnumerable<UnattributedExtensionCandidate> unattributedCandidates,
            IEnumerable<KeyValuePair<string, UpmPackageProvenance>> packages)
        {
            Extensions = ReadOnlyCollections.CopyList(extensions);
            ProjectCandidates = ReadOnlyCollections.CopyList(projectCandidates);
            UnattributedCandidates = ReadOnlyCollections.CopyList(unattributedCandidates);
            Packages = ReadOnlyCollections.CopyMap(packages);

            // The per-item provenance lookups (SPEC §C3) are derived here
            // from the same verdicts and package map this pass produced —
            // an accepted item and its package provenance come out of one
            // discovery pass, so they cannot diverge. Only accepted
            // extensions contribute; a rejected extension registers
            // nothing and therefore owns no lookup entry. T-W2.2-07
            // embeds these two keyed maps into RegistrySnapshot; until
            // then this report is the discovery/registration result the
            // Registry build consumes.
            var operations = new Dictionary<ExtensionOperationRef, UpmPackageProvenance>();
            var providers = new Dictionary<string, UpmPackageProvenance>(StringComparer.Ordinal);
            foreach (var outcome in Extensions)
            {
                if (outcome is null || !outcome.Accepted)
                {
                    continue;
                }
                var provenance = outcome.Package;
                if (provenance is null &&
                    !Packages.TryGetValue(outcome.PackageId, out provenance))
                {
                    continue;
                }
                if (outcome.Manifest is not null)
                {
                    foreach (var operation in outcome.Manifest.Operations)
                    {
                        if (operation is not null)
                        {
                            operations[operation] = provenance;
                        }
                    }
                }
                foreach (var binding in outcome.TypeProviders)
                {
                    if (binding is not null && binding.ProviderId is not null)
                    {
                        providers[binding.ProviderId] = provenance;
                    }
                }
            }
            OperationProvenance =
                new ReadOnlyDictionary<ExtensionOperationRef, UpmPackageProvenance>(operations);
            TypeProviderProvenance =
                new ReadOnlyDictionary<string, UpmPackageProvenance>(providers);
        }

        /// <summary>Verdicts for every extension-relevant installed package.</summary>
        public IReadOnlyList<ExtensionValidationOutcome> Extensions { get; }

        /// <summary>
        /// The UPM-resolved provenance of every installed package this pass
        /// resolved, keyed by package ID (Ordinal) — the package info slot
        /// the RegistrySnapshot representation exposes (SPEC §C3) so that
        /// describe_operation and the Registry tab can show the Package ID,
        /// source repository and resolved commit of the package that
        /// contributed each registered item. The standard
        /// <c>com.opweave.workflow</c> package is included. Values come from
        /// the manifest / lock resolution only, never self-declared metadata
        /// (SPEC §X.3.2, §G0).
        /// </summary>
        public IReadOnlyDictionary<string, UpmPackageProvenance> Packages { get; }

        /// <summary>
        /// The per-Operation provenance lookup the RegistrySnapshot embeds
        /// (SPEC §C3): every Operation <c>(id, version)</c> identity an
        /// accepted extension contributes, mapped to the UPM-resolved
        /// provenance of the package that supplies it — Package ID, package
        /// version, source kind and the resolved full commit SHA for Git
        /// packages, never self-declared fields. Because an accepted
        /// extension's declared <c>operations[]</c> equals its discovered
        /// Descriptor set (SPEC §X.3.2), these keys are the identities that
        /// actually register. Rejected extensions contribute nothing.
        /// Standard Operations are not extension candidates and are not
        /// keyed here; the Registry build resolves their package through
        /// <see cref="Packages"/> under
        /// <see cref="EditorAssemblyResolver.StandardPackageId"/>.
        /// T-W2.2-07 embeds this map into <c>RegistrySnapshot</c> so
        /// describe_operation and the Registry tab read each registered
        /// Operation's package info from the same snapshot.
        /// </summary>
        public IReadOnlyDictionary<ExtensionOperationRef, UpmPackageProvenance> OperationProvenance { get; }

        /// <summary>
        /// The per-TypeProvider provenance lookup the RegistrySnapshot
        /// embeds (SPEC §C3): every Provider ID an accepted extension
        /// binds, mapped to the UPM-resolved provenance of the package
        /// that supplies it — Package ID, package version, source kind and
        /// the resolved full commit SHA for Git packages, never
        /// self-declared fields. Rejected extensions contribute nothing.
        /// Standard and project providers are not extension candidates
        /// and are not keyed here; the Registry build resolves their
        /// package (if any) through <see cref="Packages"/>.
        /// T-W2.2-07 embeds this map into <c>RegistrySnapshot</c> so the
        /// providers a registered Operation depends on expose their
        /// package info from the same snapshot.
        /// </summary>
        public IReadOnlyDictionary<string, UpmPackageProvenance> TypeProviderProvenance { get; }

        /// <summary>Candidates in unpackaged project Editor assemblies (never extensions).</summary>
        public IReadOnlyList<DiscoveredTypeProvider> ProjectCandidates { get; }

        /// <summary>Candidates whose defining assembly could not be attributed to a package.</summary>
        public IReadOnlyList<UnattributedExtensionCandidate> UnattributedCandidates { get; }
    }
}
