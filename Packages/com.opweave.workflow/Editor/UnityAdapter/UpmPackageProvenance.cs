#nullable enable

using System;
using UnityEditor.PackageManager;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// UPM-resolved package provenance for audit (SPEC §X.3.2, §G0): the
    /// package ID, resolved package version, install source, source
    /// repository and the resolved revision — the values the
    /// RegistrySnapshot's package info slot (SPEC §C3) carries so that
    /// describe_operation and the Registry tab can show them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value comes from the Package Manager's manifest / lock
    /// resolution (<see cref="PackageInfo"/>), never from self-declared
    /// metadata such as a <c>package.json</c> <c>repository</c> block, a
    /// README, or <c>sourceRepository</c>/<c>resolvedRevision</c> keys in
    /// <c>opweave.extension.json</c> — those keys are ignored as audit data
    /// by SPEC §X.3.2.
    /// </para>
    /// <para>
    /// For Git packages the resolved revision is the full commit SHA that
    /// the lock pinned (<c>PackageInfo.git.hash</c>), kept even when the
    /// install requested a tag or branch — the requested text stays
    /// recorded separately in <see cref="RequestedRevision"/> so the audit
    /// record shows both (SPEC §X.3.2, §G8.1). Non-Git sources carry no
    /// revision evidence: their fields are null.
    /// </para>
    /// </remarks>
    public sealed class UpmPackageProvenance
    {
        public UpmPackageProvenance(
            string packageId,
            string? version,
            PackageSource source,
            string? resolvedRevision,
            string? requestedRevision,
            string? sourceRepository)
        {
            PackageId = packageId ?? throw new ArgumentNullException(nameof(packageId));
            Version = version;
            Source = source;
            ResolvedRevision = resolvedRevision;
            RequestedRevision = requestedRevision;
            SourceRepository = sourceRepository;
        }

        /// <summary>
        /// The UPM package name (e.g. <c>com.example.opweave.animation</c>) —
        /// the identity the manifest/lock resolved, not a declared label.
        /// </summary>
        public string PackageId { get; }

        /// <summary>
        /// The package version UPM resolved for this install
        /// (<see cref="PackageInfo.version"/>) — part of the resolved
        /// package identity, or null when the resolved identity does not
        /// expose one.
        /// </summary>
        public string? Version { get; }

        /// <summary>
        /// How UPM installed the package (registry, Git, local path,
        /// embedded, tarball, built-in).
        /// </summary>
        public PackageSource Source { get; }

        /// <summary>
        /// The resolved full commit SHA for Git packages
        /// (<see cref="GitInfo.hash"/> — the lock's pinned commit), or null
        /// for non-Git sources.
        /// </summary>
        public string? ResolvedRevision { get; }

        /// <summary>
        /// The revision text the install requested verbatim
        /// (<see cref="GitInfo.revision"/>: a tag, branch, SHA or
        /// <c>"HEAD"</c>), or null for non-Git sources.
        /// </summary>
        public string? RequestedRevision { get; }

        /// <summary>
        /// The repository URL UPM resolved the package from — the manifest /
        /// lock dependency text up to the <c>#revision</c> fragment
        /// (including any <c>?path=</c> subfolder selector), or null when the
        /// resolved package identity does not carry one. Never the
        /// self-declared <c>repository</c> metadata of the package.
        /// </summary>
        public string? SourceRepository { get; }

        /// <summary>
        /// Builds the provenance record for one UPM-resolved package. Any
        /// field UPM does not expose stays null — unresolved evidence is
        /// never guessed.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="info"/> is null.</exception>
        internal static UpmPackageProvenance FromPackageInfo(PackageInfo info)
        {
            if (info is null)
            {
                throw new ArgumentNullException(nameof(info));
            }

            var name = info.name ?? "";
            var version = EmptyToNull(info.version);
            if (info.source != PackageSource.Git)
            {
                return new UpmPackageProvenance(name, version, info.source, null, null, null);
            }

            var git = info.git;
            return new UpmPackageProvenance(
                name,
                version,
                info.source,
                EmptyToNull(git?.hash),
                EmptyToNull(git?.revision),
                RepositoryFromPackageId(info.packageId, name));
        }

        // The resolved package identity is "name@<dependency text>"; the
        // dependency text is the verbatim manifest/lock request, so its part
        // before the '#revision' fragment is the resolved repository URL
        // (with any ?path= subfolder selector kept verbatim).
        private static string? RepositoryFromPackageId(string? packageId, string name)
        {
            if (string.IsNullOrEmpty(packageId) || name.Length == 0)
            {
                return null;
            }
            var prefix = name + "@";
            if (!packageId!.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }
            var dependency = packageId.Substring(prefix.Length);
            var fragment = dependency.IndexOf('#');
            var repository = fragment >= 0 ? dependency.Substring(0, fragment) : dependency;
            return repository.Length == 0 ? null : repository;
        }

        private static string? EmptyToNull(string? value) =>
            string.IsNullOrEmpty(value) ? null : value;
    }
}
