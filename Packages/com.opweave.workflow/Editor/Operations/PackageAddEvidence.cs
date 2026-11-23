#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor.PackageManager;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// One parsed package identity — the same accepted forms as
    /// <see cref="PackageIdSource"/> (registry <c>name[@version]</c>, Git
    /// URL or SCP notation with <c>?path=</c> and <c>#revision</c>), read
    /// with a tolerant grammar so a resolved manifest/lock dependency
    /// value can be compared against it. Only the Git scheme and host
    /// segments are ASCII-lowercased; path case, <c>.git</c>, explicit
    /// port, notation, revision and sub-path semantics are preserved
    /// verbatim (SPEC §G8.1 normalization rule).
    /// </summary>
    internal sealed class PackageAddSource
    {
        private readonly string? _expectedCommit;

        private PackageAddSource(
            bool isGit,
            string? packageName,
            string? version,
            string? normalizedOrigin,
            string? subPath,
            string? revision,
            string? expectedCommit = null)
        {
            IsGit = isGit;
            PackageName = packageName;
            Version = version;
            NormalizedOrigin = normalizedOrigin;
            SubPath = subPath;
            Revision = revision;
            _expectedCommit = expectedCommit;
        }

        /// <summary>True for a Git source; false for a registry id.</summary>
        internal bool IsGit { get; }

        /// <summary>The registry package name (null for Git).</summary>
        internal string? PackageName { get; }

        /// <summary>The requested registry version (null for a bare name
        /// and for Git).</summary>
        internal string? Version { get; }

        /// <summary>The normalized Git repository URL — scheme and host
        /// ASCII-lowercased, everything else verbatim (null for
        /// registry).</summary>
        internal string? NormalizedOrigin { get; }

        /// <summary>The <c>?path=</c> package sub-folder (null when
        /// absent).</summary>
        internal string? SubPath { get; }

        /// <summary>The <c>#revision</c> fragment (null when absent).</summary>
        internal string? Revision { get; }

        /// <summary>The expected commit a snapshot persists: a full-SHA
        /// request names it directly; a tag or branch has none that can be
        /// determined without a side effect (SPEC §G8.1 item 3).</summary>
        internal string? ExpectedCommit =>
            _expectedCommit ?? (IsGit && IsFullCommitSha(Revision) ? Revision : null);

        /// <summary>True for a lowercase-hex full commit SHA (40-hex
        /// SHA-1 or 64-hex SHA-256).</summary>
        internal static bool IsFullCommitSha(string? text)
        {
            if (text is null || (text.Length != 40 && text.Length != 64))
            {
                return false;
            }
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Builds a Git identity (snapshot read-back).</summary>
        internal static PackageAddSource Git(
            string normalizedOrigin,
            string? subPath,
            string? revision,
            string? expectedCommit = null) =>
            new PackageAddSource(
                true, null, null, normalizedOrigin, subPath, revision, expectedCommit);

        /// <summary>Builds a registry identity (snapshot read-back).</summary>
        internal static PackageAddSource Registry(
            string packageName, string? version) =>
            new PackageAddSource(false, packageName, version, null, null, null);

        /// <summary>
        /// The tolerant comparison parse used on both sides of the §G8.1
        /// identity check: it accepts every form <see cref="PackageIdSource"/>
        /// accepts (same normalization) and additionally tolerates
        /// schemes, hosts, ports and userinfo the strict policy parse
        /// would reject, because a *resolved* dependency string is
        /// evidence to compare, not a candidate to admit. Anything
        /// unparseable simply never matches — fail closed, never
        /// mis-attributed.
        /// </summary>
        internal static bool TryParse(string? text, out PackageAddSource? source)
        {
            source = null;
            if (text is null ||
                text.Length == 0 ||
                !PackageSourceRule.IsAsciiPrintable(text))
            {
                return false;
            }

            var body = text;
            string? revision = null;
            var hash = body.IndexOf('#');
            if (hash >= 0)
            {
                revision = body.Substring(hash + 1);
                body = body.Substring(0, hash);
                if (revision.Length == 0 || revision.IndexOf('#') >= 0)
                {
                    return false;
                }
            }

            string? subPath = null;
            var query = body.IndexOf('?');
            if (query >= 0)
            {
                var parameters = body.Substring(query + 1);
                body = body.Substring(0, query);
                foreach (var part in parameters.Split('&'))
                {
                    if (!part.StartsWith("path=", StringComparison.Ordinal))
                    {
                        return false;
                    }
                    var value = part.Substring("path=".Length);
                    if (subPath is not null || value.Length == 0)
                    {
                        return false;
                    }
                    subPath = value;
                }
                if (subPath is null)
                {
                    return false;
                }
            }

            var form = PackageSourceRule.ClassifyGitSource(body, out var split);
            if (form == GitSourceForm.Url)
            {
                var scheme = body.Substring(0, split);
                var rest = body.Substring(split + "://".Length);
                var slash = rest.IndexOf('/');
                var authority = slash >= 0 ? rest.Substring(0, slash) : rest;
                var path = slash >= 0 ? rest.Substring(slash) : string.Empty;
                var at = authority.LastIndexOf('@');
                var userinfo = at >= 0 ? authority.Substring(0, at) : string.Empty;
                var hostPort = at >= 0 ? authority.Substring(at + 1) : authority;
                var normalized = AsciiLower(scheme) + "://" +
                    (userinfo.Length > 0 ? userinfo + "@" : string.Empty) +
                    AsciiLower(hostPort) + path;
                source = new PackageAddSource(true, null, null, normalized, subPath, revision);
                return true;
            }
            if (form == GitSourceForm.Scp)
            {
                var authority = body.Substring(0, split);
                var path = body.Substring(split + 1);
                var at = authority.IndexOf('@');
                var userinfo = at >= 0 ? authority.Substring(0, at) : string.Empty;
                var host = at >= 0 ? authority.Substring(at + 1) : authority;
                var normalized =
                    (userinfo.Length > 0 ? userinfo + "@" : string.Empty) +
                    AsciiLower(host) + ":" + path;
                source = new PackageAddSource(true, null, null, normalized, subPath, revision);
                return true;
            }

            var atSign = body.IndexOf('@');
            string name;
            string? version = null;
            if (atSign < 0)
            {
                name = body;
            }
            else
            {
                name = body.Substring(0, atSign);
                version = body.Substring(atSign + 1);
                if (version.Length == 0 ||
                    version.IndexOf('@') >= 0 ||
                    version.IndexOf(':') >= 0)
                {
                    return false;
                }
            }
            if (revision is not null ||
                subPath is not null ||
                !PackageSourceRule.IsValidRegistryName(name))
            {
                return false;
            }
            source = new PackageAddSource(false, name, version, null, null, null);
            return true;
        }

        /// <summary>ASCII-lower-cases a host or scheme segment; non-ASCII
        /// or already-lowered text passes through unchanged.</summary>
        internal static string AsciiLower(string text)
        {
            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = PackageSourceRule.AsciiLower(chars[i]);
            }
            return new string(chars);
        }
    }

    /// <summary>
    /// One registered package as reconciliation evidence — the
    /// UPM-provenance shape of <see cref="UpmPackageProvenance"/> plus the
    /// verbatim dependency text of <see cref="PackageInfo.packageId"/> the
    /// G8.1 source check needs.
    /// </summary>
    internal sealed class ResolvedPackageView
    {
        internal ResolvedPackageView(
            string name,
            string? version,
            string source,
            string? dependency,
            string? resolvedCommit,
            string? requestedRevision)
        {
            Name = name;
            Version = version;
            Source = source;
            Dependency = dependency;
            ResolvedCommit = resolvedCommit;
            RequestedRevision = requestedRevision;
        }

        /// <summary>The resolved package name.</summary>
        internal string Name { get; }

        /// <summary>The resolved package version (null when UPM reports
        /// none).</summary>
        internal string? Version { get; }

        /// <summary>The <see cref="PackageSource"/> member name,
        /// lowercased (<c>"registry"</c>, <c>"git"</c>, …).</summary>
        internal string Source { get; }

        /// <summary>The verbatim dependency text UPM reports in
        /// <see cref="PackageInfo.packageId"/> after the '@'.</summary>
        internal string? Dependency { get; }

        /// <summary>The full resolved commit SHA (Git packages only;
        /// <see cref="GitInfo.hash"/>).</summary>
        internal string? ResolvedCommit { get; }

        /// <summary>The requested revision (Git packages only;
        /// <see cref="GitInfo.revision"/>).</summary>
        internal string? RequestedRevision { get; }

        /// <summary>The <c>package</c> output payload (SPEC §I.4:
        /// <c>{name, version, source}</c>).</summary>
        internal JObject OutputJson() =>
            new JObject
            {
                ["name"] = Name,
                ["version"] = Version,
                ["source"] = Source,
            };
    }

    /// <summary>
    /// The installed-package state reconciliation compares against: the
    /// project manifest's <c>dependencies</c> object, the lockfile's
    /// <c>dependencies</c> object, and the registered package list —
    /// the three §G8.1 evidence channels together (never the manifest
    /// alone, never the name alone). Every channel also records whether
    /// it was read and parsed successfully: a channel that could not be
    /// read holds no evidence and can never prove absence.
    /// </summary>
    internal sealed class PackageInstallView
    {
        internal PackageInstallView(
            JObject? manifestDependencies,
            JObject? lockDependencies,
            IReadOnlyList<ResolvedPackageView> registered,
            bool manifestRead = true,
            bool lockRead = true,
            bool registeredRead = true)
        {
            ManifestDependencies = manifestDependencies;
            LockDependencies = lockDependencies;
            Registered = registered ?? throw new ArgumentNullException(nameof(registered));
            ManifestRead = manifestRead;
            LockRead = lockRead;
            RegisteredRead = registeredRead;
        }

        /// <summary><c>manifest.json</c>'s <c>dependencies</c> object
        /// (null when the parsed file holds no <c>dependencies</c> object
        /// or the channel could not be read — <see
        /// cref="ManifestRead"/>).</summary>
        internal JObject? ManifestDependencies { get; }

        /// <summary><c>packages-lock.json</c>'s <c>dependencies</c> object
        /// (null under the same conditions as <see
        /// cref="ManifestDependencies"/> — <see cref="LockRead"/>).</summary>
        internal JObject? LockDependencies { get; }

        /// <summary>The registered package list —
        /// <see cref="PackageInfo.GetAllRegisteredPackages"/> with
        /// UPM-resolved provenance (SPEC §X.3.2: self-declared metadata is
        /// never read). Empty when the channel could not be captured —
        /// a partial list is not evidence (<see
        /// cref="RegisteredRead"/>).</summary>
        internal IReadOnlyList<ResolvedPackageView> Registered { get; }

        /// <summary>True when <c>manifest.json</c> was read and parsed —
        /// false when the file is missing, unreadable or malformed.
        /// Unread evidence can never prove absence (SPEC §G8.1).</summary>
        internal bool ManifestRead { get; }

        /// <summary>True when <c>packages-lock.json</c> was read and
        /// parsed — the same conditions as <see
        /// cref="ManifestRead"/>.</summary>
        internal bool LockRead { get; }

        /// <summary>True when the registered package list was captured
        /// whole — false when <see
        /// cref="PackageInfo.GetAllRegisteredPackages"/> failed or
        /// returned nothing to inspect.</summary>
        internal bool RegisteredRead { get; }

        /// <summary>True when all three channels were read and parsed —
        /// the only capture that can prove absence (SPEC §G8.1
        /// NotApplied).</summary>
        internal bool AllChannelsRead =>
            ManifestRead && LockRead && RegisteredRead;

        /// <summary>The production capture: real manifest/lock reads and
        /// the real registered package list, each channel flagged by
        /// whether its read succeeded.</summary>
        internal static PackageInstallView Capture()
        {
            var registered = new List<ResolvedPackageView>();
            var registeredRead = CaptureRegistered(registered);
            var packagesDir = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Packages");
            return new PackageInstallView(
                ReadDependencies(packagesDir, "manifest.json", out var manifestRead),
                ReadDependencies(packagesDir, "packages-lock.json", out var lockRead),
                registered,
                manifestRead,
                lockRead,
                registeredRead);
        }

        internal static string SourceName(PackageSource source) =>
            source.ToString().ToLowerInvariant();

        internal static string? DependencyOf(string packageId)
        {
            var at = packageId.IndexOf('@');
            return at < 0 ? null : packageId.Substring(at + 1);
        }

        // A failed or partial registered-package capture is no evidence
        // at all: the list is cleared so nothing downstream can mistake
        // half a channel for proof (SPEC §G8.1).
        private static bool CaptureRegistered(List<ResolvedPackageView> registered)
        {
            try
            {
                var infos = PackageInfo.GetAllRegisteredPackages();
                if (infos is null)
                {
                    return false;
                }
                for (var i = 0; i < infos.Length; i++)
                {
                    var provenance = UpmPackageProvenance.FromPackageInfo(infos[i]);
                    registered.Add(new ResolvedPackageView(
                        provenance.PackageId,
                        provenance.Version,
                        SourceName(provenance.Source),
                        DependencyOf(infos[i].packageId),
                        provenance.ResolvedRevision,
                        provenance.RequestedRevision));
                }
                return true;
            }
            catch (Exception)
            {
                registered.Clear();
                return false;
            }
        }

        /// <summary>Reads one Packages-directory file's
        /// <c>dependencies</c> object. <paramref name="read"/> reports
        /// whether the channel evidence exists at all: false when the
        /// file is missing, unreadable or malformed (unparseable JSON,
        /// or a <c>dependencies</c> value that is not an object). A
        /// parsed file without <c>dependencies</c> is a successful read
        /// that returns null — the channel proved it holds no
        /// dependencies.</summary>
        internal static JObject? ReadDependencies(
            string packagesDir, string fileName, out bool read)
        {
            read = false;
            try
            {
                var path = Path.Combine(packagesDir, fileName);
                if (!File.Exists(path))
                {
                    return null;
                }
                var dependencies =
                    JObject.Parse(File.ReadAllText(path))["dependencies"];
                if (dependencies is not null && dependencies is not JObject)
                {
                    return null;
                }
                read = true;
                return dependencies as JObject;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The SPEC §G8.1 reconciliation evidence of <c>unity.package.add</c>:
    /// what Preflight persists before <c>StepStarted</c>, and how
    /// Reconcile decides <c>Completed</c> / <c>NotApplied</c> /
    /// <c>Unknown</c> against the persisted snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot carries the expected source identity (registry
    /// name/version or normalized Git origin + sub-path + requested
    /// revision + expected commit where determinable) and the recorded
    /// pre-execution state of all three channels restricted to this
    /// target — manifest deps, lock deps and registered packages that
    /// match the target — plus every package name each channel held,
    /// whatever its source, so a same-name artifact from a different
    /// source is still pre-existing evidence. §F5 common rule: a
    /// verdict needs evidence, and the Git evidence row is the full
    /// resolved commit SHA — package existence, name, manifest presence
    /// or a tag/branch name never qualify.
    /// </para>
    /// <para>
    /// <c>Completed</c> requires the three channels to agree exactly,
    /// all of them read now: exactly one registered package whose
    /// identity matches (registry name + resolved version; Git origin +
    /// sub-path + requested revision + full commit SHA equal to the
    /// persisted expected commit), exactly one manifest entry for the
    /// resolved name whose spec equals the request (the resolved
    /// version for registry; the origin + sub-path + requested revision
    /// for Git), exactly one lock entry for the resolved name whose
    /// source and resolved value agree — the version for registry, the
    /// resolved hash for Git, equal to the registered package's
    /// version / the persisted expected commit — and a snapshot whose
    /// recorded pre-state proves the target was absent — every channel
    /// read at preflight, nothing recorded, and the resolved name
    /// absent from every recorded name set. Any disagreement between
    /// the manifest, the lockfile and the registered package list is
    /// <c>Unknown</c> (SPEC §G8.1). An unread preflight channel or a
    /// snapshot without the name channels can never prove the
    /// installed package was not already there, so a fully matching
    /// install is then <c>Unknown</c>, never attributed to this step.
    /// <c>NotApplied</c> requires absence proven everywhere — every
    /// channel read successfully — <i>and</i> a snapshot that recorded
    /// absence with every channel read (any recorded evidence is a
    /// diverged pre-state → Unknown, and an unavailable channel means
    /// absence is not proven → Unknown). Everything else — moved tags,
    /// partial manifests, extra matches, modified artifacts — is
    /// <c>Unknown</c>; reconciliation never re-adds and never removes.
    /// </para>
    /// </remarks>
    internal static class PackageAddEvidence
    {
        // Fixed English diagnostic fragments for the Unknown verdict —
        // none of them carries the candidate id, a URL, or any
        // secret-bearing text (§G8 item 5: diagnostics never echo).
        internal const string SnapshotMalformed =
            "The persisted reconciliation snapshot is missing or malformed.";
        internal const string Diverged =
            "The target package is absent while the persisted snapshot " +
            "recorded it before the step ran.";
        internal const string Partial =
            "The package state is partial or inconsistent across the " +
            "manifest, the lockfile and the registered package list.";
        internal const string Multiple =
            "More than one installed package matches the expected source.";
        internal const string NoExpectedCommit =
            "The persisted expected commit is absent for a tag or branch request.";
        internal const string NoResolvedCommit =
            "The resolved package does not expose a full commit SHA.";
        internal const string CommitMismatch =
            "The resolved commit does not match the persisted expected commit.";
        internal const string VersionMismatch =
            "The resolved package version does not match the requested version.";
        internal const string SourceMismatch =
            "The resolved package source does not match the requested source kind.";
        internal const string PreExisting =
            "A package matching the expected source was already installed " +
            "before the step ran.";
        internal const string EvidenceUnreadable =
            "The manifest, the lockfile or the registered package list " +
            "could not be read, so absence cannot be proven.";
        internal const string RecordedStateIncomplete =
            "The persisted snapshot was recorded while a package-state " +
            "channel could not be read, so recorded absence cannot be proven.";
        internal const string NamePresenceUnproven =
            "The persisted snapshot does not prove whether a package with " +
            "the resolved name was already installed before the step ran.";

        private sealed class RecordedView
        {
            internal JObject Manifest = new JObject();
            internal JObject Lock = new JObject();
            internal List<ResolvedPackageView> Registered = new List<ResolvedPackageView>();

            // Every package name each channel held before Execute —
            // whatever its source, not only the target's entries. The
            // resolved candidate's name appearing in any of them makes
            // the current match a possible pre-existing same-name
            // artifact (SPEC §F5 attribution).
            internal HashSet<string> ManifestNames =
                new HashSet<string>(StringComparer.Ordinal);
            internal HashSet<string> LockNames =
                new HashSet<string>(StringComparer.Ordinal);
            internal HashSet<string> RegisteredNames =
                new HashSet<string>(StringComparer.Ordinal);

            // True only when the preflight capture could read all three
            // channels — an empty RecordedView without it is an unproven
            // pre-state, never recorded absence (SPEC §G8.1).
            internal bool AllRead;

            // True only when the persisted snapshot carries the three
            // name channels — a snapshot journaled without them can
            // never prove a same-name package was absent (SPEC §F5).
            internal bool NamesProven;

            internal bool IsEmpty =>
                Manifest.Count == 0 && Lock.Count == 0 && Registered.Count == 0;
        }

        /// <summary>The §G8.1 preflight snapshot: expected identity plus
        /// the recorded pre-execution state, restricted to the target.
        /// </summary>
        internal static JsonValue BuildSnapshot(
            string id, PackageAddSource source, PackageInstallView view)
        {
            if (id is null)
            {
                throw new ArgumentNullException(nameof(id));
            }
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (view is null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            var obj = new JObject
            {
                ["v"] = 1,
                ["id"] = id,
                ["kind"] = source.IsGit ? "git" : "registry",
            };
            if (source.IsGit)
            {
                obj["repositoryUrl"] = source.NormalizedOrigin;
                if (source.SubPath is not null)
                {
                    obj["packageSubPath"] = source.SubPath;
                }
                if (source.Revision is not null)
                {
                    obj["requestedRevision"] = source.Revision;
                }
                if (source.ExpectedCommit is { } expected)
                {
                    obj["expectedCommit"] = expected;
                }
            }
            else
            {
                obj["packageName"] = source.PackageName;
                if (source.Version is not null)
                {
                    obj["version"] = source.Version;
                }
            }
            obj["manifest"] = ToJObject(MatchingManifest(source, view.ManifestDependencies));
            obj["lock"] = ToJObject(MatchingLock(source, view.LockDependencies));
            obj["registered"] = ToJArray(MatchingRegistered(source, view.Registered));
            // The name evidence the source filter cannot express: every
            // package name each channel held before Execute, whatever its
            // source — registry, another Git origin or sub-path, local,
            // embedded. Only the resolved package learns its name, so the
            // snapshot records names rather than name-keyed entries.
            obj["names"] = new JObject
            {
                ["manifest"] = NamesOf(view.ManifestDependencies),
                ["lock"] = NamesOf(view.LockDependencies),
                ["registered"] = NamesOf(view.Registered),
            };
            obj["read"] = new JObject
            {
                ["manifest"] = view.ManifestRead,
                ["lock"] = view.LockRead,
                ["registered"] = view.RegisteredRead,
            };
            return JsonValue.From(obj);
        }

        /// <summary>True when the dependency text resolves to the same
        /// Git origin and sub-path as <paramref name="expected"/> —
        /// the shared identity half of both the snapshot and the verdict.
        /// </summary>
        internal static bool SameGitSource(PackageAddSource expected, string? dependency)
        {
            return dependency is not null &&
                PackageAddSource.TryParse(dependency, out var resolved) &&
                resolved!.IsGit &&
                string.Equals(
                    resolved.NormalizedOrigin, expected.NormalizedOrigin,
                    StringComparison.Ordinal) &&
                string.Equals(
                    resolved.SubPath, expected.SubPath, StringComparison.Ordinal);
        }

        /// <summary>
        /// The §G8.1 verdict: <paramref name="snapshot"/> is the persisted
        /// preflight evidence, <paramref name="view"/> the current
        /// install state.
        /// </summary>
        // Completed is awarded only when ALL of the following hold —
        // the §F5/§G8.1 three-channel agreement rule; any single failure
        // is Unknown, and NotApplied is returned only from the absence
        // branch when absence is proven in every channel:
        //   1. the persisted snapshot parses and carries the expected
        //      identity (else SnapshotMalformed);
        //   2. all three channels — manifest, lockfile and the
        //      registered package list — were read and parsed *now*, so
        //      each channel's evidence exists (else EvidenceUnreadable);
        //   3. exactly one registered package matches the expected
        //      source (else Partial or Multiple) and its identity
        //      matches the request: source "registry" and a version
        //      equal to the requested version for a registry request,
        //      or source "git", a persisted expected commit that could
        //      be determined, and a resolved full commit SHA equal to
        //      it for a Git request;
        //   4. exactly one manifest entry carries the expected source,
        //      keyed by the resolved name, and its spec equals the
        //      request — the resolved version for registry, the
        //      requested revision on the matching origin + sub-path for
        //      Git (else Partial);
        //   5. exactly one lock entry carries the expected source,
        //      keyed by the resolved name, its source/URL and sub-path
        //      match the request and its resolved value — the version
        //      for registry, the resolved hash for Git — equals the
        //      registered package's version / the persisted expected
        //      commit (else Partial);
        //   6. the preflight snapshot proves the target was absent:
        //      every channel read at capture, no recorded trace of the
        //      target or of the resolved name in any channel, and the
        //      recorded name sets present (else PreExisting,
        //      RecordedStateIncomplete or NamePresenceUnproven).
        // Any disagreement between two channels — a diverged manifest
        // spec, a lock version or hash that contradicts the registered
        // package — is Unknown per SPEC §G8.1.
        internal static ReconcileResult Judge(JsonValue snapshot, PackageInstallView view)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (view is null)
            {
                throw new ArgumentNullException(nameof(view));
            }
            if (!TryReadSnapshot(snapshot, out var expected, out var recorded) ||
                expected is null ||
                recorded is null)
            {
                return Unknown(SnapshotMalformed);
            }

            // A channel that could not be read holds no evidence: it is
            // excluded from matching so its content can neither count as
            // a match nor as proven absence (SPEC §G8.1).
            var mNow = MatchingManifest(
                expected,
                view.ManifestRead ? view.ManifestDependencies : null);
            var lNow = MatchingLock(
                expected,
                view.LockRead ? view.LockDependencies : null);
            var rNow = MatchingRegistered(
                expected,
                view.RegisteredRead
                    ? view.Registered
                    : (IReadOnlyList<ResolvedPackageView>)Array.Empty<ResolvedPackageView>());

            // Absent everywhere → NotApplied only when every channel
            // could be read *and* the persisted snapshot recorded a
            // fully-read absence too; an unread channel or a partial
            // recorded pre-state means absence is not proven, and
            // recorded evidence means the pre-state diverged — nothing
            // can be proven (§F5 rule 5, §G8.1 NotApplied).
            if (mNow.Count == 0 && lNow.Count == 0 && rNow.Count == 0)
            {
                if (!view.AllChannelsRead)
                {
                    return Unknown(EvidenceUnreadable);
                }
                if (recorded.IsEmpty)
                {
                    return recorded.AllRead
                        ? new ReconcileResult.NotApplied()
                        : Unknown(RecordedStateIncomplete);
                }
                return Unknown(Diverged);
            }
            if (rNow.Count == 0)
            {
                return Unknown(Partial);
            }
            if (rNow.Count > 1)
            {
                return Unknown(Multiple);
            }
            // A registered match exists, so the other channels' evidence
            // must exist too: a channel that could not be read now can
            // neither agree with the match nor prove anything (§G8.1).
            if (!view.AllChannelsRead)
            {
                return Unknown(EvidenceUnreadable);
            }

            var candidate = rNow[0];
            if (expected.IsGit)
            {
                if (!string.Equals(candidate.Source, "git", StringComparison.Ordinal))
                {
                    return Unknown(SourceMismatch);
                }
                if (expected.ExpectedCommit is null)
                {
                    return Unknown(NoExpectedCommit);
                }
                if (!PackageAddSource.IsFullCommitSha(candidate.ResolvedCommit))
                {
                    return Unknown(NoResolvedCommit);
                }
                if (!string.Equals(
                    candidate.ResolvedCommit, expected.ExpectedCommit,
                    StringComparison.Ordinal))
                {
                    return Unknown(CommitMismatch);
                }
            }
            else
            {
                if (!string.Equals(candidate.Source, "registry", StringComparison.Ordinal))
                {
                    return Unknown(SourceMismatch);
                }
                if (expected.Version is { } wanted &&
                    !string.Equals(candidate.Version, wanted, StringComparison.Ordinal))
                {
                    return Unknown(VersionMismatch);
                }
            }
            if (!ManifestConsistent(mNow, expected, candidate) ||
                !LockConsistent(lNow, expected, candidate))
            {
                return Unknown(Partial);
            }
            if (RecordedMatch(recorded, expected, candidate))
            {
                return Unknown(PreExisting);
            }
            // A channel the preflight capture could not read recorded
            // nothing, so the empty record cannot prove the target was
            // absent: a fully matching installation may be pre-existing
            // and attribution is unproven (SPEC §F5).
            if (!recorded.AllRead)
            {
                return Unknown(RecordedStateIncomplete);
            }
            // A snapshot without the recorded name channels cannot prove
            // a same-name package was absent either, so attribution is
            // just as unproven (SPEC §F5).
            if (!recorded.NamesProven)
            {
                return Unknown(NamePresenceUnproven);
            }
            return new ReconcileResult.Completed(
                new Dictionary<string, JsonValue>
                {
                    [UnityPackageAddOperation.PackagePort] =
                        JsonValue.From(candidate.OutputJson()),
                });
        }

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        // ---------- snapshot read-back ----------

        private static bool TryReadSnapshot(
            JsonValue snapshot,
            out PackageAddSource? expected,
            out RecordedView? recorded)
        {
            expected = null;
            recorded = null;
            if (snapshot.ToToken() is not JObject obj ||
                obj["kind"] is not JValue { Type: JTokenType.String } kind)
            {
                return false;
            }
            var kindText = (string)kind.Value!;
            if (string.Equals(kindText, "git", StringComparison.Ordinal))
            {
                if (obj["repositoryUrl"] is not JValue { Type: JTokenType.String } origin)
                {
                    return false;
                }
                expected = PackageAddSource.Git(
                    (string)origin.Value!,
                    Text(obj["packageSubPath"]),
                    Text(obj["requestedRevision"]),
                    Text(obj["expectedCommit"]));
                recorded = ReadRecorded(obj);
                return true;
            }
            if (string.Equals(kindText, "registry", StringComparison.Ordinal))
            {
                if (obj["packageName"] is not JValue { Type: JTokenType.String } name)
                {
                    return false;
                }
                expected = PackageAddSource.Registry(
                    (string)name.Value!, Text(obj["version"]));
                recorded = ReadRecorded(obj);
                return true;
            }
            return false;

            static string? Text(JToken? token) =>
                token is JValue { Type: JTokenType.String } value
                    ? (string)value.Value!
                    : null;
        }

        private static RecordedView ReadRecorded(JObject obj)
        {
            var recorded = new RecordedView();
            if (obj["manifest"] is JObject manifest)
            {
                recorded.Manifest = manifest;
            }
            if (obj["lock"] is JObject lockDeps)
            {
                recorded.Lock = lockDeps;
            }
            if (obj["registered"] is JArray registered)
            {
                foreach (var entry in registered)
                {
                    if (entry is JObject item &&
                        item["name"] is JValue { Type: JTokenType.String } name)
                    {
                        recorded.Registered.Add(new ResolvedPackageView(
                            (string)name.Value!,
                            Text(item["version"]),
                            Text(item["source"]) ?? string.Empty,
                            Text(item["dependency"]),
                            Text(item["resolvedCommit"]),
                            Text(item["requestedRevision"])));
                    }
                }
            }
            // A snapshot without readable-channel flags cannot prove its
            // recorded absence — missing or malformed flags fail closed.
            recorded.AllRead =
                obj["read"] is JObject read &&
                Flag(read["manifest"]) &&
                Flag(read["lock"]) &&
                Flag(read["registered"]);
            // A snapshot without the per-channel name sets cannot prove
            // a same-name package was absent — the same fail-closed rule.
            recorded.NamesProven = ReadNames(obj["names"], recorded);
            return recorded;

            static bool Flag(JToken? token) =>
                token is JValue { Type: JTokenType.Boolean } value &&
                (bool)value.Value!;

            static string? Text(JToken? token) =>
                token is JValue { Type: JTokenType.String } value
                    ? (string)value.Value!
                    : null;
        }

        // The §F5 same-name evidence of the snapshot schema: every
        // package name each channel held, whatever its source. A missing
        // or malformed names object proves nothing — fail closed.
        private static bool ReadNames(JToken? token, RecordedView recorded)
        {
            if (token is not JObject names ||
                names["manifest"] is not JArray manifest ||
                names["lock"] is not JArray lockDeps ||
                names["registered"] is not JArray registered)
            {
                return false;
            }
            CollectNames(manifest, recorded.ManifestNames);
            CollectNames(lockDeps, recorded.LockNames);
            CollectNames(registered, recorded.RegisteredNames);
            return true;

            static void CollectNames(JArray array, HashSet<string> into)
            {
                foreach (var item in array)
                {
                    if (item is JValue { Type: JTokenType.String } name)
                    {
                        into.Add((string)name.Value!);
                    }
                }
            }
        }

        // The §F5 pre-existing-artifact half of the verdict: *any*
        // recorded trace of the target in any channel — a manifest dep, a
        // lock entry, or a registered package at whatever version/commit —
        // means the current match cannot be attributed to this step, so
        // the verdict stays Unknown (never Completed on a state the
        // preflight already saw).
        private static bool RecordedMatch(
            RecordedView recorded, PackageAddSource expected, ResolvedPackageView candidate)
        {
            if (MatchingManifest(expected, recorded.Manifest).Count > 0 ||
                MatchingLock(expected, recorded.Lock).Count > 0)
            {
                return true;
            }
            foreach (var entry in recorded.Registered)
            {
                if (expected.IsGit
                    ? SameGitSource(expected, entry.Dependency)
                    : string.Equals(
                        entry.Name, expected.PackageName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            // The resolved candidate's name recorded in *any* channel
            // before Execute — a registry version, another Git origin or
            // sub-path, a local or embedded package — means the current
            // match may be that pre-existing same-name artifact, never
            // proof this step installed it (SPEC §F5 attribution).
            return recorded.ManifestNames.Contains(candidate.Name) ||
                recorded.LockNames.Contains(candidate.Name) ||
                recorded.RegisteredNames.Contains(candidate.Name);
        }

        // ---------- target matching over the three channels ----------

        private static List<KeyValuePair<string, string>> MatchingManifest(
            PackageAddSource expected, JObject? dependencies)
        {
            var matches = new List<KeyValuePair<string, string>>();
            if (dependencies is null)
            {
                return matches;
            }
            foreach (var property in dependencies.Properties())
            {
                if (expected.IsGit)
                {
                    if (property.Value is JValue { Type: JTokenType.String } text &&
                        SameGitSource(expected, (string)text.Value!))
                    {
                        matches.Add(new KeyValuePair<string, string>(
                            property.Name, (string)text.Value!));
                    }
                }
                else if (string.Equals(
                    property.Name, expected.PackageName, StringComparison.Ordinal))
                {
                    matches.Add(new KeyValuePair<string, string>(
                        property.Name,
                        property.Value is JValue { Type: JTokenType.String } text
                            ? (string)text.Value! : string.Empty));
                }
            }
            return matches;
        }

        private static List<KeyValuePair<string, JToken>> MatchingLock(
            PackageAddSource expected, JObject? dependencies)
        {
            var matches = new List<KeyValuePair<string, JToken>>();
            if (dependencies is null)
            {
                return matches;
            }
            foreach (var property in dependencies.Properties())
            {
                if (expected.IsGit)
                {
                    if (property.Value is JObject entry &&
                        entry["version"] is JValue { Type: JTokenType.String } text &&
                        SameGitSource(expected, (string)text.Value!))
                    {
                        matches.Add(new KeyValuePair<string, JToken>(property.Name, entry));
                    }
                }
                else if (string.Equals(
                    property.Name, expected.PackageName, StringComparison.Ordinal))
                {
                    matches.Add(new KeyValuePair<string, JToken>(property.Name, property.Value));
                }
            }
            return matches;
        }

        private static List<ResolvedPackageView> MatchingRegistered(
            PackageAddSource expected, IReadOnlyList<ResolvedPackageView> registered)
        {
            var matches = new List<ResolvedPackageView>();
            foreach (var entry in registered)
            {
                var match = expected.IsGit
                    ? SameGitSource(expected, entry.Dependency)
                    : string.Equals(entry.Name, expected.PackageName, StringComparison.Ordinal);
                if (match)
                {
                    matches.Add(entry);
                }
            }
            return matches;
        }

        // The manifest's half of the §G8.1 three-channel agreement:
        // exactly one entry may carry the expected source, it must be
        // keyed by the resolved package name, and its spec must equal
        // the request — the resolved version for registry, the
        // requested revision on the already-matched origin + sub-path
        // for Git. A second entry carrying the same source, a
        // differently keyed match, or a diverged spec is channel
        // disagreement, never Completed.
        private static bool ManifestConsistent(
            IReadOnlyList<KeyValuePair<string, string>> manifestNow,
            PackageAddSource expected,
            ResolvedPackageView candidate)
        {
            if (manifestNow.Count != 1 ||
                !string.Equals(
                    manifestNow[0].Key, candidate.Name, StringComparison.Ordinal))
            {
                return false;
            }
            if (expected.IsGit)
            {
                return PackageAddSource.TryParse(manifestNow[0].Value, out var resolved) &&
                    string.Equals(
                        resolved!.Revision, expected.Revision, StringComparison.Ordinal);
            }
            return string.Equals(
                manifestNow[0].Value, candidate.Version, StringComparison.Ordinal);
        }

        // The lockfile's half of the same rule: exactly one entry may
        // carry the expected source, it must be keyed by the resolved
        // package name, its declared "source" must equal the request's
        // kind — "git" for a Git request, "registry" for a registry
        // request — its source/URL and sub-path (Git) must match the
        // request, and its resolved value must agree with the registered
        // package — the resolved version for registry, the resolved
        // hash equal to the persisted expected commit for Git. A lock
        // entry that names the package but declares another source or
        // none, or resolves a different version or commit, is
        // manifest/lockfile/Package Manager disagreement, never
        // Completed (SPEC §G8.1).
        private static bool LockConsistent(
            IReadOnlyList<KeyValuePair<string, JToken>> lockNow,
            PackageAddSource expected,
            ResolvedPackageView candidate)
        {
            if (lockNow.Count != 1 ||
                !string.Equals(
                    lockNow[0].Key, candidate.Name, StringComparison.Ordinal) ||
                lockNow[0].Value is not JObject entry)
            {
                return false;
            }
            if (entry["source"] is not JValue { Type: JTokenType.String } source ||
                !string.Equals(
                    (string)source.Value!,
                    expected.IsGit ? "git" : "registry",
                    StringComparison.Ordinal))
            {
                return false;
            }
            if (expected.IsGit)
            {
                if (entry["version"] is not JValue { Type: JTokenType.String } text ||
                    !SameGitSource(expected, (string)text.Value!) ||
                    !PackageAddSource.TryParse((string)text.Value!, out var resolved) ||
                    !string.Equals(
                        resolved!.Revision, expected.Revision, StringComparison.Ordinal))
                {
                    return false;
                }
                return entry["hash"] is JValue { Type: JTokenType.String } hash &&
                    string.Equals(
                        (string)hash.Value!, expected.ExpectedCommit,
                        StringComparison.Ordinal);
            }
            return entry["version"] is JValue { Type: JTokenType.String } version &&
                string.Equals(
                    (string)version.Value!, candidate.Version, StringComparison.Ordinal);
        }

        // ---------- snapshot writers ----------

        private static JObject ToJObject(IReadOnlyList<KeyValuePair<string, string>> pairs)
        {
            var obj = new JObject();
            foreach (var pair in pairs)
            {
                obj[pair.Key] = pair.Value;
            }
            return obj;
        }

        private static JObject ToJObject(IReadOnlyList<KeyValuePair<string, JToken>> pairs)
        {
            var obj = new JObject();
            foreach (var pair in pairs)
            {
                obj[pair.Key] = pair.Value;
            }
            return obj;
        }

        private static JArray ToJArray(IReadOnlyList<ResolvedPackageView> entries)
        {
            var array = new JArray();
            foreach (var entry in entries)
            {
                var item = new JObject
                {
                    ["name"] = entry.Name,
                    ["source"] = entry.Source,
                };
                if (entry.Version is not null)
                {
                    item["version"] = entry.Version;
                }
                if (entry.Dependency is not null)
                {
                    item["dependency"] = entry.Dependency;
                }
                if (entry.ResolvedCommit is not null)
                {
                    item["resolvedCommit"] = entry.ResolvedCommit;
                }
                if (entry.RequestedRevision is not null)
                {
                    item["requestedRevision"] = entry.RequestedRevision;
                }
                array.Add(item);
            }
            return array;
        }

        // Every dependency name a manifest/lockfile channel held —
        // every source, not only the target's.
        private static JArray NamesOf(JObject? dependencies)
        {
            var names = new JArray();
            if (dependencies is not null)
            {
                foreach (var property in dependencies.Properties())
                {
                    names.Add(property.Name);
                }
            }
            return names;
        }

        // Every package name the registered list held — every source.
        private static JArray NamesOf(IReadOnlyList<ResolvedPackageView> registered)
        {
            var names = new JArray();
            foreach (var entry in registered)
            {
                names.Add(entry.Name);
            }
            return names;
        }
    }
}
