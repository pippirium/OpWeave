#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The settings-driven <see cref="IExtensionExclusionPredicate"/> of SPEC
    /// §C0.2/§H4.1: the loaded <see cref="OpWeaveSettings"/> answers which
    /// extension candidates may be instantiated — the
    /// <c>disabledOperations</c> deny list first, then the
    /// <c>discoveryMode</c> axis (<c>auto</c> admits every candidate that
    /// reaches the predicate; <c>whitelist</c> admits only the exact
    /// configured identities).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>disabledOperations</c> is consulted before the mode and applies to
    /// every Operation candidate whatever its origin: it names Operation
    /// IDs, so it removes the ID at <em>every</em> declared version (SPEC
    /// §H4.1 "disabledOperations は Operation ID の配列で、DiscoveryMode
    /// に関わらず最優先で除外する（全 version）"). Matching is on the
    /// attribute-declared identity the stage binds without construction
    /// (SPEC §C2 item 2, §C3 rule 5) — the predicate never touches the
    /// candidate <see cref="Type"/> itself.
    /// </para>
    /// <para>
    /// Under <see cref="DiscoveryMode.Whitelist"/> a non-standard candidate
    /// registers only when an entry matches its exact identity: Operations
    /// on <c>packageId + assemblyName + operationId + version</c>,
    /// TypeProviders on <c>packageId + providerId</c>, where the packageId of
    /// a project (unpackaged) assembly is <see cref="ProjectPackageId"/>
    /// (<c>"@project"</c>). The loader has already rejected wildcards and
    /// empty keys (OW-CFG-002), so every comparison is a plain Ordinal
    /// equality — nothing matches by pattern. Standard candidates — the
    /// product's own assemblies — are not extensions and always pass the
    /// mode check; an empty whitelist therefore still registers the
    /// standard Operations, exactly like the §H4.1 settings-error path that
    /// registers "standard Operations only" (SPEC §C0.2, §H4.1).
    /// </para>
    /// <para>
    /// Whitelist exclusion is discovery permission only — it is never a
    /// substitute for the <c>ask</c> side-effect approval a descriptor's
    /// effects still require at Plan/Run time (SPEC §C0.2 contract).
    /// </para>
    /// </remarks>
    internal sealed class SettingsExtensionExclusion : IExtensionExclusionPredicate
    {
        /// <summary>The packageId a project (unpackaged) assembly carries in whitelist entries.</summary>
        internal const string ProjectPackageId = "@project";

        internal const string DisabledReason =
            "disabledOperations: the declared Operation ID is disabled by settings";
        internal const string OperationWhitelistReason =
            "discoveryMode whitelist: the Operation identity is not whitelisted";
        internal const string ProviderWhitelistReason =
            "discoveryMode whitelist: the TypeProvider is not whitelisted";

        private readonly DiscoveryMode _mode;
        private readonly HashSet<string> _disabledOperationIds;
        private readonly IReadOnlyList<OperationWhitelistEntry> _operations;
        private readonly IReadOnlyList<TypeProviderWhitelistEntry> _typeProviders;

        private SettingsExtensionExclusion(
            DiscoveryMode mode,
            IEnumerable<string> disabledOperationIds,
            IEnumerable<OperationWhitelistEntry> operations,
            IEnumerable<TypeProviderWhitelistEntry> typeProviders)
        {
            _mode = mode;
            _disabledOperationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in disabledOperationIds)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    _disabledOperationIds.Add(id);
                }
            }
            _operations = ReadOnlyCollections.CopyList(operations);
            _typeProviders = ReadOnlyCollections.CopyList(typeProviders);
        }

        /// <summary>
        /// The predicate of one loaded settings object: its
        /// <see cref="OpWeaveSettings.DiscoveryMode"/>,
        /// <see cref="OpWeaveSettings.WhitelistedOperations"/> /
        /// <see cref="OpWeaveSettings.WhitelistedTypeProviders"/> and
        /// <see cref="OpWeaveSettings.DisabledOperations"/> decide admission.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
        internal static SettingsExtensionExclusion For(OpWeaveSettings settings)
        {
            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            return new SettingsExtensionExclusion(
                settings.DiscoveryMode,
                settings.DisabledOperations,
                settings.WhitelistedOperations,
                settings.WhitelistedTypeProviders);
        }

        /// <inheritdoc/>
        public string? Exclude(ExtensionCandidate candidate)
        {
            // disabledOperations has priority over both modes and removes
            // every version of a matching declared ID (SPEC §H4.1).
            if (candidate.Kind == ExtensionCandidateKind.Operation &&
                candidate.DeclaredId is not null &&
                _disabledOperationIds.Contains(candidate.DeclaredId))
            {
                return DisabledReason;
            }

            // auto admits every candidate that reached the predicate; under
            // auto the whitelist list is unused (SPEC §H4.1). Standard
            // candidates — the product's own package — are never extensions
            // and pass under either mode.
            if (_mode == DiscoveryMode.Auto || candidate.Origin == OperationOrigin.Standard)
            {
                return null;
            }

            return candidate.Kind == ExtensionCandidateKind.Operation
                ? WhitelistedOperation(candidate) ? null : OperationWhitelistReason
                : WhitelistedTypeProvider(candidate) ? null : ProviderWhitelistReason;
        }

        // whitelist Operations: the exact (packageId, assemblyName,
        // operationId, version) tuple — a project assembly's packageId is
        // "@project". A candidate without a declared identity cannot match
        // a tuple and is excluded (fail closed; SPEC §C0.2).
        private bool WhitelistedOperation(ExtensionCandidate candidate)
        {
            if (candidate.DeclaredId is null || candidate.DeclaredVersion is null)
            {
                return false;
            }
            var packageId = candidate.PackageId ?? ProjectPackageId;
            for (var i = 0; i < _operations.Count; i++)
            {
                var entry = _operations[i];
                if (string.Equals(entry.PackageId, packageId, StringComparison.Ordinal) &&
                    string.Equals(entry.AssemblyName, candidate.AssemblyName, StringComparison.Ordinal) &&
                    string.Equals(entry.OperationId, candidate.DeclaredId, StringComparison.Ordinal) &&
                    entry.Version == candidate.DeclaredVersion.Value)
                {
                    return true;
                }
            }
            return false;
        }

        // whitelist TypeProviders: the exact (packageId, providerId) pair —
        // an unlisted TypeProvider is never invoked (SPEC §C0.2, §C3 rule 5).
        private bool WhitelistedTypeProvider(ExtensionCandidate candidate)
        {
            if (candidate.ProviderId is null)
            {
                return false;
            }
            var packageId = candidate.PackageId ?? ProjectPackageId;
            for (var i = 0; i < _typeProviders.Count; i++)
            {
                var entry = _typeProviders[i];
                if (string.Equals(entry.PackageId, packageId, StringComparison.Ordinal) &&
                    string.Equals(entry.ProviderId, candidate.ProviderId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
